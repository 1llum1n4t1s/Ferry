using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ferry.Infrastructure;

/// <summary>
/// rere #D-001(b): ペア間 E2E 暗号の純粋プリミティブ群（static・I/O 非依存・テスト対象の中核）。
///
/// すべて Native AOT 安全な System.Security.Cryptography で構成する:
/// ECDiffieHellman(P-256/nistP256) で鍵交換、HKDF(SHA256) で pairId・方向ごとに鍵を分離導出、
/// AesGcm(256bit) で AEAD（封筒化）、HMACSHA256 で相互認証、CryptographicOperations.FixedTimeEquals で定数時間比較。
/// リフレクション不使用なので trim/AOT で安全。
///
/// 鍵階層（HKDF コンテキスト分離）:
///   PairSecret(32B, peers.json 永続) = HKDF(ECDH raw, salt=pairId, info="ferry-pair-v1")
///   sessionKey = HKDF(PairSecret, salt=offerNonce||answerNonce, info="ferry-session-v1")
///   keyO2A/keyA2O = HKDF(sessionKey, info="ferry-o2a-v1"/"ferry-a2o-v1")  # 送受で別鍵 → nonce 空間を完全分離
///   hmacKey = HKDF(sessionKey, info="ferry-auth-v1")
/// </summary>
public static class PairCrypto
{
    /// <summary>AES-256 / HMAC-SHA256 の鍵長（バイト）。</summary>
    public const int KeySize = 32;

    /// <summary>AES-GCM nonce 長（バイト）。下位 8 バイトに送信カウンタを BigEndian で格納する。</summary>
    public const int NonceSize = 12;

    /// <summary>AES-GCM 認証タグ長（バイト）。</summary>
    public const int TagSize = 16;

    /// <summary>暗号封筒のバージョンバイト（AAD に含めてダウングレードを防ぐ）。</summary>
    public const byte EnvelopeVersion = 0x01;

    /// <summary>封筒のオーバーヘッド: ver(1) + nonce(12) + tag(16) = 29 バイト。</summary>
    public const int EnvelopeOverhead = 1 + NonceSize + TagSize;

    private static readonly byte[] PairInfo = Encoding.UTF8.GetBytes("ferry-pair-v1");
    private static readonly byte[] SessionInfo = Encoding.UTF8.GetBytes("ferry-session-v1");
    private static readonly byte[] O2AInfo = Encoding.UTF8.GetBytes("ferry-o2a-v1");
    private static readonly byte[] A2OInfo = Encoding.UTF8.GetBytes("ferry-a2o-v1");
    private static readonly byte[] AuthInfo = Encoding.UTF8.GetBytes("ferry-auth-v1");

    /// <summary>暗号学的に安全な乱数バイト列を返す。</summary>
    public static byte[] RandomBytes(int length)
    {
        var b = new byte[length];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    /// <summary>
    /// 自分の ECDH 鍵と相手の SPKI（SubjectPublicKeyInfo, DER）から ECDH raw 共有秘密を導出する。
    /// DeriveKeyFromHash(SHA256) は両端で同一値になる（ECDH の対称性）。
    /// </summary>
    public static byte[] DeriveRawSharedSecret(ECDiffieHellman own, byte[] peerSpki)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerSpki, out _);
        using var peerPub = peer.PublicKey;
        return own.DeriveKeyFromHash(peerPub, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// ECDH raw 共有秘密と pairId から、peers.json に永続するルート鍵 PairSecret(32B) を導出する。
    /// pairId を salt にすることでペアごとに鍵を分離する。
    /// </summary>
    public static byte[] DerivePairSecret(byte[] rawSharedSecret, string pairId)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, rawSharedSecret, KeySize,
                          salt: Encoding.UTF8.GetBytes(pairId), info: PairInfo);

