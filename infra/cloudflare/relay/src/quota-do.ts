/**
 * リレー帯域・同時ルーム数の singleton quota Durable Object。
 *
 * QUOTA は `idFromName("global")` で 1 インスタンスへ集約する。予約と
 * settle は同じ DO の storage transaction 内で完結させるため、複数の
 * Worker が同時に入室しても月次 quota と active room 数を取りこぼさない。
 */

export type RelayTier = 'authenticated' | 'legacy';
export type RelayRole = 'offer' | 'answer';

export interface RelayQuotaLease {
  leaseId: string;
  roomId: string;
  tier: RelayTier;
  expiresAt: number;
  maxBytes: number;
  maxMessages: number;
  maxIdleMs: number;
  maxFrameBytes: number;
}

export interface RelayQuotaReserveInput {
  roomId: string;
  tier: RelayTier;
  deviceId: string;
  role: RelayRole;
}

export interface RelayQuotaSettleInput {
  roomId: string;
  leaseId: string;
  actualBytes?: number;
  actualMessages?: number;
  actualDurationSeconds?: number;
  /** 旧呼び出し側との境界を狭く保つための短い別名。正規化後は actual* を使う。 */
  bytes?: number;
  messages?: number;
  durationMs?: number;
  durationSeconds?: number;
  actual?: {
    bytes?: number;
    messages?: number;
    durationMs?: number;
    durationSeconds?: number;
  };
}

/**
 * 実際の `Env` は index.ts 側で定義するため、ここでは quota が必要とする
 * 部分だけを公開する。設定値は Wrangler の文字列 binding/secret を想定する。
 */
export interface RelayQuotaEnv {
  QUOTA?: DurableObjectNamespace;
  RELAY_CIRCUIT_OPEN?: string;
  RELAY_MAX_CONCURRENT_ROOMS?: string;
  RELAY_MONTHLY_BYTES?: string;
  RELAY_MONTHLY_MESSAGES?: string;
  RELAY_MONTHLY_DURATION_SECONDS?: string;
  RELAY_AUTH_SESSION_BYTES?: string;
  RELAY_AUTH_SESSION_MESSAGES?: string;
  RELAY_AUTH_SESSION_SECONDS?: string;
  RELAY_AUTH_SESSION_IDLE_SECONDS?: string;
  /** 現行 vars 名。移行中の session 名も受け付ける。 */
  RELAY_AUTH_IDLE_SECONDS?: string;
  RELAY_LEGACY_MONTHLY_BYTES?: string;
  RELAY_LEGACY_MONTHLY_MESSAGES?: string;
  RELAY_LEGACY_MONTHLY_DURATION_SECONDS?: string;
  RELAY_LEGACY_SESSION_BYTES?: string;
  RELAY_LEGACY_SESSION_MESSAGES?: string;
  RELAY_LEGACY_SESSION_SECONDS?: string;
  RELAY_LEGACY_SESSION_IDLE_SECONDS?: string;
  /** 現行 vars 名。移行中の session 名も受け付ける。 */
  RELAY_LEGACY_IDLE_SECONDS?: string;
  RELAY_MAX_FRAME_BYTES?: string;
}

const CONFIG_KEYS = [
  'RELAY_CIRCUIT_OPEN',
  'RELAY_MAX_CONCURRENT_ROOMS',
  'RELAY_MONTHLY_BYTES',
  'RELAY_MONTHLY_MESSAGES',
  'RELAY_MONTHLY_DURATION_SECONDS',
  'RELAY_AUTH_SESSION_BYTES',
  'RELAY_AUTH_SESSION_MESSAGES',
  'RELAY_AUTH_SESSION_SECONDS',
  'RELAY_AUTH_IDLE_SECONDS',
  'RELAY_LEGACY_MONTHLY_BYTES',
  'RELAY_LEGACY_MONTHLY_MESSAGES',
  'RELAY_LEGACY_MONTHLY_DURATION_SECONDS',
  'RELAY_LEGACY_SESSION_BYTES',
  'RELAY_LEGACY_SESSION_MESSAGES',
  'RELAY_LEGACY_SESSION_SECONDS',
  'RELAY_LEGACY_IDLE_SECONDS',
  'RELAY_MAX_FRAME_BYTES',
] as const;

type ConfigKey = (typeof CONFIG_KEYS)[number];

const CONFIG_ALIASES: Partial<Record<ConfigKey, string>> = {
  RELAY_AUTH_IDLE_SECONDS: 'RELAY_AUTH_SESSION_IDLE_SECONDS',
  RELAY_LEGACY_IDLE_SECONDS: 'RELAY_LEGACY_SESSION_IDLE_SECONDS',
};

interface MetricLimits {
  bytes: number;
  messages: number;
  durationSeconds: number;
}

interface TierLimits extends MetricLimits {
  sessionSeconds: number;
  idleSeconds: number;
}

interface ParsedQuotaConfig {
  circuitOpen: boolean;
  maxConcurrentRooms: number;
  monthly: MetricLimits;
  legacyMonthly: MetricLimits;
  authenticated: TierLimits;
  legacy: TierLimits;
  maxFrameBytes: number;
}

