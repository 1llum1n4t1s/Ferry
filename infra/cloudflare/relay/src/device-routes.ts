/**
 * Ferry device ルート (`/presence/...`, `/inbox`) — CF 単独完結移行 Step 2。
 *
 * 認可をここで完結させて DeviceDO に委譲する:
 *   - presence write/delete: cfToken の deviceId == path の deviceId (本人のみ)
 *   - presence read: 任意の認証済 (相手のオンライン検知の正当用途。Firebase rules の受容リスクと同等)
 *   - inbox WS: cfToken の deviceId の DeviceDO にのみ接続 (path/query の deviceId は信用しない)
 *
 * deviceId は SALT 付き SHA-256 でハッシュ化して DO 名に使う (生 deviceId を idFromName に渡さない)。
 */

import type { Env } from './index';
import { hashPairId } from './index'; // 汎用 salted SHA-256 (pairId/deviceId 共通の DO 名ハッシュ)
import { verifySessionToken } from './auth';
import { jsonError } from './http';

const DEVICE_RE = /^[a-f0-9]{32}$/;

async function authBearer(req: Request, env: Env): Promise<{ deviceId: string } | null> {
  const authz = req.headers.get('Authorization');
  if (!authz || !authz.startsWith('Bearer ')) return null;
  return verifySessionToken(authz.slice('Bearer '.length), env);
}

async function deviceStub(env: Env, deviceId: string): Promise<DurableObjectStub> {
  const id = env.DEVICE.idFromName(await hashPairId(deviceId, env.SALT));
  return env.DEVICE.get(id);
}

export async function handlePresence(req: Request, env: Env, url: URL): Promise<Response> {
  // /presence/{deviceId} または /presence/{deviceId}/last-seen
  const segs = url.pathname.split('/').filter((s) => s.length > 0);
  const deviceId = segs[1] ?? '';
  if (!DEVICE_RE.test(deviceId)) return jsonError(400, 'BAD_DEVICE_ID', 'deviceId must be 32 hex');

  const claims = await authBearer(req, env);
  if (!claims) return jsonError(401, 'BAD_TOKEN', 'cfToken invalid or expired');

  const method = req.method;
  if ((method === 'POST' || method === 'DELETE') && claims.deviceId !== deviceId) {
    return jsonError(403, 'NOT_SELF', 'presence write/delete must target own deviceId');
  }

  const stub = await deviceStub(env, deviceId);
  const fwdHeaders = new Headers();
  const inm = req.headers.get('If-None-Match');
  if (inm) fwdHeaders.set('If-None-Match', inm);
  let body: string | undefined;
  if (method === 'POST') {
    body = await req.text();
    fwdHeaders.set('content-type', 'application/json');
  }
  const sub = segs[2] === 'last-seen' ? 'presence/last-seen' : 'presence';
  return stub.fetch(new Request(`https://do/${sub}`, { method, headers: fwdHeaders, body }));
}

export async function handleInbox(req: Request, env: Env): Promise<Response> {
  if (req.headers.get('Upgrade') !== 'websocket') {
    return new Response('Expected websocket', { status: 426 });
  }
  // ClientWebSocket は Authorization ヘッダを付けられる (C# 側 Options.SetRequestHeader)。
  const claims = await authBearer(req, env);
  if (!claims) return new Response('Unauthorized', { status: 401 });

  // 接続先 DeviceDO は token.deviceId から決まる (自分の inbox にしか繋げない)。
  const stub = await deviceStub(env, claims.deviceId);
  // WS upgrade をそのまま forward (URL を DO 内部パスに差し替えつつ upgrade を保持)。
  return stub.fetch(new Request('https://do/inbox', req));
}

/**
 * Worker 内部: ペア成立 / 接続ノックを deviceId の DeviceDO inbox へ push する。
 *
 * rere レビュー #C-30: DeviceDO の notify は `{ ok, delivered }` を返しているのに
 * 呼び出し側が戻り値を捨てていたため、「相手の inbox WS へ実際に届いたか」がどこにも
 * 残らなかった。接続中 WS の本数を返して呼び出し側が観測できるようにする
 * （delivered=0 は「相手がオフライン」という正常な縮退で、エラーではない）。
 */
export async function notifyInbox(env: Env, deviceId: string, event: object): Promise<number> {
  const stub = await deviceStub(env, deviceId);
  const resp = await stub.fetch(
    new Request('https://do/notify', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(event),
    }),
  );
  if (!resp.ok) throw new Error(`notify failed: HTTP ${resp.status}`);
  const body = (await resp.json()) as { delivered?: unknown };
  return typeof body.delivered === 'number' ? body.delivered : 0;
}
