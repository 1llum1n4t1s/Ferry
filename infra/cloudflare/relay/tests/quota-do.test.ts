import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  RelayQuotaDO,
  reserveRelayQuota,
  settleRelayQuota,
  validateRelayQuotaConfig,
} from '../src/quota-do';

const BASE_CONFIG = {
  RELAY_CIRCUIT_OPEN: '0',
  RELAY_MAX_CONCURRENT_ROOMS: '2',
  RELAY_MONTHLY_BYTES: '1000',
  RELAY_MONTHLY_MESSAGES: '100',
  RELAY_MONTHLY_DURATION_SECONDS: '1000',
  RELAY_AUTH_SESSION_BYTES: '100',
  RELAY_AUTH_SESSION_MESSAGES: '10',
  RELAY_AUTH_SESSION_SECONDS: '60',
  RELAY_AUTH_IDLE_SECONDS: '15',
  RELAY_LEGACY_MONTHLY_BYTES: '500',
  RELAY_LEGACY_MONTHLY_MESSAGES: '50',
  RELAY_LEGACY_MONTHLY_DURATION_SECONDS: '500',
  RELAY_LEGACY_SESSION_BYTES: '50',
  RELAY_LEGACY_SESSION_MESSAGES: '5',
  RELAY_LEGACY_SESSION_SECONDS: '30',
  RELAY_LEGACY_IDLE_SECONDS: '10',
  RELAY_MAX_FRAME_BYTES: '64',
};

interface FakeTransaction {
  get<T = unknown>(key: string): Promise<T | undefined>;
  put<T>(key: string, value: T): Promise<void>;
  delete(key: string): Promise<boolean>;
  getAlarm(): Promise<number | null>;
  setAlarm(time: number | Date): Promise<void>;
  deleteAlarm(): Promise<void>;
}

class FakeStorage {
  private readonly values = new Map<string, unknown>();
  private queue: Promise<void> = Promise.resolve();
  alarmAt: number | null = null;

  async get<T = unknown>(key: string): Promise<T | undefined> {
    return this.values.get(key) as T | undefined;
  }

  async put<T>(key: string, value: T): Promise<void> {
    this.values.set(key, value);
  }

  async delete(key: string): Promise<boolean> {
    return this.values.delete(key);
  }

  async setAlarm(time: number | Date): Promise<void> {
    this.alarmAt = time instanceof Date ? time.getTime() : time;
  }

  async deleteAlarm(): Promise<void> {
    this.alarmAt = null;
  }

  async transaction<T>(closure: (txn: FakeTransaction) => Promise<T>): Promise<T> {
    const previous = this.queue;
    let release: () => void = () => undefined;
    this.queue = new Promise<void>((resolve) => {
      release = resolve;
    });
    await previous;
    const txn: FakeTransaction = {
      get: async <V>(key: string) => this.get<V>(key),
      put: async <V>(key: string, value: V) => this.put(key, value),
      delete: (key: string) => this.delete(key),
      getAlarm: async () => this.alarmAt,
      setAlarm: async (time: number | Date) => this.setAlarm(time),
      deleteAlarm: async () => this.deleteAlarm(),
    };
    try {
      return await closure(txn);
    } finally {
      release();
    }
  }
}

interface TestState {
  activeRooms: number;
  globalMonths: Record<string, {
    reservedBytes: number;
    usedBytes: number;
    reservedMessages: number;
    usedMessages: number;
    reservedDurationSeconds: number;
    usedDurationSeconds: number;
  }>;
  legacyMonths: Record<string, {
    reservedBytes: number;
    usedBytes: number;
    reservedMessages: number;
    usedMessages: number;
    reservedDurationSeconds: number;
    usedDurationSeconds: number;
  }>;
  settled: Record<string, unknown>;
}

function makeFixture(overrides: Record<string, string> = {}): {
  env: Record<string, unknown>;
  object: RelayQuotaDO;
  storage: FakeStorage;
  idFromName: ReturnType<typeof vi.fn>;
  doFetch: ReturnType<typeof vi.fn>;
} {
  const storage = new FakeStorage();
  const idFromName = vi.fn((name: string) => ({ name }));
  let object: RelayQuotaDO;
  const doFetch = vi.fn((request: Request) => object.fetch(request));
  const env: Record<string, unknown> = {
    ...BASE_CONFIG,
    ...overrides,
    QUOTA: {
      idFromName,
      get: vi.fn(() => ({ fetch: doFetch })),
    },
  };
  object = new RelayQuotaDO({ storage } as never, env);
  return { env, object, storage, idFromName, doFetch };
}

