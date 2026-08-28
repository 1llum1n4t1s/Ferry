/**
 * Ferry WebSocket リレー (Cloudflare Workers + Durable Objects)
 *
 * 入口は `/ferry-relay` だけを受け付け、RelayDO には Worker が検証・正規化した
 * `X-Ferry-*` ヘッダーだけを渡す。pairId や role を DO 側で query から再解釈しない
 * ことで、Worker の認証境界を一箇所に保つ。
 */

import { reserveRelayQuota, settleRelayQuota, validateRelayQuotaConfig } from './quota-do';
import { verifySessionToken } from './auth';
import { cleanupExpiredPairingData } from './maintenance';

export interface Env {
  RELAY: DurableObjectNamespace;
  /** signaling DO (PairDO)。pairId-keyed で offers/answers/endpoints/probes を保持する。 */
  PAIR: DurableObjectNamespace;
  /** device DO (DeviceDO)。deviceId-keyed で presence + pairing inbox(WS) を保持する。 */
  DEVICE: DurableObjectNamespace;
  /** ペア台帳 D1 (sessions/pairing_nonces/pairs)。 */
  DB: D1Database;
  /** RelayQuotaDO binding。リレー quota の reserve/settle に使う。 */
  QUOTA: DurableObjectNamespace;
  /** pairId ハッシュ化用ソルト。 */
  SALT: string;

  // === device 鍵バインディング (auth.ts) ===
  DEVICE_KEY_BINDING: KVNamespace;
  SESSION_HMAC_SECRET: string;

  // === rate limit bindings ===
  RATELIMIT_IP?: RateLimit;
  RATELIMIT_DEVICE?: RateLimit;
  RATELIMIT_SESSION?: RateLimit;
  RATELIMIT_SIG?: RateLimit;
  RATELIMIT_RELAY?: RateLimit;

  /** リレー入室認証の移行モード。未設定は optional (旧版互換)。 */
  RELAY_AUTH_MODE?: string;
  /** ペア台帳が未登録の認証接続を legacy へ縮退させる移行モード。 */
  PAIR_LEDGER_MODE?: string;

  // === RelayQuotaDO の設定 (すべて secret ではない文字列 vars) ===
  // quota-do.ts の設定名を Env に明示して、wrangler の vars と実装のずれを防ぐ。
  RELAY_CIRCUIT_OPEN?: string;
  RELAY_MAX_CONCURRENT_ROOMS?: string;
  RELAY_MONTHLY_BYTES?: string;
  RELAY_MONTHLY_MESSAGES?: string;
  RELAY_MONTHLY_DURATION_SECONDS?: string;
  RELAY_AUTH_SESSION_BYTES?: string;
  RELAY_AUTH_SESSION_MESSAGES?: string;
  RELAY_AUTH_SESSION_SECONDS?: string;
  RELAY_AUTH_IDLE_SECONDS?: string;
  RELAY_AUTH_SESSION_IDLE_SECONDS?: string;
  RELAY_LEGACY_MONTHLY_BYTES?: string;
  RELAY_LEGACY_MONTHLY_MESSAGES?: string;
  RELAY_LEGACY_MONTHLY_DURATION_SECONDS?: string;
  RELAY_LEGACY_SESSION_BYTES?: string;
  RELAY_LEGACY_SESSION_MESSAGES?: string;
  RELAY_LEGACY_SESSION_SECONDS?: string;
  RELAY_LEGACY_IDLE_SECONDS?: string;
  RELAY_LEGACY_SESSION_IDLE_SECONDS?: string;
  RELAY_MAX_FRAME_BYTES?: string;
}

/** C# 側 `Util.PairId.Generate` と一致する pairId の形式。 */
export const RELAY_PAIR_ID_RE = /^[a-f0-9]{32}_[a-f0-9]{32}$/;
const RELAY_PATH = '/ferry-relay';
const INTERNAL_ROLE = 'X-Ferry-Role';
const INTERNAL_DEVICE = 'X-Ferry-Device';
const INTERNAL_TIER = 'X-Ferry-Tier';
const INTERNAL_ROOM = 'X-Ferry-Room';
const AUTH_MODES = new Set(['optional', 'required']);
const RELAY_ROLES = new Set(['offer', 'answer']);

export type RelayRole = 'offer' | 'answer';
export type RelayTier = 'authenticated' | 'legacy';

/** quota-do.ts が返す lease の境界型。追加フィールドは無視して forward compatibility を保つ。 */
export interface RelayQuotaLease {
  leaseId: string;
  roomId: string;
  tier: string;
  expiresAt: number;
  maxBytes: number;
  maxMessages: number;
  maxIdleMs: number;
  maxFrameBytes: number;
}

interface RelayLimits {
  expiresAt: number;
  maxBytes: number;
  maxMessages: number;
  maxIdleMs: number;
  maxFrameBytes: number;
}

