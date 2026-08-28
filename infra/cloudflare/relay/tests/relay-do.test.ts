/**
 * RelayDO の protocol / quota 境界テスト。
 * Cloudflare の実 DO runtime ではなく、Hibernation API の観測面を再現する
 * 小さな fake state と fake WebSocketPair を使う。
 */
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest';

const quota = vi.hoisted(() => ({
  reserve: vi.fn(),
  settle: vi.fn(),
}));

vi.mock('../src/quota-do', () => ({
  reserveRelayQuota: quota.reserve,
  settleRelayQuota: quota.settle,
  validateRelayQuotaConfig: vi.fn(() => []),
  RelayQuotaDO: class RelayQuotaDO {},
}));

import { RelayDO } from '../src/index';

const ROOM = `${'a'.repeat(32)}_${'b'.repeat(32)}`;
const DEVICE_A = 'a'.repeat(32);
const DEVICE_B = 'b'.repeat(32);

class FakeWebSocket {
  readyState = 1;
  sent: ArrayBuffer[] | string[] = [];
  closeCalls: Array<{ code?: number; reason?: string }> = [];
  attachment: unknown = null;
  serializeError = false;
  sendError = false;
  tags: string[] = [];

  send(message: ArrayBuffer | string): void {
    if (this.sendError) throw new Error('send failed');
    this.sent.push(message);
  }

  close(code?: number, reason?: string): void {
    this.closeCalls.push({ code, reason });
    this.readyState = 3;
  }

  serializeAttachment(value: unknown): void {
    if (this.serializeError) throw new Error('serialize failed');
    this.attachment = structuredClone(value);
  }

  deserializeAttachment(): unknown {
    return this.attachment === null ? null : structuredClone(this.attachment);
  }
}

class FakeWebSocketPair {
  [index: number]: FakeWebSocket;

  constructor() {
    this[0] = new FakeWebSocket();
    this[1] = new FakeWebSocket();
  }
}

class FakeStorage {
  alarmAt: number | null = null;
  setAlarmCalls: number[] = [];
  setAlarmError = false;

  async getAlarm(): Promise<number | null> {
    return this.alarmAt;
  }

  async setAlarm(value: number | Date): Promise<void> {
    if (this.setAlarmError) throw new Error('alarm failed');
    const at = value instanceof Date ? value.getTime() : value;
    this.alarmAt = at;
    this.setAlarmCalls.push(at);
  }

  async deleteAlarm(): Promise<void> {
    this.alarmAt = null;
  }
}

class FakeState {
  readonly id = { toString: () => 'room-test-id' };
  readonly storage = new FakeStorage();
  readonly sockets: FakeWebSocket[] = [];
  acceptCalls = 0;

  acceptWebSocket(socket: FakeWebSocket, tags?: string[]): void {
    this.acceptCalls += 1;
    socket.tags = tags ?? [];
    this.sockets.push(socket);
  }

  getWebSockets(): FakeWebSocket[] {
    return this.sockets;
  }
}

function lease(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    leaseId: 'lease-1',
    roomId: ROOM,
    tier: 'authenticated',
    expiresAt: Date.now() + 60_000,
    maxBytes: 100,
    maxMessages: 10,
    maxIdleMs: 30_000,
    maxFrameBytes: 32,
    ...overrides,
  };
}

function relayRequest(role: 'offer' | 'answer', deviceId: string, tier: 'authenticated' | 'legacy' = 'authenticated'): Request {
  return new Request(`https://do.internal/ferry-relay?pairId=${ROOM}&role=${role}`, {
    headers: {
      'X-Ferry-Role': role,
      'X-Ferry-Device': deviceId,
      'X-Ferry-Tier': tier,
      'X-Ferry-Room': ROOM,
    },
  });
}

function fixture(): { object: RelayDO; state: FakeState; env: Record<string, unknown> } {
  const state = new FakeState();
  const env = { RELAY_CIRCUIT_OPEN: '0' };
  const object = new RelayDO(state as never, env as never);
  return { object, state, env };
}

async function join(
  object: RelayDO,
  role: 'offer' | 'answer',
  deviceId: string,
  tier: 'authenticated' | 'legacy' = 'authenticated',
  leaseOverrides: Record<string, unknown> = {},
): Promise<Response> {
  quota.reserve.mockResolvedValueOnce({ ok: true, lease: lease(leaseOverrides) });
  return object.fetch(relayRequest(role, deviceId, tier));
}

