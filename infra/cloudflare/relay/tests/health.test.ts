import { describe, expect, it, vi } from 'vitest';
import worker from '../src/index';
import type { Env } from '../src/index';

function healthyEnv() {
  const prepare = vi.fn();
  const kvGet = vi.fn();
  const env = {
    SESSION_HMAC_SECRET: 'test-secret',
    SALT: 'test-salt',
    RELAY_AUTH_MODE: 'optional',
    PAIR_LEDGER_MODE: 'transition',
    DB: { prepare },
    DEVICE_KEY_BINDING: { get: kvGet },
    QUOTA: { idFromName: vi.fn(), get: vi.fn() },
    RELAY_CIRCUIT_OPEN: '0',
    RELAY_MAX_CONCURRENT_ROOMS: '16',
    RELAY_MONTHLY_BYTES: '1000',
    RELAY_MONTHLY_MESSAGES: '1000',
    RELAY_MONTHLY_DURATION_SECONDS: '1000',
    RELAY_AUTH_SESSION_BYTES: '100',
    RELAY_AUTH_SESSION_MESSAGES: '100',
    RELAY_AUTH_SESSION_SECONDS: '100',
    RELAY_AUTH_IDLE_SECONDS: '10',
    RELAY_LEGACY_MONTHLY_BYTES: '100',
    RELAY_LEGACY_MONTHLY_MESSAGES: '100',
    RELAY_LEGACY_MONTHLY_DURATION_SECONDS: '100',
    RELAY_LEGACY_SESSION_BYTES: '10',
    RELAY_LEGACY_SESSION_MESSAGES: '10',
    RELAY_LEGACY_SESSION_SECONDS: '10',
    RELAY_LEGACY_IDLE_SECONDS: '5',
    RELAY_MAX_FRAME_BYTES: '1024',
  } as unknown as Env;
  return { env, prepare, kvGet };
}

describe('/health', () => {
  it('設定/binding readiness を確認するが公開リクエストごとに D1/KV を呼ばない', async () => {
    const { env, prepare, kvGet } = healthyEnv();
    const response = await worker.fetch(
      new Request('https://relay.test/health'),
      env,
      {} as ExecutionContext,
    );

    expect(response.status).toBe(200);
    expect(prepare).not.toHaveBeenCalled();
    expect(kvGet).not.toHaveBeenCalled();
  });
});
