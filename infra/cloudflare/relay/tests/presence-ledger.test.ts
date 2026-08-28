import { describe, it, expect, vi } from 'vitest';
import { handlePresence } from '../src/device-routes';
import { mintSessionToken } from '../src/auth';
import type { Env } from '../src/index';

const A = 'a'.repeat(32);
const B = 'b'.repeat(32);
const SECRET = 'test-cf-hmac-secret-0123456789';

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

function makeEnv(db: D1Database, fetch = vi.fn(async () => new Response(JSON.stringify({ ok: true }), { status: 200 }))): Env {
  return {
    DB: db,
    SALT: 'test-salt',
    SESSION_HMAC_SECRET: SECRET,
    DEVICE: {
      idFromName: vi.fn(() => ({ id: 'device' })),
      get: vi.fn(() => ({ fetch })),
    },
  } as unknown as Env;
}

async function presenceRequest(env: Env, method: string, path: string): Promise<Response> {
  const token = await mintSessionToken(A, 3600, env);
  const url = new URL(`https://relay.test${path}`);
  return handlePresence(
    new Request(url, { method, headers: { Authorization: `Bearer ${token}` } }),
    env,
    url,
  );
}

describe('peer presence の正式 pair 台帳 gate', () => {
  it('required では未登録 peer の GET を拒否し DeviceDO を起こさない', async () => {
    const fetch = vi.fn(async () => new Response(JSON.stringify({ ok: true }), { status: 200 }));
    const env = { ...makeEnv(pairLedgerDb(null), fetch), PAIR_LEDGER_MODE: 'required' } as unknown as Env;

    const res = await presenceRequest(env, 'GET', `/presence/${B}`);

    expect(res.status).toBe(403);
    expect(((await res.json()) as { error: string }).error).toBe('NOT_PAIRED');
    expect(fetch).not.toHaveBeenCalled();
  });

  it('registered peer の GET は DeviceDO へ forward する', async () => {
    const fetch = vi.fn(async () => new Response(JSON.stringify({ lastSeen: 1 }), { status: 200 }));
    const env = { ...makeEnv(pairLedgerDb({ pair_id: `${A}_${B}` }), fetch), PAIR_LEDGER_MODE: 'required' } as unknown as Env;

    const res = await presenceRequest(env, 'GET', `/presence/${B}/last-seen`);

    expect(res.status).toBe(200);
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it('D1 例外は 503 で fail closed にし、DeviceDO を起こさない', async () => {
    const fetch = vi.fn(async () => new Response(null, { status: 200 }));
    const env = { ...makeEnv(pairLedgerDb(new Error('D1 down')), fetch), PAIR_LEDGER_MODE: 'required' } as unknown as Env;

    const res = await presenceRequest(env, 'GET', `/presence/${B}`);

    expect(res.status).toBe(503);
    expect(fetch).not.toHaveBeenCalled();
  });

  it('明示された不正な PAIR_LEDGER_MODE は transition に降格せず 503', async () => {
    const fetch = vi.fn(async () => new Response(null, { status: 200 }));
    const env = { ...makeEnv(pairLedgerDb(null), fetch), PAIR_LEDGER_MODE: 'typo' } as unknown as Env;

    const res = await presenceRequest(env, 'GET', `/presence/${B}`);

    expect(res.status).toBe(503);
    expect(((await res.json()) as { error: string }).error).toBe('PAIR_LEDGER_MISCONFIGURED');
    expect(fetch).not.toHaveBeenCalled();
  });

  it('self の GET/POST/DELETE は台帳 gate を受けず従来契約を維持する', async () => {
    const fetch = vi.fn(async () => new Response(JSON.stringify({ ok: true }), { status: 200 }));
    const env = { ...makeEnv(pairLedgerDb(new Error('D1 down')), fetch), PAIR_LEDGER_MODE: 'required' } as unknown as Env;
    const token = await mintSessionToken(A, 3600, env);
    const url = new URL(`https://relay.test/presence/${A}`);

    const get = await handlePresence(new Request(url, { method: 'GET', headers: { Authorization: `Bearer ${token}` } }), env, url);
    const post = await handlePresence(new Request(url, { method: 'POST', headers: { Authorization: `Bearer ${token}`, 'content-type': 'application/json' }, body: '{}' }), env, url);
    const del = await handlePresence(new Request(url, { method: 'DELETE', headers: { Authorization: `Bearer ${token}` } }), env, url);

    expect(get.status).toBe(200);
    expect(post.status).toBe(200);
    expect(del.status).toBe(200);
    expect(fetch).toHaveBeenCalledTimes(3);
  });

  it('self presence の 64KiB 超過本文は DeviceDO を起こす前に 413', async () => {
    const fetch = vi.fn(async () => new Response(null, { status: 200 }));
    const env = makeEnv(pairLedgerDb(null), fetch);
    const token = await mintSessionToken(A, 3600, env);
    const url = new URL(`https://relay.test/presence/${A}`);
    const res = await handlePresence(new Request(url, {
      method: 'POST',
      headers: { Authorization: `Bearer ${token}`, 'content-type': 'application/json' },
      body: 'x'.repeat(64 * 1024 + 1),
    }), env, url);

    expect(res.status).toBe(413);
    expect(((await res.json()) as { error: string }).error).toBe('BODY_TOO_LARGE');
    expect(env.DEVICE.idFromName).not.toHaveBeenCalled();
    expect(fetch).not.toHaveBeenCalled();
  });
});
