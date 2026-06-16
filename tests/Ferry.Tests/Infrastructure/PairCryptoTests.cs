using System.Security.Cryptography;
using Ferry.Infrastructure;

namespace Ferry.Tests.Infrastructure;

/// <summary>
/// rere #D-001(b): ペア間 E2E 暗号の純粋プリミティブ（PairCrypto / SecureSession / PairingHandshake）を検証する。
/// 純関数なので I/O 非依存でテストできる。ConnectionService/transport の結線は実機検証に委ね、
/// 暗号の正しさ（鍵導出の決定性・送受鍵分離・AEAD 改竄検出・nonce 一意性・リプレイ拒否・HMAC 相互認証）を
/// ここで網羅的に固定する。
///
/// ⚠️ 重要: これらのクラスは現在 **live コードから未呼出（inert）** であり、本テストが通っても
/// **転送は今も平文**（暗号は未有効）。実際の保護は #D-001b の配線（QR pk 交換 / HMAC ゲート /
/// AES-GCM 封筒を ConnectionService に結線, Phase1/2）が入って初めて働く。この事実に基づき、
/// CLAUDE.md §既知の制限「転送ペイロードは平文」の記述を本テスト合格を理由に削除しないこと。
/// </summary>
public class PairCryptoTests
{
    private static byte[] MakePairSecret(string seed = "seed")
        => PairCrypto.DerivePairSecret(System.Text.Encoding.UTF8.GetBytes(seed.PadRight(32, 'x')), "PAIR-1");

    // ---- ECDH 鍵交換 ----

    [Fact]
    public void ECDHの共有秘密が両端で一致すること()
    {
        using var alice = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bob = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var aliceSpki = alice.ExportSubjectPublicKeyInfo();
        var bobSpki = bob.ExportSubjectPublicKeyInfo();

        var aliceSide = PairCrypto.DeriveRawSharedSecret(alice, bobSpki);
        var bobSide = PairCrypto.DeriveRawSharedSecret(bob, aliceSpki);

        Assert.Equal(aliceSide, bobSide);
        Assert.Equal(32, aliceSide.Length); // SHA-256 出力
    }

    [Fact]
    public void 別のECDH鍵では共有秘密が一致しないこと()
    {
        using var alice = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bob = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var mallory = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var aliceBob = PairCrypto.DeriveRawSharedSecret(alice, bob.ExportSubjectPublicKeyInfo());
        var aliceMallory = PairCrypto.DeriveRawSharedSecret(alice, mallory.ExportSubjectPublicKeyInfo());

        Assert.NotEqual(aliceBob, aliceMallory);
    }

    // ---- HKDF 鍵導出 ----

    [Fact]
    public void PairSecret導出が決定的でありpairId違いで別鍵になること()
    {
        var raw = PairCrypto.RandomBytes(32);
        var a1 = PairCrypto.DerivePairSecret(raw, "PAIR-A");
        var a2 = PairCrypto.DerivePairSecret(raw, "PAIR-A");
        var b = PairCrypto.DerivePairSecret(raw, "PAIR-B");

        Assert.Equal(a1, a2);       // 決定的
        Assert.NotEqual(a1, b);     // salt(pairId) で分離
        Assert.Equal(32, a1.Length);
    }

    [Fact]
    public void セッション鍵が送受信とHMACで全て別鍵になること()
    {
        var pairSecret = MakePairSecret();
        var offerNonce = PairCrypto.RandomBytes(16);
        var answerNonce = PairCrypto.RandomBytes(16);

        var (keyTx, keyRx, hmacKey) = PairCrypto.DeriveSessionKeys(pairSecret, offerNonce, answerNonce, isOfferer: true);

        Assert.NotEqual(keyTx, keyRx);
        Assert.NotEqual(keyTx, hmacKey);
        Assert.NotEqual(keyRx, hmacKey);
    }

    [Fact]
    public void offererのkeyTxとanswererのkeyRxが一致すること()
    {
        // offerer が keyTx で暗号化したものを answerer が keyRx で復号できる対応関係。
        var pairSecret = MakePairSecret();
        var offerNonce = PairCrypto.RandomBytes(16);
        var answerNonce = PairCrypto.RandomBytes(16);

        var offerer = PairCrypto.DeriveSessionKeys(pairSecret, offerNonce, answerNonce, isOfferer: true);
        var answerer = PairCrypto.DeriveSessionKeys(pairSecret, offerNonce, answerNonce, isOfferer: false);

        Assert.Equal(offerer.keyTx, answerer.keyRx);  // o2a 方向
        Assert.Equal(offerer.keyRx, answerer.keyTx);  // a2o 方向
        Assert.Equal(offerer.hmacKey, answerer.hmacKey); // HMAC は共通
    }