interface MetricBucket {
  reservedBytes: number;
  usedBytes: number;
  reservedMessages: number;
  usedMessages: number;
  reservedDurationSeconds: number;
  usedDurationSeconds: number;
}

interface LeaseRecord {
  leaseId: string;
  roomId: string;
  tier: RelayTier;
  deviceId: string;
  monthBucket: string;
  expiresAt: number;
  maxBytes: number;
  maxMessages: number;
  maxDurationSeconds: number;
  maxIdleMs: number;
  maxFrameBytes: number;
  /** 同一 lease で各 peer role を一度だけ入室させる。 */
  admittedRoles: RelayRole[];
}

interface SettledRecord {
  roomId: string;
  settledAt: number;
  kind: 'settled' | 'expired';
}

interface PersistedQuotaState {
  version: 1;
  activeRooms: number;
  globalMonths: Record<string, MetricBucket>;
  legacyMonths: Record<string, MetricBucket>;
  leases: Record<string, LeaseRecord>;
  settled: Record<string, SettledRecord>;
}

interface QuotaBindingLike {
  idFromName(name: string): unknown;
  get(id: unknown): QuotaStubLike;
}

interface QuotaStubLike {
  fetch(request: Request): Promise<Response>;
}

interface NormalizedSettleInput {
  roomId: string;
  leaseId: string;
  actualBytes: number;
  actualMessages: number;
  actualDurationSeconds: number;
}

interface ReserveResult {
  ok: true;
  lease: RelayQuotaLease;
}

interface QuotaReject {
  ok: false;
  reason: 'active' | 'monthly' | 'legacy-monthly' | 'reentry';
}

type InternalReserveResult = ReserveResult | QuotaReject;

const STATE_KEY = 'relay-quota-state-v1';
const MONTH_RE = /^\d{4}-\d{2}$/;
const MAX_SAFE = Number.MAX_SAFE_INTEGER;
// settle の冪等性 tombstone は無制限に保持すると singleton の状態が肥大する。
// 期限切れ lease の遅延 settle を吸収できる期間を確保しつつ、個数にも上限を設ける。
const SETTLED_HISTORY_LIMIT = 512;
const SETTLED_HISTORY_MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000;

/** 必須文字列設定を検証する。エラー時は quota を fail-closed にする。 */
export function validateRelayQuotaConfig(env: unknown): string[] {
  try {
    return collectRelayQuotaConfigErrors(env);
  } catch {
    // Env は通常 plain object だが、読み取り不能な binding も fail-closed にする。
    return ['RELAY_QUOTA_CONFIG_UNREADABLE'];
  }
}

function collectRelayQuotaConfigErrors(env: unknown): string[] {
  const errors: string[] = [];
  const source = asRecord(env);

  const circuit = source?.RELAY_CIRCUIT_OPEN;
  if (circuit !== '0' && circuit !== '1') {
    errors.push('RELAY_CIRCUIT_OPEN must be exactly 0 or 1');
  }

  for (const key of CONFIG_KEYS) {
    if (key === 'RELAY_CIRCUIT_OPEN') continue;
    const value = parseNonNegativeInteger(configValue(source, key));
    if (value === null) {
      const alias = CONFIG_ALIASES[key];
      errors.push(`${key}${alias ? ` (or ${alias})` : ''} must be a non-negative safe integer string`);
    }
  }
  return errors;
}

function parseQuotaConfig(env: unknown): { config: ParsedQuotaConfig | null; errors: string[] } {
  const errors = validateRelayQuotaConfig(env);
  if (errors.length > 0) return { config: null, errors };

  const source = asRecord(env);
  // validateRelayQuotaConfig が先に全キーを検査しているため、ここで null にはならない。
  const number = (key: ConfigKey): number => parseNonNegativeInteger(configValue(source, key)) ?? 0;
  return {
    config: {
      circuitOpen: source?.RELAY_CIRCUIT_OPEN === '1',
      maxConcurrentRooms: number('RELAY_MAX_CONCURRENT_ROOMS'),
      monthly: {
        bytes: number('RELAY_MONTHLY_BYTES'),
        messages: number('RELAY_MONTHLY_MESSAGES'),
        durationSeconds: number('RELAY_MONTHLY_DURATION_SECONDS'),
      },
      legacyMonthly: {
        bytes: number('RELAY_LEGACY_MONTHLY_BYTES'),
        messages: number('RELAY_LEGACY_MONTHLY_MESSAGES'),
        durationSeconds: number('RELAY_LEGACY_MONTHLY_DURATION_SECONDS'),
      },
      authenticated: {
        bytes: number('RELAY_AUTH_SESSION_BYTES'),
        messages: number('RELAY_AUTH_SESSION_MESSAGES'),
        durationSeconds: number('RELAY_AUTH_SESSION_SECONDS'),
        sessionSeconds: number('RELAY_AUTH_SESSION_SECONDS'),
        idleSeconds: number('RELAY_AUTH_IDLE_SECONDS'),
      },
      legacy: {
        bytes: number('RELAY_LEGACY_SESSION_BYTES'),
        messages: number('RELAY_LEGACY_SESSION_MESSAGES'),
        durationSeconds: number('RELAY_LEGACY_SESSION_SECONDS'),
        sessionSeconds: number('RELAY_LEGACY_SESSION_SECONDS'),
        idleSeconds: number('RELAY_LEGACY_IDLE_SECONDS'),
      },
      maxFrameBytes: number('RELAY_MAX_FRAME_BYTES'),
    },
    errors: [],
  };
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null ? (value as Record<string, unknown>) : null;
}