beforeEach(() => {
  vi.stubGlobal('WebSocket', { OPEN: 1, CONNECTING: 0, CLOSING: 2, CLOSED: 3 });
  vi.stubGlobal('WebSocketPair', FakeWebSocketPair);
  quota.reserve.mockReset();
  quota.settle.mockReset();
  quota.settle.mockResolvedValue(true);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RelayDO 入室', () => {
  it('quota reserve 拒否時は acceptWebSocket せず response を返す', async () => {
    const { object, state } = fixture();
    quota.reserve.mockResolvedValue({ ok: false, response: new Response('quota', { status: 429 }) });

    const response = await object.fetch(relayRequest('offer', DEVICE_A));

    expect(response.status).toBe(429);
    expect(state.acceptCalls).toBe(0);
    expect(state.sockets).toHaveLength(0);
  });

  it('2 role を受け付けたときだけ ready を双方へ送る', async () => {
    const { object, state } = fixture();

    expect((await join(object, 'offer', DEVICE_A)).status).toBe(101);
    expect(state.sockets[0].sent).toEqual([]);
    expect((await join(object, 'answer', DEVICE_B)).status).toBe(101);

    expect(state.sockets[0].sent).toEqual(['ready']);
    expect(state.sockets[1].sent).toEqual(['ready']);
  });

  it('同一 role と authenticated device の重複を 409 で拒否する', async () => {
    const { object, state } = fixture();

    expect((await join(object, 'offer', DEVICE_A)).status).toBe(101);
    expect((await join(object, 'offer', DEVICE_B)).status).toBe(409);
    expect((await join(object, 'answer', DEVICE_A)).status).toBe(409);
    expect(state.acceptCalls).toBe(1);
    expect(quota.reserve).toHaveBeenCalledTimes(1);
  });

  it('quota reserve 待機中の同一 role 同時入室を直列化して一方だけ受け付ける', async () => {
    const { object, state } = fixture();
    let releaseQuota: (value: { ok: true; lease: Record<string, unknown> }) => void = () => undefined;
    quota.reserve.mockImplementationOnce(() => new Promise((resolve) => {
      releaseQuota = resolve;
    }));

    const first = object.fetch(relayRequest('offer', DEVICE_A));
    await vi.waitFor(() => expect(quota.reserve).toHaveBeenCalledTimes(1));
    const second = object.fetch(relayRequest('offer', DEVICE_B));
    await Promise.resolve();
    expect(quota.reserve).toHaveBeenCalledTimes(1);

    releaseQuota({ ok: true, lease: lease() });
    expect((await first).status).toBe(101);
    expect((await second).status).toBe(409);
    expect(state.acceptCalls).toBe(1);
    expect(quota.reserve).toHaveBeenCalledTimes(1);
  });

  it('quota reserve 待機中に旧 room が close/settle された lease は accept しない', async () => {
    const { object, state } = fixture();
    expect((await join(object, 'offer', DEVICE_A)).status).toBe(101);

    let releaseReserve: (value: { ok: true; lease: Record<string, unknown> }) => void = () => undefined;
    quota.reserve.mockImplementationOnce(() => new Promise((resolve) => {
      releaseReserve = resolve;
    }));
    let releaseSettle: (value: boolean) => void = () => undefined;
    quota.settle.mockImplementationOnce(() => new Promise((resolve) => {
      releaseSettle = resolve;
    }));

    const pendingAdmission = object.fetch(relayRequest('answer', DEVICE_B));
    await vi.waitFor(() => expect(quota.reserve).toHaveBeenCalledTimes(2));
    const closing = object.webSocketClose(state.sockets[0] as never, 1000, '', true);
    await vi.waitFor(() => expect(quota.settle).toHaveBeenCalledTimes(1));

    releaseReserve({ ok: true, lease: lease() });
    await Promise.resolve();
    expect(state.acceptCalls).toBe(1);
    releaseSettle(true);
    await closing;

    expect((await pendingAdmission).status).toBe(409);
    expect(state.acceptCalls).toBe(1);
    expect(quota.settle).toHaveBeenCalledTimes(1);
  });

  it('mixed authenticated/legacy peer は同じ idempotent lease で接続し lease limits を使う', async () => {
    const { object, state } = fixture();

    expect((await join(object, 'offer', DEVICE_A, 'authenticated')).status).toBe(101);
    expect((await join(object, 'answer', '', 'legacy')).status).toBe(101);
    expect(state.sockets[1].attachment).toMatchObject({ tier: 'legacy', leaseId: 'lease-1' });
    expect(quota.reserve.mock.calls[1][1]).toMatchObject({ tier: 'legacy' });
  });

  it('alarm の初回設定に失敗した接続は閉じて reservation を settle する', async () => {
    const { object, state } = fixture();
    state.storage.setAlarmError = true;

    const response = await join(object, 'offer', DEVICE_A);

    expect(response.status).toBe(503);
    expect(state.sockets[0].closeCalls[0]).toMatchObject({ code: 1011 });
    expect(quota.settle).toHaveBeenCalledTimes(1);
  });
});

