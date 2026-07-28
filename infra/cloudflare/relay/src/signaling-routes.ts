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
import { jsonError } from './http';

const PAIR_ID_RE = /^[a-f0-9]{32}_[a-f0-9]{32}$/;

/** ctx.waitUntil だけを使うので最小形で受ける（テストからスタブを渡せる）。 */
type WaitUntil = Pick<ExecutionContext, 'waitUntil'>;

export async function handleSignaling(req: Request, env: Env, url: URL, ctx?: WaitUntil): Promise<Response> {
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

  // 2.5. rere レビュー #C-31: device-scoped rate limit。
  // 当事者検証は「pairId の 2 要素の一方が自分」しか見ず D1 `pairs` の実在を確認しないため、
  // cfToken を 1 つ取れば `{自分}_{任意の32hex}` を無数に作って PairDO を生成でき、
  // 各々が storage 書込 + 1h alarm を保持する（DO/storage 課金の増殖）。それを抑止する。
  //
  // ⚠️ 枠は **RATELIMIT_SIG（/sig/* 専用）** を使う。v1.0.70 で低頻度用の RATELIMIT_DEVICE
  // (30 req/60s、/auth/token は約 50 分に 1 回・/pair/link はペアリング時のみ) を流用したが、
  // シグナリングの実レートは桁が違う（接続 1 回 = offer POST + answer GET 400ms×20s ≒ 52 req、
  // 経路 Probe 1 回 ≒ 17 req）。結果として **送信側が自分の枠を焼き切り、相手が返した answer を
  // 読む GET が 429 で弾かれ続けて「相手から応答がありません」で必ず失敗**していた
  // (2026-07-28 実測。429 も枠を消費するのでポーリング継続中は枠が回復せず自己閉塞する)。
  // 上限は実測ピーク（接続 1 + Probe 2 ≒ 90 req/分）に複数ピア・リトライ分の余裕を見た値。
  // RATELIMIT_DEVICE へフォールバックしないのは、それが上記の障害そのものを復活させるため
  // （binding は wrangler.toml で宣言済みなので本番では常に存在する）。
  if (env.RATELIMIT_SIG) {
    const { success } = await env.RATELIMIT_SIG.limit({ key: claims.deviceId });
    if (!success) return jsonError(429, 'DEVICE_RATE_LIMIT', 'deviceId rate limit exceeded');
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
  // ノック配送はレスポンス値に使わず失敗も無害なので、待たずに ctx.waitUntil へ逃がす
  // （相手 DeviceDO が cold start だと subrequest 1 往復ぶん offer POST が遅れ、接続確立の
  //   有界予算〔answer 20s / offer-v2 8s / endpoint 10s〕を無駄に食う）。
  if (resp.ok && req.method === 'POST' && (action === 'offer' || action === 'probe-offer')) {
    const peer = claims.deviceId === a ? b : a;
    // rere レビュー #C-30: 配送結果 (delivered) を捨てず観測する。
    // ノックは v1.0.67 で着信検知の主経路になったが、成否も到達率もどこにも記録されて
    // いなかった。inbox WS 接続が壊れると全ユーザーが黙って安全網ポーリングへ退行し、
    // 機能は「動く」ので healthcheck も転送成功率も無傷 → 退行が起きた事実自体を
    // 検知できない silent failure になっていた。delivered=0（相手 WS 未接続）は正常な
    // 縮退なので info、例外は error として残す。
    const knock = notifyInbox(env, peer, {
      type: 'knock',
      pairId,
      from: claims.deviceId,
      createdAt: Date.now(),
    })
      .then((delivered) => {
        console.log('knock', JSON.stringify({ action, delivered }));
      })
      .catch((e) => {
        /* ノック失敗自体は無害（listener の安全網ポーリングが拾う）。offer 書込は成功済み */
        console.error('knock failed', JSON.stringify({ action, error: String(e) }));
      });
    if (ctx) ctx.waitUntil(knock);
    else await knock; // ctx 無し（テスト等）は従来どおり待つ
  }
  return resp;
}
