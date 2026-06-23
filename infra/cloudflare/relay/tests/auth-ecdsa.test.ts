/**
 * verifyEcdsaSig: ECDSA P-256 SHA-256 IEEE P1363 raw 署名検証の純関数テスト。
 *
 * /auth/token と /signaling・/pair/create 等の認可入口で再利用される土台。
 * 守るべき不変量:
 *   - 正しい署名は ok:true で受理
 *   - 改竄メッセージは BAD_SIGNATURE
 *   - 壊れた pubKey や sig は INVALID_SIG_FORMAT（throw でなく構造化エラー）
 */
import { describe, it, expect, beforeAll } from 'vitest';
import { verifyEcdsaSig, base64UrlEncode } from '../src/auth';

let pubKeyB64Url = '';
let validSigB64Url = '';
const message = 'ferry-auth-v1|deadbeef00112233445566778899aabb|pubkey-fixture|1700000000000';

beforeAll(async () => {
  const kp = await crypto.subtle.generateKey(
    { name: 'ECDSA', namedCurve: 'P-256' },
    true,
    ['sign', 'verify'],
  );
  const spki = await crypto.subtle.exportKey('spki', kp.publicKey);
  pubKeyB64Url = base64UrlEncode(new Uint8Array(spki));
  const sigBuf = await crypto.subtle.sign(
    { name: 'ECDSA', hash: 'SHA-256' },
    kp.privateKey,
    new TextEncoder().encode(message),
  );
  validSigB64Url = base64UrlEncode(new Uint8Array(sigBuf));
});

describe('verifyEcdsaSig', () => {
  it('正しい署名は ok:true を返す', async () => {
    const r = await verifyEcdsaSig(pubKeyB64Url, message, validSigB64Url);
    expect(r.ok).toBe(true);
  });

  it('メッセージ改竄を BAD_SIGNATURE で拒否する', async () => {
    const r = await verifyEcdsaSig(pubKeyB64Url, message + 'x', validSigB64Url);
    expect(r).toEqual({ ok: false, code: 'BAD_SIGNATURE' });
  });

  it('別の鍵で生成した署名は BAD_SIGNATURE で拒否する', async () => {
    const otherKp = await crypto.subtle.generateKey(
      { name: 'ECDSA', namedCurve: 'P-256' },
      true,
      ['sign', 'verify'],
    );
    const otherSig = await crypto.subtle.sign(
      { name: 'ECDSA', hash: 'SHA-256' },
      otherKp.privateKey,
      new TextEncoder().encode(message),
    );
    const r = await verifyEcdsaSig(pubKeyB64Url, message, base64UrlEncode(new Uint8Array(otherSig)));
    expect(r).toEqual({ ok: false, code: 'BAD_SIGNATURE' });
  });

  it('壊れた pubKey は INVALID_SIG_FORMAT を返す（throw しない）', async () => {
    const r = await verifyEcdsaSig('!!!not-base64!!!', message, validSigB64Url);
    expect(r).toEqual({ ok: false, code: 'INVALID_SIG_FORMAT' });
  });

  it('壊れた sig は ok:false を返す（throw しない・形式 or 検証どちらかで拒否）', async () => {
    // base64Url として decode できるが短すぎる sig は WebCrypto verify まで通って BAD_SIGNATURE になる。
    // decode 自体が壊れる文字列は INVALID_SIG_FORMAT になる。どちらの経路でも throw せず構造化エラーで返ることが重要。
    const tooShort = await verifyEcdsaSig(pubKeyB64Url, message, 'XX');
    expect(tooShort.ok).toBe(false);
    if (!tooShort.ok) expect(['BAD_SIGNATURE', 'INVALID_SIG_FORMAT']).toContain(tooShort.code);

    const undecodable = await verifyEcdsaSig(pubKeyB64Url, message, '!!!');
    expect(undecodable.ok).toBe(false);
  });
});