function configValue(source: Record<string, unknown> | null, key: ConfigKey): unknown {
  const value = source?.[key];
  if (value !== undefined) return value;
  const alias = CONFIG_ALIASES[key];
  return alias ? source?.[alias] : undefined;
}

function parseNonNegativeInteger(value: unknown): number | null {
  if (typeof value !== 'string' || !/^(0|[1-9]\d*)$/.test(value)) return null;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : null;
}

function parseMetric(value: unknown, fallback = 0): number | null {
  if (value === undefined) return fallback;
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0 || value > MAX_SAFE) return null;
  return value;
}

function parseId(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 && value.length <= 512 ? value : null;
}

function normalizeReserveInput(value: unknown): RelayQuotaReserveInput | null {
  const source = asRecord(value);
  if (!source) return null;
  const roomId = parseId(source.roomId);
  const deviceId = parseId(source.deviceId);
  const tier = source.tier;
  const role = source.role;
  if (!roomId || !deviceId ||
    (tier !== 'authenticated' && tier !== 'legacy') ||
    (role !== 'offer' && role !== 'answer')) return null;
  return { roomId, tier, deviceId, role };
}

function normalizeSettleInput(value: unknown): NormalizedSettleInput | null {
  const source = asRecord(value);
  if (!source) return null;
  const roomId = parseId(source.roomId);
  const leaseId = parseId(source.leaseId);
  if (!roomId || !leaseId) return null;

  const nested = asRecord(source.actual);
  const bytesValue = source.actualBytes ?? source.bytes ?? nested?.bytes;
  const messagesValue = source.actualMessages ?? source.messages ?? nested?.messages;
  const durationValue = source.actualDurationSeconds ?? source.durationSeconds ?? nested?.durationSeconds;
  const actualBytes = parseMetric(bytesValue);
  const actualMessages = parseMetric(messagesValue);
  const durationMsValue = source.durationMs ?? nested?.durationMs;
  const durationMs = durationMsValue === undefined ? 0 : parseMetric(durationMsValue);
  const actualDurationSeconds = durationValue === undefined
    ? (durationMs === null ? null : Math.ceil(durationMs / 1000))
    : parseMetric(durationValue);
  if (actualBytes === null || actualMessages === null || actualDurationSeconds === null) return null;
  return { roomId, leaseId, actualBytes, actualMessages, actualDurationSeconds };
}

function emptyBucket(): MetricBucket {
  return {
    reservedBytes: 0,
    usedBytes: 0,
    reservedMessages: 0,
    usedMessages: 0,
    reservedDurationSeconds: 0,
    usedDurationSeconds: 0,
  };
}

function numberOrZero(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= MAX_SAFE ? value : 0;
}

function normalizeBucket(value: unknown): MetricBucket {
  const source = asRecord(value);
  return {
    reservedBytes: numberOrZero(source?.reservedBytes),
    usedBytes: numberOrZero(source?.usedBytes),
    reservedMessages: numberOrZero(source?.reservedMessages),
    usedMessages: numberOrZero(source?.usedMessages),
    reservedDurationSeconds: numberOrZero(source?.reservedDurationSeconds),
    usedDurationSeconds: numberOrZero(source?.usedDurationSeconds),
  };
}

function normalizeLease(value: unknown, roomId: string): LeaseRecord | null {
  const source = asRecord(value);
  if (!source) return null;
  const leaseId = parseId(source.leaseId);
  const storedRoomId = parseId(source.roomId) ?? roomId;
  const tier = source.tier;
  const deviceId = parseId(source.deviceId) ?? '';
  const monthBucket = typeof source.monthBucket === 'string' && MONTH_RE.test(source.monthBucket)
    ? source.monthBucket
    : '1970-01';
  const expiresAt = numberOrZero(source.expiresAt);
  const maxBytes = numberOrZero(source.maxBytes);
  const maxMessages = numberOrZero(source.maxMessages);
  const maxDurationSeconds = numberOrZero(source.maxDurationSeconds);
  const maxIdleMs = numberOrZero(source.maxIdleMs);
  const maxFrameBytes = numberOrZero(source.maxFrameBytes);
  // v4 初期版の保存済み lease には admittedRoles が無い。再入室可能として
  // 復元すると settle 障害時に上限を反復利用できるため、旧・破損値は両 role
  // 消費済みとして fail closed にする。
  const rawAdmittedRoles = source.admittedRoles;
  const admittedRoles: RelayRole[] = Array.isArray(rawAdmittedRoles) && rawAdmittedRoles.length > 0 &&
    rawAdmittedRoles.every((role) => role === 'offer' || role === 'answer')
    ? [...new Set(rawAdmittedRoles as RelayRole[])]
    : ['offer', 'answer'];
  if (!leaseId || !storedRoomId || (tier !== 'authenticated' && tier !== 'legacy') || expiresAt <= 0) return null;
  return {
    leaseId,
    roomId: storedRoomId,
    tier,
    deviceId,
    monthBucket,
    expiresAt,
    maxBytes,
    maxMessages,
    maxDurationSeconds,
    maxIdleMs,
    maxFrameBytes,
    admittedRoles,
  };
}

