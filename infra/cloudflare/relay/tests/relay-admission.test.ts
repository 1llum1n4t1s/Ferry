/**
 * `/ferry-relay` WebSocket 入室の入口検証（pairId 形式 + per-IP rate limit）の回帰テスト。
 *
 * 入室認可（Bearer 必須 + pairId 当事者検証）は出荷済みクライアントへの Bearer 普及待ちで
 * まだ課せない。その間、任意文字列の pairId で `idFromName` 経由に RelayDO を無制限に
 * 起こせる状態（課金・接続枠を狙った DoS）だけは塞いでおく必要がある。
 * 「正規 pairId は必ず通る」「不正形式と枠超過は DO へ到達しない」の両方を固定する。
 */
import { describe, it, expect, vi } from 'vitest';
import worker from '../src/index';
import type { Env } from '../src/index';

const A = 'a'.repeat(32);
const B = 'b'.repeat(32);
const PAIR_ID = `${A}_${B}`;

/** RELAY スタブ。DO へ到達したかを idFromName / fetch の呼び出しで観測する。 */
function makeEnv(extra: Partial<Env> = {}): {
  env: Env;
  idFromName: ReturnType<typeof vi.fn>;
  doFetch: ReturnType<typeof vi.fn>;
} {
  const doFetch = vi.fn(async () => new Response('ok', { status: 200 }));
  const idFromName = vi.fn((_: string) => ({ id: 'x' }));
  const env = {
    SALT: 'test-salt',
    RELAY: { idFromName, get: (_: unknown) => ({ fetch: doFetch }) },
    ...extra,
  } as unknown as Env;
  return { env, idFromName, doFetch };
}

function wsRequest(pairId: string, ip = '1.2.3.4'): Request {
  return new Request(`https://relay.test/ferry-relay?pairId=${encodeURIComponent(pairId)}&role=offer`, {
    headers: { Upgrade: 'websocket', 'CF-Connecting-IP': ip },
  });
}

const CTX = { waitUntil: () => {}, passThroughOnException: () => {} } as unknown as ExecutionContext;

describe('リレー入室の pairId 形式検証', () => {
  it('正規形式 {32hex}_{32hex} は従来どおり RelayDO へ forward する', async () => {
    const { env, idFromName, doFetch } = makeEnv();

    const res = await worker.fetch(wsRequest(PAIR_ID), env, CTX);

    expect(res.status).toBe(200);
    expect(idFromName).toHaveBeenCalledTimes(1);
    expect(doFetch).toHaveBeenCalledTimes(1);
    // role はクエリで DO に持ち越す契約
    expect(doFetch.mock.calls[0][0].url).toContain('__role=offer');
  });

  it.each([
    ['非 hex 文字', `${'z'.repeat(32)}_${B}`],
    ['桁数不足', `${'a'.repeat(31)}_${B}`],
    ['区切り無し', `${A}${B}`],
    ['大文字', `${'A'.repeat(32)}_${B}`],
    ['任意文字列', 'room-1'],
  ])('%s の pairId は 400 で RelayDO を起こさない', async (_label, pairId) => {
    const { env, idFromName, doFetch } = makeEnv();

    const res = await worker.fetch(wsRequest(pairId), env, CTX);

    expect(res.status).toBe(400);
    expect(idFromName).not.toHaveBeenCalled();
    expect(doFetch).not.toHaveBeenCalled();
  });

  it('pairId 欠落は従来どおり 400', async () => {
    const { env, doFetch } = makeEnv();
    const req = new Request('https://relay.test/ferry-relay?role=offer', {
      headers: { Upgrade: 'websocket' },
    });

    const res = await worker.fetch(req, env, CTX);

    expect(res.status).toBe(400);
    expect(doFetch).not.toHaveBeenCalled();
  });

  it('WebSocket 以外は従来どおり 426', async () => {
    const { env, doFetch } = makeEnv();

    const res = await worker.fetch(new Request(`https://relay.test/ferry-relay?pairId=${PAIR_ID}`), env, CTX);

    expect(res.status).toBe(426);
    expect(doFetch).not.toHaveBeenCalled();
  });
});

describe('リレー入室の per-IP rate limit', () => {
  it('RATELIMIT_RELAY を CF-Connecting-IP で消費する', async () => {
    const relayLimit = vi.fn(async () => ({ success: true }));
    const ipLimit = vi.fn(async () => ({ success: true }));
    const { env, doFetch } = makeEnv({
      RATELIMIT_RELAY: { limit: relayLimit },
      RATELIMIT_IP: { limit: ipLimit },
    } as unknown as Partial<Env>);

    const res = await worker.fetch(wsRequest(PAIR_ID, '203.0.113.9'), env, CTX);

    expect(res.status).toBe(200);
    expect(relayLimit).toHaveBeenCalledTimes(1);
    expect(relayLimit.mock.calls[0][0]).toEqual({ key: '203.0.113.9' });
    // 枠の分離: リレー乱打が /auth/token・/pair/create の枠を焼かないこと
    expect(ipLimit).not.toHaveBeenCalled();
    expect(doFetch).toHaveBeenCalledTimes(1);
  });

  it('枠が枯渇したら 429 を返し RelayDO へ到達しない', async () => {
    const relayLimit = vi.fn(async () => ({ success: false }));
    const { env, idFromName, doFetch } = makeEnv({
      RATELIMIT_RELAY: { limit: relayLimit },
    } as unknown as Partial<Env>);

    const res = await worker.fetch(wsRequest(PAIR_ID), env, CTX);

    expect(res.status).toBe(429);
    expect(idFromName).not.toHaveBeenCalled();
    expect(doFetch).not.toHaveBeenCalled();
  });

  it('RATELIMIT_RELAY 未設定でも入室は従来どおり通る（binding 追加前のデプロイを壊さない）', async () => {
    const { env, doFetch } = makeEnv();

    const res = await worker.fetch(wsRequest(PAIR_ID), env, CTX);

    expect(res.status).toBe(200);
    expect(doFetch).toHaveBeenCalledTimes(1);
  });
});
