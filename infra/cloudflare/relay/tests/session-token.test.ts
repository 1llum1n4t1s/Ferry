/**
 * CF 単独完結移行 Step 1: cfToken (自前 HMAC bearer) の単体テスト。
 *
 * mintSessionToken / verifySessionToken は Cloudflare bindings に依存しない純関数なので
 * Node.js (Web Crypto 内蔵) でそのまま検証する。署名すり替え・期限切れ・別 secret・形式不正を固定。
 */
import { describe, it, expect } from 'vitest';
import { mintSessionToken, verifySessionToken } from '../src/auth';
import type { Env } from '../src/index';

function envWithSecret(secret = 'test-hmac-secret-0123456789abcdef'): Env {
  return { SESSION_HMAC_SECRET: secret } as unknown as Env;
}
const DEVICE = 'a'.repeat(32);

describe('mintSessionToken / verifySessionToken', () => {
  it('mint→verify が deviceId を復元する (HS256 3 分割)', async () => {
    const env = envWithSecret();
    const token = await mintSessionToken(DEVICE, 3600, env);
    expect(token.split('.').length).toBe(3);
    const claims = await verifySessionToken(token, env);
    expect(claims?.deviceId).toBe(DEVICE);
  });

  it('payload すり替え (別 deviceId・署名は元のまま) は null', async () => {
    const env = envWithSecret();
    const token = await mintSessionToken(DEVICE, 3600, env);
    const [h, , s] = token.split('.');
    const evilPayload = Buffer.from(
      JSON.stringify({ sub: 'b'.repeat(32), iat: 1, exp: 9999999999 }),
    ).toString('base64url');
    expect(await verifySessionToken(`${h}.${evilPayload}.${s}`, env)).toBeNull();
  });

  it('期限切れ token は null', async () => {
    const env = envWithSecret();
    const token = await mintSessionToken(DEVICE, -10, env); // exp が既に過去
    expect(await verifySessionToken(token, env)).toBeNull();
  });

  it('別 secret で発行した token は検証側 secret では null (鍵すり替え防御)', async () => {
    const token = await mintSessionToken(DEVICE, 3600, envWithSecret('secret-A'));
    expect(await verifySessionToken(token, envWithSecret('secret-B'))).toBeNull();
  });

  it('SESSION_HMAC_SECRET 未設定なら verify は null (dual-path 後方互換)', async () => {
    const token = await mintSessionToken(DEVICE, 3600, envWithSecret());
    expect(await verifySessionToken(token, {} as unknown as Env)).toBeNull();
  });

  it('形式不正 (3 分割でない) は null', async () => {
    const env = envWithSecret();
    expect(await verifySessionToken('not-a-jwt', env)).toBeNull();
    expect(await verifySessionToken('a.b', env)).toBeNull();
  });

  it('sub が 32hex でない token は null (deviceId 形式強制)', async () => {
    const env = envWithSecret();
    const token = await mintSessionToken('short', 3600, env); // mint は形式チェックしない
    expect(await verifySessionToken(token, env)).toBeNull();
  });
});
