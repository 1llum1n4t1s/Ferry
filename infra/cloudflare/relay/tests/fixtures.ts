/**
 * テスト用の暗号鍵 fixture。Node.js の Web Crypto API (globalThis.crypto.subtle) を使う。
 *
 *   - generateEcdsaP256Fixture: PC 端末用 ECDSA P-256 鍵 (SPKI export + private CryptoKey) を返す
 *   - signIeeeP1363: ECDSA P-256 IEEE P1363 raw 64-byte 署名（auth.ts handleAuthToken と同一形式）
 */

export async function generateKeyPair(
    algo: RsaHashedKeyGenParams | EcKeyGenParams,
    usages: KeyUsage[],
): Promise<CryptoKeyPair> {
    return crypto.subtle.generateKey(algo, true, usages) as Promise<CryptoKeyPair>;
}

export async function exportKey(format: 'pkcs8' | 'spki', key: CryptoKey): Promise<ArrayBuffer> {
    return crypto.subtle.exportKey(format, key);
}

/** PC 端末 fixture: P-256 鍵を生成し SPKI DER + 署名用 privateKey を返す。 */
export async function generateEcdsaP256Fixture(): Promise<{ spkiDer: ArrayBuffer; privateKey: CryptoKey }> {
    const kp = await generateKeyPair(
        { name: 'ECDSA', namedCurve: 'P-256' },
        ['sign', 'verify'],
    );
    const spkiDer = await exportKey('spki', kp.publicKey);
    return { spkiDer, privateKey: kp.privateKey };
}

/** ECDSA P-256 SHA-256 IEEE P1363 raw 64-byte 署名（Workers handleAuthToken と同一形式）。 */
export async function signIeeeP1363(privateKey: CryptoKey, data: Uint8Array): Promise<ArrayBuffer> {
    return crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, privateKey, data);
}
