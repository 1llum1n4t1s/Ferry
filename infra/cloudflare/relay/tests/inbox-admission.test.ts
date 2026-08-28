import { describe, expect, it, vi } from 'vitest';
import { mintSessionToken } from '../src/auth';
import { handleInbox } from '../src/device-routes';
import type { Env } from '../src/index';

const DEVICE = 'a'.repeat(32);
const SECRET = 'test-cf-hmac-secret-0123456789';

async function fixture(success: boolean) {
  const fetch = vi.fn(async () => new Response(null, { status: 200 }));
  const limit = vi.fn(async () => ({ success }));
  const env = {
    SALT: 'test-salt',
    SESSION_HMAC_SECRET: SECRET,
    DEVICE: {
      idFromName: vi.fn(() => ({ id: 'device' })),
      get: vi.fn(() => ({ fetch })),
    },
    RATELIMIT_DEVICE: { limit },
  } as unknown as Env;
  const token = await mintSessionToken(DEVICE, 3600, env);
  const request = new Request('https://relay.test/inbox', {
    headers: { Upgrade: 'websocket', Authorization: `Bearer ${token}` },
  });
  return { env, request, fetch, limit };
}

describe('/inbox admission', () => {
  it('device単位の専用keyで接続乱打を制限する', async () => {
    const { env, request, fetch, limit } = await fixture(false);
    const response = await handleInbox(request, env);
    expect(response.status).toBe(429);
    expect(limit).toHaveBeenCalledWith({ key: `inbox:${DEVICE}` });
    expect(fetch).not.toHaveBeenCalled();
  });

  it('枠内の認証済み接続だけを本人 DeviceDO へ渡す', async () => {
    const { env, request, fetch } = await fixture(true);
    const response = await handleInbox(request, env);
    expect(response.status).toBe(200);
    expect(fetch).toHaveBeenCalledTimes(1);
  });
});