function normalizeState(value: unknown): PersistedQuotaState {
  const source = asRecord(value);
  // roomId/leaseId は外部入力由来なので、prototype pollution を避けるため
  // 状態 map は null-prototype で保持する。
  const globalMonths = Object.create(null) as Record<string, MetricBucket>;
  const legacyMonths = Object.create(null) as Record<string, MetricBucket>;
  const leases = Object.create(null) as Record<string, LeaseRecord>;
  const settled = Object.create(null) as Record<string, SettledRecord>;

  const rawGlobal = asRecord(source?.globalMonths);
  if (rawGlobal) {
    for (const [bucket, raw] of Object.entries(rawGlobal)) {
      if (MONTH_RE.test(bucket)) globalMonths[bucket] = normalizeBucket(raw);
    }
  }
  const rawLegacy = asRecord(source?.legacyMonths);
  if (rawLegacy) {
    for (const [bucket, raw] of Object.entries(rawLegacy)) {
      if (MONTH_RE.test(bucket)) legacyMonths[bucket] = normalizeBucket(raw);
    }
  }
  const rawLeases = asRecord(source?.leases);
  if (rawLeases) {
    for (const [roomId, raw] of Object.entries(rawLeases)) {
      const lease = normalizeLease(raw, roomId);
      if (lease) leases[roomId] = lease;
    }
  }
  const rawSettled = asRecord(source?.settled);
  if (rawSettled) {
    for (const [leaseId, raw] of Object.entries(rawSettled)) {
      const item = asRecord(raw);
      const roomId = parseId(item?.roomId);
      const settledAt = numberOrZero(item?.settledAt);
      const kind = item?.kind === 'expired' ? 'expired' : item?.kind === 'settled' ? 'settled' : null;
      if (roomId && settledAt > 0 && kind) settled[leaseId] = { roomId, settledAt, kind };
    }
  }
  return {
    version: 1,
    activeRooms: numberOrZero(source?.activeRooms),
    globalMonths,
    legacyMonths,
    leases,
    settled,
  };
}

function utcMonthBucket(now: number): string {
  const date = new Date(now);
  return `${date.getUTCFullYear().toString().padStart(4, '0')}-${(date.getUTCMonth() + 1).toString().padStart(2, '0')}`;
}

function getBucket(months: Record<string, MetricBucket>, bucket: string): MetricBucket {
  const current = months[bucket];
  if (current) return current;
  const created = emptyBucket();
  months[bucket] = created;
  return created;
}

function publicLease(record: LeaseRecord): RelayQuotaLease {
  return {
    leaseId: record.leaseId,
    roomId: record.roomId,
    tier: record.tier,
    expiresAt: record.expiresAt,
    maxBytes: record.maxBytes,
    maxMessages: record.maxMessages,
    maxIdleMs: record.maxIdleMs,
    maxFrameBytes: record.maxFrameBytes,
  };
}

function millisFromSeconds(seconds: number): number {
  const milliseconds = seconds * 1000;
  return Number.isSafeInteger(milliseconds) ? milliseconds : MAX_SAFE;
}

function expiresAt(now: number, sessionSeconds: number): number {
  const ttl = millisFromSeconds(sessionSeconds);
  const sessionExpiry = now > MAX_SAFE - ttl ? MAX_SAFE : now + ttl;
  // 月をまたいで旧月の予約を新月の quota に持ち越さない。月末境界は
  // UTC の次月 00:00 とし、同時刻に alarm が走れば expiry 扱いにする。
  const current = new Date(now);
  const monthBoundary = Date.UTC(current.getUTCFullYear(), current.getUTCMonth() + 1, 1);
  return Math.min(sessionExpiry, Number.isFinite(monthBoundary) ? monthBoundary : MAX_SAFE);
}

function metricWouldExceed(used: number, reserved: number, additional: number, limit: number): boolean {
  const remaining = limit - used - reserved;
  return remaining < additional;
}

function bucketWouldExceed(bucket: MetricBucket, add: MetricLimits, limit: MetricLimits): boolean {
  return (
    metricWouldExceed(bucket.usedBytes, bucket.reservedBytes, add.bytes, limit.bytes) ||
    metricWouldExceed(bucket.usedMessages, bucket.reservedMessages, add.messages, limit.messages) ||
    metricWouldExceed(bucket.usedDurationSeconds, bucket.reservedDurationSeconds, add.durationSeconds, limit.durationSeconds)
  );
}