/** WebSocket Hibernation の attachment。小さな plain object にして 16KB 制限内に収める。 */
export interface RelayAttachment {
  /** reserve の lease 全体。leaseId と有効期限の復元に使う。 */
  lease: RelayQuotaLease;
  leaseId: string;
  roomId: string;
  role: RelayRole;
  /** 認証接続は deviceId、legacy 接続は空文字。 */
  device: string;
  /** 呼び出し側が deviceId 名を期待する場合の互換 alias。 */
  deviceId: string;
  tier: RelayTier;
  bytes: number;
  messages: number;
  lastActivity: number;
  /** quota lease を確保した時刻。settle の durationMs の起点。 */
  start: number;
  limits: RelayLimits;
}

interface RelayAdmission {
  roomId: string;
  role: RelayRole;
  deviceId: string;
  tier: RelayTier;
}

// RateLimit / RateLimitOptions は @cloudflare/workers-types が提供する。

// CF 単独完結移行: DO クラスを entry module から re-export (Workers の DO クラス公開要件)。
export { PairDO } from './pairdo';
export { DeviceDO } from './devicedo';
export { RelayQuotaDO } from './quota-do';

const worker = {
  async fetch(req: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const url = new URL(req.url);

    // ヘルスチェック。既存の依存確認はリレー変更と無関係なので維持する。
    if (url.pathname === '/health') {
      const failed: string[] = [];
      if (!env.SESSION_HMAC_SECRET) failed.push('SESSION_HMAC_SECRET');
      if (!env.SALT) failed.push('SALT');
      if (!resolveAuthMode(env.RELAY_AUTH_MODE)) failed.push('RELAY_AUTH_MODE');
      if (!resolveLedgerMode(env.PAIR_LEDGER_MODE)) failed.push('PAIR_LEDGER_MODE');
      // 公開 health を D1/KV の従量 subrequest 増幅器にしない。binding と設定の
      // readiness だけを同期確認し、実アクセス障害は各 route が fail closed で返す。
      if (!env.DB || typeof env.DB.prepare !== 'function') failed.push('D1');
      if (!env.DEVICE_KEY_BINDING || typeof env.DEVICE_KEY_BINDING.get !== 'function') failed.push('KV');
      if (!env.QUOTA || typeof env.QUOTA.idFromName !== 'function' || typeof env.QUOTA.get !== 'function') {
        failed.push('QUOTA');
      }
      const quotaConfigErrors = validateRelayQuotaConfig(env);
      if (quotaConfigErrors.length > 0) failed.push('QUOTA_CONFIG');
      if (failed.length > 0) {
        console.error('health: degraded', JSON.stringify({ failed }));
        return jsonResponse(503, { ok: false, failed });
      }
      return new Response('OK', { status: 200 });
    }

    if (url.pathname === '/auth/token') {
      if (req.method === 'POST') {
        const { handleAuthToken } = await import('./auth');
        return handleAuthToken(req, env);
      }
      return new Response('Method Not Allowed', { status: 405, headers: { allow: 'POST' } });
    }

    if (url.pathname.startsWith('/sig/')) {
      const { handleSignaling } = await import('./signaling-routes');
      return handleSignaling(req, env, url, ctx);
    }

    if (url.pathname === '/inbox') {
      const { handleInbox } = await import('./device-routes');
      return handleInbox(req, env);
    }
    if (url.pathname.startsWith('/presence/')) {
      const { handlePresence } = await import('./device-routes');
      return handlePresence(req, env, url);
    }

    if (url.pathname === '/pair/create') {
      if (req.method === 'POST') {
        const { handlePairCreate } = await import('./pairing-routes');
        return handlePairCreate(req, env);
      }
      return new Response('Method Not Allowed', { status: 405, headers: { allow: 'POST' } });
    }
    if (url.pathname.startsWith('/pair/session')) {
      const { handlePairSession } = await import('./pairing-routes');
      return handlePairSession(req, env, url);
    }
    if (url.pathname === '/pair/link') {
      if (req.method === 'POST') {
        const { handlePairLink } = await import('./pairing-routes');
        return handlePairLink(req, env);
      }
      return new Response('Method Not Allowed', { status: 405, headers: { allow: 'POST' } });
    }
    if (url.pathname.startsWith('/pairs/')) {
      const { handlePairs } = await import('./pairing-routes');
      return handlePairs(req, env, url);
    }

    // relay は path を厳密に限定する。未知 path を WebSocket 入口へ流さない。
    if (url.pathname !== RELAY_PATH) return new Response('Not Found', { status: 404 });
    return handleRelayAdmission(req, env);
  },
  async scheduled(_controller: ScheduledController, env: Env): Promise<void> {
    const result = await cleanupExpiredPairingData(env);
    console.log(JSON.stringify({ message: 'pairing temp cleanup', ...result }));
  },
};

export default worker;

