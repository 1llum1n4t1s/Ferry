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
import { jsonOk, jsonError, readJsonObject } from './http';

const HEX32 = /^[a-f0-9]{32}$/;
const PAIR_ID_RE = /^[a-f0-9]{32}_[a-f0-9]{32}$/;
const NONCE_TTL_MS = 60 * 60 * 1000;
const MAX_DISPLAY_NAME_LEN = 128;
const MAX_PUBLIC_KEY_LEN = 256;

/**
 * 台帳の段階移行フラグ。
 *
 * Env は index.ts 側で宣言されるが、このファイルは型宣言の更新前でも単体テスト
 * できるよう、ここでは実行時の optional field として扱う。未設定だけは既存
 * クライアント互換の transition とし、明示された値は厳密に検証する。
 */
type PairLedgerMode = 'transition' | 'required';
type RuntimeEnv = Env & { PAIR_LEDGER_MODE?: unknown };

function pairLedgerMode(env: Env): PairLedgerMode | null {
  const configured = (env as RuntimeEnv).PAIR_LEDGER_MODE;
  if (configured === undefined) return 'transition';
  if (configured === 'transition' || configured === 'required') return configured;
  return null;
}

function ledgerMisconfigured(): Response {
  return jsonError(503, 'PAIR_LEDGER_MISCONFIGURED', 'PAIR_LEDGER_MODE must be transition or required');
}

function d1Unavailable(): Response {
  return jsonError(503, 'D1_UNAVAILABLE', 'pair ledger unavailable');
}

/** 任意文字列を上限付きで読む。未指定は旧クライアント互換で空文字とする。 */
function boundedText(
  body: Record<string, unknown>,
  key: string,
  max: number,
  errorCode: string,
): { value: string } | { error: Response } {
  const value = body[key];
  if (value === undefined) return { value: '' };
  if (typeof value !== 'string' || value.length > max) {
    return { error: jsonError(400, errorCode, `${key} must be a string of at most ${max} characters`) };
  }
  return { value };
}

function readPairNames(body: Record<string, unknown>): { nameA: string; nameB: string } | { error: Response } {
  const nameA = boundedText(body, 'nameA', MAX_DISPLAY_NAME_LEN, 'BAD_NAME');
  if ('error' in nameA) return nameA;
  const nameB = boundedText(body, 'nameB', MAX_DISPLAY_NAME_LEN, 'BAD_NAME');
  if ('error' in nameB) return nameB;
  return { nameA: nameA.value, nameB: nameB.value };
}

