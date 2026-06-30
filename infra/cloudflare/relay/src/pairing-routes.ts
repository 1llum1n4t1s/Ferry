/**
 * Ferry pairing / pairs ルート — CF 単独完結移行 Step 2 / Step 7。
 *
 *   POST   /pair/session            — PC が自分のセッション+nonce を登録 (bearer self)
 *   GET    /pair/session/{sid}      — セッションの displayName/publicKey 取得 (bearer 任意・コード貼付用)
 *   DELETE /pair/session/{sid}      — セッション+nonce を revoke (bearer self)
 *   POST   /pair/create             — **両 sid の nonce 値所有が認可** (bearer なし)。D1 で server 検証し
 *                                     両 DeviceDO inbox へペア成立を push。Bridge(browser) から同一オリジンで呼ぶ
 *   POST   /pair/link               — **自分の bearer + 相手セッションの存在**が認可 (PC コード貼付ペアリング用)。
 *                                     sidA は cfToken の claims から (=本人性は HMAC 署名が担保)、相手 sidB は
 *                                     「pairing_nonces に行がありセッションが 1h 以内」であることだけを要求する
 *                                     (相手の nonce 値の所有は不要)。旧 Firebase rules の
 *                                     `pairing_nonces/{sidB}.exists()` 条件と同じセキュリティレベル。
 *   PUT    /pairs/{pairId}          — pairs SSoT 書込 (bearer 当事者)
 *   GET    /pairs/{pairId}          — pairs SSoT 取得 (bearer 当事者)。404 で remote-unpair 検出
 *   DELETE /pairs/{pairId}          — unpair (bearer 当事者)。相手 inbox へ unpair push も送る
 *
 * D1 read のポーリング線形増殖 (realtime 批判) を避けるため、unpair は GET ポーリング検出だけに頼らず
 * DELETE 時に相手へ push する。/pairs GET は起動時/復帰時の低頻度 reconcile に留める方針 (クライアント側)。
 */

import type { Env } from './index';
import { verifySessionToken } from './auth';
import { notifyInbox } from './device-routes';

const HEX32 = /^[a-f0-9]{32}$/;
const PAIR_ID_RE = /^[a-f0-9]{32}_[a-f0-9]{32}$/;
const NONCE_TTL_MS = 60 * 60 * 1000;

/** C# ConnectionService.GeneratePairId と同じ規約: deviceId を Ordinal 昇順に並べて "_" 連結。
 *  hex 小文字なら JS 文字列比較 (UTF-16 code unit) は .NET Ordinal と一致する。 */
export function derivePairId(a: string, b: string): string {
  return a < b ? `${a}_${b}` : `${b}_${a}`;
}

const str = (v: unknown): string => (typeof v === 'string' ? v : '');

async function authBearer(req: Request, env: Env): Promise<{ deviceId: string } | null> {
  const authz = req.headers.get('Authorization');
  if (!authz || !authz.startsWith('Bearer ')) return null;
  return verifySessionToken(authz.slice('Bearer '.length), env);
}

// ---------- /pair/session ----------

export async function handlePairSession(req: Request, env: Env, url: URL): Promise<Response> {
  const segs = url.pathname.split('/').filter((s) => s.length > 0); // ["pair","session", sid?]
  const claims = await authBearer(req, env);
  if (!claims) return jsonError(401, 'BAD_TOKEN', 'cfToken invalid or expired');
  const method = req.method;

  // POST /pair/session — 自分のセッション登録
  if (method === 'POST' && segs.length === 2) {
    const body = (await req.json()) as Record<string, unknown>;
    const nonce = str(body.pairingNonce);
    if (!HEX32.test(nonce)) return jsonError(400, 'BAD_NONCE', 'pairingNonce must be 32 hex');
    const now = Date.now();
    await env.DB.prepare(
      'INSERT OR REPLACE INTO sessions (sid, display_name, public_key, created_at) VALUES (?,?,?,?)',
    )
      .bind(claims.deviceId, str(body.displayName), str(body.publicKey), now)
      .run();
    await env.DB.prepare(
      'INSERT OR REPLACE INTO pairing_nonces (sid, nonce, created_at) VALUES (?,?,?)',
    )
      .bind(claims.deviceId, nonce, now)
      .run();
    return jsonOk({ ok: true });
  }

  const sid = segs[2] ?? '';
  if (!HEX32.test(sid)) return jsonError(400, 'BAD_SID', 'sid must be 32 hex');

  if (method === 'GET') {
    const row = await env.DB.prepare('SELECT display_name, public_key FROM sessions WHERE sid=?')
      .bind(sid)
      .first<{ display_name: string; public_key: string }>();
    if (!row) return jsonError(404, 'NOT_FOUND', 'session not present');
    return jsonOk({ displayName: row.display_name, publicKey: row.public_key });
  }
  if (method === 'DELETE') {
    if (claims.deviceId !== sid) return jsonError(403, 'NOT_SELF', 'can only revoke own session');
    await env.DB.prepare('DELETE FROM sessions WHERE sid=?').bind(sid).run();
    await env.DB.prepare('DELETE FROM pairing_nonces WHERE sid=?').bind(sid).run();
    return jsonOk({ ok: true });
  }
  return jsonError(405, 'METHOD_NOT_ALLOWED', method);
}

