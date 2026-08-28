/**
 * `/sig/*` の device-scoped rate limit（枠の分離）に関する回帰テスト。
 *
 * 2026-07-28 の障害: v1.0.70 が /sig/* の rate limit に低頻度用の RATELIMIT_DEVICE
 * (30 req/60s) を流用したため、シグナリングの実レート（接続 1 回 ≒ 52 req、経路 Probe 1 回
 * ≒ 17 req）が枠を焼き切り、相手が返した answer を読む GET が 429 で弾かれ続けて
 * 「相手から応答がありません」で必ず接続失敗していた。
 * 枠を RATELIMIT_SIG（600 req/60s）へ分離したことを、消費先と 429 挙動の両面で固定する。
 */
import { describe, it, expect, vi } from 'vitest';
import { handleSignaling } from '../src/signaling-routes';
import { mintSessionToken } from '../src/auth';
import type { Env } from '../src/index';

vi.mock('../src/device-routes', () => ({ notifyInbox: async () => 0 }));

const A = 'a'.repeat(32);
const B = 'b'.repeat(32);
const PAIR_ID = `${A}_${B}`;
const SECRET = 'test-cf-hmac-secret-0123456789';

/** PAIR スタブ（DO へ到達したかを fetch 呼び出し回数で観測する）。 */
function makeEnv(extra: Partial<Env> = {}): { env: Env; doFetch: ReturnType<typeof vi.fn> } {
  const doFetch = vi.fn(
    async () => new Response(JSON.stringify({ ok: true }), { status: 200, headers: { 'content-type': 'application/json' } }),
  );
  const env = {
    SALT: 'test-salt',
    SESSION_HMAC_SECRET: SECRET,
    PAIR: { idFromName: (_: string) => ({ id: 'x' }), get: (_: unknown) => ({ fetch: doFetch }) },
    ...extra,
  } as unknown as Env;
  return { env, doFetch };
}

function pairLedgerDb(row: object | null | Error): D1Database {
  return {
    prepare: vi.fn(() => ({
      bind: vi.fn(() => ({
        first: vi.fn(async () => {
          if (row instanceof Error) throw row;
          return row;
        }),
      })),
    })),
  } as unknown as D1Database;
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

describe('handleSignaling rate limit の枠分離', () => {
  it('/sig/* は RATELIMIT_SIG を消費し、低頻度用の RATELIMIT_DEVICE は消費しない', async () => {
    const sigLimit = vi.fn(async () => ({ success: true }));
    const deviceLimit = vi.fn(async () => ({ success: true }));
    const { env } = makeEnv({
      RATELIMIT_SIG: { limit: sigLimit },
      RATELIMIT_DEVICE: { limit: deviceLimit },
    } as unknown as Partial<Env>);

    const res = await sigRequest(env, A, 'GET', `/sig/${PAIR_ID}/answer?from=${B}`);

    expect(res.status).toBe(200);
    expect(sigLimit).toHaveBeenCalledTimes(1);
    expect(sigLimit.mock.calls[0][0]).toEqual({ key: A });
    // ここが本障害の核心: /sig/* が /auth/token と枠を共有すると接続が自己閉塞する
    expect(deviceLimit).not.toHaveBeenCalled();
  });

  it('RATELIMIT_SIG が枯渇したら 429 DEVICE_RATE_LIMIT を返し PairDO へ forward しない', async () => {
    const sigLimit = vi.fn(async () => ({ success: false }));
    const { env, doFetch } = makeEnv({ RATELIMIT_SIG: { limit: sigLimit } } as unknown as Partial<Env>);

    const res = await sigRequest(env, A, 'POST', `/sig/${PAIR_ID}/offer`, { sdp: 'x', createdAt: 1 });

    expect(res.status).toBe(429);
    expect(((await res.json()) as { error: string }).error).toBe('DEVICE_RATE_LIMIT');
    expect(doFetch).not.toHaveBeenCalled();
  });

  it('RATELIMIT_SIG 未設定なら RATELIMIT_DEVICE へフォールバックしない（障害の復活を防ぐ）', async () => {
    const deviceLimit = vi.fn(async () => ({ success: false }));
    const { env, doFetch } = makeEnv({ RATELIMIT_DEVICE: { limit: deviceLimit } } as unknown as Partial<Env>);

    const res = await sigRequest(env, A, 'GET', `/sig/${PAIR_ID}/answer?from=${B}`);

    expect(res.status).toBe(200);
    expect(deviceLimit).not.toHaveBeenCalled();
    expect(doFetch).toHaveBeenCalledTimes(1);
  });
});

describe('handleSignaling の正式 pair 台帳 gate', () => {
  it('required では未登録 pair を 404 で拒否し PairDO を起こさない', async () => {
    const { env, doFetch } = makeEnv({
      DB: pairLedgerDb(null),
      PAIR_LEDGER_MODE: 'required',
    } as unknown as Partial<Env>);

    const res = await sigRequest(env, A, 'GET', `/sig/${PAIR_ID}/answer?from=${B}`);

    expect(res.status).toBe(404);
    expect(((await res.json()) as { error: string }).error).toBe('PAIR_NOT_FOUND');
    expect(doFetch).not.toHaveBeenCalled();
  });

  it('transition では未登録 pair を legacy 互換許可する', async () => {
    const { env, doFetch } = makeEnv({ DB: pairLedgerDb(null) } as unknown as Partial<Env>);

    const res = await sigRequest(env, A, 'GET', `/sig/${PAIR_ID}/answer?from=${B}`);

    expect(res.status).toBe(200);
    expect(doFetch).toHaveBeenCalledTimes(1);
  });

  it('D1 例外は 503 で fail closed にし、PairDO を起こさない', async () => {
    const { env, doFetch } = makeEnv({ DB: pairLedgerDb(new Error('D1 down')) } as unknown as Partial<Env>);

    const res = await sigRequest(env, A, 'GET', `/sig/${PAIR_ID}/answer?from=${B}`);

    expect(res.status).toBe(503);
    expect(doFetch).not.toHaveBeenCalled();
  });

  it('非 canonical pairId は台帳照会前に拒否する', async () => {
    const db = pairLedgerDb({ pair_id: `${B}_${A}` });
    const { env, doFetch } = makeEnv({ DB: db } as unknown as Partial<Env>);
    const token = await mintSessionToken(A, 3600, env);
    const reverse = `${B}_${A}`;
    const url = new URL(`https://relay.test/sig/${reverse}/answer`);
    const req = new Request(url, { headers: { Authorization: `Bearer ${token}` } });

    const res = await handleSignaling(req, env, url);

    expect(res.status).toBe(400);
    expect(doFetch).not.toHaveBeenCalled();
    expect((db.prepare as ReturnType<typeof vi.fn>)).not.toHaveBeenCalled();
  });

  it('明示された不正な PAIR_LEDGER_MODE は transition に降格せず 503', async () => {
    const { env, doFetch } = makeEnv({
      DB: pairLedgerDb(null),
      PAIR_LEDGER_MODE: 'typo',
    } as unknown as Partial<Env>);

    const res = await sigRequest(env, A, 'GET', `/sig/${PAIR_ID}/answer?from=${B}`);

    expect(res.status).toBe(503);
    expect(((await res.json()) as { error: string }).error).toBe('PAIR_LEDGER_MISCONFIGURED');
    expect(doFetch).not.toHaveBeenCalled();
  });
});