/** pairId / pairs の列順を C# の Ordinal 昇順へ揃える。 */
function orientPairNames(
  sidA: string,
  nameA: string,
  sidB: string,
  nameB: string,
): { nameA: string; nameB: string } {
  return sidA < sidB ? { nameA, nameB } : { nameA: nameB, nameB: nameA };
}

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
    // セッション発行は低頻度操作なので専用枠を本人 deviceId で消費する。
    // 認証前に deviceId を受け取って課金すると他人の枠を焼けるため、必ず bearer 検証後に行う。
    if (env.RATELIMIT_SESSION) {
      try {
        const { success } = await env.RATELIMIT_SESSION.limit({ key: claims.deviceId });
        if (!success) return jsonError(429, 'SESSION_RATE_LIMIT', 'session rate limit exceeded');
      } catch (e) {
        console.error('pair/session rate limit failed', String(e));
        return jsonError(503, 'RATE_LIMIT_UNAVAILABLE', 'rate limit unavailable');
      }
    }
    const parsed = await readJsonObject(req);
    if ('error' in parsed) return parsed.error;
    const body = parsed.value;
    const nonce = str(body.pairingNonce);
    if (!HEX32.test(nonce)) return jsonError(400, 'BAD_NONCE', 'pairingNonce must be 32 hex');
    const displayName = boundedText(body, 'displayName', MAX_DISPLAY_NAME_LEN, 'BAD_DISPLAY_NAME');
    if ('error' in displayName) return displayName.error;
    const publicKey = boundedText(body, 'publicKey', MAX_PUBLIC_KEY_LEN, 'BAD_PUBLIC_KEY');
    if ('error' in publicKey) return publicKey.error;
    const now = Date.now();
    // sessions と pairing_nonces は 1 つの論理操作なので batch (単一トランザクション) で書く。
    // 別々の run() だと 1 文目成功・2 文目失敗で「GET /pair/session は成功して QR も出るのに
    // /pair/create が INVALID_NONCE_MATCH を返す」中間状態が残り、再登録するまで直らない。
    if (!env.DB) return d1Unavailable();
    try {
      await env.DB.batch([
        env.DB.prepare(
          'INSERT OR REPLACE INTO sessions (sid, display_name, public_key, created_at) VALUES (?,?,?,?)',
        ).bind(claims.deviceId, displayName.value, publicKey.value, now),
        env.DB.prepare(
          'INSERT OR REPLACE INTO pairing_nonces (sid, nonce, created_at) VALUES (?,?,?)',
        ).bind(claims.deviceId, nonce, now),
      ]);
    } catch (e) {
      console.error('pair/session D1 failed', String(e));
      return d1Unavailable();
    }
    return jsonOk({ ok: true });
  }

  const sid = segs[2] ?? '';
  if (!HEX32.test(sid)) return jsonError(400, 'BAD_SID', 'sid must be 32 hex');

  if (method === 'GET') {
    if (!env.DB) return d1Unavailable();
    let row: { display_name: string; public_key: string } | null;
    try {
      row = await env.DB.prepare('SELECT display_name, public_key FROM sessions WHERE sid=?')
        .bind(sid)
        .first<{ display_name: string; public_key: string }>();
    } catch (e) {
      console.error('pair/session GET D1 failed', String(e));
      return d1Unavailable();
    }
    if (!row) return jsonError(404, 'NOT_FOUND', 'session not present');
    return jsonOk({ displayName: row.display_name, publicKey: row.public_key });
  }
  if (method === 'DELETE') {
    if (claims.deviceId !== sid) return jsonError(403, 'NOT_SELF', 'can only revoke own session');
    // POST と同じ理由で batch (片方だけ消えた中間状態を残さない)。
    if (!env.DB) return d1Unavailable();
    try {
      await env.DB.batch([
        env.DB.prepare('DELETE FROM sessions WHERE sid=?').bind(sid),
        env.DB.prepare('DELETE FROM pairing_nonces WHERE sid=?').bind(sid),
      ]);
    } catch (e) {
      console.error('pair/session DELETE D1 failed', String(e));
      return d1Unavailable();
    }
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

  const parsed = await readJsonObject(req);
  if ('error' in parsed) return parsed.error;
  const body = parsed.value;
  const sidA = str(body.sidA);
  const sidB = str(body.sidB);
  const nonceA = str(body.nonceA);
  const nonceB = str(body.nonceB);
  if (!HEX32.test(sidA) || !HEX32.test(sidB)) return jsonError(400, 'BAD_SID', 'sidA/sidB must be 32 hex');
  if (sidA === sidB) return jsonError(400, 'SAME_SID', 'sidA and sidB must differ');
  if (!HEX32.test(nonceA) || !HEX32.test(nonceB)) return jsonError(400, 'BAD_NONCE', 'nonceA/nonceB must be 32 hex');
  const names = readPairNames(body);
  if ('error' in names) return names.error;
  if (!env.DB) return d1Unavailable();

  // 両 sid の nonce を D1 で server 検証 (Ghost peer 注入防止: nonce 値一致 + 1h 以内)。
  let errA: Response | null;
  try {
    errA = await verifyNonce(env, sidA, nonceA, 'A');
  } catch (e) {
    console.error('pair/create nonce A D1 failed', String(e));
    return d1Unavailable();
  }
  if (errA) return errA;
  let errB: Response | null;
  try {
    errB = await verifyNonce(env, sidB, nonceB, 'B');
  } catch (e) {
    console.error('pair/create nonce B D1 failed', String(e));
    return d1Unavailable();
  }
  if (errB) return errB;

  // 通知に載せる鍵も台帳の権威データから transaction 前に読む。ここで D1 が壊れている
  // 場合は pairs/nonce の状態を進めず 503 にして、push だけ先行する事態を防ぐ。
  let keys: { pkA: string; pkB: string };
  try {
    keys = await loadSessionPublicKeys(env, sidA, sidB);
  } catch (e) {
    console.error('pair/create public key D1 failed', String(e));
    return d1Unavailable();
  }

  // 成立させる**前に** nonce を条件付き DELETE で消費し、正式 pairs upsert と同じ
  // D1 batch (単一 transaction) に含める。batch が成功してからだけ inbox へ push する。
  //
  // 旧実装は verifyNonce の SELECT で検証 → notifyPairEstablished → その後に `DELETE WHERE sid=?`
  // という順序だったため、検証と消費が原子的でなかった。同じ sid/nonce への並列 POST は両方とも
  // SELECT を通過でき、双方がペア成立を push できる（QR 撮影直後の二重送信や、nonce を握った
  // 攻撃者による同時リプレイ）。「サーバ権威で単回使用」というこの関数の契約が並列下で破れていた。
  //
  // 対策は両 nonce を 1 本の UPDATE でランダムな claim 値へ同時に置換し、claim を取得できた
  // batch だけが pairs を書ける形にする。DELETE 2 本を先に並べるだけでは、A-B と A-C の競合で
  // A を先に失った負け側も C だけを消費し、さらに後続 INSERT が実行される余地がある。
  // 1 statement の UPDATE は、両行が元 nonce のまま存在するときだけ 2 行とも claim するため、
  // 同一ペアだけでなく相手違いの並行要求でも勝者は 1 本に決まる。
  //
  // verifyNonce（SELECT）は残す。エラーコード SESSION_NOT_FOUND / INVALID_NONCE_MATCH /
  // EXPIRED_SESSION の返し分けはクライアントとテストが依存する外部契約で、条件付き DELETE 一発に
  // 畳むとすべて 409 に潰れてしまうため。検証＝診断、DELETE＝認可、と役割を分ける。
  const pairingId = derivePairId(sidA, sidB);
  const orientedNames = orientPairNames(sidA, names.nameA, sidB, names.nameB);
  const createdAt = Date.now();
  const nonceClaim = crypto.randomUUID().replaceAll('-', '');
  const nonceCutoff = createdAt - NONCE_TTL_MS;
  let consumed: unknown[];
  try {
    consumed = await env.DB.batch([
      env.DB.prepare(
        `UPDATE pairing_nonces SET nonce=?
         WHERE sid IN (?,?)
           AND EXISTS (SELECT 1 FROM pairing_nonces WHERE sid=? AND nonce=? AND created_at>=?)
           AND EXISTS (SELECT 1 FROM pairing_nonces WHERE sid=? AND nonce=? AND created_at>=?)`,
      ).bind(nonceClaim, sidA, sidB, sidA, nonceA, nonceCutoff, sidB, nonceB, nonceCutoff),
      // claim した 2 行が同じ batch 内にある場合だけ正式台帳を upsert する。競合に負けた
      // batch は claim 行が 0 件なので、この INSERT も changes=0 のまま副作用を残さない。
      env.DB.prepare(
        `INSERT OR REPLACE INTO pairs (pair_id, name_a, name_b, created_at)
         SELECT ?,?,?,?
         WHERE (SELECT COUNT(*) FROM pairing_nonces WHERE sid IN (?,?) AND nonce=?)=2`,
      ).bind(pairingId, orientedNames.nameA, orientedNames.nameB, createdAt, sidA, sidB, nonceClaim),
      env.DB.prepare(
        'DELETE FROM pairing_nonces WHERE sid IN (?,?) AND nonce=?',
      ).bind(sidA, sidB, nonceClaim),
    ]);
  } catch (e) {
    console.error('pair/create D1 transaction failed', String(e));
    return d1Unavailable();
  }
  if (changesOf(consumed[0]) !== 2) {
    return jsonError(409, 'NONCE_ALREADY_CONSUMED', 'pairing nonce was already consumed');
  }
  // claim 取得後の INSERT/DELETE は同一 transaction 内では必ず 1/2 行になる。不変条件が
  // 崩れた場合は push せず fail closed にし、台帳無しの成立をクライアントへ返さない。
  if (changesOf(consumed[1]) !== 1 || changesOf(consumed[2]) !== 2) return d1Unavailable();

  // 公開鍵は body.pkA / body.pkB ではなく D1 の権威データを使う (#C-32)。正式台帳と
  // nonce 消費の transaction が成功した後にだけ両 DeviceDO へ push する。
  await notifyPairEstablished(env, sidA, names.nameA, sidB, names.nameB, keys);

  // （消費は上の条件付き DELETE で完了済み）単回使用がサーバ側で保証されることの背景:
  // 旧実装は verifyNonce が SELECT のみで、消費はクライアント側の revoke
  // (OnPairingDetected → DELETE /pair/session) 任せの best-effort だった。相手 PC がオフラインで
  // inbox push を受け取れないケースや revoke 要求が失敗したケースでは nonce が TTL(1h) いっぱい
  // 有効なまま残り、撮影・流出した QR の nonce で何度でも /pair/create を通せる
  // (攻撃者が自分の sid を相手に据えれば、利用者が「使い終わった」と思っている QR で
  //  そのままペアを張れる)。ここで消すと単回使用がサーバ側で保証される。
  // 通常運用に影響は無い: ペア成立した PC は元々自分のセッションを revoke するため、
  // 表示中の QR は既に無効。次のペアリングはペアリング画面を開き直して新しい nonce を発行する。

  return jsonOk({ ok: true, pairingId });
}