describe('RelayDO protocol / quota', () => {
  it('2本揃う前の frame と text は protocol close し転送しない', async () => {
    const { object, state } = fixture();
    expect((await join(object, 'offer', DEVICE_A)).status).toBe(101);

    await object.webSocketMessage(state.sockets[0] as never, new ArrayBuffer(1));

    expect(state.sockets[0].closeCalls[0]).toMatchObject({ code: 1002 });
    expect(quota.settle).toHaveBeenCalledTimes(1);
    expect(state.sockets[0].sent).toEqual([]);
  });

  it('binary frame を転送し attachment counters を保存する', async () => {
    const { object, state } = fixture();
    await join(object, 'offer', DEVICE_A);
    await join(object, 'answer', DEVICE_B);
    const frame = new ArrayBuffer(10);

    await object.webSocketMessage(state.sockets[0] as never, frame);

    expect(state.sockets[1].sent).toEqual(['ready', frame]);
    expect(state.sockets[0].attachment).toMatchObject({ bytes: 10, messages: 1 });
    expect(state.sockets[1].attachment).toMatchObject({
      bytes: 10,
      messages: 1,
      lastActivity: (state.sockets[0].attachment as { lastActivity: number }).lastActivity,
    });
    // chunk ごとに alarm を更新せず、入室時に設定した 1 件を維持する。
    expect(state.storage.setAlarmCalls).toHaveLength(1);
  });

  it('frame 上限と room 合算 bytes/messages 上限を転送前に拒否する', async () => {
    const { object, state } = fixture();
    await join(object, 'offer', DEVICE_A, 'authenticated', { maxFrameBytes: 4, maxBytes: 8, maxMessages: 1 });
    await join(object, 'answer', DEVICE_B, 'authenticated', { maxFrameBytes: 4, maxBytes: 8, maxMessages: 1 });

    await object.webSocketMessage(state.sockets[0] as never, new ArrayBuffer(5));
    expect(state.sockets[0].closeCalls[0]).toMatchObject({ code: 1009 });
    expect(state.sockets[1].sent).toEqual(['ready']);
    expect(quota.settle).toHaveBeenCalledTimes(1);
  });

  it('text frame と attachment 欠落は protocol close する', async () => {
    const textFixture = fixture();
    await join(textFixture.object, 'offer', DEVICE_A);
    await join(textFixture.object, 'answer', DEVICE_B);
    await textFixture.object.webSocketMessage(textFixture.state.sockets[0] as never, 'not-binary');
    expect(textFixture.state.sockets[0].closeCalls[0]).toMatchObject({ code: 1003 });

    const attachmentFixture = fixture();
    await join(attachmentFixture.object, 'offer', DEVICE_A);
    await join(attachmentFixture.object, 'answer', DEVICE_B);
    attachmentFixture.state.sockets[0].attachment = null;
    await attachmentFixture.object.webSocketMessage(attachmentFixture.state.sockets[0] as never, new ArrayBuffer(1));
    expect(attachmentFixture.state.sockets[0].closeCalls[0]).toMatchObject({ code: 1002 });
  });

  it('normal close は全 socket を閉じ、両方向 counters を合算して lease 一回だけ settle する', async () => {
    const { object, state } = fixture();
    await join(object, 'offer', DEVICE_A);
    await join(object, 'answer', DEVICE_B);
    await object.webSocketMessage(state.sockets[0] as never, new ArrayBuffer(7));
    await object.webSocketMessage(state.sockets[1] as never, new ArrayBuffer(3));

    await object.webSocketClose(state.sockets[0] as never, 1000, '', true);
    await object.webSocketClose(state.sockets[1] as never, 1000, '', true);

    expect(state.sockets[0].closeCalls.at(-1)).toMatchObject({ code: 1001 });
    expect(state.sockets[1].closeCalls.at(-1)).toMatchObject({ code: 1001 });
    expect(quota.settle).toHaveBeenCalledTimes(1);
    expect(quota.settle.mock.calls[0][1]).toMatchObject({ bytes: 10, messages: 2, durationMs: expect.any(Number) });
    expect(quota.settle.mock.calls[0][1]).toMatchObject({ actualBytes: 10, actualMessages: 2 });
  });

  it('切断 peer が socket 一覧から消えても残存 attachment の room 合算値で settle する', async () => {
    const { object, state } = fixture();
    await join(object, 'offer', DEVICE_A);
    await join(object, 'answer', DEVICE_B);
    const offer = state.sockets[0];
    const answer = state.sockets[1];
    await object.webSocketMessage(offer as never, new ArrayBuffer(7));
    await object.webSocketMessage(answer as never, new ArrayBuffer(3));

    // Hibernation API は切断済み socket を getWebSockets() に返さない。
    state.sockets.splice(0, 1);
    await object.webSocketClose(answer as never, 1000, '', true);

    expect(quota.settle).toHaveBeenCalledTimes(1);
    expect(quota.settle.mock.calls[0][1]).toMatchObject({
      actualBytes: 10,
      actualMessages: 2,
    });
  });

  it('close/settle 中の同じ room への再入室を 409 で拒否する', async () => {
    const { object, state } = fixture();
    await join(object, 'offer', DEVICE_A);
    await join(object, 'answer', DEVICE_B);
    let release = (_value: boolean): void => undefined;
    quota.settle.mockImplementationOnce(() => new Promise<boolean>((resolve) => { release = resolve; }));

    const closing = object.webSocketClose(state.sockets[0] as never, 1000, '', true);
    await Promise.resolve();
    const response = await object.fetch(relayRequest('offer', DEVICE_A));

    expect(response.status).toBe(409);
    expect(quota.reserve).toHaveBeenCalledTimes(2);
    release(true);
    await closing;
  });

  it('前 lease の遅延 close は新 lease の socket を閉じない', async () => {
    const { object, state } = fixture();
    await join(object, 'offer', DEVICE_A);
    await join(object, 'answer', DEVICE_B);
    const oldSocket = state.sockets[0];
    await object.webSocketClose(oldSocket as never, 1000, '', true);

    await join(object, 'offer', DEVICE_A, 'authenticated', { leaseId: 'lease-2' });
    const newSocket = state.sockets[2];
    await object.webSocketClose(oldSocket as never, 1000, '', true);

    expect(newSocket.closeCalls).toEqual([]);
  });

  it('attachment persist 失敗時は peer.send せず fail closed', async () => {
    const { object, state } = fixture();
    await join(object, 'offer', DEVICE_A);
    await join(object, 'answer', DEVICE_B);
    state.sockets[1].serializeError = true;

    await object.webSocketMessage(state.sockets[0] as never, new ArrayBuffer(2));

    expect(state.sockets[1].sent).toEqual(['ready']);
    expect(quota.settle).toHaveBeenCalledTimes(1);
    expect(state.sockets[0].closeCalls[0]).toMatchObject({ code: 1011 });
  });
});