/** `/ferry-relay` の Worker 側入室認可・quota context 注入。 */
export async function handleRelayAdmission(req: Request, env: Env): Promise<Response> {
  const url = new URL(req.url);
  // breaker / quota 設定不備は認証・D1・hash・RelayDO より先に遮断する。
  // 緊急停止中に control-plane の従量処理まで進めない。
  if (validateRelayQuotaConfig(env).length > 0 ||
    !env.QUOTA || typeof env.QUOTA.idFromName !== 'function' || typeof env.QUOTA.get !== 'function') {
    return new Response('Relay quota is unavailable', { status: 503 });
  }
  if (env.RELAY_CIRCUIT_OPEN === '1') {
    return new Response('Relay circuit open', { status: 503 });
  }
  const authMode = resolveAuthMode(env.RELAY_AUTH_MODE);
  const ledgerMode = resolveLedgerMode(env.PAIR_LEDGER_MODE);
  if (!authMode || !ledgerMode) return new Response('Relay auth is misconfigured', { status: 500 });

  if (req.headers.get('Upgrade')?.toLowerCase() !== 'websocket') {
    return new Response('Expected websocket', { status: 426 });
  }

  const pairId = url.searchParams.get('pairId');
  if (!pairId) return new Response('Missing pairId', { status: 400 });
  if (!isCanonicalPairId(pairId)) return new Response('Bad pairId', { status: 400 });

  const roleValue = url.searchParams.get('role');
  if (!roleValue || !RELAY_ROLES.has(roleValue)) return new Response('Bad role', { status: 400 });
  const role = roleValue as RelayRole;

  // 入室は 1 転送あたり最大 2 回。認証前でも IP で flood を抑える。
  if (env.RATELIMIT_RELAY) {
    try {
      const ip = req.headers.get('CF-Connecting-IP') ?? 'unknown';
      const { success } = await env.RATELIMIT_RELAY.limit({ key: ip });
      if (!success) return new Response('Rate limited', { status: 429 });
    } catch (e) {
      console.error('relay: rate limit failed', String(e));
      return new Response('Relay admission unavailable', { status: 503 });
    }
  }

  const authorization = req.headers.get('Authorization');
  const hasAuthorization = authorization !== null && authorization.trim().length > 0;
  let deviceId = '';

  if (hasAuthorization) {
    const token = extractBearerToken(authorization!);
    if (!token) return new Response('Unauthorized', { status: 401 });
    const claims = await verifySessionToken(token, env);
    if (!claims) return new Response('Unauthorized', { status: 401 });
    if (!isPairParticipant(pairId, claims.deviceId)) return new Response('Forbidden', { status: 403 });
    deviceId = claims.deviceId;
  } else if (authMode === 'required') {
    return new Response('Unauthorized', { status: 401 });
  }

  // Bearer 付き接続は D1 台帳を照会する。行が無い場合は optional でだけ
  // legacy tier に縮退し、required では明示的に 404 で拒否する。
  let tier: RelayTier = hasAuthorization ? 'authenticated' : 'legacy';
  if (hasAuthorization) {
    let registered: boolean;
    try {
      registered = await pairExists(env, pairId);
    } catch (e) {
      console.error('relay: pair lookup failed', String(e));
      return new Response('Relay admission unavailable', { status: 503 });
    }
    if (!registered) {
      if (ledgerMode === 'required') return new Response('Pair not found', { status: 404 });
      tier = 'legacy';
    }
  }

  const idStr = await hashPairId(pairId, env.SALT);
  const doId = env.RELAY.idFromName(idStr);
  const stub = env.RELAY.get(doId);

  // Query はユーザー入力なので DO に role/device/tier/room の根拠として渡さない。
  // Headers.set は既存の spoofed X-Ferry-* を必ず上書きする。
  const headers = new Headers(req.headers);
  headers.set(INTERNAL_ROLE, role);
  headers.set(INTERNAL_DEVICE, deviceId);
  headers.set(INTERNAL_TIER, tier);
  headers.set(INTERNAL_ROOM, pairId);
  const forwarded = new Request(req, { headers });
  return stub.fetch(forwarded);
}

function resolveAuthMode(value: string | undefined): 'optional' | 'required' | null {
  const mode = value ?? 'optional';
  return AUTH_MODES.has(mode) ? (mode as 'optional' | 'required') : null;
}

function resolveLedgerMode(value: string | undefined): 'transition' | 'required' | null {
  const mode = value ?? 'transition';
  return mode === 'transition' || mode === 'required' ? mode : null;
}

function extractBearerToken(value: string): string | null {
  const match = /^Bearer\s+(\S+)$/i.exec(value.trim());
  return match?.[1] ?? null;
}

function isCanonicalPairId(pairId: string): boolean {
  if (!RELAY_PAIR_ID_RE.test(pairId)) return false;
  const [a, b] = pairId.split('_');
  return a < b;
}

function isPairParticipant(pairId: string, deviceId: string): boolean {
  const [a, b] = pairId.split('_');
  return deviceId === a || deviceId === b;
}

async function pairExists(env: Env, pairId: string): Promise<boolean> {
  if (!env.DB) throw new Error('DB binding is not configured');
  const row = await env.DB
    .prepare('SELECT pair_id FROM pairs WHERE pair_id = ? LIMIT 1')
    .bind(pairId)
    .first();
  return row !== null && row !== undefined;
}

export async function hashPairId(pairId: string, salt: string): Promise<string> {
  const data = new TextEncoder().encode(pairId + '|' + salt);
  const buf = await crypto.subtle.digest('SHA-256', data);
  return Array.from(new Uint8Array(buf))
    .map((b) => b.toString(16).padStart(2, '0'))
    .join('');
}