function bucketWouldExceedAfterReplacing(
  bucket: MetricBucket,
  remove: MetricLimits,
  add: MetricLimits,
  limit: MetricLimits,
): boolean {
  return (
    metricWouldExceed(bucket.usedBytes, Math.max(0, bucket.reservedBytes - remove.bytes), add.bytes, limit.bytes) ||
    metricWouldExceed(
      bucket.usedMessages,
      Math.max(0, bucket.reservedMessages - remove.messages),
      add.messages,
      limit.messages,
    ) ||
    metricWouldExceed(
      bucket.usedDurationSeconds,
      Math.max(0, bucket.reservedDurationSeconds - remove.durationSeconds),
      add.durationSeconds,
      limit.durationSeconds,
    )
  );
}

function addReservation(bucket: MetricBucket, limits: MetricLimits): void {
  bucket.reservedBytes = safeAdd(bucket.reservedBytes, limits.bytes);
  bucket.reservedMessages = safeAdd(bucket.reservedMessages, limits.messages);
  bucket.reservedDurationSeconds = safeAdd(bucket.reservedDurationSeconds, limits.durationSeconds);
}

function releaseReservation(bucket: MetricBucket, limits: MetricLimits): void {
  bucket.reservedBytes = Math.max(0, bucket.reservedBytes - limits.bytes);
  bucket.reservedMessages = Math.max(0, bucket.reservedMessages - limits.messages);
  bucket.reservedDurationSeconds = Math.max(0, bucket.reservedDurationSeconds - limits.durationSeconds);
}

function releaseReservationAndAddUsage(bucket: MetricBucket, reservation: MetricLimits, actual: MetricLimits): void {
  bucket.reservedBytes = Math.max(0, bucket.reservedBytes - reservation.bytes);
  bucket.reservedMessages = Math.max(0, bucket.reservedMessages - reservation.messages);
  bucket.reservedDurationSeconds = Math.max(0, bucket.reservedDurationSeconds - reservation.durationSeconds);
  bucket.usedBytes = safeAdd(bucket.usedBytes, Math.min(reservation.bytes, actual.bytes));
  bucket.usedMessages = safeAdd(bucket.usedMessages, Math.min(reservation.messages, actual.messages));
  bucket.usedDurationSeconds = safeAdd(bucket.usedDurationSeconds, Math.min(reservation.durationSeconds, actual.durationSeconds));
}

function safeAdd(left: number, right: number): number {
  return left >= MAX_SAFE - right ? MAX_SAFE : left + right;
}

function settleRecord(
  state: PersistedQuotaState,
  record: LeaseRecord,
  actual: MetricLimits,
  now: number,
  kind: SettledRecord['kind'],
): void {
  const reservation: MetricLimits = {
    bytes: record.maxBytes,
    messages: record.maxMessages,
    durationSeconds: record.maxDurationSeconds,
  };
  // global 月次枠は authenticated/legacy の両 tier に共通で課金する。
  // legacy はさらに専用の subset 枠にも課金する。
  const globalBucket = getBucket(state.globalMonths, record.monthBucket);
  releaseReservationAndAddUsage(globalBucket, reservation, actual);
  if (record.tier === 'legacy') {
    const legacyBucket = getBucket(state.legacyMonths, record.monthBucket);
    releaseReservationAndAddUsage(legacyBucket, reservation, actual);
  }
  delete state.leases[record.roomId];
  state.activeRooms = Math.max(0, state.activeRooms - 1);
  state.settled[record.leaseId] = { roomId: record.roomId, settledAt: now, kind };
}

function expireDueLeases(state: PersistedQuotaState, now: number): boolean {
  let changed = false;
  for (const record of Object.values(state.leases)) {
    if (record.expiresAt <= now) {
      // 接続が settle されなかった場合は予約全量を利用済みに確定する。
      const full: MetricLimits = {
        bytes: record.maxBytes,
        messages: record.maxMessages,
        durationSeconds: record.maxDurationSeconds,
      };
      settleRecord(state, record, full, now, 'expired');
      changed = true;
    }
  }
  return changed;
}

function pruneSettledHistory(state: PersistedQuotaState, now: number): boolean {
  let changed = false;
  const cutoff = now - SETTLED_HISTORY_MAX_AGE_MS;
  for (const [leaseId, record] of Object.entries(state.settled)) {
    if (record.settledAt <= cutoff) {
      delete state.settled[leaseId];
      changed = true;
    }
  }

  const entries = Object.entries(state.settled);
  if (entries.length > SETTLED_HISTORY_LIMIT) {
    entries.sort((left, right) => left[1].settledAt - right[1].settledAt);
    const removeCount = entries.length - SETTLED_HISTORY_LIMIT;
    for (let index = 0; index < removeCount; index += 1) {
      delete state.settled[entries[index][0]];
    }
    changed = true;
  }
  return changed;
}

function cleanState(state: PersistedQuotaState, now: number): boolean {
  const expired = expireDueLeases(state, now);
  // expiry が大量に発生した場合も cap を越えた tombstone を同一 transaction
  // 内で整理する。通常の予約/settle 前の age prune もここで兼ねる。
  const pruned = pruneSettledHistory(state, now);
  return expired || pruned;
}

function nextLeaseExpiry(state: PersistedQuotaState): number | null {
  let next: number | null = null;
  for (const record of Object.values(state.leases)) {
    if (next === null || record.expiresAt < next) next = record.expiresAt;
  }
  return next;
}

