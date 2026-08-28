import { describe, expect, it, vi } from 'vitest';
import { MAX_ENDPOINT_BYTES, MAX_PROBE_OFFERS, MAX_SDP_BYTES, PairDO } from '../src/pairdo';

const DEVICE = 'a'.repeat(32);
const OTHER_DEVICE = 'b'.repeat(32);

function makeFakeState() {
  const values = new Map<string, unknown>();
  let alarm: number | null = null;
  const alarmCalls: number[] = [];
  const storage = {
    get: vi.fn(async <T>(key: string): Promise<T | undefined> => values.get(key) as T | undefined),
    put: vi.fn(async (key: string, value: unknown): Promise<void> => {
      values.set(key, value);
    }),
    delete: vi.fn(async (key: string | string[]): Promise<void> => {
      for (const item of Array.isArray(key) ? key : [key]) values.delete(item);
    }),
    list: vi.fn(async <T>(options?: { prefix?: string; limit?: number }): Promise<Map<string, T>> => {
      const prefix = options?.prefix ?? '';
      return new Map(
        [...values.entries()]
          .filter(([key]) => key.startsWith(prefix))
          .slice(0, options?.limit ?? Infinity)
          .map(([key, value]) => [key, value as T]),
      );
    }),
    getAlarm: vi.fn(async (): Promise<number | null> => alarm),
    setAlarm: vi.fn(async (value: number): Promise<void> => {
      alarm = value;
      alarmCalls.push(value);
    }),
    deleteAll: vi.fn(async (): Promise<void> => values.clear()),
  };
  return { storage, values, alarmCalls };
}

