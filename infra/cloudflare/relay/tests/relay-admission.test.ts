/**
 * `/ferry-relay` の入口契約。
 *
 * 入口で検証した値だけが RelayDO に届くこと、optional では旧版を legacy tier に
 * 縮退できること、required では認証と D1 台帳を必須にすることを固定する。
 */
import { describe, expect, it, vi } from 'vitest';
import worker from '../src/index';
import { mintSessionToken } from '../src/auth';
import type { Env } from '../src/index';

const A = 'a'.repeat(32);
const B = 'b'.repeat(32);
const OUTSIDER = 'c'.repeat(32);
const PAIR_ID = `${A}_${B}`;
const SECRET = 'test-session-secret-0123456789';
const QUOTA_CONFIG = {
  RELAY_CIRCUIT_OPEN: '0',
  RELAY_MAX_CONCURRENT_ROOMS: '16',
  RELAY_MONTHLY_BYTES: '1000',
  RELAY_MONTHLY_MESSAGES: '1000',
  RELAY_MONTHLY_DURATION_SECONDS: '1000',
  RELAY_AUTH_SESSION_BYTES: '100',
  RELAY_AUTH_SESSION_MESSAGES: '100',
  RELAY_AUTH_SESSION_SECONDS: '60',
  RELAY_AUTH_IDLE_SECONDS: '30',
  RELAY_LEGACY_MONTHLY_BYTES: '100',
  RELAY_LEGACY_MONTHLY_MESSAGES: '100',
  RELAY_LEGACY_MONTHLY_DURATION_SECONDS: '100',
  RELAY_LEGACY_SESSION_BYTES: '10',
  RELAY_LEGACY_SESSION_MESSAGES: '10',
  RELAY_LEGACY_SESSION_SECONDS: '30',
  RELAY_LEGACY_IDLE_SECONDS: '10',
  RELAY_MAX_FRAME_BYTES: '64',
};

interface FakeD1 {
  prepare: ReturnType<typeof vi.fn>;
  registered: boolean;
}

function makeD1(registered = true): FakeD1 {
  const db: FakeD1 = {
    registered,
    prepare: vi.fn((sql: string) => ({
      bind: vi.fn((pairId: string) => ({
        first: vi.fn(async () => (db.registered && pairId === PAIR_ID ? { pair_id: pairId } : null)),
      })),
    })),
  };
  return db;
}

function makeEnv(extra: Partial<Env> = {}): {
  env: Env;
  idFromName: ReturnType<typeof vi.fn>;
  doFetch: ReturnType<typeof vi.fn>;
  db: FakeD1;
} {
  const doFetch = vi.fn(async (request: Request) => new Response(request.url, { status: 200 }));
  const idFromName = vi.fn((name: string) => ({ name }));
  const db = makeD1();
  const env = {
    ...QUOTA_CONFIG,
    SALT: 'test-salt',
    SESSION_HMAC_SECRET: SECRET,
    DB: db,
    RELAY: { idFromName, get: vi.fn(() => ({ fetch: doFetch })) },
    QUOTA: { idFromName: vi.fn(), get: vi.fn() },
    ...extra,
  } as unknown as Env;
  return { env, idFromName, doFetch, db };
}

function wsRequest(
  pairId = PAIR_ID,
  role = 'offer',
  headers: Record<string, string> = {},
  path = '/ferry-relay',
): Request {
  return new Request(`https://relay.test${path}?pairId=${encodeURIComponent(pairId)}&role=${encodeURIComponent(role)}`, {
    headers: {
      Upgrade: 'websocket',
      'CF-Connecting-IP': '1.2.3.4',
      ...headers,
    },
  });
}

const CTX = { waitUntil: () => undefined, passThroughOnException: () => undefined } as unknown as ExecutionContext;

async function tokenFor(deviceId: string, env: Env): Promise<string> {
  return mintSessionToken(deviceId, 3600, env);
}