/** D1 の実行結果から影響行数を取り出す（条件付き DELETE の compare-and-swap 判定用）。
 *  meta が無い / changes が数値でない実装差は「消せなかった」= 0 に倒して安全側に寄せる。 */
function changesOf(result: unknown): number {
  const meta = (result as { meta?: { changes?: unknown } } | undefined)?.meta;
  return typeof meta?.changes === 'number' ? meta.changes : 0;
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

/**
 * rere レビュー #C-32: ペア成立イベントに載せる公開鍵を D1 `sessions.public_key`
 * （= /pair/session で bearer 本人が登録した権威データ）から引き直す。
 *
 * 旧実装はリクエストボディの pkA / pkB をそのまま両者の inbox へ流していた。公開鍵は
 * PairSecret（ECDH ルート鍵）の導出元なので、呼び出し元が申告した値をそのまま採用すると
 * 鍵配送の完全性が「呼び出し元の善意」に依存する。改造 Bridge や nonce を握った中間者が
 * pkB を差し替えると両 PC が別々の PairSecret を導出し、SecureChannel.ApplyConfirm が
 * HMAC 不一致で Failed → そのペアは恒久的に転送不能（平文フォールバックもしない）になる。
 * サーバが権威データを持っているのに照合していなかったのが root cause なので、
 * 申告値は無視して D1 から読む。
 *
 * public_key が空のセッション（公開鍵交換前の旧クライアント）や sessions 行が無いケースは
 * 空文字のまま流し、従来どおりクライアント側の平文フォールバックに委ねる。
 * ここで 404 にすると「pairing_nonces はあるが sessions が無い」不整合状態でペアリングが
 * 止まってしまうので、可用性を優先する。申告値を絶対に使わない時点で防御目的は達成される。
 */
async function loadSessionPublicKeys(env: Env, sidA: string, sidB: string): Promise<{ pkA: string; pkB: string }> {
  const [rowA, rowB] = await Promise.all([
    env.DB.prepare('SELECT public_key FROM sessions WHERE sid=?').bind(sidA).first<{ public_key: string }>(),
    env.DB.prepare('SELECT public_key FROM sessions WHERE sid=?').bind(sidB).first<{ public_key: string }>(),
  ]);
  return { pkA: rowA?.public_key ?? '', pkB: rowB?.public_key ?? '' };
}

/** pairingId を導出し、両 deviceId の DeviceDO inbox にペア成立イベントを push する
 *  (handlePairCreate / handlePairLink で共通)。2 つの DO は互いに独立しているため並列実行する。
 *  公開鍵は呼び出し元の申告ではなく D1 の権威データを使う (#C-32)。 */
async function notifyPairEstablished(
  env: Env,
  sidA: string,
  nameA: string,
  sidB: string,
  nameB: string,
  suppliedKeys?: { pkA: string; pkB: string },
): Promise<string> {
  const keys = suppliedKeys ?? (await loadSessionPublicKeys(env, sidA, sidB));
  const pairingId = derivePairId(sidA, sidB);
  const event = {
    pairingId,
    sidA,
    nameA: nameA || 'PC-A',
    sidB,
    nameB: nameB || 'PC-B',
    pkA: keys.pkA,
    pkB: keys.pkB,
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

  const parsed = await readJsonObject(req);
  if ('error' in parsed) return parsed.error;
  const body = parsed.value;
  const sidA = claims.deviceId;
  const sidB = str(body.sidB);
  if (!HEX32.test(sidB)) return jsonError(400, 'BAD_SID', 'sidB must be 32 hex');
  if (sidA === sidB) return jsonError(400, 'SAME_SID', 'sidB must differ from caller deviceId');
  const names = readPairNames(body);
  if ('error' in names) return names.error;
  if (!env.DB) return d1Unavailable();

  let sessionErr: Response | null;
  try {
    sessionErr = await verifySessionActive(env, sidB, 'sidB');
  } catch (e) {
    console.error('pair/link session D1 failed', String(e));
    return d1Unavailable();
  }
  if (sessionErr) return sessionErr;

  let keys: { pkA: string; pkB: string };
  try {
    keys = await loadSessionPublicKeys(env, sidA, sidB);
  } catch (e) {
    console.error('pair/link public key D1 failed', String(e));
    return d1Unavailable();
  }

  // 公開鍵は body.pkA / body.pkB ではなく D1 の権威データを使う (#C-32)。
  // /pair/link は sidA=claims.deviceId が保証される一方で pkA/pkB は呼び出し元申告だったため、
  // 相手側 (sidB) に渡る自分の公開鍵を任意値にできる状態だった。
  const pairingId = derivePairId(sidA, sidB);
  const orientedNames = orientPairNames(sidA, names.nameA, sidB, names.nameB);
  try {
    await env.DB.prepare(
      'INSERT OR REPLACE INTO pairs (pair_id, name_a, name_b, created_at) VALUES (?,?,?,?)',
    )
      .bind(pairingId, orientedNames.nameA, orientedNames.nameB, Date.now())
      .run();
  } catch (e) {
    console.error('pair/link pairs upsert D1 failed', String(e));
    return d1Unavailable();
  }

  // 正式台帳への upsert 成功後にだけ、両 DeviceDO へ成立イベントを push する。
  await notifyPairEstablished(env, sidA, names.nameA, sidB, names.nameB, keys);
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
  if (a >= b) return jsonError(400, 'BAD_PAIR_ID', 'pairId must use canonical deviceId order');
  if (claims.deviceId !== a && claims.deviceId !== b) {
    return jsonError(403, 'NOT_PARTICIPANT', 'deviceId is not a participant of pairId');
  }
  const method = req.method;

  if (method === 'PUT') {
    const mode = pairLedgerMode(env);
    if (!mode) return ledgerMisconfigured();
    // 旧クライアントの backfill を transition 中だけ受ける経路も、本人 deviceId
    // で必ず低頻度レート制限を消費する。未認証/当事者外の入力には課金しない。
    if (env.RATELIMIT_DEVICE) {
      try {
        const { success } = await env.RATELIMIT_DEVICE.limit({ key: claims.deviceId });
        if (!success) return jsonError(429, 'DEVICE_RATE_LIMIT', 'deviceId rate limit exceeded');
      } catch (e) {
        console.error('pairs PUT rate limit failed', String(e));
        return jsonError(503, 'RATE_LIMIT_UNAVAILABLE', 'rate limit unavailable');
      }
    }
    const parsed = await readJsonObject(req);
    if ('error' in parsed) return parsed.error;
    const names = readPairNames(parsed.value);
    if ('error' in names) return names.error;
    if (!env.DB) return d1Unavailable();
    const createdAt = Date.now();

    // required では、新しいペアリング経路が先に作った正式行だけを更新する。
    // UPDATE の changes=0 は proof (正式ペア成立) が無いことを意味し、任意 pairId
    // を bearer だけで注入できないよう 409 で止める。
    try {
      const updated = await env.DB.prepare(
        'UPDATE pairs SET name_a=?, name_b=?, created_at=? WHERE pair_id=?',
      )
        .bind(names.nameA, names.nameB, createdAt, pairId)
        .run();
      if (changesOf(updated) !== 0) return jsonOk({ ok: true });

      if (mode === 'required') {
        return jsonError(409, 'PAIR_PROOF_REQUIRED', 'pair must be created by an authenticated pairing flow');
      }

      // transition は出荷済み legacy client の pairing 完了後 PUT を backfill として
      // 受ける。INSERT は本人 + canonical pairId + device rate limit の三条件を通過した
      // 後にだけ行い、createdAt はクライアント申告値を使わず server 時刻にする。
      await env.DB.prepare(
        'INSERT OR IGNORE INTO pairs (pair_id, name_a, name_b, created_at) VALUES (?,?,?,?)',
      )
        .bind(pairId, names.nameA, names.nameB, createdAt)
        .run();
      console.log('pairs PUT legacy backfill accepted', JSON.stringify({ mode: 'transition' }));
      return jsonOk({ ok: true });
    } catch (e) {
      console.error('pairs PUT D1 failed', String(e));
      return d1Unavailable();
    }
  }
  if (method === 'GET') {
    if (!env.DB) return d1Unavailable();
    let row: { pair_id: string; name_a: string; name_b: string; created_at: number } | null;
    try {
      row = await env.DB.prepare('SELECT pair_id, name_a, name_b, created_at FROM pairs WHERE pair_id=?')
        .bind(pairId)
        .first<{ pair_id: string; name_a: string; name_b: string; created_at: number }>();
    } catch (e) {
      console.error('pairs GET D1 failed', String(e));
      return d1Unavailable();
    }
    if (!row) return jsonError(404, 'NOT_FOUND', 'pair not present'); // PairSyncService の remote-unpair 検出
    return jsonOk({ pairId: row.pair_id, nameA: row.name_a, nameB: row.name_b, createdAt: row.created_at });
  }
  if (method === 'DELETE') {
    if (!env.DB) return d1Unavailable();
    try {
      await env.DB.prepare('DELETE FROM pairs WHERE pair_id=?').bind(pairId).run();
    } catch (e) {
      console.error('pairs DELETE D1 failed', String(e));
      return d1Unavailable();
    }
    // 相手へ unpair を push (D1 ポーリング線形増殖を避ける・realtime 批判 §0-5)。
    const peer = claims.deviceId === a ? b : a;
    await notifyInbox(env, peer, { type: 'unpair', pairingId: pairId, createdAt: Date.now() });
    return jsonOk({ ok: true });
  }
  return jsonError(405, 'METHOD_NOT_ALLOWED', method);
}