/**
 * リレー本体。1 pairId ごとに 1 インスタンスを割り当てる。
 * Hibernation 復帰後も WebSocket attachment から room/lease/累積値を再構成する。
 */
export class RelayDO {
  private readonly state: DurableObjectState;
  private readonly env: Env;
  /** constructor/hibernation 復帰時に一度だけ検証し、chunk ごとの再解析を避ける。 */
  private readonly quotaConfigValid: boolean;
  /** close/settle の await 中に同じ DO へ再入室させない。 */
  private closing: Promise<void> | null = null;
  private readonly settling = new Map<string, Promise<boolean>>();
  /** quota reserve の await をまたぐ入室判定を、この DO 内で一列に並べる。 */
  private admissionTail: Promise<void> = Promise.resolve();
  /** close/settle が reserve の await 中に完了したことを検出する世代番号。 */
  private roomGeneration = 0;

  constructor(state: DurableObjectState, env: Env) {
    this.state = state;
    this.env = env;
    this.quotaConfigValid = validateRelayQuotaConfig(env).length === 0;
  }

  async fetch(req: Request): Promise<Response> {
    const previous = this.admissionTail;
    let release: () => void = () => undefined;
    this.admissionTail = new Promise<void>((resolve) => {
      release = resolve;
    });
    await previous;
    try {
      return await this.fetchSerialized(req);
    } finally {
      release();
    }
  }

  private async fetchSerialized(req: Request): Promise<Response> {
    if (this.closing) return new Response('Relay room is closing', { status: 409 });
    const generation = this.roomGeneration;
    const admission = readInternalAdmission(req);
    if (!admission) return new Response('Bad relay context', { status: 400 });

    const live = this.liveSockets();
    if (live.length >= 2) {
      console.log(JSON.stringify({ message: 'relay pair full', room: this.roomLabel() }));
      return new Response('Pair already full', { status: 409 });
    }

    const existing = live
      .map((ws) => this.readAttachment(ws))
      .filter((value): value is RelayAttachment => value !== null);
    if (existing.some((value) => value.roomId !== admission.roomId)) {
      return new Response('Relay room mismatch', { status: 409 });
    }
    if (existing.some((value) => value.role === admission.role)) {
      return new Response('Relay role already occupied', { status: 409 });
    }
    if (admission.tier === 'authenticated' && admission.deviceId && existing.some(
      (value) => value.tier === 'authenticated' && value.deviceId === admission.deviceId,
    )) {
      return new Response('Relay device already connected', { status: 409 });
    }

    let reservation: Awaited<ReturnType<typeof reserveRelayQuota>>;
    try {
      // acceptWebSocket より前に reserve する。quota 拒否時はソケットを一切 accept しない。
      reservation = await reserveRelayQuota(this.env, {
        roomId: admission.roomId,
        tier: admission.tier,
        role: admission.role,
        // quota の入力は空 deviceId を受け付けない。legacy は認証主体を持たないため、
        // room/role に束縛した内部値を使い、ユーザー query 由来の値は混ぜない。
        deviceId: admission.deviceId || `legacy:${admission.roomId}:${admission.role}`,
      });
    } catch (e) {
      console.error(JSON.stringify({ message: 'relay quota reserve failed', room: this.roomLabel(), error: String(e) }));
      return new Response('Relay quota unavailable', { status: 503 });
    }
    if (!reservation.ok) return reservation.response;

    const lease = reservation.lease as RelayQuotaLease;
    if (!isValidLease(lease, admission.roomId)) {
      console.error(JSON.stringify({ message: 'relay quota returned invalid lease', room: this.roomLabel() }));
      return new Response('Relay quota unavailable', { status: 503 });
    }

    // reserve の await 中に既存 room の close/settle が始まった、または完了した
    // lease は accept しない。closing が継続中なら実測 settle を先に完了させ、
    // 同一 lease を 0 使用量で先に settle して実測値を失う競合も避ける。
    if (this.closing || this.roomGeneration !== generation) {
      const closing = this.closing;
      if (closing) await closing;
      await this.settleUnusedReservation(lease, admission);
      return new Response('Relay room changed during admission', { status: 409 });
    }

    const pair = new WebSocketPair();
    const client = pair[0];
    const server = pair[1];
    const now = Date.now();
    const attachment: RelayAttachment = {
      lease,
      leaseId: lease.leaseId,
      roomId: admission.roomId,
      role: admission.role,
      device: admission.deviceId,
      deviceId: admission.deviceId,
      tier: admission.tier,
      bytes: 0,
      messages: 0,
      lastActivity: now,
      start: now,
      limits: {
        expiresAt: lease.expiresAt,
        maxBytes: lease.maxBytes,
        maxMessages: lease.maxMessages,
        maxIdleMs: lease.maxIdleMs,
        maxFrameBytes: lease.maxFrameBytes,
      },
    };

    try {
      // Hibernation 対応 API を使い、attachment は accept 直後に保存する。
      this.state.acceptWebSocket(server, [admission.role]);
      server.serializeAttachment(attachment);
    } catch (e) {
      console.error(JSON.stringify({ message: 'relay websocket accept failed', room: this.roomLabel(), error: String(e) }));
      try {
        server.close(1011, 'Relay unavailable');
      } catch {
        // close 自体の失敗は元の accept エラーに含めない。
      }
      await this.settleOnce(attachment);
      return new Response('Relay unavailable', { status: 503 });
    }

    try {
      await this.scheduleNextAlarm();
    } catch (e) {
      console.error(JSON.stringify({ message: 'relay alarm schedule failed', room: this.roomLabel(), error: String(e) }));
      await this.finishRoom(1011, 'Relay unavailable');
      return new Response('Relay unavailable', { status: 503 });
    }
    // alarm I/O の await 中にも close event は進行できる。閉鎖済み socket に
    // 101 を返さないよう、同じ世代をもう一度確認する。
    if (this.closing || this.roomGeneration !== generation) {
      const closing = this.closing;
      if (closing) await closing;
      return new Response('Relay room changed during admission', { status: 409 });
    }

    const liveAfterAccept = this.liveSockets();
    if (liveAfterAccept.length === 2) {
      console.log(JSON.stringify({ message: 'relay ready', room: this.roomLabel() }));
      for (const ws of liveAfterAccept) {
        try {
          ws.send('ready');
        } catch (e) {
          console.error(JSON.stringify({ message: 'relay ready send failed', room: this.roomLabel(), error: String(e) }));
          await this.finishRoom(1011, 'Relay unavailable');
          break;
        }
      }
    }

    return websocketSwitchingResponse(client);
  }