    /// <summary>
    /// 接続ごとのセッション鍵を PairSecret と offer/answer nonce から導出する。
    /// 送信方向(keyTx)・受信方向(keyRx)を別鍵にして nonce 空間を完全に分離する。
    /// <paramref name="isOfferer"/> は deviceId 序列（GeneratePairId と同じ Ordinal 比較）で決まる固定の役割で、
    /// どちらが実際に SDP offer を送ったかとは独立（両端が同じ方向→鍵の対応を得るため）。
    /// </summary>
    public static (byte[] keyTx, byte[] keyRx, byte[] hmacKey) DeriveSessionKeys(
        byte[] pairSecret, byte[] offerNonce, byte[] answerNonce, bool isOfferer)
    {
        var salt = new byte[offerNonce.Length + answerNonce.Length];
        Buffer.BlockCopy(offerNonce, 0, salt, 0, offerNonce.Length);
        Buffer.BlockCopy(answerNonce, 0, salt, offerNonce.Length, answerNonce.Length);

        var sessionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, pairSecret, KeySize, salt, SessionInfo);
        var keyO2A = HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionKey, KeySize, salt: null, info: O2AInfo);
        var keyA2O = HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionKey, KeySize, salt: null, info: A2OInfo);
        var hmacKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sessionKey, KeySize, salt: null, info: AuthInfo);

        // offerer 役の送信は o2a、answerer 役の送信は a2o。受信は逆向き。
        var keyTx = isOfferer ? keyO2A : keyA2O;
        var keyRx = isOfferer ? keyA2O : keyO2A;
        return (keyTx, keyRx, hmacKey);
    }

    /// <summary>
    /// 平文を AES-GCM で封筒化する。封筒 = [ver(1)][nonce(12)][ciphertext][tag(16)]。
    /// nonce は <paramref name="counter"/> を下位 8 バイトに BigEndian 格納（上位 4 バイトは 0）。
    /// 同一 key で同一 counter を二度使うと AES-GCM が破綻するため、呼び出し側がカウンタの単調増加を保証すること。
    /// </summary>
    public static byte[] Encrypt(byte[] key, ulong counter, ReadOnlySpan<byte> plaintext)
    {
        var envelope = new byte[EnvelopeOverhead + plaintext.Length];
        envelope[0] = EnvelopeVersion;
        var nonce = envelope.AsSpan(1, NonceSize);
        nonce.Clear();
        BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(NonceSize - 8), counter);

        var ciphertext = envelope.AsSpan(1 + NonceSize, plaintext.Length);
        var tag = envelope.AsSpan(1 + NonceSize + plaintext.Length, TagSize);

        using var aes = new AesGcm(key, TagSize);
        Span<byte> aad = stackalloc byte[1] { EnvelopeVersion };
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        return envelope;
    }

    /// <summary>
    /// AES-GCM 封筒を復号し、平文と nonce に埋め込まれた counter を返す（counter はリプレイ判定用）。
    /// 改竄（tag/ciphertext/AAD 不一致）や形式不正は <see cref="CryptographicException"/> を投げる。
    /// </summary>
    public static (byte[] plaintext, ulong counter) Decrypt(byte[] key, ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length < EnvelopeOverhead || envelope[0] != EnvelopeVersion)
            throw new CryptographicException("暗号封筒の形式が不正です。");

        var nonce = envelope.Slice(1, NonceSize);
        var counter = BinaryPrimitives.ReadUInt64BigEndian(nonce.Slice(NonceSize - 8));
        var ctLength = envelope.Length - EnvelopeOverhead;
        var ciphertext = envelope.Slice(1 + NonceSize, ctLength);
        var tag = envelope.Slice(1 + NonceSize + ctLength, TagSize);

        var plaintext = new byte[ctLength];
        using var aes = new AesGcm(key, TagSize);
        Span<byte> aad = stackalloc byte[1] { EnvelopeVersion };
        aes.Decrypt(nonce, ciphertext, tag, plaintext, aad); // 改竄時 CryptographicException
        return (plaintext, counter);
    }

    /// <summary>
    /// 相互認証用 HMAC を計算する。HMAC(hmacKey, challengeNonce || roleTag)。
    /// roleTag を含めることで、攻撃者がチャレンジをそのまま反射する reflection attack を防ぐ
    /// （各端は自分の roleTag で応答し、相手は相手の roleTag で検証する）。
    /// </summary>
    public static byte[] ComputeHmac(byte[] hmacKey, ReadOnlySpan<byte> challengeNonce, byte roleTag)
    {
        Span<byte> input = stackalloc byte[challengeNonce.Length + 1];
        challengeNonce.CopyTo(input);
        input[challengeNonce.Length] = roleTag;
        return HMACSHA256.HashData(hmacKey, input);
    }

    /// <summary>
    /// 期待 HMAC を定数時間比較で検証する（タイミング攻撃対策）。
    /// </summary>
    public static bool VerifyHmac(byte[] hmacKey, ReadOnlySpan<byte> challengeNonce, byte roleTag, ReadOnlySpan<byte> expected)
    {
        var actual = ComputeHmac(hmacKey, challengeNonce, roleTag);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
