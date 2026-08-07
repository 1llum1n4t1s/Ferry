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
import { DeviceDO } from '../src/devicedo';
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
  const sent: string[] = [];
  const fakeWs = { readyState: 1, send: (m: string) => sent.push(m) };
  const state = {
    storage: {
      get: async (k: string) => store.get(k),
      put: putSpy,
      delete: async (k: string) => void store.delete(k),
    },
    getWebSockets: () => [fakeWs],
    acceptWebSocket: (_: unknown) => {},
  };
  return { state, putSpy, sent, store };
}

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
});
