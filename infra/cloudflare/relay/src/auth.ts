/**
 * Ferry 認証 (CF 単独完結)
 *
 * エンドポイント:
 *   POST /auth/token  - PC 用 (1h): ECDSA P-256 IEEE P1363 raw 署名チャレンジで deviceId 所有を証明し、
 *                       自前 HMAC bearer (cfToken) を発行する。
 *
 * cfToken は Worker が自前 HMAC で検証するので外部 ID プロバイダへのログインが要らない。
 * /signaling・/presence・/pairs・/inbox の認可入口で verifySessionToken により検証する。
 *
 * 設計詳細は docs/design/cf-only-migration.md §2。
 * （旧 Firebase Custom Token 経路 /pair/token と mintCustomToken は CF 単独完結 Step 7 で撤去済み。
 *   Bridge のペアリングは同一オリジンの /pair/create（D1 nonce 認可）に一本化した。）
 */

import type { Env } from './index';

// ---------- Public handlers ----------

/** PC 用: 署名チャレンジ検証 + KV first-write-wins binding + 1h cfToken */
export async function handleAuthToken(req: Request, env: Env): Promise<Response> {
  const ip = req.headers.get('CF-Connecting-IP') ?? 'unknown';
  if (env.RATELIMIT_IP) {
    const { success } = await env.RATELIMIT_IP.limit({ key: ip });
    if (!success) return jsonError(429, 'IP_RATE_LIMIT', 'IP rate limit exceeded');
  }

  const body = await readJsonBody(req);
  if ('error' in body) return body.error;

  const { deviceId, pubKeySpki, ts, sig } = body.value as Record<string, unknown>;
  if (typeof deviceId !== 'string' || !/^[a-f0-9]{32}$/.test(deviceId)) {
    return jsonError(400, 'INVALID_DEVICE_ID', 'deviceId must be 32 hex chars');
  }
  if (typeof pubKeySpki !== 'string' || pubKeySpki.length === 0 || pubKeySpki.length > 256) {
    return jsonError(400, 'INVALID_PUBKEY', 'pubKeySpki must be non-empty base64url <= 256 chars');
  }
  if (typeof ts !== 'number' || !Number.isFinite(ts)) {
    return jsonError(400, 'INVALID_TS', 'ts must be a unix-ms number');
  }
  const now = Date.now();
  if (Math.abs(ts - now) > 60_000) {
    return jsonError(400, 'CLOCK_SKEW', 'ts skew > 60s', { serverTime: now });
  }
  if (typeof sig !== 'string' || sig.length === 0 || sig.length > 200) {
    return jsonError(400, 'INVALID_SIG', 'sig must be non-empty base64url');
  }

  // Codex 第6弾 #2 fix: device-scoped rate limit は **signature 検証後** に移動。
  // 未検証段階で deviceId に課金すると、攻撃者が他人の deviceId で /auth/token を
  // 無効署名で叩き続けて RATELIMIT_DEVICE を枯渇させ、本物 client の auth を 429 で
  // 止める DoS が成立する (deviceId は QR 経由で peer に渡るため秘密ではない)。
  // 検証前の保護は前段の RATELIMIT_IP (per-IP) が担当する。
  // ECDSA verify 自体の CPU コストは数 ms で、IP rate limit が flood を抑える前提。

  // ECDSA P-256 SHA-256 IEEE P1363 raw 署名検証（純関数 verifyEcdsaSig に抽出 — CF 単独完結移行で
  // /signaling 等の他経路からも device 署名検証を再利用できるようにする）。エラーコードは従来と同一。
  const message = `ferry-auth-v1|${deviceId}|${pubKeySpki}|${ts}`;
  const sigResult = await verifyEcdsaSig(pubKeySpki, message, sig);
  if (!sigResult.ok) {
    return sigResult.code === 'INVALID_SIG_FORMAT'
      ? jsonError(400, 'INVALID_SIG_FORMAT', 'pubKey or signature could not be decoded/imported')
      : jsonError(401, 'BAD_SIGNATURE', 'signature verification failed');
  }

  // Codex 第7弾 #4 fix (P2): device rate limit は **KV binding 一致確認後** に消費する。
  // 旧実装は signature verify 直後に device RL を消費していたため、attacker が自分の鍵で
  // 「victim deviceId を主張する」リクエストを作ると、 signature は自鍵で valid → device RL を
  // victim 名義で消費 → 続いて KV check で DEVICE_PUBKEY_MISMATCH 401 を受ける、 という流れで
  // victim の RATELIMIT_DEVICE 枠だけ枯渇させられた (本物 client が 429 で締め出される DoS)。
  // 対策: KV existing と pubKeySpki が mismatch なら device RL を消費せず即 401 を返す。
  // mismatch でない (= 既存 binding と一致 / 新規 bind) ときだけ device RL を消費する。
  // signature verify 済かつ pubKey 一致前提なので、 RL 消費は正規 client のみに帰着する。
  const kvKey = `device-pubkey:${deviceId}`;
  const existing = await env.DEVICE_KEY_BINDING.get(kvKey);
  if (existing !== null && existing !== pubKeySpki) {
    // identity.key 紛失時のクライアントは clean slate UI を出してから別 deviceId で再認証する。
    // device RL を消費せずに即 reject (attacker が victim の RL を消費する経路を閉じる)。
    return jsonError(401, 'DEVICE_PUBKEY_MISMATCH', 'deviceId is already bound to a different pubKey');
  }

  // pubKey 一致確認後に device rate limit を消費 (mismatch では消費しない設計)
  if (env.RATELIMIT_DEVICE) {
    const { success } = await env.RATELIMIT_DEVICE.limit({ key: deviceId });
    if (!success) return jsonError(429, 'DEVICE_RATE_LIMIT', 'deviceId rate limit exceeded');
  }

  // KV first-write-wins binding (deviceId ↔ pubKeySpki): 新規のみ put
  if (existing === null) {
    await env.DEVICE_KEY_BINDING.put(kvKey, pubKeySpki);
  }

  // CF 単独完結: 自前 HMAC bearer (cfToken) を発行 (uid = deviceId, exp = iat+3600)。
  // /signaling・/presence・/pairs・/inbox の認可に使う。SESSION_HMAC_SECRET は本番必須。
  if (!env.SESSION_HMAC_SECRET) {
    return jsonError(500, 'SERVER_MISCONFIGURED', 'SESSION_HMAC_SECRET is not configured');
  }
  const cfToken = await mintSessionToken(deviceId, 3600, env);
  return jsonOk({ cfToken, expiresIn: 3600 });
}

