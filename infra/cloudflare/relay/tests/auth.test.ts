/**
 * Ferry Workers /auth/token の単体テスト（CF 単独完結）
 *
 * Cloudflare bindings に依存しない純関数 / pure handler ロジックを Node.js (Web Crypto 内蔵) で検証する。
 * - base64Url の URL safe ラウンドトリップ
 * - handleAuthToken の正常系・別鍵 401・clock skew 400・KV first-write-wins binding・cfToken 発行
 *
 * （旧 Firebase Custom Token 経路 mintCustomToken / rs256Sign / handlePairToken は Step 7 で撤去済み。）
 */
import { describe, it, expect, vi } from 'vitest';
import { generateEcdsaP256Fixture, signIeeeP1363 } from './fixtures';
import {
    base64UrlEncode,
    base64UrlDecode,
    handleAuthToken,
    verifySessionToken,
} from '../src/auth';
import type { Env } from '../src/index';

// ------------------ base64Url 純関数 ------------------

describe('base64UrlEncode / base64UrlDecode', () => {
    it('ラウンドトリップが恒等（パディング無し・URL safe）', () => {
        for (let len = 1; len < 40; len++) {
            const data = new Uint8Array(len).map((_, i) => (i * 31 + 7) & 0xff);
            const s = base64UrlEncode(data);
            expect(s).not.toContain('+');
            expect(s).not.toContain('/');
            expect(s).not.toContain('=');
            expect(Array.from(base64UrlDecode(s))).toEqual(Array.from(data));
        }
    });
});

// ------------------ handleAuthToken (KV stub + RateLimit 無し) ------------------

class InMemoryKv {
    private store = new Map<string, string>();
    async get(k: string): Promise<string | null> {
        return this.store.has(k) ? this.store.get(k)! : null;
    }
    async put(k: string, v: string): Promise<void> {
        this.store.set(k, v);
    }
}

function makeAuthEnv() {
    const kv = new InMemoryKv();
    const env = {
        DEVICE_KEY_BINDING: kv,
        SESSION_HMAC_SECRET: 'test-cf-hmac-secret-0123456789',
    } as unknown as Env;
    return { env, kv };
}

async function buildSignedAuthBody(deviceId: string, tsOverride?: number) {
    const ec = await generateEcdsaP256Fixture();
    const pubKeySpki = base64UrlEncode(new Uint8Array(ec.spkiDer));
    const ts = tsOverride ?? Date.now();
    const message = new TextEncoder().encode(`ferry-auth-v1|${deviceId}|${pubKeySpki}|${ts}`);
    const sigBuf = await signIeeeP1363(ec.privateKey, message);
    const sig = base64UrlEncode(new Uint8Array(sigBuf));
    return { deviceId, pubKeySpki, ts, sig, ec };
}

function mkRequest(body: unknown): Request {
    return new Request('https://relay.test/auth/token', {
        method: 'POST',
        headers: { 'content-type': 'application/json', 'CF-Connecting-IP': '127.0.0.1' },
        body: JSON.stringify(body),
    });
}