describe('リレー入室の入口形式・rate limit', () => {
  it('正規形式と role は内部ヘッダーへ固定して RelayDO へ forward する', async () => {
    const { env, idFromName, doFetch } = makeEnv();
    const req = wsRequest(PAIR_ID, 'answer', {
      ["X-Ferry-Role"]: 'evil',
      ["X-Ferry-Device"]: OUTSIDER,
      ["X-Ferry-Tier"]: 'authenticated',
      ["X-Ferry-Room"]: `${B}_${A}`,
    });

    const res = await worker.fetch(req, env, CTX);

    expect(res.status).toBe(200);
    expect(idFromName).toHaveBeenCalledTimes(1);
    expect(doFetch).toHaveBeenCalledTimes(1);
    const forwarded = doFetch.mock.calls[0][0] as Request;
    expect(forwarded.headers.get('X-Ferry-Role')).toBe('answer');
    expect(forwarded.headers.get('X-Ferry-Device')).toBe('');
    expect(forwarded.headers.get('X-Ferry-Tier')).toBe('legacy');
    expect(forwarded.headers.get('X-Ferry-Room')).toBe(PAIR_ID);
    expect(forwarded.url).not.toContain('__role');
  });

  it.each([
    ['非 hex 文字', `${'z'.repeat(32)}_${B}`],
    ['桁数不足', `${'a'.repeat(31)}_${B}`],
    ['区切り無し', `${A}${B}`],
    ['大文字', `${'A'.repeat(32)}_${B}`],
    ['逆順の pairId', `${B}_${A}`],
    ['同じ device の pairId', `${A}_${A}`],
    ['任意文字列', 'room-1'],
  ])('%s の pairId は 400 で RelayDO を起こさない', async (_label, pairId) => {
    const { env, idFromName, doFetch } = makeEnv();

    const res = await worker.fetch(wsRequest(pairId), env, CTX);

    expect(res.status).toBe(400);
    expect(idFromName).not.toHaveBeenCalled();
    expect(doFetch).not.toHaveBeenCalled();
  });

  it.each([
    ['role 欠落', ''],
    ['role 不正', 'unknown'],
  ])('%s は 400 で RelayDO を起こさない', async (_label, role) => {
    const { env, doFetch } = makeEnv();
    const request = role === ''
      ? new Request(`https://relay.test/ferry-relay?pairId=${PAIR_ID}`, { headers: { Upgrade: 'websocket' } })
      : wsRequest(PAIR_ID, role);

    const res = await worker.fetch(request, env, CTX);

    expect(res.status).toBe(400);
    expect(doFetch).not.toHaveBeenCalled();
  });

  it('WebSocket 以外は 426、未知 path は 404', async () => {
    const { env, doFetch } = makeEnv();
    const notWebSocket = new Request(`https://relay.test/ferry-relay?pairId=${PAIR_ID}&role=offer`);
    expect((await worker.fetch(notWebSocket, env, CTX)).status).toBe(426);
    expect((await worker.fetch(wsRequest(PAIR_ID, 'offer', {}, '/ferry-relay/extra'), env, CTX)).status).toBe(404);
    expect(doFetch).not.toHaveBeenCalled();
  });

  it('RATELIMIT_RELAY は専用 IP 枠を一度だけ消費し、枯渇時は DO へ到達しない', async () => {
    const relayLimit = vi.fn(async () => ({ success: true }));
    const otherIpLimit = vi.fn(async () => ({ success: true }));
    const { env, doFetch } = makeEnv({
      RATELIMIT_RELAY: { limit: relayLimit },
      RATELIMIT_IP: { limit: otherIpLimit },
    } as unknown as Partial<Env>);

    expect((await worker.fetch(wsRequest(PAIR_ID), env, CTX)).status).toBe(200);
    expect(relayLimit).toHaveBeenCalledWith({ key: '1.2.3.4' });
    expect(otherIpLimit).not.toHaveBeenCalled();
    expect(doFetch).toHaveBeenCalledTimes(1);

    const rejected = makeEnv({
      RATELIMIT_RELAY: { limit: vi.fn(async () => ({ success: false })) },
    } as unknown as Partial<Env>);
    expect((await worker.fetch(wsRequest(PAIR_ID), rejected.env, CTX)).status).toBe(429);
    expect(rejected.idFromName).not.toHaveBeenCalled();
  });

  it.each([
    ['breaker', { RELAY_CIRCUIT_OPEN: '1' }],
    ['quota 設定不備', { RELAY_MAX_FRAME_BYTES: 'invalid' }],
  ])('%s は rate limit・認証・D1 より前に 503 で遮断する', async (_label, extra) => {
    const relayLimit = vi.fn(async () => ({ success: true }));
    const { env, idFromName, doFetch, db } = makeEnv({
      ...extra,
      RATELIMIT_RELAY: { limit: relayLimit },
    } as unknown as Partial<Env>);

    const response = await worker.fetch(wsRequest(), env, CTX);

    expect(response.status).toBe(503);
    expect(relayLimit).not.toHaveBeenCalled();
    expect(db.prepare).not.toHaveBeenCalled();
    expect(idFromName).not.toHaveBeenCalled();
    expect(doFetch).not.toHaveBeenCalled();
  });
});

