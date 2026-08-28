/**
 * CF 使用量削減: 「接続ノック」の単体テスト。
 *
 * - handleSignaling: offer / probe-offer の POST 成功時にペア相手へ type=knock を notifyInbox する
 *   (GET / answer / DO エラー時は knock しない)
 * - DeviceDO.notify: type=knock は transient (storage に積まない・接続中 WS にだけ送る)。
 *   knock が INBOX_MAX を溢れさせてペア成立イベントを押し出す事故と、次回接続時の stale knock replay を防ぐ。
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { handleSignaling } from '../src/signaling-routes';
import { DeviceDO, INBOX_MAX_CONNECTIONS } from '../src/devicedo';
import { mintSessionToken } from '../src/auth';
import type { Env } from '../src/index';

const notifyInboxMock = vi.fn(async () => {});
vi.mock('../src/device-routes', () => ({
  notifyInbox: (...args: unknown[]) => notifyInboxMock(...args),
}));

const A = 'a'.repeat(32);
const B = 'b'.repeat(32);
const PAIR_ID = `${A}_${B}`;
const SECRET = 'test-cf-hmac-secret-0123456789';

function makeEnv(doStatus = 200): Env {
  const stub = {
    fetch: async () =>
      new Response(JSON.stringify(doStatus === 200 ? { ok: true } : { error: 'DO_ERROR' }), {
        status: doStatus,
        headers: { 'content-type': 'application/json' },
      }),
  };
  return {
    SALT: 'test-salt',
    SESSION_HMAC_SECRET: SECRET,
    PAIR: { idFromName: (_: string) => ({ id: 'x' }), get: (_: unknown) => stub },
  } as unknown as Env;
}

async function sigRequest(env: Env, deviceId: string, method: string, path: string, body?: object): Promise<Response> {
  const token = await mintSessionToken(deviceId, 3600, env);
  const url = new URL(`https://relay.test${path}`);
  const req = new Request(url, {
    method,
    headers: { Authorization: `Bearer ${token}`, 'content-type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined,
  });
  return handleSignaling(req, env, url);
}

describe('handleSignaling 接続ノック', () => {
  beforeEach(() => notifyInboxMock.mockClear());

  it('offer POST 成功時にペア相手 (B) へ type=knock を push する', async () => {
    const env = makeEnv();
    const res = await sigRequest(env, A, 'POST', `/sig/${PAIR_ID}/offer`, { sdp: 'x', createdAt: 1 });
    expect(res.status).toBe(200);
    expect(notifyInboxMock).toHaveBeenCalledTimes(1);
    const [, peer, event] = notifyInboxMock.mock.calls[0] as unknown[];
    expect(peer).toBe(B);
    expect((event as { type: string; pairId: string; from: string }).type).toBe('knock');
    expect((event as { pairId: string }).pairId).toBe(PAIR_ID);
    expect((event as { from: string }).from).toBe(A);
  });

  it('B が送信すればノック先は A になる（ペアのもう一方が宛先）', async () => {
    const env = makeEnv();
    await sigRequest(env, B, 'POST', `/sig/${PAIR_ID}/probe-offer/deadbeef`, { sdp: 'x' });
    expect(notifyInboxMock).toHaveBeenCalledTimes(1);
    expect(notifyInboxMock.mock.calls[0][1]).toBe(A);
  });

  it('offer GET（ポーリング読み）ではノックしない', async () => {
    const env = makeEnv();
    await sigRequest(env, B, 'GET', `/sig/${PAIR_ID}/offer?from=${A}`);
    expect(notifyInboxMock).not.toHaveBeenCalled();
  });

  it('answer POST ではノックしない（送信側が能動ポーリングで有界に待つ経路）', async () => {
    const env = makeEnv();
    await sigRequest(env, B, 'POST', `/sig/${PAIR_ID}/answer`, { sdp: 'x' });
    expect(notifyInboxMock).not.toHaveBeenCalled();
  });

  it('PairDO がエラーを返したらノックしない', async () => {
    const env = makeEnv(500);
    await sigRequest(env, A, 'POST', `/sig/${PAIR_ID}/offer`, { sdp: 'x', createdAt: 1 });
    expect(notifyInboxMock).not.toHaveBeenCalled();
  });

  it('notifyInbox が throw しても offer 書込のレスポンスは成功のまま', async () => {
    const env = makeEnv();
    notifyInboxMock.mockRejectedValueOnce(new Error('DO down'));
    const res = await sigRequest(env, A, 'POST', `/sig/${PAIR_ID}/offer`, { sdp: 'x', createdAt: 1 });
    expect(res.status).toBe(200);
  });

  it('ctx がある場合はノック完了を待たずに応答し、waitUntil に載せる', async () => {
    const env = makeEnv();
    let release: () => void = () => {};
    notifyInboxMock.mockImplementationOnce(
      () => new Promise<void>((resolve) => (release = resolve)),
    );
    const pending: Promise<unknown>[] = [];
    const ctx = { waitUntil: (p: Promise<unknown>) => void pending.push(p) };

    const token = await mintSessionToken(A, 3600, env);
    const url = new URL(`https://relay.test/sig/${PAIR_ID}/offer`);
    const req = new Request(url, {
      method: 'POST',
      headers: { Authorization: `Bearer ${token}`, 'content-type': 'application/json' },
      body: JSON.stringify({ sdp: 'x', createdAt: 1 }),
    });

    // ノックが未解決のままでもレスポンスは返る（DO cold start が offer POST を遅らせない）
    const res = await handleSignaling(req, env, url, ctx);
    expect(res.status).toBe(200);
    expect(pending.length).toBe(1);
    release();
    await pending[0];
  });
});

// ------------------ DeviceDO.notify: knock は transient ------------------

function makeFakeState() {
  const store = new Map<string, unknown>();
  const putSpy = vi.fn(async (k: string, v: unknown) => void store.set(k, v));
  const deleteSpy = vi.fn(async (k: string) => {
    store.delete(k);
    return true;
  });
  const deleteAllSpy = vi.fn(async () => store.clear());
  let alarm: number | null = null;
  const getAlarmSpy = vi.fn(async () => alarm);
  const setAlarmSpy = vi.fn(async (scheduledTime: number | Date) => {
    alarm = typeof scheduledTime === 'number' ? scheduledTime : scheduledTime.getTime();
  });
  const sent: string[] = [];
  const fakeWs = { readyState: 1, send: (m: string) => sent.push(m) };
  const state = {
    storage: {
      get: async (k: string) => store.get(k),
      put: putSpy,
      delete: deleteSpy,
      deleteAll: deleteAllSpy,
      getAlarm: getAlarmSpy,
      setAlarm: setAlarmSpy,
    },
    getWebSockets: () => [fakeWs],
    acceptWebSocket: (_: unknown) => {},
  };
  return { state, putSpy, deleteSpy, deleteAllSpy, getAlarmSpy, setAlarmSpy, sent, store };
}

function installFakeWebSocketPair(flushed: string[]): () => void {
  const globals = globalThis as unknown as Record<string, unknown>;
  const previous = globals.WebSocketPair;
  globals.WebSocketPair = class {
    0 = {};
    1 = { send: (message: string) => flushed.push(message) };
  };
  return () => {
    if (previous === undefined) delete globals.WebSocketPair;
    else globals.WebSocketPair = previous;
  };
}

function installFakeResponse(): () => void {
  const globals = globalThis as unknown as Record<string, unknown>;
  const previous = globals.Response;
  globals.Response = class {
    readonly status: number;
    readonly webSocket: unknown;

    constructor(_body: unknown, init: { status?: number; webSocket?: unknown } = {}) {
      this.status = init.status ?? 200;
      this.webSocket = init.webSocket;
    }
  };
  return () => {
    globals.Response = previous;
  };
}

describe('DeviceDO inbox connection 上限', () => {
  it('同一 device の live inbox が4本なら5本目を accept 前に拒否する', async () => {
    const { state } = makeFakeState();
    state.getWebSockets = () => Array.from(
      { length: INBOX_MAX_CONNECTIONS },
      () => ({ readyState: 1, send: () => undefined }),
    );
    vi.stubGlobal('WebSocket', { OPEN: 1, CONNECTING: 0, CLOSED: 3 });
    try {
      const dev = new DeviceDO(state as never, {});
      const response = await (dev as unknown as { openInbox: () => Promise<Response> }).openInbox();
      expect(response.status).toBe(429);
      await expect(response.json()).resolves.toMatchObject({ error: 'INBOX_CONNECTION_LIMIT' });
    } finally {
      vi.unstubAllGlobals();
    }
  });
});

describe('DeviceDO.notify の knock transient 化', () => {
  it('type=knock は storage に積まず WS にだけ送る', async () => {
    if (!(globalThis as { WebSocket?: unknown }).WebSocket) {
      (globalThis as { WebSocket?: unknown }).WebSocket = { OPEN: 1 };
    }
    const { state, putSpy, sent } = makeFakeState();
    const dev = new DeviceDO(state as never, {});
    const res = await dev.fetch(
      new Request('https://do/notify', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ type: 'knock', pairId: PAIR_ID, from: A, createdAt: Date.now() }),
      }),
    );
    expect(res.status).toBe(200);
    expect(putSpy).not.toHaveBeenCalled(); // transient: inbox に永続化しない
    expect(sent.length).toBe(1);
    expect(JSON.parse(sent[0]).type).toBe('knock');
  });

  it('ペア成立イベント（type なし）は従来どおり storage に積む', async () => {
    if (!(globalThis as { WebSocket?: unknown }).WebSocket) {
      (globalThis as { WebSocket?: unknown }).WebSocket = { OPEN: 1 };
    }
    const { state, putSpy, sent } = makeFakeState();
    const dev = new DeviceDO(state as never, {});
    const res = await dev.fetch(
      new Request('https://do/notify', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ pairingId: 'p', sidA: A, sidB: B, createdAt: Date.now() }),
      }),
    );
    expect(res.status).toBe(200);
    expect(putSpy).toHaveBeenCalledTimes(1); // inbox に永続化される
    expect(sent.length).toBe(1);
  });

  it('同じ pairingId の再通知は積み増さず最新 1 件に畳む (inbox 押し出し防止)', async () => {
    if (!(globalThis as { WebSocket?: unknown }).WebSocket) {
      (globalThis as { WebSocket?: unknown }).WebSocket = { OPEN: 1 };
    }
    const { state, store } = makeFakeState();
    const dev = new DeviceDO(state as never, {});
    const post = (body: unknown) =>
      dev.fetch(
        new Request('https://do/notify', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify(body),
        }),
      );

    // /pair/link は同じ相手に何度でも成立できるため、同一 pairingId が繰り返し届きうる。
    // 積み増すと INBOX_MAX(50) を溢れさせて他ピアの未読イベントを押し出せてしまう。
    for (let i = 0; i < 5; i++) {
      await post({ pairingId: 'p1', sidA: A, sidB: B, createdAt: Date.now() + i });
    }
    // 別ペアのイベントは残る（畳み込みは pairingId 単位）
    await post({ pairingId: 'p2', sidA: A, sidB: B, createdAt: Date.now() });
    // unpair は type が違うので成立イベントとは別枠で保持される
    await post({ type: 'unpair', pairingId: 'p1', createdAt: Date.now() });

    const inbox = store.get('inbox') as Array<{ pairingId: string; type?: string }>;
    expect(inbox.filter((x) => x.pairingId === 'p1' && x.type === undefined)).toHaveLength(1);
    expect(inbox.filter((x) => x.pairingId === 'p2')).toHaveLength(1);
    expect(inbox.filter((x) => x.type === 'unpair')).toHaveLength(1);
    expect(inbox).toHaveLength(3);
  });

  it('openInbox は stale 行を storage から prune して fresh 行だけ flush する', async () => {
    const now = 1_800_000_000_000;
    vi.spyOn(Date, 'now').mockReturnValue(now);
    const { state, putSpy, deleteSpy, store, setAlarmSpy } = makeFakeState();
    const stale = { pairingId: 'stale', createdAt: now - 60 * 60 * 1000 };
    const fresh = { pairingId: 'fresh', createdAt: now - 1_000 };
    store.set('inbox', [stale, fresh]);
    const flushed: string[] = [];
    const restorePair = installFakeWebSocketPair(flushed);
    const restoreResponse = installFakeResponse();
    try {
      const dev = new DeviceDO(state as never, {});
      const response = await (dev as unknown as { openInbox: () => Promise<Response> }).openInbox();
      expect(response.status).toBe(101);
      expect(store.get('inbox')).toEqual([fresh]);
      expect(putSpy).toHaveBeenCalledWith('inbox', [fresh]);
      expect(deleteSpy).not.toHaveBeenCalledWith('inbox');
      expect(flushed).toEqual([JSON.stringify(fresh)]);
      expect(setAlarmSpy).toHaveBeenCalledWith(fresh.createdAt + 60 * 60 * 1000);
    } finally {
      restoreResponse();
      restorePair();
      vi.restoreAllMocks();
    }
  });

  it('alarm は stale 行を削除し、残りの最短期限へ再設定する', async () => {
    const now = 1_800_000_000_000;
    vi.spyOn(Date, 'now').mockReturnValue(now);
    const { state, putSpy, setAlarmSpy, store } = makeFakeState();
    const stale = { pairingId: 'stale', createdAt: now - 60 * 60 * 1000 - 1 };
    const soon = { pairingId: 'soon', createdAt: now - 60 * 60 * 1000 + 1_000 };
    const later = { pairingId: 'later', createdAt: now - 60 * 60 * 1000 + 5_000 };
    const presence = { lastSeen: now, displayName: 'device', version: '1' };
    store.set('inbox', [stale, soon, later]);
    store.set('presence', presence);
    try {
      const dev = new DeviceDO(state as never, {});
      await dev.alarm();
      expect(store.get('inbox')).toEqual([soon, later]);
      expect(putSpy).toHaveBeenCalledWith('inbox', [soon, later]);
      expect(setAlarmSpy).toHaveBeenCalledWith(soon.createdAt + 60 * 60 * 1000);
      expect(store.get('presence')).toEqual(presence);
    } finally {
      vi.restoreAllMocks();
    }
  });

  it('alarm は全件 stale なら inbox だけを削除し、presence は残す', async () => {
    const now = 1_800_000_000_000;
    vi.spyOn(Date, 'now').mockReturnValue(now);
    const { state, deleteSpy, deleteAllSpy, store } = makeFakeState();
    const stale = { pairingId: 'stale', createdAt: now - 60 * 60 * 1000 - 1 };
    const presence = { lastSeen: now, displayName: 'device', version: '1' };
    store.set('inbox', [stale]);
    store.set('presence', presence);
    try {
      const dev = new DeviceDO(state as never, {});
      await dev.alarm();
      await dev.alarm(); // at-least-once 実行でも同じ結果になることを確認
      expect(store.has('inbox')).toBe(false);
      expect(deleteSpy).toHaveBeenCalledWith('inbox');
      expect(deleteAllSpy).not.toHaveBeenCalled();
      expect(store.get('presence')).toEqual(presence);
    } finally {
      vi.restoreAllMocks();
    }
  });

  it('非transient notify は保持イベントの最も遅い期限へ alarm を設定する', async () => {
    const now = 1_800_000_000_000;
    vi.spyOn(Date, 'now').mockReturnValue(now);
    const { state, setAlarmSpy } = makeFakeState();
    const dev = new DeviceDO(state as never, {});
    const post = (body: unknown) =>
      dev.fetch(
        new Request('https://do/notify', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify(body),
        }),
      );
    try {
      await post({ pairingId: 'old', createdAt: now - 1_000 });
      await post({ pairingId: 'new', createdAt: now });
      expect(setAlarmSpy).toHaveBeenLastCalledWith(now + 60 * 60 * 1000);
    } finally {
      vi.restoreAllMocks();
    }
  });
});