// ---------- CF 単独完結: device 署名検証 + 自前 HMAC bearer (cfToken) ----------
//
// docs/design/cf-only-migration.md §2。Firebase の「Worker が Custom Token mint → クライアントが
// Firebase ログイン → idToken」二段を「Worker が ECDSA 検証して自前 HMAC bearer を発行 →
// クライアントが Bearer で DO/D1 を叩く」一段に畳んだ。Firebase RTDB 撤去後もそのまま生きる。

/** verifyEcdsaSig の結果。decode/import 失敗は INVALID_SIG_FORMAT、検証不成立は BAD_SIGNATURE。 */
export type EcdsaSigResult = { ok: true } | { ok: false; code: 'INVALID_SIG_FORMAT' | 'BAD_SIGNATURE' };

/**
 * ECDSA P-256 SHA-256 IEEE P1363 raw 署名検証（純関数）。
 * handleAuthToken からの抽出。/signaling・/pair/create 等が device 所有を検証する際に再利用する。
 */
export async function verifyEcdsaSig(
  pubKeySpki: string,
  message: string,
  sigB64Url: string,
): Promise<EcdsaSigResult> {
  let verified = false;
  try {
    const pubKey = await crypto.subtle.importKey(
      'spki',
      base64UrlDecode(pubKeySpki),
      { name: 'ECDSA', namedCurve: 'P-256' },
      false,
      ['verify'],
    );
    verified = await crypto.subtle.verify(
      { name: 'ECDSA', hash: 'SHA-256' },
      pubKey,
      base64UrlDecode(sigB64Url),
      new TextEncoder().encode(message),
    );
  } catch {
    return { ok: false, code: 'INVALID_SIG_FORMAT' };
  }
  return verified ? { ok: true } : { ok: false, code: 'BAD_SIGNATURE' };
}