async function reserve(object: RelayQuotaDO, body: object): Promise<Response> {
  return object.fetch(new Request('https://quota.test/reserve', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ role: 'offer', ...body }),
  }));
}

async function settle(object: RelayQuotaDO, body: object): Promise<Response> {
  return object.fetch(new Request('https://quota.test/settle', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  }));
}

async function stateOf(storage: FakeStorage): Promise<TestState> {
  const state = await storage.get<TestState>('relay-quota-state-v1');
  if (!state) throw new Error('quota state was not persisted');
  return state;
}

afterEach(() => {
  vi.useRealTimers();
});

describe('RelayQuotaDO', () => {
  it('global singleton 経由で lease を発行し、settle で予約を利用量へ移す', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-29T00:00:00Z'));
    const fixture = makeFixture();

    const result = await reserveRelayQuota(fixture.env, {
      roomId: 'room-a',
      tier: 'authenticated',
      deviceId: 'device-a',
      role: 'offer',
    });

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.lease).toMatchObject({
      roomId: 'room-a',
      tier: 'authenticated',
      maxBytes: 100,
      maxMessages: 10,
      maxIdleMs: 15_000,
      maxFrameBytes: 64,
    });
    expect(fixture.idFromName).toHaveBeenCalledWith('global');
    expect(await settleRelayQuota(fixture.env, {
      roomId: 'room-a',
      leaseId: result.lease.leaseId,
      actualBytes: 40,
      actualMessages: 4,
      actualDurationSeconds: 8,
    })).toBe(true);

    const state = await stateOf(fixture.storage);
    const month = state.globalMonths['2026-08'];
    expect(month).toMatchObject({
      reservedBytes: 0,
      usedBytes: 40,
      reservedMessages: 0,
      usedMessages: 4,
      reservedDurationSeconds: 0,
      usedDurationSeconds: 8,
    });
    expect(state.activeRooms).toBe(0);
  });

  it('同じ room は offer/answer を一度ずつ同じ lease へ入れ、再入室と同時 quota 超過を防ぐ', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-29T00:00:00Z'));
    const fixture = makeFixture({ RELAY_MAX_CONCURRENT_ROOMS: '1' });

    const first = await reserve(fixture.object, { roomId: 'same-room', tier: 'legacy', deviceId: 'device-a' });
    const second = await reserve(fixture.object, {
      roomId: 'same-room', tier: 'legacy', deviceId: 'device-b', role: 'answer',
    });
    const firstLease = (await first.json() as { lease: { leaseId: string } }).lease;
    const secondLease = (await second.json() as { lease: { leaseId: string } }).lease;
    expect(first.status).toBe(200);
    expect(second.status).toBe(200);
    expect(secondLease.leaseId).toBe(firstLease.leaseId);
    const repeated = await reserve(fixture.object, {
      roomId: 'same-room', tier: 'legacy', deviceId: 'device-a', role: 'offer',
    });
    expect(repeated.status).toBe(409);
    expect(await repeated.json()).toMatchObject({ error: 'LEASE_ROLE_ALREADY_ADMITTED' });

    const competing = makeFixture({ RELAY_MAX_CONCURRENT_ROOMS: '1' });
    const [left, right] = await Promise.all([
      reserve(competing.object, { roomId: 'room-left', tier: 'legacy', deviceId: 'device-b' }),
      reserve(competing.object, { roomId: 'room-right', tier: 'legacy', deviceId: 'device-c' }),
    ]);
    expect([left.status, right.status].filter((status) => status === 200)).toHaveLength(1);
    expect([left.status, right.status].filter((status) => status === 429)).toHaveLength(1);
    expect((await stateOf(competing.storage)).activeRooms).toBe(1);
  });

  it('authenticated 先着へ legacy が合流したら同じ lease を legacy 小枠へ降格する', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-29T00:00:00Z'));
    const fixture = makeFixture();

    const authenticated = await reserve(fixture.object, {
      roomId: 'mixed-room',
      tier: 'authenticated',
      deviceId: 'device-a',
    });
    const authenticatedLease = (await authenticated.json() as { lease: { leaseId: string } }).lease;
    const legacy = await reserve(fixture.object, {
      roomId: 'mixed-room',
      tier: 'legacy',
      deviceId: 'legacy:mixed-room:answer',
      role: 'answer',
    });
    const legacyLease = (await legacy.json() as {
      lease: { leaseId: string; tier: string; maxBytes: number; maxMessages: number; maxIdleMs: number };
    }).lease;

    expect(legacy.status).toBe(200);
    expect(legacyLease).toMatchObject({
      leaseId: authenticatedLease.leaseId,
      tier: 'legacy',
      maxBytes: 50,
      maxMessages: 5,
      maxIdleMs: 10_000,
    });
    const state = await stateOf(fixture.storage);
    expect(state.activeRooms).toBe(1);
    expect(state.globalMonths['2026-08']).toMatchObject({ reservedBytes: 50, reservedMessages: 5 });
    expect(state.legacyMonths['2026-08']).toMatchObject({ reservedBytes: 50, reservedMessages: 5 });
  });

  it('global 月次枠は tier 共通で、legacy は追加 subset 枠も消費する', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-29T00:00:00Z'));
    const fixture = makeFixture({
      RELAY_MAX_CONCURRENT_ROOMS: '3',
      RELAY_MONTHLY_BYTES: '100',
    });

    expect((await reserve(fixture.object, {
      roomId: 'authenticated-room',
      tier: 'authenticated',
      deviceId: 'device-a',
    })).status).toBe(200);
    const legacy = await reserve(fixture.object, {
      roomId: 'legacy-room',
      tier: 'legacy',
      deviceId: 'device-b',
    });
    expect(legacy.status).toBe(429);
    expect((await stateOf(fixture.storage)).globalMonths['2026-08']).toMatchObject({
      reservedBytes: 100,
      usedBytes: 0,
    });
  });

  it('stale lease を拒否し、actual を予約上限へ clamp して再 settle を無害に処理する', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-29T00:00:00Z'));
    const fixture = makeFixture();
    const response = await reserve(fixture.object, { roomId: 'room-a', tier: 'authenticated', deviceId: 'device-a' });
    const lease = (await response.json() as { lease: { leaseId: string } }).lease;

    expect((await settle(fixture.object, {
      roomId: 'room-a',
      leaseId: 'stale-lease',
      actualBytes: 1,
    })).status).toBe(404);
    expect((await stateOf(fixture.storage)).activeRooms).toBe(1);

    const body = {
      roomId: 'room-a',
      leaseId: lease.leaseId,
      actualBytes: 999,
      actualMessages: 999,
      actualDurationSeconds: 999,
    };
    expect((await settle(fixture.object, body)).status).toBe(200);
    expect((await settle(fixture.object, body)).status).toBe(200);
    const month = (await stateOf(fixture.storage)).globalMonths['2026-08'];
    expect(month).toMatchObject({ usedBytes: 100, usedMessages: 10, usedDurationSeconds: 60 });
  });

  it('expiry alarm は未確定利用を予約全量として確定する', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-29T00:00:00Z'));
    const fixture = makeFixture({ RELAY_LEGACY_SESSION_SECONDS: '1' });
    const response = await reserve(fixture.object, { roomId: 'legacy-room', tier: 'legacy', deviceId: 'device-a' });
    expect(response.status).toBe(200);
    expect(fixture.storage.alarmAt).toBe(new Date('2026-08-29T00:00:01Z').getTime());

    vi.setSystemTime(new Date('2026-08-29T00:00:02Z'));
    await fixture.object.alarm();
    const state = await stateOf(fixture.storage);
    expect(state.activeRooms).toBe(0);
    expect(state.globalMonths['2026-08']).toMatchObject({ reservedBytes: 0, usedBytes: 50 });
    expect(state.legacyMonths['2026-08']).toMatchObject({ reservedBytes: 0, usedBytes: 50 });
    expect(fixture.storage.alarmAt).toBeNull();
  });

  it('UTC 月が変わると新しい月bucketで quota を判定する', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-31T23:59:59Z'));
    const fixture = makeFixture({ RELAY_MONTHLY_BYTES: '100' });
    const oldMonth = await reserve(fixture.object, { roomId: 'old-room', tier: 'authenticated', deviceId: 'device-a' });
    const oldLease = (await oldMonth.json() as { lease: { leaseId: string } }).lease;
    await settle(fixture.object, {
      roomId: 'old-room',
      leaseId: oldLease.leaseId,
      actualBytes: 100,
      actualMessages: 10,
      actualDurationSeconds: 60,
    });

    vi.setSystemTime(new Date('2026-09-01T00:00:00Z'));
    const newMonth = await reserve(fixture.object, { roomId: 'new-room', tier: 'authenticated', deviceId: 'device-b' });
    expect(newMonth.status).toBe(200);
    expect((await stateOf(fixture.storage)).globalMonths['2026-09']).toMatchObject({ reservedBytes: 100 });
  });

  it('lease の期限は session timeout と UTC 次月境界の早い方になる', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-08-31T23:59:59Z'));
    const fixture = makeFixture({ RELAY_AUTH_SESSION_SECONDS: '60' });
    const response = await reserve(fixture.object, {
      roomId: 'month-end-room',
      tier: 'authenticated',
      deviceId: 'device-a',
    });
    const body = await response.json() as { lease: { expiresAt: number } };
    const boundary = new Date('2026-09-01T00:00:00Z').getTime();
    expect(response.status).toBe(200);
    expect(body.lease.expiresAt).toBe(boundary);
    expect(fixture.storage.alarmAt).toBe(boundary);

    // 月替わり後の最初の操作で旧月 lease を expiry 扱いにしてから新月を予約する。
    vi.setSystemTime(new Date('2026-09-01T00:00:00Z'));
    expect((await reserve(fixture.object, {
      roomId: 'next-month-room',
      tier: 'authenticated',
      deviceId: 'device-b',
    })).status).toBe(200);
    const state = await stateOf(fixture.storage);
    expect(state.globalMonths['2026-08']).toMatchObject({ reservedBytes: 0, usedBytes: 100 });
    expect(state.globalMonths['2026-09']).toMatchObject({ reservedBytes: 100, usedBytes: 0 });
  });

  it('settled idempotency history は年齢と個数で prune される', async () => {
    vi.useFakeTimers();
    const start = new Date('2026-08-29T00:00:00Z');
    vi.setSystemTime(start);
    const fixture = makeFixture({ RELAY_MAX_CONCURRENT_ROOMS: '1' });

    for (let index = 0; index < 520; index += 1) {
      const roomId = `history-room-${index}`;
      const response = await reserve(fixture.object, {
        roomId,
        tier: 'authenticated',
        deviceId: `device-${index}`,
      });
      const body = await response.json() as { lease: { leaseId: string } };
      expect(await settle(fixture.object, {
        roomId,
        leaseId: body.lease.leaseId,
        actualBytes: 0,
        actualMessages: 0,
        actualDurationSeconds: 0,
      })).toHaveProperty('status', 200);
    }
    const bounded = await stateOf(fixture.storage);
    expect(Object.keys(bounded.settled ?? {})).toHaveLength(512);

    vi.setSystemTime(new Date(start.getTime() + 8 * 24 * 60 * 60 * 1000));
    await reserve(fixture.object, {
      roomId: 'history-after-retention',
      tier: 'authenticated',
      deviceId: 'device-after-retention',
    });
    const pruned = await stateOf(fixture.storage);
    expect(Object.keys(pruned.settled ?? {})).toHaveLength(0);
  });

  it('設定不備は fail-closed 503、breaker は reserve を 503 にする', async () => {
    const invalid = makeFixture({ RELAY_MAX_FRAME_BYTES: 'not-a-number' });
    expect(validateRelayQuotaConfig(invalid.env)).toEqual(expect.arrayContaining([
      expect.stringContaining('RELAY_MAX_FRAME_BYTES'),
    ]));
    const invalidResult = await reserveRelayQuota(invalid.env, {
      roomId: 'room-a',
      tier: 'authenticated',
      deviceId: 'device-a',
      role: 'offer',
    });
    expect(invalidResult.ok).toBe(false);
    if (!invalidResult.ok) expect(invalidResult.response.status).toBe(503);

    const breaker = makeFixture({ RELAY_CIRCUIT_OPEN: '1' });
    const breakerResult = await reserveRelayQuota(breaker.env, {
      roomId: 'room-a',
      tier: 'authenticated',
      deviceId: 'device-a',
      role: 'offer',
    });
    expect(breakerResult.ok).toBe(false);
    if (!breakerResult.ok) expect(breakerResult.response.status).toBe(503);
    expect(breaker.doFetch).not.toHaveBeenCalled();
  });
});