describe('RelayDO alarm / circuit breaker', () => {
  it('最新 attachment の expiry/idle deadline を alarm で再設定し、idle 時は全閉鎖する', async () => {
    const { object, state } = fixture();
    await join(object, 'offer', DEVICE_A, 'authenticated', { maxIdleMs: 20_000, expiresAt: Date.now() + 60_000 });
    await join(object, 'answer', DEVICE_B, 'authenticated', { maxIdleMs: 20_000, expiresAt: Date.now() + 60_000 });
    const before = state.storage.setAlarmCalls.length;
    vi.setSystemTime(Date.now() + 10_000);

    await object.alarm();
    expect(state.storage.setAlarmCalls.length).toBe(before + 1);
    expect(state.storage.alarmAt).toBeGreaterThan(Date.now());

    vi.setSystemTime(Date.now() + 11_000);
    await object.alarm();
    expect(quota.settle).toHaveBeenCalledTimes(1);
    expect(state.sockets[0].closeCalls.at(-1)).toMatchObject({ code: 1001 });
  });

  it('circuit open は既存 room の次の message/alarm も閉じる', async () => {
    const { object, state, env } = fixture();
    await join(object, 'offer', DEVICE_A);
    await join(object, 'answer', DEVICE_B);
    env.RELAY_CIRCUIT_OPEN = '1';

    await object.webSocketMessage(state.sockets[0] as never, new ArrayBuffer(1));

    expect(state.sockets[0].closeCalls[0]).toMatchObject({ code: 1011 });
    expect(state.sockets[1].closeCalls[0]).toMatchObject({ code: 1011 });
    expect(quota.settle).toHaveBeenCalledTimes(1);
  });
});
