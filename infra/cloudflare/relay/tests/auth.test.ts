/**
 * Ferry Workers /auth/token + /pair/token の単体テスト（rere #D-001a Phase B）
 *
 * Cloudflare bindings に依存しない純関数 / pure handler ロジックを Node.js (Web Crypto 内蔵) で検証する。
 * - base64Url の URL safe ラウンドトリップ
 * - mintCustomToken の JWT 構造 (header alg=RS256 / payload claims / RS256 署名検証)
 * - handleAuthToken の正常系・別鍵 401・clock skew 400・KV first-write-wins binding
 *
 * Firebase REST 呼出を含む handlePairToken は fetch mock の重さに見合わないため対象外。
 */
import { describe, it, expect, vi } from 'vitest';
import { generateKeyPair, exportKey, generateRsaPemFixture, generateEcdsaP256Fixture, signIeeeP1363 } from './fixtures';
import {
    base64UrlEncode,
    base64UrlDecode,
    mintCustomToken,
    handleAuthToken,
    rs256Sign,
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

// ------------------ mintCustomToken / rs256Sign ------------------

describe('mintCustomToken', () => {
    it('header.alg=RS256, typ=JWT で必須 claim (iss/sub/aud/iat/exp/uid) を満たす', async () => {
        const { pem } = await generateRsaPemFixture();
        const env = {
            FIREBASE_PRIVATE_KEY: pem,
            FIREBASE_CLIENT_EMAIL: 'sa@ferry-test.iam.gserviceaccount.com',
        } as unknown as Env;

        const token = await mintCustomToken('device-uid-123', 3600, env);
        const [headerB64, payloadB64, sigB64] = token.split('.');
        expect(headerB64).toBeTruthy();
        expect(payloadB64).toBeTruthy();
        expect(sigB64).toBeTruthy();

        const header = JSON.parse(new TextDecoder().decode(base64UrlDecode(headerB64)));
        const payload = JSON.parse(new TextDecoder().decode(base64UrlDecode(payloadB64)));
        expect(header).toEqual({ alg: 'RS256', typ: 'JWT' });
        expect(payload.iss).toBe('sa@ferry-test.iam.gserviceaccount.com');
        expect(payload.sub).toBe('sa@ferry-test.iam.gserviceaccount.com');
        expect(payload.aud).toBe(
            'https://identitytoolkit.googleapis.com/google.identity.identitytoolkit.v1.IdentityToolkit',
        );
        expect(payload.uid).toBe('device-uid-123');
        expect(payload.exp).toBe(payload.iat + 3600);
        // iat は概ね今 (±5s 余裕)
        const now = Math.floor(Date.now() / 1000);
        expect(Math.abs(payload.iat - now)).toBeLessThanOrEqual(5);
    });

    it('Codex 第8弾 #2: source=bridge + extraClaims={pairAuth:true} の payload に pairAuth=true が埋まる', async () => {
        const { pem } = await generateRsaPemFixture();
        const env = { FIREBASE_PRIVATE_KEY: pem, FIREBASE_CLIENT_EMAIL: 'sa@x' } as unknown as Env;
        const token = await mintCustomToken('uid', 60, env, 'bridge', { pairAuth: true });
        const [, payloadB64] = token.split('.');
        const payload = JSON.parse(new TextDecoder().decode(base64UrlDecode(payloadB64)));
        expect(payload.claims).toEqual({ src: 'bridge', pairAuth: true });
    });

    it('Codex 第8弾 #2: source=bridge かつ extraClaims なしなら pairAuth が含まれない (1-QR token)', async () => {
        const { pem } = await generateRsaPemFixture();
        const env = { FIREBASE_PRIVATE_KEY: pem, FIREBASE_CLIENT_EMAIL: 'sa@x' } as unknown as Env;
        const token = await mintCustomToken('uid', 60, env, 'bridge');
        const [, payloadB64] = token.split('.');
        const payload = JSON.parse(new TextDecoder().decode(base64UrlDecode(payloadB64)));
        expect(payload.claims).toEqual({ src: 'bridge' });
        expect((payload.claims as Record<string, unknown>).pairAuth).toBeUndefined();
    });

    it('JWT 署名が同じ SA 公開鍵で verify 通る', async () => {
        const { pem, publicKey } = await generateRsaPemFixture();
        const env = {
            FIREBASE_PRIVATE_KEY: pem,
            FIREBASE_CLIENT_EMAIL: 'sa@x.iam',
        } as unknown as Env;
        const token = await mintCustomToken('uid', 60, env);
        const [headerB64, payloadB64, sigB64] = token.split('.');
        const unsigned = new TextEncoder().encode(`${headerB64}.${payloadB64}`);
        const verified = await crypto.subtle.verify(
            'RSASSA-PKCS1-v1_5',
            publicKey,
            base64UrlDecode(sigB64),
            unsigned,
        );
        expect(verified).toBe(true);
    });
});

describe('rs256Sign', () => {
    it('別 SA 鍵では verify が失敗する（鍵すり替え防御）', async () => {
        const { pem } = await generateRsaPemFixture();
        const { publicKey: otherPub } = await generateRsaPemFixture();
        const token = await rs256Sign({ alg: 'RS256', typ: 'JWT' }, { iss: 'x', uid: 'y' }, pem);
        const [h, p, s] = token.split('.');
        const verified = await crypto.subtle.verify(
            'RSASSA-PKCS1-v1_5',
            otherPub,
            base64UrlDecode(s),
            new TextEncoder().encode(`${h}.${p}`),
        );
        expect(verified).toBe(false);
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

async function makeAuthEnv() {
    const { pem } = await generateRsaPemFixture();
    const kv = new InMemoryKv();
    const env = {
        DEVICE_KEY_BINDING: kv,
        FIREBASE_PRIVATE_KEY: pem,
        FIREBASE_CLIENT_EMAIL: 'sa@ferry-test.iam',
        FIREBASE_DATABASE_URL: 'https://example.firebaseio.com',
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
    it('正常系: 署名検証 通過 + KV 新規バインド + customToken 返却', async () => {
        const { env, kv } = await makeAuthEnv();
        const deviceId = 'a'.repeat(32);
        const body = await buildSignedAuthBody(deviceId);
        const res = await handleAuthToken(mkRequest(body), env);
        expect(res.status).toBe(200);
        const j = (await res.json()) as { customToken: string; expiresIn: number };
        expect(j.customToken.split('.').length).toBe(3);
        expect(j.expiresIn).toBe(3600);
        // KV に新規バインドされている
        expect(await kv.get(`device-pubkey:${deviceId}`)).toBe(body.pubKeySpki);
    });

    it('clock skew > 60s で 400 CLOCK_SKEW', async () => {
        const { env } = await makeAuthEnv();
        const deviceId = 'b'.repeat(32);
        const future = Date.now() + 120_000;
        const body = await buildSignedAuthBody(deviceId, future);
        const res = await handleAuthToken(mkRequest(body), env);
        expect(res.status).toBe(400);
        const j = (await res.json()) as { error: string };
        expect(j.error).toBe('CLOCK_SKEW');
    });

    it('別鍵の署名は 401 BAD_SIGNATURE（鍵すり替え攻撃の防御）', async () => {
        const { env } = await makeAuthEnv();
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
        const { env, kv } = await makeAuthEnv();
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
        const { env: baseEnv, kv } = await makeAuthEnv();
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
        const { env: baseEnv, kv } = await makeAuthEnv();
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
        const { env, kv } = await makeAuthEnv();
        const deviceId = 'e'.repeat(32);
        const body = await buildSignedAuthBody(deviceId);
        await kv.put(`device-pubkey:${deviceId}`, body.pubKeySpki);
        const res = await handleAuthToken(mkRequest(body), env);
        expect(res.status).toBe(200);
    });

    it('deviceId が 32hex でないと 400 INVALID_DEVICE_ID', async () => {
        const { env } = await makeAuthEnv();
        const body = await buildSignedAuthBody('not-hex');
        const res = await handleAuthToken(mkRequest(body), env);
        expect(res.status).toBe(400);
        const j = (await res.json()) as { error: string };
        expect(j.error).toBe('INVALID_DEVICE_ID');
    });
});

// keep imports referenced (suppresses TS unused warning if a test is commented out)
void generateKeyPair;
void exportKey;