async function applyAlarmInTransaction(txn: DurableObjectTransaction, alarmAt: number | null): Promise<void> {
  if (alarmAt === null) {
    await txn.deleteAlarm();
    return;
  }
  await txn.setAlarm(alarmAt);
}

async function transactionWithAlarm<T>(
  storage: DurableObjectStorage,
  work: (txn: DurableObjectTransaction) => Promise<{ value: T; alarmAt: number | null; syncAlarm: boolean }>,
): Promise<T> {
  return storage.transaction(async (txn) => {
    const outcome = await work(txn);
    if (outcome.syncAlarm) await applyAlarmInTransaction(txn, outcome.alarmAt);
    return outcome.value;
  });
}

function jsonResponse(status: number, body: object): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function misconfiguredResponse(): Response {
  return jsonResponse(503, { error: 'QUOTA_MISCONFIGURED' });
}

function unavailableResponse(): Response {
  return jsonResponse(503, { error: 'QUOTA_UNAVAILABLE' });
}

function circuitOpenResponse(): Response {
  return jsonResponse(503, { error: 'RELAY_CIRCUIT_OPEN' });
}

function quotaExceededResponse(reason: QuotaReject['reason']): Response {
  if (reason === 'reentry') {
    return jsonResponse(409, { error: 'LEASE_ROLE_ALREADY_ADMITTED' });
  }
  return jsonResponse(429, { error: 'QUOTA_EXCEEDED', reason });
}

function getQuotaBinding(env: unknown): QuotaBindingLike | null {
  const source = asRecord(env);
  const candidate = source?.QUOTA;
  if (!candidate || typeof candidate !== 'object') return null;
  const binding = candidate as QuotaBindingLike;
  if (typeof binding.idFromName !== 'function' || typeof binding.get !== 'function') return null;
  return binding;
}

function isRelayQuotaLease(value: unknown): value is RelayQuotaLease {
  const source = asRecord(value);
  return !!source &&
    typeof source.leaseId === 'string' && source.leaseId.length > 0 &&
    typeof source.roomId === 'string' && source.roomId.length > 0 &&
    (source.tier === 'authenticated' || source.tier === 'legacy') &&
    typeof source.expiresAt === 'number' && Number.isSafeInteger(source.expiresAt) && source.expiresAt > 0 &&
    typeof source.maxBytes === 'number' && Number.isSafeInteger(source.maxBytes) && source.maxBytes >= 0 &&
    typeof source.maxMessages === 'number' && Number.isSafeInteger(source.maxMessages) && source.maxMessages >= 0 &&
    typeof source.maxIdleMs === 'number' && Number.isSafeInteger(source.maxIdleMs) && source.maxIdleMs >= 0 &&
    typeof source.maxFrameBytes === 'number' && Number.isSafeInteger(source.maxFrameBytes) && source.maxFrameBytes >= 0;
}

/** QUOTA singleton へ reserve を転送する Worker 側 helper。例外は必ず 503 へ畳み込む。 */
export async function reserveRelayQuota(
  env: unknown,
  input: RelayQuotaReserveInput,
): Promise<{ ok: true; lease: RelayQuotaLease } | { ok: false; response: Response }> {
  try {
    const parsed = parseQuotaConfig(env);
    if (parsed.errors.length > 0 || !parsed.config) return { ok: false, response: misconfiguredResponse() };
    if (parsed.config.circuitOpen) return { ok: false, response: circuitOpenResponse() };
    const normalized = normalizeReserveInput(input);
    if (!normalized) return { ok: false, response: jsonResponse(400, { error: 'INVALID_REQUEST' }) };
    const binding = getQuotaBinding(env);
    if (!binding) return { ok: false, response: unavailableResponse() };
    const id = binding.idFromName('global');
    const stub = binding.get(id);
    const response = await stub.fetch(new Request('https://quota.internal/reserve', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(normalized),
    }));
    if (response.status !== 200) {
      // quota 超過 (429) と breaker (503) は呼び出し側へそのまま返す。
      // それ以外の応答は、予約 helper の成功契約を満たさない QUOTA
      // 障害として 503 に正規化する。
      return {
        ok: false,
        response: response.status === 409 || response.status === 429 || response.status === 503
          ? response
          : unavailableResponse(),
      };
    }
    let body: unknown;
    try {
      body = await response.json();
    } catch {
      return { ok: false, response: unavailableResponse() };
    }
    const source = asRecord(body);
    const leaseValue = source?.lease ?? body;
    if (!isRelayQuotaLease(leaseValue)) return { ok: false, response: unavailableResponse() };
    return { ok: true, lease: leaseValue };
  } catch {
    return { ok: false, response: unavailableResponse() };
  }
}

/** QUOTA singleton へ settle を転送する helper。失敗・例外はすべて false。 */
export async function settleRelayQuota(env: unknown, input: RelayQuotaSettleInput): Promise<boolean> {
  try {
    const parsed = parseQuotaConfig(env);
    if (parsed.errors.length > 0 || !parsed.config) return false;
    const normalized = normalizeSettleInput(input);
    if (!normalized) return false;
    const binding = getQuotaBinding(env);
    if (!binding) return false;
    const id = binding.idFromName('global');
    const stub = binding.get(id);
    const response = await stub.fetch(new Request('https://quota.internal/settle', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(normalized),
    }));
    return response.status === 200;
  } catch {
    return false;
  }
}

