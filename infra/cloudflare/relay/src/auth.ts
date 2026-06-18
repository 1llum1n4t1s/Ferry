/**
 * Ferry Custom Token Auth (rere #D-001a Phase B)
 *
 * 2 つのエンドポイント:
 *   POST /auth/token  - PC 用 (1h): ECDSA P-256 IEEE P1363 raw 署名チャレンジで deviceId 所有を証明
 *   POST /pair/token  - Bridge 用 (5min): sessions/{sid}/PairingNonce 一致で QR と紐付け
 *
 * 設計詳細は docs/design/firebase-auth-pair-ssot.md §4。
 *
 * Firebase Custom Token JWT 仕様: https://firebase.google.com/docs/auth/admin/create-custom-tokens
 *   - 必須 claims: iss, sub (= SA client_email), aud (Identity Toolkit), iat, exp, uid
 *   - 署名: RS256 (SA private_key PKCS#8 PEM を Web Crypto API に import)
 *   - firebase-admin は Cloudflare Workers 非対応 → 手書き JWT で実装
 */

import type { Env } from './index';

// ---------- Public handlers ----------

/** PC 用: 署名チャレンジ検証 + KV first-write-wins binding + 1h Custom Token */
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

  // ECDSA P-256 SHA-256 IEEE P1363 raw 署名検証
  const message = `ferry-auth-v1|${deviceId}|${pubKeySpki}|${ts}`;
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
      base64UrlDecode(sig),
      new TextEncoder().encode(message),
    );
  } catch {
    return jsonError(400, 'INVALID_SIG_FORMAT', 'pubKey or signature could not be decoded/imported');
  }
  if (!verified) {
    return jsonError(401, 'BAD_SIGNATURE', 'signature verification failed');
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

  // Custom Token 発行 (uid = deviceId, exp = iat+3600, src=pc)
  const customToken = await mintCustomToken(deviceId, 3600, env, 'pc');
  return jsonOk({ customToken, expiresIn: 3600 });
}