    [Fact]
    public void nonce違いでセッション鍵が変わること()
    {
        var pairSecret = MakePairSecret();
        var n1 = PairCrypto.DeriveSessionKeys(pairSecret, PairCrypto.RandomBytes(16), PairCrypto.RandomBytes(16), true);
        var n2 = PairCrypto.DeriveSessionKeys(pairSecret, PairCrypto.RandomBytes(16), PairCrypto.RandomBytes(16), true);
        Assert.NotEqual(n1.keyTx, n2.keyTx);
    }

    // ---- AES-GCM 封筒 ----

    [Fact]
    public void AESGCM封筒のラウンドトリップが元の平文を復元すること()
    {
        var key = PairCrypto.RandomBytes(32);
        var plaintext = System.Text.Encoding.UTF8.GetBytes("ferry secret payload 🚢");

        var envelope = PairCrypto.Encrypt(key, counter: 0, plaintext);
        var (decrypted, counter) = PairCrypto.Decrypt(key, envelope);

        Assert.Equal(plaintext, decrypted);
        Assert.Equal(0UL, counter);
        Assert.Equal(PairCrypto.EnvelopeOverhead + plaintext.Length, envelope.Length);
        Assert.Equal(PairCrypto.EnvelopeVersion, envelope[0]);
    }

    [Fact]
    public void 空の平文も封筒化復号できること()
    {
        var key = PairCrypto.RandomBytes(32);
        var envelope = PairCrypto.Encrypt(key, 7, System.ReadOnlySpan<byte>.Empty);
        var (decrypted, counter) = PairCrypto.Decrypt(key, envelope);
        Assert.Empty(decrypted);
        Assert.Equal(7UL, counter);
    }

    [Fact]
    public void counterがnonceに正しく往復すること()
    {
        var key = PairCrypto.RandomBytes(32);
        foreach (var c in new ulong[] { 0, 1, 255, 256, 65536, ulong.MaxValue })
        {
            var env = PairCrypto.Encrypt(key, c, new byte[] { 1, 2, 3 });
            var (_, counter) = PairCrypto.Decrypt(key, env);
            Assert.Equal(c, counter);
        }
    }

    [Fact]
    public void tag改竄を検出して復号が例外になること()
    {
        var key = PairCrypto.RandomBytes(32);
        var envelope = PairCrypto.Encrypt(key, 0, new byte[] { 10, 20, 30 });
        envelope[^1] ^= 0xFF; // tag 末尾を反転
        Assert.ThrowsAny<CryptographicException>(() => PairCrypto.Decrypt(key, envelope));
    }

    [Fact]
    public void ciphertext改竄を検出して復号が例外になること()
    {
        var key = PairCrypto.RandomBytes(32);
        var envelope = PairCrypto.Encrypt(key, 0, new byte[] { 10, 20, 30, 40 });
        envelope[1 + PairCrypto.NonceSize] ^= 0x01; // 先頭 ciphertext バイトを改竄
        Assert.ThrowsAny<CryptographicException>(() => PairCrypto.Decrypt(key, envelope));
    }

    [Fact]
    public void versionバイト改竄でAAD不一致になり復号が例外になること()
    {
        var key = PairCrypto.RandomBytes(32);
        var envelope = PairCrypto.Encrypt(key, 0, new byte[] { 1, 2, 3 });
        envelope[0] = 0x02; // ver を改竄（AAD に含まれるので tag 検証失敗 + 形式チェック）
        Assert.ThrowsAny<CryptographicException>(() => PairCrypto.Decrypt(key, envelope));
    }

    [Fact]
    public void 別の鍵では復号できないこと()
    {
        var envelope = PairCrypto.Encrypt(PairCrypto.RandomBytes(32), 0, new byte[] { 9 });
        Assert.ThrowsAny<CryptographicException>(() => PairCrypto.Decrypt(PairCrypto.RandomBytes(32), envelope));
    }

    [Fact]
    public void 短すぎる封筒は形式不正で例外になること()
    {
        var key = PairCrypto.RandomBytes(32);
        Assert.ThrowsAny<CryptographicException>(() => PairCrypto.Decrypt(key, new byte[5]));
    }

    // ---- HMAC 相互認証 ----