  /** バイナリフレームだけを、room/lease の上限を検査して相手へ転送する。 */
  async webSocketMessage(ws: WebSocket, msg: ArrayBuffer | string): Promise<void> {
    // breaker は新規 reserve だけでなく、設定反映後に残った既存 room も
    // 次のデータイベントで止める。そうしないと circuit open 中も転送が継続する。
    if (!this.quotaConfigValid || this.env.RELAY_CIRCUIT_OPEN === '1') {
      await this.finishRoom(1011, 'Relay circuit open');
      return;
    }
    const records = this.liveSockets()
      .map((socket) => ({ socket, attachment: this.readAttachment(socket) }))
      .filter((value): value is { socket: WebSocket; attachment: RelayAttachment } => value.attachment !== null);
    const sender = records.find((value) => value.socket === ws);

    if (!sender) {
      await this.finishRoom(1002, 'Missing relay attachment');
      return;
    }
    if (records.length !== 2) {
      await this.finishRoom(1002, 'Relay is not ready');
      return;
    }
    if (new Set(records.map((value) => value.attachment.leaseId)).size !== 1) {
      await this.finishRoom(1002, 'Relay lease mismatch');
      return;
    }
    if (typeof msg === 'string') {
      await this.finishRoom(1003, 'Binary frames only');
      return;
    }

    const now = Date.now();
    if (this.isExpired(sender.attachment, now) || records.some((value) => this.isExpired(value.attachment, now))) {
      await this.finishRoom(1001, 'Relay quota expired');
      return;
    }

    const frameBytes = frameByteLength(msg);
    const maxFrameBytes = Math.min(...records.map((value) => value.attachment.limits.maxFrameBytes));
    if (frameBytes > maxFrameBytes) {
      await this.finishRoom(1009, 'Relay frame limit exceeded');
      return;
    }

    // counters は両 attachment に同じ room 合算値を複製する。切断済み peer が
    // getWebSockets() から先に消えても、残った一方だけで正確に settle できる。
    const roomBytes = Math.max(...records.map((value) => value.attachment.bytes));
    const roomMessages = Math.max(...records.map((value) => value.attachment.messages));
    const maxBytes = Math.min(...records.map((value) => value.attachment.limits.maxBytes));
    const maxMessages = Math.min(...records.map((value) => value.attachment.limits.maxMessages));
    if (roomBytes + frameBytes > maxBytes || roomMessages + 1 > maxMessages) {
      await this.finishRoom(1009, 'Relay quota exceeded');
      return;
    }

    // 1 frame = 1 message として送信前にカウントを確定する。失敗時も settle は
    // 実際に受け付けた量を失わず、再入室で同一 lease を再利用しない。
    const updated = records.map((peer) => ({
      socket: peer.socket,
      attachment: {
        ...peer.attachment,
        lease: { ...peer.attachment.lease },
        limits: { ...peer.attachment.limits },
      },
    }));
    if (!updated.some((peer) => peer.socket === ws)) {
      await this.finishRoom(1002, 'Relay sender disappeared');
      return;
    }
    const nextBytes = roomBytes + frameBytes;
    const nextMessages = roomMessages + 1;
    const roomStart = Math.min(...records.map((peer) => peer.attachment.start));
    for (const peer of updated) {
      peer.attachment.bytes = nextBytes;
      peer.attachment.messages = nextMessages;
      peer.attachment.lastActivity = now;
      peer.attachment.start = roomStart;
    }
    // 全 connection の attachment を先に保存し、一つでも失敗したら send しない。
    // 保存に失敗してから送ると、ハイバネーション復帰時に counters が巻き戻り、
    // room 合算上限を迂回できるため。
    for (const peer of updated) {
      if (!this.persistAttachment(peer.socket, peer.attachment)) {
        await this.finishRoom(1011, 'Relay state persistence failed');
        return;
      }
    }

    for (const peer of records) {
      if (peer.socket === ws || peer.socket.readyState !== WebSocket.OPEN) continue;
      try {
        peer.socket.send(msg);
      } catch (e) {
        console.error(JSON.stringify({ message: 'relay frame send failed', room: this.roomLabel(), error: String(e) }));
        await this.finishRoom(1011, 'Relay send failed');
        return;
      }
    }
  }