/** Bridge 用: sessions/{sid}/PairingNonce 一致 → 5min Custom Token */
export async function handlePairToken(req: Request, env: Env): Promise<Response> {
  const ip = req.headers.get('CF-Connecting-IP') ?? 'unknown';
  if (env.RATELIMIT_IP) {
    const { success } = await env.RATELIMIT_IP.limit({ key: ip });
    if (!success) return jsonError(429, 'IP_RATE_LIMIT', 'IP rate limit exceeded');
  }

  const body = await readJsonBody(req);
  if ('error' in body) return body.error;

  // Codex P1 (第2弾) fix: Bridge は QR スキャンで「自分の sid+nonce」と「相手 (scanned) の sid+nonce」の
  // 4 値を持つ。旧仕様は sidA+nonceA だけを verify していたので、攻撃者が他人の sidB を知っていれば自分の
  // sidA で auth → pairings/{sidB}/{pid} に好きなデータを書ける (rules は片側 nonce 検証しかしていなかった
  // ペアリング起点を強制できず Ghost peer 注入可能だった)。peer* が提供されたら両方の nonce を verify する。
  // peer* を省略した場合は従来どおり片側 verify のみ (PC コード貼付ペアリング経路の後方互換)。
  const { sessionId, pairingNonce, peerSessionId, peerPairingNonce } = body.value as Record<string, unknown>;
  if (typeof sessionId !== 'string' || !/^[a-f0-9]{32}$/.test(sessionId)) {
    return jsonError(400, 'INVALID_SESSION_ID', 'sessionId must be 32 hex chars');
  }
  if (typeof pairingNonce !== 'string' || !/^[a-f0-9]{32}$/.test(pairingNonce)) {
    return jsonError(400, 'INVALID_NONCE', 'pairingNonce must be 32 hex chars');
  }
  const hasPeer = peerSessionId !== undefined || peerPairingNonce !== undefined;
  if (hasPeer) {
    if (typeof peerSessionId !== 'string' || !/^[a-f0-9]{32}$/.test(peerSessionId)) {
      return jsonError(400, 'INVALID_PEER_SESSION_ID', 'peerSessionId must be 32 hex chars');
    }
    if (typeof peerPairingNonce !== 'string' || !/^[a-f0-9]{32}$/.test(peerPairingNonce)) {
      return jsonError(400, 'INVALID_PEER_NONCE', 'peerPairingNonce must be 32 hex chars');
    }
    if (peerSessionId === sessionId) {
      return jsonError(400, 'PEER_SAME_AS_SELF', 'peerSessionId must differ from sessionId');
    }
  }

  // Codex 第6弾 #2 fix: session-scoped rate limit は **nonce 検証後** に移動。
  // 未検証段階で sessionId に課金すると、攻撃者が他人の sessionId で /pair/token を
  // 不正 nonce で叩き続けて RATELIMIT_SESSION を枯渇させ、本物 Bridge tab の token
  // 発行を 429 で止める DoS が成立する。検証前の保護は前段の RATELIMIT_IP (per-IP)
  // が担当する。なお Firebase REST に対する fetch のコストはあるが、IP rate limit
  // (60req/60s) で flood は抑えられる前提。

  // Codex P1 fix: PairingNonce は sessions/ ではなく pairing_nonces/ (rules で .read=false の server-only ノード) に分離。
  // SA access_token で pairing_nonces/{sid} を読んで一致確認する。
  const accessToken = await getServiceAccountAccessToken(env);

  async function verifyNonce(sid: string, expectedNonce: string, label: string): Promise<Response | null> {
    const url = `${env.FIREBASE_DATABASE_URL}/pairing_nonces/${sid}.json?access_token=${encodeURIComponent(accessToken)}`;
    const r = await fetch(url);
    if (!r.ok) return jsonError(502, 'FIREBASE_ERROR', `Firebase GET pairing_nonces/${sid} (${label}) -> ${r.status}`);
    const record = (await r.json()) as { CreatedAt?: number; Nonce?: string } | null;
    if (!record || typeof record.Nonce !== 'string') return jsonError(404, 'SESSION_NOT_FOUND', `${label} pairing_nonces not present`);
    if (record.Nonce !== expectedNonce) return jsonError(401, 'INVALID_NONCE_MATCH', `${label} PairingNonce does not match`);
    if (typeof record.CreatedAt !== 'number' || Date.now() - record.CreatedAt > 3_600_000) {
      return jsonError(401, 'EXPIRED_SESSION', `${label} session expired (>1h)`);
    }
    return null;
  }

  const selfErr = await verifyNonce(sessionId, pairingNonce, 'self');
  if (selfErr) return selfErr;
  if (hasPeer) {
    const peerErr = await verifyNonce(peerSessionId as string, peerPairingNonce as string, 'peer');
    if (peerErr) return peerErr;
  }

  // nonce 検証成功後に session rate limit を消費 (正規 Bridge tab のみカウント)
  if (env.RATELIMIT_SESSION) {
    const { success } = await env.RATELIMIT_SESSION.limit({ key: sessionId });
    if (!success) return jsonError(429, 'SESSION_RATE_LIMIT', 'sessionId rate limit exceeded');
  }

  // 5min Custom Token (uid = sessionId, src=bridge → rules で pairing_nonces/sessions 書込不可)
  // Codex 第8弾 #2 fix (P1): 2-nonce verified (hasPeer=true) のときだけ `pairAuth: true` を claim に埋める。
  // 1-QR の bridge token (sessions read のみで pairings には書かせない) には pairAuth を載せない。
  // rules で `pairings/{$deviceId}/{$pid}.write` に `auth.token.pairAuth == true` を AND し、
  // QR 1 枚だけで取った bridge token で他人 inbox に pairing 注入する経路を構造的に塞ぐ。
  const customToken = await mintCustomToken(
    sessionId,
    300,
    env,
    'bridge',
    hasPeer ? { pairAuth: true } : undefined,
  );
  return jsonOk({ customToken, expiresIn: 300 });
}

// ---------- Custom Token / OAuth ----------

/**
 * Firebase Custom Token JWT を SA 鍵で署名して発行する。
 * 必須 claims (iss, sub, aud, iat, exp, uid) を満たさないと Identity Toolkit が 400 を返す。
 *
 * Codex P1 fix (第3弾): `src` を Custom Token の追加 claims に埋め、Firebase rules 側で
 * `auth.token.src` として PC/Bridge を区別できるようにする。Bridge token (src="bridge") は
 * pairing_nonces を書けない / sessions を書けない 等を rules で禁じ、PC token (src="pc") のみが
 * セッション関連 state を作成・更新できるようにする (Bridge tab が tab 残存中に nonce rotation
 * で session を蘇生する穴を塞ぐ)。
 *
 * Codex 第8弾 #2 fix (P1): `extraClaims` で `pairAuth: true` を bridge token に埋める経路を追加。
 * handlePairToken は **2-nonce verified** (= hasPeer=true) の path でだけ `pairAuth: true` を渡し、
 * 1-QR path (sessions read only) では渡さない。rules 側で pairings 書込時に
 * `auth.token.pairAuth == true` を必須化し、「QR 1 枚だけで取った bridge token で他人 inbox に
 * pairing 注入」を構造的に防ぐ。
 */