function request(
  path: string,
  method: string,
  body?: unknown,
  device = DEVICE,
): Request {
  return new Request(`https://do/${path}`, {
    method,
    headers: {
      'X-Ferry-Device': device,
      ...(body === undefined ? {} : { 'content-type': 'application/json' }),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

async function post(doObject: PairDO, path: string, body: unknown, device = DEVICE): Promise<Response> {
  return doObject.fetch(request(path, 'POST', body, device));
}

function asciiOfBytes(length: number): string {
  return 'x'.repeat(length);
}

describe('PairDO 入力境界', () => {
  it.each([
    ['offer', 'sdp', MAX_SDP_BYTES],
    ['answer', 'sdp', MAX_SDP_BYTES],
    ['probe-offer/00000000000000000000000000000001', 'sdp', MAX_SDP_BYTES],
    ['probe-answer/00000000000000000000000000000001', 'sdp', MAX_SDP_BYTES],
  ])('%s の SDP は %s bytes まで受理する', async (path, field, length) => {
    const state = makeFakeState();
    const doObject = new PairDO(state as never, {});
    const res = await post(doObject, path, { [field]: asciiOfBytes(length) });
    expect(res.status).toBe(200);
  });

  it.each([
    ['offer', MAX_SDP_BYTES],
    ['answer', MAX_SDP_BYTES],
    ['probe-offer/00000000000000000000000000000001', MAX_SDP_BYTES],
    ['probe-answer/00000000000000000000000000000001', MAX_SDP_BYTES],
  ])('%s の SDP が 1 byte 超えると 413 BODY_TOO_LARGE', async (path, length) => {
    const doObject = new PairDO(makeFakeState() as never, {});
    const res = await post(doObject, path, { sdp: asciiOfBytes(length + 1) });
    expect(res.status).toBe(413);
    await expect(res.json()).resolves.toMatchObject({ error: 'BODY_TOO_LARGE' });
  });

  it('endpoint は 2KiB まで受理し、1 byte 超過を拒否する', async () => {
    const state = makeFakeState();
    const doObject = new PairDO(state as never, {});
    expect((await post(doObject, 'endpoint', { endpoint: asciiOfBytes(MAX_ENDPOINT_BYTES) })).status).toBe(200);

    const tooLarge = await post(doObject, 'endpoint', { endpoint: asciiOfBytes(MAX_ENDPOINT_BYTES + 1) });
    expect(tooLarge.status).toBe(413);
    await expect(tooLarge.json()).resolves.toMatchObject({ error: 'BODY_TOO_LARGE' });
  });

  it('deviceId と probe nonce は 32 桁小文字 hex だけを受理する', async () => {
    const doObject = new PairDO(makeFakeState() as never, {});
    expect((await post(doObject, 'offer', { sdp: 'x' }, `${DEVICE.slice(0, -1)}g`)).status).toBe(400);
    expect((await post(doObject, 'probe-offer/not-a-nonce', { sdp: 'x' })).status).toBe(400);
  });
});

describe('PairDO createdAt / alarm', () => {
  it.each([
    ['answer', { sdp: 'answer' }, '/answer?from=' + DEVICE, { data: 'answer' }],
    ['endpoint', { endpoint: '127.0.0.1:1234' }, '/endpoint?from=' + DEVICE, { endpoint: '127.0.0.1:1234', from: DEVICE }],
    [
      'probe-answer',
      { sdp: 'probe-answer' },
      '/probe-answer/00000000000000000000000000000001',
      { sdp: 'probe-answer' },
    ],
  ])('%s の書込だけでも server createdAt と alarm を更新する', async (kind, body, readPath, expected) => {
    const state = makeFakeState();
    const doObject = new PairDO(state as never, {});
    const path = kind === 'probe-answer' ? 'probe-answer/00000000000000000000000000000001' : kind;
    const before = Date.now();
    expect((await post(doObject, path, body)).status).toBe(200);
    const createdAt = state.values.get('createdAt');
    expect(typeof createdAt).toBe('number');
    expect(createdAt as number).toBeGreaterThanOrEqual(before);
    expect(state.alarmCalls).toHaveLength(1);
    expect(state.alarmCalls[0]).toBe((createdAt as number) + 60 * 60 * 1000);

    const read = await doObject.fetch(request(readPath, 'GET', undefined, OTHER_DEVICE));
    expect(read.status).toBe(200);
    await expect(read.json()).resolves.toEqual(expected);
  });
});

describe('PairDO probe offer 上限', () => {
  it('新しい nonce は最大 16 件まで、17 件目は 429 で拒否する', async () => {
    const state = makeFakeState();
    const doObject = new PairDO(state as never, {});
    for (let i = 0; i < MAX_PROBE_OFFERS; i++) {
      const nonce = i.toString(16).padStart(32, '0');
      expect((await post(doObject, `probe-offer/${nonce}`, { sdp: `offer-${i}` })).status).toBe(200);
    }

    const overflow = await post(doObject, 'probe-offer/000000000000000000000000000000ff', { sdp: 'overflow' });
    expect(overflow.status).toBe(429);
    await expect(overflow.json()).resolves.toMatchObject({ error: 'PROBE_LIMIT' });

    // レスポンスも上限件数を超えず、保存済みデータを漏らさない。
    const list = await doObject.fetch(request('probe-offers', 'GET'));
    expect(list.status).toBe(200);
    const payload = (await list.json()) as { offers: unknown[] };
    expect(payload.offers).toHaveLength(MAX_PROBE_OFFERS);
    expect(state.values.has('probeOffer:000000000000000000000000000000ff')).toBe(false);
  });

  it('上限到達後も既存 nonce の再送は上書きできる', async () => {
    const state = makeFakeState();
    const doObject = new PairDO(state as never, {});
    const firstNonce = '00000000000000000000000000000000';
    for (let i = 0; i < MAX_PROBE_OFFERS; i++) {
      const nonce = i.toString(16).padStart(32, '0');
      await post(doObject, `probe-offer/${nonce}`, { sdp: `offer-${i}` });
    }

    expect((await post(doObject, `probe-offer/${firstNonce}`, { sdp: 'updated' })).status).toBe(200);
    const read = await doObject.fetch(request('probe-offers', 'GET'));
    const offers = (await read.json()) as { offers: Array<{ nonce: string; sdp: string }> };
    expect(offers.offers.find((offer) => offer.nonce === firstNonce)?.sdp).toBe('updated');
  });

  it('probe answer も nonce を最大 16 件に制限する', async () => {
    const state = makeFakeState();
    const doObject = new PairDO(state as never, {});
    for (let i = 0; i < MAX_PROBE_OFFERS; i++) {
      const nonce = i.toString(16).padStart(32, '0');
      expect((await post(doObject, `probe-answer/${nonce}`, { sdp: `answer-${i}` })).status).toBe(200);
    }

    const overflowNonce = '000000000000000000000000000000ff';
    const overflow = await post(doObject, `probe-answer/${overflowNonce}`, { sdp: 'overflow' });
    expect(overflow.status).toBe(429);
    await expect(overflow.json()).resolves.toMatchObject({ error: 'PROBE_LIMIT' });
    expect(state.values.has(`probeAnswer:${overflowNonce}`)).toBe(false);
  });
});