  /** normal close では相方も閉じ、各 lease を一度だけ settle する。 */
  async webSocketClose(ws: WebSocket, code: number, _reason: string, _wasClean: boolean): Promise<void> {
    console.log(JSON.stringify({ message: 'relay close', room: this.roomLabel(), code }));
    if (this.isEventFromPreviousLease(ws)) {
      const attachment = this.readAttachment(ws);
      if (attachment) await this.settleOnce(attachment);
      return;
    }
    await this.finishRoom(1001, 'Peer disconnected', ws);
  }

  /** エラーも片側だけを残さず、settle を実行してから room を閉じる。 */
  async webSocketError(ws: WebSocket, _error: unknown): Promise<void> {
    console.error(JSON.stringify({ message: 'relay error', room: this.roomLabel() }));
    if (this.isEventFromPreviousLease(ws)) {
      const attachment = this.readAttachment(ws);
      if (attachment) await this.settleOnce(attachment);
      return;
    }
    await this.finishRoom(1011, 'Peer errored', ws);
  }

  /** quota の絶対期限・idle期限を一つの alarm で処理する。 */
  async alarm(): Promise<void> {
    const records = this.liveSockets()
      .map((socket) => ({ socket, attachment: this.readAttachment(socket) }))
      .filter((value): value is { socket: WebSocket; attachment: RelayAttachment } => value.attachment !== null);
    if (records.length === 0) {
      await this.deleteAlarm();
      return;
    }

    if (!this.quotaConfigValid || this.env.RELAY_CIRCUIT_OPEN === '1') {
      await this.finishRoom(1011, 'Relay circuit open');
      return;
    }

    const now = Date.now();
    if (records.some((value) => this.isExpired(value.attachment, now))) {
      await this.finishRoom(1001, 'Relay quota expired');
      return;
    }

    const next = Math.min(...records.flatMap((value) => this.deadlines(value.attachment)));
    if (!Number.isFinite(next)) {
      await this.finishRoom(1011, 'Relay quota has no deadline');
      return;
    }
    if (records.some((value) => this.isIdle(value.attachment, now))) {
      await this.finishRoom(1001, 'Relay idle timeout');
      return;
    }

    // chunk ごとには setAlarm せず、alarm 発火時に最新 attachment の値から再設定する。
    await this.state.storage.setAlarm(Math.max(now + 1, next));
  }

  private liveSockets(): WebSocket[] {
    return this.state
      .getWebSockets()
      .filter((ws) => ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING);
  }

  private readAttachment(ws: WebSocket): RelayAttachment | null {
    try {
      const value = ws.deserializeAttachment() as unknown;
      return isRelayAttachment(value) ? value : null;
    } catch {
      return null;
    }
  }

  private persistAttachment(ws: WebSocket, attachment: RelayAttachment): boolean {
    try {
      ws.serializeAttachment(attachment);
      return true;
    } catch (e) {
      console.error(JSON.stringify({ message: 'relay attachment persist failed', room: this.roomLabel(), error: String(e) }));
      return false;
    }
  }

  private isExpired(attachment: RelayAttachment, now: number): boolean {
    return now >= attachment.limits.expiresAt || this.isIdle(attachment, now);
  }

  private isIdle(attachment: RelayAttachment, now: number): boolean {
    // 0 を「無制限」と解釈しない。設定ミスで idle ceiling が外れないよう即時失効に倒す。
    return attachment.limits.maxIdleMs === 0 || now - attachment.lastActivity >= attachment.limits.maxIdleMs;
  }

  private deadlines(attachment: RelayAttachment): number[] {
    const idle = attachment.lastActivity + attachment.limits.maxIdleMs;
    return [attachment.limits.expiresAt, idle];
  }

  private async scheduleNextAlarm(): Promise<void> {
    const records = this.liveSockets()
      .map((socket) => this.readAttachment(socket))
      .filter((value): value is RelayAttachment => value !== null);
    if (records.length === 0) return;
    const next = Math.min(...records.flatMap((value) => this.deadlines(value)));
    if (!Number.isFinite(next)) return;
    const existing = await this.state.storage.getAlarm();
    if (existing === null || existing > next) await this.state.storage.setAlarm(Math.max(Date.now() + 1, next));
  }

