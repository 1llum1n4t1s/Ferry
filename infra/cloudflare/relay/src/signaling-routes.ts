/**
 * Ferry signaling HTTP ルート (`/sig/{pairId}/...`) — CF 単独完結移行 Step 1。
 *
 * 認可をここで完結させてから PairDO に委譲する:
 *   1. Authorization: Bearer <cfToken> を verifySessionToken で検証し deviceId を得る
 *   2. pairId 形式 ({a}_{b}, 各 32hex) + 当事者検証 (deviceId ∈ {a, b})
 *   3. 書込は sender キー強制 (PairDO へ X-Ferry-Device=deviceId を渡し、DO は自分キーにしか書かない)
 *   4. pairId を SALT 付き SHA-256 でハッシュ化して PairDO インスタンスへ forward
 *
 * これにより Firebase rules がやっていた「当事者限定 R/W」「per-sender なりすまし防止 (#D-003)」を
 * 宣言的 rules でなく Worker コードで担保する。クライアント (C# desktop) からの呼出なので CORS は不要。
 */

import type { Env } from './index';
import { hashPairId } from './index';
import { verifySessionToken } from './auth';
import { notifyInbox } from './device-routes';

const PAIR_ID_RE = /^[a-f0-9]{32}_[a-f0-9]{32}$/;

export async function handleSignaling(req: Request, env: Env, url: URL): Promise<Response> {
  // path: /sig/{pairId}/{rest...}
  const segs = url.pathname.split('/').filter((s) => s.length > 0); // ["sig", pairId, ...rest]
  if (segs.length < 2) return jsonError(400, 'BAD_PATH', 'expected /sig/{pairId}/...');
  const pairId = segs[1];
  const rest = segs.slice(2).join('/');
  const action = segs[2] ?? '';

  // 1. Bearer 認可
  const authz = req.headers.get('Authorization');
  if (!authz || !authz.startsWith('Bearer ')) {
    return jsonError(401, 'NO_BEARER', 'Authorization: Bearer <cfToken> required');
  }
  const claims = await verifySessionToken(authz.slice('Bearer '.length), env);
  if (!claims) return jsonError(401, 'BAD_TOKEN', 'cfToken invalid or expired');

  // 2. pairId 形式 + 当事者検証
  if (!PAIR_ID_RE.test(pairId)) return jsonError(400, 'BAD_PAIR_ID', 'pairId must be {32hex}_{32hex}');
  const [a, b] = pairId.split('_');
  if (claims.deviceId !== a && claims.deviceId !== b) {
    return jsonError(403, 'NOT_PARTICIPANT', 'deviceId is not a participant of pairId');
  }

  // 3+4. PairDO へ forward (sender キーは X-Ferry-Device で強制)
  const doId = env.PAIR.idFromName(await hashPairId(pairId, env.SALT));
  const stub = env.PAIR.get(doId);

  const fwdHeaders = new Headers();
  fwdHeaders.set('X-Ferry-Device', claims.deviceId);
  let body: string | undefined;
  if (req.method === 'POST') {
    body = await req.text();
    fwdHeaders.set('content-type', 'application/json');
  }
  const fwdUrl = `https://do/${rest}${url.search}`;
  const fwdReq = new Request(fwdUrl, { method: req.method, headers: fwdHeaders, body });
  const resp = await stub.fetch(fwdReq);

  // CF 使用量削減: offer / probe-offer の書込成功時、ペア相手の DeviceDO inbox へ「接続ノック」を
  // push する（type=knock・transient、DeviceDO は storage に積まず接続中 WS にだけ送る）。
  // クライアント listener はこのノックを主検知経路にして、常時 400ms ポーリング（~20万 req/日/ペア）を
  // 低頻度の安全網ポーリングへ落とす。answer/endpoint は送信側が能動ポーリング（数秒で有界）なので不要。
  if (resp.ok && req.method === 'POST' && (action === 'offer' || action === 'probe-offer')) {
    const peer = claims.deviceId === a ? b : a;
    try {
      await notifyInbox(env, peer, { type: 'knock', pairId, from: claims.deviceId, createdAt: Date.now() });
    } catch {
      /* ノック失敗は無害（listener の安全網ポーリングが拾う）。offer 書込自体は成功済み */
    }
  }
  return resp;
}

function jsonError(status: number, code: string, message: string): Response {
  return new Response(JSON.stringify({ error: code, message }), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}