/**
 * cfToken (自前 HMAC bearer) を HS256 で発行。
 * payload は最小 `{ sub: deviceId, iat, exp }`。クライアントは decode 不要
 * (uid は自分の deviceId として自明、exp はレスポンス値) なので AOT セーフ。
 */
export async function mintSessionToken(deviceId: string, expiresInSec: number, env: Env): Promise<string> {
  const iat = Math.floor(Date.now() / 1000);
  const exp = iat + expiresInSec;
  const header = { alg: 'HS256', typ: 'JWT' };
  const payload = { sub: deviceId, iat, exp };
  return hs256Sign(header, payload, env.SESSION_HMAC_SECRET);
}

/**
 * cfToken を検証して deviceId を返す。署名不一致 / 期限切れ / 形式不正 / secret 未設定は null。
 * /signaling・/presence・/pairs・/inbox の認可入口で使う。
 */
export async function verifySessionToken(token: string, env: Env): Promise<{ deviceId: string } | null> {
  if (!env.SESSION_HMAC_SECRET) return null;
  const parts = token.split('.');
  if (parts.length !== 3) return null;
  const [headerB64, payloadB64, sigB64] = parts;
  let ok = false;
  try {
    const key = await hmacKey(env.SESSION_HMAC_SECRET);
    ok = await crypto.subtle.verify(
      'HMAC',
      key,
      base64UrlDecode(sigB64),
      new TextEncoder().encode(`${headerB64}.${payloadB64}`),
    );
  } catch {
    return null;
  }
  if (!ok) return null;
  let payload: { sub?: unknown; exp?: unknown };
  try {
    payload = JSON.parse(new TextDecoder().decode(base64UrlDecode(payloadB64)));
  } catch {
    return null;
  }
  if (typeof payload.sub !== 'string' || !/^[a-f0-9]{32}$/.test(payload.sub)) return null;
  // exp は秒。clock skew は持たせず厳密に判定 (1h TTL に対し数秒のズレは誤差範囲)。
  if (typeof payload.exp !== 'number' || payload.exp * 1000 < Date.now()) return null;
  return { deviceId: payload.sub };
}

async function hmacKey(secret: string): Promise<CryptoKey> {
  return crypto.subtle.importKey(
    'raw',
    new TextEncoder().encode(secret),
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign', 'verify'],
  );
}

/** HS256 JWT を HMAC secret で署名する (header.payload.signature の base64url 連結)。 */
async function hs256Sign(header: object, payload: object, secret: string): Promise<string> {
  if (!secret) throw new Error('SESSION_HMAC_SECRET is not configured');
  const enc = new TextEncoder();
  const headerB64 = base64UrlEncode(enc.encode(JSON.stringify(header)));
  const payloadB64 = base64UrlEncode(enc.encode(JSON.stringify(payload)));
  const unsigned = `${headerB64}.${payloadB64}`;
  const key = await hmacKey(secret);
  const sigBuf = await crypto.subtle.sign('HMAC', key, enc.encode(unsigned));
  return `${unsigned}.${base64UrlEncode(new Uint8Array(sigBuf))}`;
}

// ---------- Helpers ----------

export function base64UrlEncode(buf: Uint8Array): string {
  let s = '';
  for (const b of buf) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function base64UrlDecode(s: string): Uint8Array {
  const pad = s.length % 4 === 0 ? '' : '='.repeat(4 - (s.length % 4));
  const std = s.replace(/-/g, '+').replace(/_/g, '/') + pad;
  const bin = atob(std);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

async function readJsonBody(req: Request): Promise<{ value: unknown } | { error: Response }> {
  try {
    const v = await req.json();
    if (v === null || typeof v !== 'object') {
      return { error: jsonError(400, 'INVALID_JSON', 'JSON body must be an object') };
    }
    return { value: v };
  } catch {
    return { error: jsonError(400, 'INVALID_JSON', 'JSON parse failed') };
  }
}

function jsonOk(body: object): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });
}

function jsonError(status: number, code: string, message: string, extra?: object): Response {
  const body = { error: code, message, ...(extra ?? {}) };
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}