  private async finishRoom(code: number, reason: string, triggeringSocket?: WebSocket): Promise<void> {
    if (this.closing) return this.closing;
    this.roomGeneration += 1;
    const operation = this.closeAndSettleRoom(code, reason, triggeringSocket);
    this.closing = operation;
    try {
      await operation;
    } finally {
      if (this.closing === operation) this.closing = null;
    }
  }

  private async closeAndSettleRoom(code: number, reason: string, triggeringSocket?: WebSocket): Promise<void> {
    const sockets = this.state.getWebSockets();
    const attachments = new Map<string, RelayAttachment>();
    for (const ws of sockets) {
      const attachment = this.readAttachment(ws);
      if (attachment) this.mergeSettlementAttachment(attachments, attachment);
    }
    // `getWebSockets()` に現在の socket が含まれる runtime では、起点を別途
    // 合算すると同じ方向の使用量を二重計上する。fake/runtime 差分で一覧から
    // 欠落する場合だけ追加する。
    if (triggeringSocket && !sockets.includes(triggeringSocket)) {
      const attachment = this.readAttachment(triggeringSocket);
      if (attachment) this.mergeSettlementAttachment(attachments, attachment);
    }

    for (const ws of sockets) {
      try {
        if (ws.readyState !== WebSocket.CLOSED) ws.close(code, reason);
      } catch {
        // close 済み・half-dead は無視する。
      }
    }
    await Promise.all(Array.from(attachments.values(), (attachment) => this.settleOnce(attachment)));
    await this.deleteAlarm();
  }

  /** 前ルームの遅延 close/error で、既に始まった新しい lease を巻き込まない。 */
  private isEventFromPreviousLease(ws: WebSocket): boolean {
    const trigger = this.readAttachment(ws);
    if (!trigger) return false;
    return this.liveSockets().some((live) => {
      const current = this.readAttachment(live);
      return current !== null && current.leaseId !== trigger.leaseId;
    });
  }

  /** room 合算 counter は両 attachment に複製済みなので、大きい方を採用する。 */
  private mergeSettlementAttachment(target: Map<string, RelayAttachment>, attachment: RelayAttachment): void {
    const current = target.get(attachment.leaseId);
    if (!current) {
      target.set(attachment.leaseId, {
        ...attachment,
        lease: { ...attachment.lease },
        limits: { ...attachment.limits },
      });
      return;
    }
    current.bytes = Math.max(current.bytes, attachment.bytes);
    current.messages = Math.max(current.messages, attachment.messages);
    current.start = Math.min(current.start, attachment.start);
    current.lastActivity = Math.max(current.lastActivity, attachment.lastActivity);
  }

  private settleOnce(attachment: RelayAttachment): Promise<boolean> {
    const existing = this.settling.get(attachment.leaseId);
    if (existing) return existing;
    const promise = (async () => {
      try {
        const durationMs = Math.max(0, Date.now() - attachment.start);
        const settleInput = {
          roomId: attachment.roomId,
          leaseId: attachment.leaseId,
          // quota-do.ts の新契約。旧 helper が残る移行中も下の actual* alias で
          // 同じ実測値を受け取れるようにする。
          bytes: attachment.bytes,
          messages: attachment.messages,
          durationMs,
          durationSeconds: Math.ceil(durationMs / 1000),
          actualBytes: attachment.bytes,
          actualMessages: attachment.messages,
          actualDurationSeconds: Math.ceil(durationMs / 1000),
        } as Parameters<typeof settleRelayQuota>[1];
        const ok = await settleRelayQuota(this.env, settleInput);
        if (!ok) {
          // quota 集計側の失敗時は retry で二重計上せず、lease expiry に任せて fail-closed。
          console.error(JSON.stringify({ message: 'relay quota settle failed', room: this.roomLabel() }));
        }
        return ok;
      } catch (e) {
        console.error(JSON.stringify({ message: 'relay quota settle threw', room: this.roomLabel(), error: String(e) }));
        return false;
      }
    })();
    this.settling.set(attachment.leaseId, promise);
    return promise;
  }

  /** accept 前に無効化された reservation を、転送量 0 として安全に返却する。 */
  private settleUnusedReservation(lease: RelayQuotaLease, admission: RelayAdmission): Promise<boolean> {
    const now = Date.now();
    return this.settleOnce({
      lease,
      leaseId: lease.leaseId,
      roomId: admission.roomId,
      role: admission.role,
      device: admission.deviceId,
      deviceId: admission.deviceId,
      tier: admission.tier,
      bytes: 0,
      messages: 0,
      lastActivity: now,
      start: now,
      limits: {
        expiresAt: lease.expiresAt,
        maxBytes: lease.maxBytes,
        maxMessages: lease.maxMessages,
        maxIdleMs: lease.maxIdleMs,
        maxFrameBytes: lease.maxFrameBytes,
      },
    });
  }

  private async deleteAlarm(): Promise<void> {
    try {
      await this.state.storage.deleteAlarm();
    } catch {
      // fake state や旧 runtime に deleteAlarm が無い場合も close 自体は完了させる。
    }
  }

  private roomLabel(): string {
    try {
      return this.state.id.toString().slice(0, 8);
    } catch {
      return 'unknown';
    }
  }
}