// ---------- /pair/create (nonce 所有が認可・bearer なし) ----------

export async function handlePairCreate(req: Request, env: Env): Promise<Response> {
  // bearer 不要の公開エンドポイント (Bridge から呼ぶ) なので IP rate limit で乱打を抑える
  // (/auth/token の handleAuthToken と同じパターン)。
  const ip = req.headers.get('CF-Connecting-IP') ?? 'unknown';
  if (env.RATELIMIT_IP) {
    const { success } = await env.RATELIMIT_IP.limit({ key: ip });
    if (!success) return jsonError(429, 'IP_RATE_LIMIT', 'IP rate limit exceeded');
  }

  const body = (await req.json()) as Record<string, unknown>;
  const sidA = str(body.sidA);
  const sidB = str(body.sidB);
  const nonceA = str(body.nonceA);
  const nonceB = str(body.nonceB);
  if (!HEX32.test(sidA) || !HEX32.test(sidB)) return jsonError(400, 'BAD_SID', 'sidA/sidB must be 32 hex');
  if (sidA === sidB) return jsonError(400, 'SAME_SID', 'sidA and sidB must differ');
  if (!HEX32.test(nonceA) || !HEX32.test(nonceB)) return jsonError(400, 'BAD_NONCE', 'nonceA/nonceB must be 32 hex');

  // 両 sid の nonce を D1 で server 検証 (Ghost peer 注入防止: nonce 値一致 + 1h 以内)。
  const errA = await verifyNonce(env, sidA, nonceA, 'A');
  if (errA) return errA;
  const errB = await verifyNonce(env, sidB, nonceB, 'B');
  if (errB) return errB;

  const pairingId = await notifyPairEstablished(env, sidA, str(body.nameA), sidB, str(body.nameB), str(body.pkA), str(body.pkB));
  return jsonOk({ ok: true, pairingId });
}

async function verifyNonce(env: Env, sid: string, nonce: string, label: string): Promise<Response | null> {
  const row = await env.DB.prepare('SELECT nonce, created_at FROM pairing_nonces WHERE sid=?')
    .bind(sid)
    .first<{ nonce: string; created_at: number }>();
  if (!row) return jsonError(404, 'SESSION_NOT_FOUND', `${label}: pairing_nonces not present`);
  if (row.nonce !== nonce) return jsonError(401, 'INVALID_NONCE_MATCH', `${label}: nonce mismatch`);
  if (Date.now() - row.created_at > NONCE_TTL_MS) return jsonError(401, 'EXPIRED_SESSION', `${label}: session expired`);
  return null;
}

/** sid のセッションが現在アクティブ（pairing_nonces に行があり 1h 以内）か、nonce 値を問わずに確認する。
 *  verifyNonce との違いは「値の一致」を見ない点のみ (handlePairLink の「相手の nonce 値所有は不要」要件用)。 */
async function verifySessionActive(env: Env, sid: string, label: string): Promise<Response | null> {
  const row = await env.DB.prepare('SELECT created_at FROM pairing_nonces WHERE sid=?')
    .bind(sid)
    .first<{ created_at: number }>();
  if (!row) return jsonError(404, 'SESSION_NOT_FOUND', `${label}: pairing_nonces not present`);
  if (Date.now() - row.created_at > NONCE_TTL_MS) return jsonError(401, 'EXPIRED_SESSION', `${label}: session expired`);
  return null;
}

/** pairingId を導出し、両 deviceId の DeviceDO inbox にペア成立イベントを push する
 *  (handlePairCreate / handlePairLink で共通)。2 つの DO は互いに独立しているため並列実行する。 */
async function notifyPairEstablished(
  env: Env,
  sidA: string,
  nameA: string,
  sidB: string,
  nameB: string,
  pkA: string,
  pkB: string,
): Promise<string> {
  const pairingId = derivePairId(sidA, sidB);
  const event = {
    pairingId,
    sidA,
    nameA: nameA || 'PC-A',
    sidB,
    nameB: nameB || 'PC-B',
    pkA,
    pkB,
    createdAt: Date.now(),
  };
  await Promise.all([notifyInbox(env, sidA, event), notifyInbox(env, sidB, event)]);
  return pairingId;
}

// ---------- /pair/link (bearer 必須・PC コード貼付ペアリング用) ----------

