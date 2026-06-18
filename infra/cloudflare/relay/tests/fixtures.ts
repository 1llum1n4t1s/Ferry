/**
 * テスト用の暗号鍵 fixture。Node.js の Web Crypto API (globalThis.crypto.subtle) を使う。
 *
 *   - generateRsaPemFixture: SA 用 RSA-2048 鍵を生成して PKCS#8 PEM 文字列 + CryptoKey publicKey を返す
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

/**
 * 生 PKCS#8 DER を PEM 文字列（BEGIN/END 区切り + 64桁折返）に整形する。
 * Workers の pemPkcs8ToDer はこの形式の逆向き変換のみ受ける。
 */
function pkcs8DerToPem(der: ArrayBuffer): string {
    const bytes = new Uint8Array(der);
    let bin = '';
    for (const b of bytes) bin += String.fromCharCode(b);
    const b64 = btoa(bin);
    const wrapped = b64.match(/.{1,64}/g)?.join('\n') ?? b64;
    return `-----BEGIN PRIVATE KEY-----\n${wrapped}\n-----END PRIVATE KEY-----`;
}

/** SA fixture: 一意な RSA-2048 鍵を生成して PEM 文字列 + verify 用 publicKey を返す。 */
export async function generateRsaPemFixture(): Promise<{ pem: string; publicKey: CryptoKey }> {
    const kp = await generateKeyPair(
        {
            name: 'RSASSA-PKCS1-v1_5',
            modulusLength: 2048,
            publicExponent: new Uint8Array([1, 0, 1]),
            hash: 'SHA-256',
        },
        ['sign', 'verify'],
    );
    const der = await exportKey('pkcs8', kp.privateKey);
    return { pem: pkcs8DerToPem(der), publicKey: kp.publicKey };
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