describe('handleAuthToken', () => {
    it('正常系: 署名検証 通過 + KV 新規バインド + cfToken 返却', async () => {
        const { env, kv } = makeAuthEnv();
        const deviceId = 'a'.repeat(32);
        const body = await buildSignedAuthBody(deviceId);
        const res = await handleAuthToken(mkRequest(body), env);
        expect(res.status).toBe(200);
        const j = (await res.json()) as { cfToken: string; expiresIn: number };
        expect(j.cfToken.split('.').length).toBe(3);
        expect(j.expiresIn).toBe(3600);
        // cfToken は同じ secret で verify が通り deviceId を含む
        const claims = await verifySessionToken(j.cfToken, env);
        expect(claims?.deviceId).toBe(deviceId);
        // KV に新規バインドされている
        expect(await kv.get(`device-pubkey:${deviceId}`)).toBe(body.pubKeySpki);
    });

    it('SESSION_HMAC_SECRET 未設定なら 500 SERVER_MISCONFIGURED', async () => {
        const { env: baseEnv } = makeAuthEnv();
        const env = { ...baseEnv, SESSION_HMAC_SECRET: '' } as unknown as Env;
        const deviceId = 'a'.repeat(32);
        const body = await buildSignedAuthBody(deviceId);
        const res = await handleAuthToken(mkRequest(body), env);
        expect(res.status).toBe(500);
        const j = (await res.json()) as { error: string };
        expect(j.error).toBe('SERVER_MISCONFIGURED');
    });

    it('clock skew > 60s で 400 CLOCK_SKEW', async () => {
        const { env } = makeAuthEnv();
        const deviceId = 'b'.repeat(32);
        const future = Date.now() + 120_000;
        const body = await buildSignedAuthBody(deviceId, future);
        const res = await handleAuthToken(mkRequest(body), env);
        expect(res.status).toBe(400);
        const j = (await res.json()) as { error: string };
        expect(j.error).toBe('CLOCK_SKEW');
    });

    it('別鍵の署名は 401 BAD_SIGNATURE（鍵すり替え攻撃の防御）', async () => {
        const { env } = makeAuthEnv();
        const deviceId = 'c'.repeat(32);
        const body = await buildSignedAuthBody(deviceId);
        // 攻撃者: pubKeySpki は被害者鍵のまま、sig は別鍵で作る
        const evil = await generateEcdsaP256Fixture();
        const message = new TextEncoder().encode(
            `ferry-auth-v1|${deviceId}|${body.pubKeySpki}|${body.ts}`,
        );
        const evilSig = base64UrlEncode(new Uint8Array(await signIeeeP1363(evil.privateKey, message)));
        const res = await handleAuthToken(mkRequest({ ...body, sig: evilSig }), env);
        expect(res.status).toBe(401);
        const j = (await res.json()) as { error: string };
        expect(j.error).toBe('BAD_SIGNATURE');
    });

    it('KV に既存と異なる pubKey が binding 済みなら 401 DEVICE_PUBKEY_MISMATCH', async () => {
        const { env, kv } = makeAuthEnv();
        const deviceId = 'd'.repeat(32);
        await kv.put(`device-pubkey:${deviceId}`, 'totally-different-key');
        const body = await buildSignedAuthBody(deviceId);
        const res = await handleAuthToken(mkRequest(body), env);
        expect(res.status).toBe(401);
        const j = (await res.json()) as { error: string };
        expect(j.error).toBe('DEVICE_PUBKEY_MISMATCH');
    });

    it('Codex 第7弾 #4: DEVICE_PUBKEY_MISMATCH のとき RATELIMIT_DEVICE.limit を消費しない', async () => {
        // attacker が他者 deviceId を主張して別 key で署名して repeat すると、 victim の device RL
        // を消費させてしまう DoS の回帰防止テスト。 KV binding mismatch check を device RL の前に
        // 置く設計を vitest で固定する。
        const { env: baseEnv, kv } = makeAuthEnv();
        const deviceId = 'd'.repeat(32);
        await kv.put(`device-pubkey:${deviceId}`, 'totally-different-key');
        const limitSpy = vi.fn(async () => ({ success: true }));
        const env = { ...baseEnv, RATELIMIT_DEVICE: { limit: limitSpy } } as unknown as Env;
        const body = await buildSignedAuthBody(deviceId);
        const res = await handleAuthToken(mkRequest(body), env);
        expect(res.status).toBe(401);
        const j = (await res.json()) as { error: string };
        expect(j.error).toBe('DEVICE_PUBKEY_MISMATCH');
        // 重要: mismatch では device RL を消費しない (victim deviceId の枠を attacker が奪わない)
        expect(limitSpy).not.toHaveBeenCalled();
    });

    it('Codex 第7弾 #4: KV 一致時のみ RATELIMIT_DEVICE.limit を消費する', async () => {
        // 設計の対称性を固定: binding 一致 (= 本物 client) なら RL を消費する。
        const { env: baseEnv, kv } = makeAuthEnv();
        const deviceId = 'e'.repeat(32);
        const body = await buildSignedAuthBody(deviceId);
        await kv.put(`device-pubkey:${deviceId}`, body.pubKeySpki);
        const limitSpy = vi.fn(async () => ({ success: true }));
        const env = { ...baseEnv, RATELIMIT_DEVICE: { limit: limitSpy } } as unknown as Env;
        const res = await handleAuthToken(mkRequest(body), env);
        expect(res.status).toBe(200);
        expect(limitSpy).toHaveBeenCalledOnce();
        expect(limitSpy).toHaveBeenCalledWith({ key: deviceId });
    });

    it('既存 binding が同じ pubKey なら 200 を返す（再認証 OK）', async () => {
        const { env, kv } = makeAuthEnv();
        const deviceId = 'e'.repeat(32);
        const body = await buildSignedAuthBody(deviceId);
        await kv.put(`device-pubkey:${deviceId}`, body.pubKeySpki);
        const res = await handleAuthToken(mkRequest(body), env);
        expect(res.status).toBe(200);
    });

    it('deviceId が 32hex でないと 400 INVALID_DEVICE_ID', async () => {
        const { env } = makeAuthEnv();
        const body = await buildSignedAuthBody('not-hex');
        const res = await handleAuthToken(mkRequest(body), env);
        expect(res.status).toBe(400);
        const j = (await res.json()) as { error: string };
        expect(j.error).toBe('INVALID_DEVICE_ID');
    });

    it('CF 単独完結: 返却 cfToken が verifySessionToken で deviceId に解決する', async () => {
        const { env } = makeAuthEnv();
        const deviceId = 'a'.repeat(32);
        const body = await buildSignedAuthBody(deviceId);
        const res = await handleAuthToken(mkRequest(body), env);
        expect(res.status).toBe(200);
        const j = (await res.json()) as { cfToken: string; expiresIn: number };
        expect(typeof j.cfToken).toBe('string');
        const claims = await verifySessionToken(j.cfToken, env);
        expect(claims?.deviceId).toBe(deviceId);
    });
});