/**
 * 自分の bearer (cfToken) で sidA=本人を保証し、相手 (sidB) は「セッションが現在アクティブ
 * (pairing_nonces に行があり 1h 以内)」であることだけを要求する。相手の nonce 値の所有は不要。
 *
 * 攻撃者が被害者 C の sidC（コード）を知っているだけで C の inbox にペアリングを注入できないか:
 * - claims.deviceId は cfToken の HMAC 署名で保証されるため、攻撃者は「自分自身」としてしか
 *   sidA を名乗れない (sidA == claims.deviceId を強制)。
 * - 攻撃者は自分 (sidA=攻撃者) と sidB=C の組で /pair/link を呼べるが、これは
 *   「攻撃者が C とペアリングしようとした」というだけの事象で、C 側ユーザーが PairingDetected を
 *   受けて新規ピアとして表示される (rere #D-001(b) の通常のペアリング成立と同じ)。第三者 X の
 *   inbox に「sidA=攻撃者, sidB=X」以外の組（例えば被害者 Y と Z を勝手にペアリングする等）を
 *   注入することはできない（sidA は常に claims.deviceId 固定）。
 */
export async function handlePairLink(req: Request, env: Env): Promise<Response> {
  const claims = await authBearer(req, env);
  if (!claims) return jsonError(401, 'BAD_TOKEN', 'cfToken invalid or expired');

  // bearer 検証済み (本人確認済み) なので deviceId-scoped rate limit で sidB 総当たり探索や
  // notifyInbox 経由のペアリング通知スパムを抑える (/auth/token の RATELIMIT_DEVICE と同じパターン)。
  if (env.RATELIMIT_DEVICE) {
    const { success } = await env.RATELIMIT_DEVICE.limit({ key: claims.deviceId });
    if (!success) return jsonError(429, 'DEVICE_RATE_LIMIT', 'deviceId rate limit exceeded');
  }

  const body = (await req.json()) as Record<string, unknown>;
  const sidA = claims.deviceId;
  const sidB = str(body.sidB);
  if (!HEX32.test(sidB)) return jsonError(400, 'BAD_SID', 'sidB must be 32 hex');
  if (sidA === sidB) return jsonError(400, 'SAME_SID', 'sidB must differ from caller deviceId');

  const sessionErr = await verifySessionActive(env, sidB, 'sidB');
  if (sessionErr) return sessionErr;

  const pairingId = await notifyPairEstablished(env, sidA, str(body.nameA), sidB, str(body.nameB), str(body.pkA), str(body.pkB));
  return jsonOk({ ok: true, pairingId });
}

// ---------- /pairs/{pairId} (SSoT) ----------

export async function handlePairs(req: Request, env: Env, url: URL): Promise<Response> {
  const segs = url.pathname.split('/').filter((s) => s.length > 0); // ["pairs", pairId]
  const pairId = segs[1] ?? '';
  if (!PAIR_ID_RE.test(pairId)) return jsonError(400, 'BAD_PAIR_ID', 'pairId must be {32hex}_{32hex}');

  const claims = await authBearer(req, env);
  if (!claims) return jsonError(401, 'BAD_TOKEN', 'cfToken invalid or expired');
  const [a, b] = pairId.split('_');
  if (claims.deviceId !== a && claims.deviceId !== b) {
    return jsonError(403, 'NOT_PARTICIPANT', 'deviceId is not a participant of pairId');
  }
  const method = req.method;

  if (method === 'PUT') {
    const body = (await req.json()) as Record<string, unknown>;
    const createdAt = typeof body.createdAt === 'number' ? body.createdAt : Date.now();
    await env.DB.prepare(
      'INSERT OR REPLACE INTO pairs (pair_id, name_a, name_b, created_at) VALUES (?,?,?,?)',
    )
      .bind(pairId, str(body.nameA), str(body.nameB), createdAt)
      .run();
    return jsonOk({ ok: true });
  }
  if (method === 'GET') {
    const row = await env.DB.prepare('SELECT pair_id, name_a, name_b, created_at FROM pairs WHERE pair_id=?')
      .bind(pairId)
      .first<{ pair_id: string; name_a: string; name_b: string; created_at: number }>();
    if (!row) return jsonError(404, 'NOT_FOUND', 'pair not present'); // PairSyncService の remote-unpair 検出
    return jsonOk({ pairId: row.pair_id, nameA: row.name_a, nameB: row.name_b, createdAt: row.created_at });
  }
  if (method === 'DELETE') {
    await env.DB.prepare('DELETE FROM pairs WHERE pair_id=?').bind(pairId).run();
    // 相手へ unpair を push (D1 ポーリング線形増殖を避ける・realtime 批判 §0-5)。
    const peer = claims.deviceId === a ? b : a;
    await notifyInbox(env, peer, { type: 'unpair', pairingId: pairId, createdAt: Date.now() });
    return jsonOk({ ok: true });
  }
  return jsonError(405, 'METHOD_NOT_ALLOWED', method);
}

function jsonOk(body: object): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'content-type': 'application/json' } });
}
function jsonError(status: number, code: string, message: string): Response {
  return new Response(JSON.stringify({ error: code, message }), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}