export async function mintCustomToken(
  uid: string,
  expiresInSec: number,
  env: Env,
  source: 'pc' | 'bridge' = 'pc',
  extraClaims?: Record<string, unknown>,
): Promise<string> {
  const iat = Math.floor(Date.now() / 1000);
  const exp = iat + expiresInSec;
  const header = { alg: 'RS256', typ: 'JWT' };
  const payload = {
    iss: env.FIREBASE_CLIENT_EMAIL,
    sub: env.FIREBASE_CLIENT_EMAIL,
    aud: 'https://identitytoolkit.googleapis.com/google.identity.identitytoolkit.v1.IdentityToolkit',
    iat,
    exp,
    uid,
    // Firebase Custom Token は `claims` 直下の任意フィールドを ID token の `auth.token.<key>` に伝搬する。
    // extraClaims が undefined のときは spread が no-op になるので 1-QR path では src のみが残る。
    claims: { src: source, ...(extraClaims ?? {}) },
  };
  return rs256Sign(header, payload, env.FIREBASE_PRIVATE_KEY);
}

/**
 * SA を OAuth 2.0 JWT-bearer で交換して REST 用 access_token を取得。
 *
 * モジュールレベルでトークンをキャッシュする（Cloudflare Workers の同一 isolate 内で再利用）。
 * `expires_in` から 60s のマージンを引いた時刻まで再利用し、それ以降は新規取得する。
 * 同一 isolate 内で /pair/token が連続呼出されるケースで Google OAuth 200-500ms RTT と
 * QPS リミット消費を削減する目的（Gemini code review 指摘）。新 isolate では再取得が走る。
 */
let cachedAccessToken: { token: string; expiresAt: number } | null = null;

export async function getServiceAccountAccessToken(env: Env): Promise<string> {
  const now = Date.now();
  if (cachedAccessToken && cachedAccessToken.expiresAt > now) {
    return cachedAccessToken.token;
  }
  const iat = Math.floor(now / 1000);
  const exp = iat + 3600;
  const header = { alg: 'RS256', typ: 'JWT' };
  const payload = {
    iss: env.FIREBASE_CLIENT_EMAIL,
    // Codex P1 fix (第3弾): Firebase Realtime Database REST は firebase.database + userinfo.email の
    // 両 scope を要求する (https://firebase.google.com/docs/database/rest/auth)。userinfo.email が欠落
    // すると DB read が permission_denied になり Bridge token 発行が連鎖失敗する。cleanup workflow と揃える。
    scope: 'https://www.googleapis.com/auth/firebase.database https://www.googleapis.com/auth/userinfo.email',
    aud: 'https://oauth2.googleapis.com/token',
    iat,
    exp,
  };
  const assertion = await rs256Sign(header, payload, env.FIREBASE_PRIVATE_KEY);
  const body = new URLSearchParams({
    grant_type: 'urn:ietf:params:oauth:grant-type:jwt-bearer',
    assertion,
  });
  const r = await fetch('https://oauth2.googleapis.com/token', {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body,
  });
  const j = (await r.json()) as { access_token?: string; expires_in?: number; error?: string };
  if (!j.access_token) {
    throw new Error('OAuth token exchange failed: ' + JSON.stringify(j));
  }
  const lifetimeMs = (j.expires_in ?? 3600) * 1000;
  cachedAccessToken = { token: j.access_token, expiresAt: now + lifetimeMs - 60_000 };
  return j.access_token;
}

/** テスト用: モジュールキャッシュを強制クリアする（本番経路では使わない）。 */
export function _resetAccessTokenCacheForTests(): void {
  cachedAccessToken = null;
}

/** RS256 JWT を SA PEM PKCS#8 で署名 */
export async function rs256Sign(
  header: object,
  payload: object,
  privateKeyPem: string,
): Promise<string> {
  const enc = new TextEncoder();
  const headerB64 = base64UrlEncode(enc.encode(JSON.stringify(header)));
  const payloadB64 = base64UrlEncode(enc.encode(JSON.stringify(payload)));
  const unsigned = `${headerB64}.${payloadB64}`;
  const der = pemPkcs8ToDer(privateKeyPem);
  const key = await crypto.subtle.importKey(
    'pkcs8',
    der,
    { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' },
    false,
    ['sign'],
  );
  const sigBuf = await crypto.subtle.sign('RSASSA-PKCS1-v1_5', key, enc.encode(unsigned));
  return `${unsigned}.${base64UrlEncode(new Uint8Array(sigBuf))}`;
}

// ---------- Helpers ----------

function pemPkcs8ToDer(pem: string): ArrayBuffer {
  const body = pem
    .replace(/-----BEGIN PRIVATE KEY-----/g, '')
    .replace(/-----END PRIVATE KEY-----/g, '')
    .replace(/\s+/g, '');
  return base64Decode(body);
}

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

function base64Decode(s: string): ArrayBuffer {
  const bin = atob(s);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out.buffer;
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