describe('リレー入室の Bearer・D1 認証', () => {
  it('optional で Bearer 無しは legacy tier として互換接続する', async () => {
    const { env, doFetch } = makeEnv();

    expect((await worker.fetch(wsRequest(), env, CTX)).status).toBe(200);
    const forwarded = doFetch.mock.calls[0][0] as Request;
    expect(forwarded.headers.get('X-Ferry-Tier')).toBe('legacy');
    expect(forwarded.headers.get('X-Ferry-Device')).toBe('');
  });

  it('Bearer が存在する場合、optional でも無効 token は 401 で legacy に縮退しない', async () => {
    const { env, doFetch } = makeEnv();

    const res = await worker.fetch(wsRequest(PAIR_ID, 'offer', { Authorization: 'Bearer invalid.token' }), env, CTX);

    expect(res.status).toBe(401);
    expect(doFetch).not.toHaveBeenCalled();
  });

  it('Bearer claims が pair 当事者でない場合は 403', async () => {
    const { env, doFetch } = makeEnv();
    const token = await tokenFor(OUTSIDER, env);

    const res = await worker.fetch(wsRequest(PAIR_ID, 'offer', { Authorization: `Bearer ${token}` }), env, CTX);

    expect(res.status).toBe(403);
    expect(doFetch).not.toHaveBeenCalled();
  });

  it('Bearer 当事者かつ D1 pairs 行ありなら authenticated context を渡す', async () => {
    const { env, doFetch, db } = makeEnv();
    const token = await tokenFor(A, env);

    const res = await worker.fetch(wsRequest(PAIR_ID, 'offer', { Authorization: `Bearer ${token}` }), env, CTX);

    expect(res.status).toBe(200);
    expect(db.prepare).toHaveBeenCalledWith('SELECT pair_id FROM pairs WHERE pair_id = ? LIMIT 1');
    const forwarded = doFetch.mock.calls[0][0] as Request;
    expect(forwarded.headers.get('X-Ferry-Device')).toBe(A);
    expect(forwarded.headers.get('X-Ferry-Tier')).toBe('authenticated');
    expect(forwarded.headers.get('X-Ferry-Room')).toBe(PAIR_ID);
  });

  it('Bearer 当事者でも D1 行なしは optional なら legacy に縮退し device は保持する', async () => {
    const { env, doFetch, db } = makeEnv();
    db.registered = false;
    const token = await tokenFor(A, env);

    const res = await worker.fetch(wsRequest(PAIR_ID, 'offer', { Authorization: `Bearer ${token}` }), env, CTX);

    expect(res.status).toBe(200);
    const forwarded = doFetch.mock.calls[0][0] as Request;
    expect(forwarded.headers.get('X-Ferry-Tier')).toBe('legacy');
    expect(forwarded.headers.get('X-Ferry-Device')).toBe(A);
  });

  it('required は Bearer 無しを 401、D1 行なしを 404 とする', async () => {
    const noToken = makeEnv({ RELAY_AUTH_MODE: 'required' } as unknown as Partial<Env>);
    expect((await worker.fetch(wsRequest(), noToken.env, CTX)).status).toBe(401);

    const missing = makeEnv({ RELAY_AUTH_MODE: 'required', PAIR_LEDGER_MODE: 'required' } as unknown as Partial<Env>);
    missing.db.registered = false;
    const token = await tokenFor(A, missing.env);
    expect((await worker.fetch(wsRequest(PAIR_ID, 'offer', { Authorization: `Bearer ${token}` }), missing.env, CTX)).status).toBe(404);
    expect(missing.doFetch).not.toHaveBeenCalled();
  });

  it('PAIR_LEDGER_MODE は RELAY_AUTH_MODE と独立して D1 欠落時の縮退を決める', async () => {
    const transition = makeEnv({ RELAY_AUTH_MODE: 'required', PAIR_LEDGER_MODE: 'transition' } as unknown as Partial<Env>);
    transition.db.registered = false;
    const transitionToken = await tokenFor(A, transition.env);
    expect((await worker.fetch(
      wsRequest(PAIR_ID, 'offer', { Authorization: `Bearer ${transitionToken}` }),
      transition.env,
      CTX,
    )).status).toBe(200);
    expect((transition.doFetch.mock.calls[0][0] as Request).headers.get('X-Ferry-Tier')).toBe('legacy');

    const ledgerRequired = makeEnv({ RELAY_AUTH_MODE: 'optional', PAIR_LEDGER_MODE: 'required' } as unknown as Partial<Env>);
    ledgerRequired.db.registered = false;
    const requiredToken = await tokenFor(A, ledgerRequired.env);
    expect((await worker.fetch(
      wsRequest(PAIR_ID, 'offer', { Authorization: `Bearer ${requiredToken}` }),
      ledgerRequired.env,
      CTX,
    )).status).toBe(404);
  });

  it('RELAY_AUTH_MODE の不正値は fail closed で 500', async () => {
    const { env, idFromName } = makeEnv({ RELAY_AUTH_MODE: 'sometimes' } as unknown as Partial<Env>);

    const res = await worker.fetch(wsRequest(), env, CTX);

    expect(res.status).toBe(500);
    expect(idFromName).not.toHaveBeenCalled();
  });
});