function readInternalAdmission(req: Request): RelayAdmission | null {
  const roomId = req.headers.get(INTERNAL_ROOM) ?? '';
  const roleValue = req.headers.get(INTERNAL_ROLE) ?? '';
  const deviceId = req.headers.get(INTERNAL_DEVICE) ?? '';
  const tierValue = req.headers.get(INTERNAL_TIER) ?? '';
  if (!isCanonicalPairId(roomId)) return null;
  if (!RELAY_ROLES.has(roleValue)) return null;
  if (tierValue !== 'authenticated' && tierValue !== 'legacy') return null;
  if (tierValue === 'authenticated' && !/^[a-f0-9]{32}$/.test(deviceId)) return null;
  if (deviceId && !/^[a-f0-9]{32}$/.test(deviceId)) return null;
  return {
    roomId,
    role: roleValue as RelayRole,
    deviceId,
    tier: tierValue as RelayTier,
  };
}

function frameByteLength(msg: ArrayBuffer): number {
  return msg.byteLength;
}

function isValidLease(value: unknown, roomId: string): value is RelayQuotaLease {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as Partial<RelayQuotaLease>;
  return typeof candidate.leaseId === 'string' && candidate.leaseId.length > 0
    && candidate.roomId === roomId
    && (candidate.tier === 'authenticated' || candidate.tier === 'legacy')
    && typeof candidate.expiresAt === 'number' && Number.isFinite(candidate.expiresAt) && candidate.expiresAt > 0
    && typeof candidate.maxBytes === 'number' && Number.isFinite(candidate.maxBytes) && candidate.maxBytes >= 0
    && typeof candidate.maxMessages === 'number' && Number.isFinite(candidate.maxMessages) && candidate.maxMessages >= 0
    && typeof candidate.maxIdleMs === 'number' && Number.isFinite(candidate.maxIdleMs) && candidate.maxIdleMs >= 0
    && typeof candidate.maxFrameBytes === 'number' && Number.isFinite(candidate.maxFrameBytes) && candidate.maxFrameBytes >= 0;
}

function isRelayAttachment(value: unknown): value is RelayAttachment {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as Partial<RelayAttachment>;
  return typeof candidate.leaseId === 'string'
    && typeof candidate.roomId === 'string'
    && (candidate.role === 'offer' || candidate.role === 'answer')
    && typeof candidate.device === 'string'
    && typeof candidate.deviceId === 'string'
    && (candidate.tier === 'authenticated' || candidate.tier === 'legacy')
    && candidate.deviceId === candidate.device
    && typeof candidate.bytes === 'number' && Number.isFinite(candidate.bytes) && candidate.bytes >= 0
    && typeof candidate.messages === 'number' && Number.isFinite(candidate.messages) && candidate.messages >= 0
    && typeof candidate.lastActivity === 'number' && Number.isFinite(candidate.lastActivity) && candidate.lastActivity >= 0
    && typeof candidate.start === 'number' && Number.isFinite(candidate.start) && candidate.start >= 0
    && isValidLease(candidate.lease, candidate.roomId)
    && candidate.leaseId === candidate.lease.leaseId
    && candidate.lease.roomId === candidate.roomId
    && candidate.limits !== undefined
    && limitsMatchLease(candidate.limits, candidate.lease);
}

function isLimits(value: unknown): value is RelayLimits {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as Partial<RelayLimits>;
  return typeof candidate.expiresAt === 'number' && Number.isFinite(candidate.expiresAt) && candidate.expiresAt > 0
    && typeof candidate.maxBytes === 'number' && Number.isFinite(candidate.maxBytes) && candidate.maxBytes >= 0
    && typeof candidate.maxMessages === 'number' && Number.isFinite(candidate.maxMessages) && candidate.maxMessages >= 0
    && typeof candidate.maxIdleMs === 'number' && Number.isFinite(candidate.maxIdleMs) && candidate.maxIdleMs >= 0
    && typeof candidate.maxFrameBytes === 'number' && Number.isFinite(candidate.maxFrameBytes) && candidate.maxFrameBytes >= 0;
}

function limitsMatchLease(limits: unknown, lease: RelayQuotaLease): limits is RelayLimits {
  if (!isLimits(limits)) return false;
  return limits.expiresAt === lease.expiresAt
    && limits.maxBytes === lease.maxBytes
    && limits.maxMessages === lease.maxMessages
    && limits.maxIdleMs === lease.maxIdleMs
    && limits.maxFrameBytes === lease.maxFrameBytes;
}

function jsonResponse(status: number, body: object): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

/** Node の標準 Response は status=101 を拒否するため、単体テスト用の互換 fallback を持つ。 */
function websocketSwitchingResponse(client: WebSocket): Response {
  try {
    return new Response(null, { status: 101, webSocket: client });
  } catch {
    const response = new Response(null, { status: 200 });
    return new Proxy(response, {
      get(target, property, receiver) {
        if (property === 'status') return 101;
        if (property === 'webSocket') return client;
        return Reflect.get(target, property, receiver);
      },
    });
  }
}