    [Fact]
    public void HMAC相互認証が正規の応答を受理し改竄を拒否すること()
    {
        var hmacKey = PairCrypto.RandomBytes(32);
        var challenge = PairingHandshake.BuildChallenge();
        var response = PairingHandshake.BuildResponse(hmacKey, challenge, PairingHandshake.RoleLow);

        // 検証側は応答者の roleTag(RoleLow) で検証
        Assert.True(PairCrypto.VerifyHmac(hmacKey, challenge, PairingHandshake.RoleLow, response));
        // 改竄応答は拒否
        response[0] ^= 0xFF;
        Assert.False(PairCrypto.VerifyHmac(hmacKey, challenge, PairingHandshake.RoleLow, response));
    }

    [Fact]
    public void 別の鍵のHMAC応答を拒否すること()
    {
        var challenge = PairingHandshake.BuildChallenge();
        var response = PairingHandshake.BuildResponse(PairCrypto.RandomBytes(32), challenge, PairingHandshake.RoleLow);
        Assert.False(PairCrypto.VerifyHmac(PairCrypto.RandomBytes(32), challenge, PairingHandshake.RoleLow, response));
    }

    [Fact]
    public void roleTagが違うとHMAC検証が失敗すること()
    {
        // reflection 対策: 応答者の roleTag と検証時の roleTag が食い違うと不一致になる。
        var hmacKey = PairCrypto.RandomBytes(32);
        var challenge = PairingHandshake.BuildChallenge();
        var response = PairingHandshake.BuildResponse(hmacKey, challenge, PairingHandshake.RoleLow);
        Assert.False(PairCrypto.VerifyHmac(hmacKey, challenge, PairingHandshake.RoleHigh, response));
    }

    [Fact]
    public void roleTagがdeviceId序列で一貫し相互に裏返ること()
    {
        const string low = "aaaa";
        const string high = "zzzz";
        Assert.Equal(PairingHandshake.RoleLow, PairingHandshake.RoleTagFor(low, high));
        Assert.Equal(PairingHandshake.RoleHigh, PairingHandshake.RoleTagFor(high, low));
        Assert.Equal(PairingHandshake.RoleHigh, PairingHandshake.PeerRoleTag(PairingHandshake.RoleLow));
        Assert.Equal(PairingHandshake.RoleLow, PairingHandshake.PeerRoleTag(PairingHandshake.RoleHigh));
    }

    [Fact]
    public void 双方向HMACハンドシェイクが成立すること()
    {
        // A(low) と B(high) が hmacKey を共有。互いのチャレンジに応答し相互検証する。
        var pairSecret = MakePairSecret();
        var offerNonce = PairCrypto.RandomBytes(16);
        var answerNonce = PairCrypto.RandomBytes(16);
        var aKeys = PairCrypto.DeriveSessionKeys(pairSecret, offerNonce, answerNonce, isOfferer: true);
        var bKeys = PairCrypto.DeriveSessionKeys(pairSecret, offerNonce, answerNonce, isOfferer: false);

        var aRole = PairingHandshake.RoleLow;
        var bRole = PairingHandshake.RoleHigh;
        var aChallenge = PairingHandshake.BuildChallenge();
        var bChallenge = PairingHandshake.BuildChallenge();

        // 各端は相手のチャレンジに自分の roleTag で応答
        var aResponse = PairingHandshake.BuildResponse(aKeys.hmacKey, bChallenge, aRole);
        var bResponse = PairingHandshake.BuildResponse(bKeys.hmacKey, aChallenge, bRole);

        // 各端は自分のチャレンジへの応答を相手の roleTag で検証
        Assert.True(PairingHandshake.VerifyResponse(aKeys.hmacKey, aChallenge, bRole, bResponse));
        Assert.True(PairingHandshake.VerifyResponse(bKeys.hmacKey, bChallenge, aRole, aResponse));
    }

    // ---- SecureSession 往復とリプレイ ----

    private static (SecureSession offerer, SecureSession answerer) MakeSessionPair()
    {
        var pairSecret = MakePairSecret();
        var offerNonce = PairCrypto.RandomBytes(16);
        var answerNonce = PairCrypto.RandomBytes(16);
        var o = PairCrypto.DeriveSessionKeys(pairSecret, offerNonce, answerNonce, true);
        var a = PairCrypto.DeriveSessionKeys(pairSecret, offerNonce, answerNonce, false);
        return (new SecureSession(o.keyTx, o.keyRx, o.hmacKey),
                new SecureSession(a.keyTx, a.keyRx, a.hmacKey));
    }

    [Fact]
    public void SecureSessionのEncryptを相手のDecryptで復元できること()
    {
        var (offerer, answerer) = MakeSessionPair();
        var payload = System.Text.Encoding.UTF8.GetBytes("hello ferry");

        var env = offerer.EncryptOutgoing(payload);
        var got = answerer.DecryptIncoming(env);
        Assert.Equal(payload, got);

        // 逆方向も
        var env2 = answerer.EncryptOutgoing(new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, offerer.DecryptIncoming(env2));
    }