/** quota singleton の Durable Object 本体。 */
export class RelayQuotaDO {
  readonly state: DurableObjectState;
  readonly env: unknown;

  constructor(state: DurableObjectState, env: unknown) {
    this.state = state;
    this.env = env;
  }

  async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname !== '/reserve' && url.pathname !== '/settle') {
      return new Response('Not Found', { status: 404 });
    }
    if (request.method !== 'POST') {
      return new Response('Method Not Allowed', { status: 405, headers: { allow: 'POST' } });
    }

    const parsed = parseQuotaConfig(this.env);
    if (parsed.errors.length > 0 || !parsed.config) return misconfiguredResponse();
    if (url.pathname === '/reserve' && parsed.config.circuitOpen) return circuitOpenResponse();

    let body: unknown;
    try {
      body = await request.json();
    } catch {
      return jsonResponse(400, { error: 'INVALID_JSON' });
    }

    if (url.pathname === '/reserve') {
      const input = normalizeReserveInput(body);
      if (!input) return jsonResponse(400, { error: 'INVALID_REQUEST' });
      try {
        const result = await this.reserve(input, parsed.config);
        if (!result.ok) return quotaExceededResponse(result.reason);
        return jsonResponse(200, { ok: true, lease: result.lease });
      } catch {
        return unavailableResponse();
      }
    }

    const input = normalizeSettleInput(body);
    if (!input) return jsonResponse(400, { error: 'INVALID_REQUEST' });
    try {
      const settled = await this.settle(input);
      return settled ? jsonResponse(200, { ok: true }) : jsonResponse(404, { ok: false, error: 'STALE_LEASE' });
    } catch {
      return unavailableResponse();
    }
  }

  async alarm(): Promise<void> {
    const now = Date.now();
    await transactionWithAlarm(this.state.storage, async (txn) => {
      const state = normalizeState(await txn.get<unknown>(STATE_KEY));
      const changed = cleanState(state, now);
      if (changed) await txn.put(STATE_KEY, state);
      return {
        value: undefined,
        syncAlarm: true,
        alarmAt: nextLeaseExpiry(state),
      };
    });
  }

  private async reserve(input: RelayQuotaReserveInput, config: ParsedQuotaConfig): Promise<InternalReserveResult> {
    const now = Date.now();
    return transactionWithAlarm<InternalReserveResult>(this.state.storage, async (txn) => {
      const state = normalizeState(await txn.get<unknown>(STATE_KEY));
      const cleaned = cleanState(state, now);
      const existing = state.leases[input.roomId];
      if (existing && existing.expiresAt > now) {
        // RelayDO の settle が失敗して room が再生成されても、同じ lease の同一
        // role を再発行しない。各 role は一度だけ消費し、未確定 lease は expiry
        // で予約全量を確定するため、セッション上限を反復利用できない。
        if (existing.admittedRoles.includes(input.role)) {
          if (cleaned) await txn.put(STATE_KEY, state);
          return {
            value: { ok: false, reason: 'reentry' },
            alarmAt: nextLeaseExpiry(state),
            syncAlarm: cleaned,
          };
        }
        // 段階移行中は、新クライアント (authenticated) と旧クライアント (legacy) が同じ
        // room に入ることがある。先着が authenticated のとき既存 lease をそのまま返すと、
        // 後着 legacy が 10 GiB の認証済み枠へ相乗りし、legacy 月次 subset を消費しない。
        // mixed room は leaseId を維持したまま小さい方の上限へ原子的に降格する。
        if (existing.tier === 'authenticated' && input.tier === 'legacy') {
          const previous: MetricLimits = {
            bytes: existing.maxBytes,
            messages: existing.maxMessages,
            durationSeconds: existing.maxDurationSeconds,
          };
          const replacement: MetricLimits = {
            bytes: Math.min(existing.maxBytes, config.legacy.bytes),
            messages: Math.min(existing.maxMessages, config.legacy.messages),
            durationSeconds: Math.min(existing.maxDurationSeconds, config.legacy.durationSeconds),
          };
          const globalBucket = getBucket(state.globalMonths, existing.monthBucket);
          const legacyBucket = getBucket(state.legacyMonths, existing.monthBucket);
          if (bucketWouldExceedAfterReplacing(globalBucket, previous, replacement, config.monthly)) {
            if (cleaned) await txn.put(STATE_KEY, state);
            return {
              value: { ok: false, reason: 'monthly' },
              alarmAt: nextLeaseExpiry(state),
              syncAlarm: cleaned,
            };
          }
          if (bucketWouldExceed(legacyBucket, replacement, config.legacyMonthly)) {
            if (cleaned) await txn.put(STATE_KEY, state);
            return {
              value: { ok: false, reason: 'legacy-monthly' },
              alarmAt: nextLeaseExpiry(state),
              syncAlarm: cleaned,
            };
          }

          releaseReservation(globalBucket, previous);
          addReservation(globalBucket, replacement);
          addReservation(legacyBucket, replacement);
          existing.tier = 'legacy';
          existing.deviceId = input.deviceId;
          existing.expiresAt = Math.min(existing.expiresAt, expiresAt(now, config.legacy.sessionSeconds));
          existing.maxBytes = replacement.bytes;
          existing.maxMessages = replacement.messages;
          existing.maxDurationSeconds = replacement.durationSeconds;
          existing.maxIdleMs = Math.min(existing.maxIdleMs, millisFromSeconds(config.legacy.idleSeconds));
          existing.maxFrameBytes = Math.min(existing.maxFrameBytes, config.maxFrameBytes);
          existing.admittedRoles.push(input.role);
          await txn.put(STATE_KEY, state);
          return {
            value: { ok: true, lease: publicLease(existing) },
            alarmAt: nextLeaseExpiry(state),
            syncAlarm: true,
          };
        }
        existing.admittedRoles.push(input.role);
        await txn.put(STATE_KEY, state);
        return {
          value: { ok: true, lease: publicLease(existing) },
          alarmAt: nextLeaseExpiry(state),
          syncAlarm: true,
        };
      }

      const tier = input.tier === 'legacy' ? config.legacy : config.authenticated;
      const monthBucket = utcMonthBucket(now);
      // global 月次枠は tier 共通。legacy の場合だけ追加 subset 枠も検査する。
      const globalBucket = getBucket(state.globalMonths, monthBucket);
      const legacyBucket = input.tier === 'legacy' ? getBucket(state.legacyMonths, monthBucket) : null;
      const reservation: MetricLimits = {
        bytes: tier.bytes,
        messages: tier.messages,
        durationSeconds: tier.durationSeconds,
      };
      let reject: QuotaReject['reason'] | null = null;
      if (state.activeRooms >= config.maxConcurrentRooms) {
        reject = 'active';
      } else if (globalBucket && bucketWouldExceed(globalBucket, reservation, config.monthly)) {
        reject = 'monthly';
      } else if (legacyBucket && bucketWouldExceed(legacyBucket, reservation, {
        bytes: config.legacyMonthly.bytes,
        messages: config.legacyMonthly.messages,
        durationSeconds: config.legacyMonthly.durationSeconds,
      })) {
        reject = 'legacy-monthly';
      }
      if (reject) {
        if (cleaned) await txn.put(STATE_KEY, state);
        return {
          value: { ok: false, reason: reject },
          alarmAt: nextLeaseExpiry(state),
          syncAlarm: cleaned,
        };
      }

      addReservation(globalBucket, reservation);
      if (legacyBucket) addReservation(legacyBucket, reservation);
      const record: LeaseRecord = {
        leaseId: crypto.randomUUID(),
        roomId: input.roomId,
        tier: input.tier,
        deviceId: input.deviceId,
        monthBucket,
        expiresAt: expiresAt(now, tier.sessionSeconds),
        maxBytes: tier.bytes,
        maxMessages: tier.messages,
        maxDurationSeconds: tier.durationSeconds,
        maxIdleMs: millisFromSeconds(tier.idleSeconds),
        maxFrameBytes: config.maxFrameBytes,
        admittedRoles: [input.role],
      };
      state.leases[input.roomId] = record;
      state.activeRooms += 1;
      await txn.put(STATE_KEY, state);
      return {
        value: { ok: true, lease: publicLease(record) },
        alarmAt: nextLeaseExpiry(state),
        syncAlarm: true,
      };
    });
  }

  private async settle(input: NormalizedSettleInput): Promise<boolean> {
    const now = Date.now();
    return transactionWithAlarm(this.state.storage, async (txn) => {
      const state = normalizeState(await txn.get<unknown>(STATE_KEY));
      // alarm 配送前に settle が来ても、期限を過ぎた lease は expiry 扱いにする。
      const cleaned = cleanState(state, now);
      const settled = state.settled[input.leaseId];
      if (settled) {
        if (cleaned) await txn.put(STATE_KEY, state);
        return {
          // 正常 settle の再送は成功扱いにするが、expiry 済み lease の後着 settle は stale。
          value: settled.kind === 'settled' && settled.roomId === input.roomId,
          alarmAt: nextLeaseExpiry(state),
          syncAlarm: cleaned,
        };
      }
      const record = state.leases[input.roomId];
      if (!record || record.leaseId !== input.leaseId) {
        if (cleaned) await txn.put(STATE_KEY, state);
        return { value: false, alarmAt: nextLeaseExpiry(state), syncAlarm: cleaned };
      }
      const actual: MetricLimits = {
        bytes: Math.min(record.maxBytes, input.actualBytes),
        messages: Math.min(record.maxMessages, input.actualMessages),
        durationSeconds: Math.min(record.maxDurationSeconds, input.actualDurationSeconds),
      };
      settleRecord(state, record, actual, now, 'settled');
      pruneSettledHistory(state, now);
      await txn.put(STATE_KEY, state);
      return { value: true, alarmAt: nextLeaseExpiry(state), syncAlarm: true };
    });
  }
}