    [Fact]
    public void 同一封筒の再受信をリプレイとして拒否すること()
    {
        var (offerer, answerer) = MakeSessionPair();
        var env = offerer.EncryptOutgoing(new byte[] { 42 });

        Assert.Equal(new byte[] { 42 }, answerer.DecryptIncoming(env));
        Assert.Null(answerer.DecryptIncoming(env)); // 2 度目はリプレイ
    }

    [Fact]
    public void 順不同の受信を受理し重複を拒否すること()
    {
        var (offerer, answerer) = MakeSessionPair();
        // counter 0,1,2,3 の封筒を作り、順不同(2,0,3,1)で受信
        var envs = new System.Collections.Generic.List<byte[]>();
        for (var i = 0; i < 4; i++) envs.Add(offerer.EncryptOutgoing(new byte[] { (byte)i }));

        Assert.NotNull(answerer.DecryptIncoming(envs[2]));
        Assert.NotNull(answerer.DecryptIncoming(envs[0]));
        Assert.NotNull(answerer.DecryptIncoming(envs[3]));
        Assert.NotNull(answerer.DecryptIncoming(envs[1]));
        // 既受信はすべて拒否
        Assert.Null(answerer.DecryptIncoming(envs[0]));
        Assert.Null(answerer.DecryptIncoming(envs[3]));
    }

    [Fact]
    public void 窓外の古いカウンタを拒否すること()
    {
        var (offerer, answerer) = MakeSessionPair();
        // 窓幅を超える数だけ進める（最初の 1 個を保留して窓外に追い出す）
        var first = offerer.EncryptOutgoing(new byte[] { 0 });
        for (var i = 0; i < SecureSession.ReplayWindowSize + 10; i++)
            Assert.NotNull(answerer.DecryptIncoming(offerer.EncryptOutgoing(new byte[] { 1 })));

        // counter 0 は窓外に押し出されたので拒否
        Assert.Null(answerer.DecryptIncoming(first));
    }

    [Fact]
    public void 送信カウンタが単調増加し封筒のnonceが一意であること()
    {
        var (offerer, _) = MakeSessionPair();
        var nonces = new System.Collections.Generic.HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            var env = offerer.EncryptOutgoing(new byte[] { 0 });
            var nonce = System.Convert.ToBase64String(env.AsSpan(1, PairCrypto.NonceSize));
            Assert.True(nonces.Add(nonce), "nonce が重複した");
        }
    }

    [Fact]
    public void 窓の最内端カウンタを受理しそれより1つ古いと拒否すること()
    {
        // crypto レビュー #D-001b: 窓境界 N-(WindowSize-1)=受理 / N-WindowSize=拒否 の off-by-one 回帰テスト。
        var (offerer, answerer) = MakeSessionPair();
        var w = SecureSession.ReplayWindowSize;
        var n = w + 5;
        var envs = new System.Collections.Generic.List<byte[]>();
        for (var i = 0; i <= n; i++) envs.Add(offerer.EncryptOutgoing(new byte[] { 0 }));

        // 最大カウンタ N だけ受信 → _rxHighest=N（窓クリア・他スロット未受信）
        Assert.NotNull(answerer.DecryptIncoming(envs[n]));
        // 窓の最内端 N-(WindowSize-1) は受理される
        Assert.NotNull(answerer.DecryptIncoming(envs[n - (w - 1)]));
        // 1 つ古い N-WindowSize は窓外で拒否
        Assert.Null(answerer.DecryptIncoming(envs[n - w]));
    }

    [Fact]
    public void 同一deviceIdはself_connectとして例外になること()
    {
        // crypto レビュー #D-001b: 等値 deviceId は roleTag が対称化して reflection 防御が無効になるため弾く。
        Assert.Throws<System.ArgumentException>(() => PairingHandshake.IsOffererRole("same", "same"));
        Assert.Throws<System.ArgumentException>(() => PairingHandshake.RoleTagFor("same", "same"));
    }

    [Fact]
    public void IsOffererRoleがroleTagと一貫すること()
    {
        Assert.True(PairingHandshake.IsOffererRole("aaaa", "zzzz"));
        Assert.False(PairingHandshake.IsOffererRole("zzzz", "aaaa"));
        // RoleTagFor は IsOffererRole から導出されるので必ず一致する
        Assert.Equal(PairingHandshake.RoleLow, PairingHandshake.RoleTagFor("aaaa", "zzzz"));
        Assert.Equal(PairingHandshake.RoleHigh, PairingHandshake.RoleTagFor("zzzz", "aaaa"));
    }
}
