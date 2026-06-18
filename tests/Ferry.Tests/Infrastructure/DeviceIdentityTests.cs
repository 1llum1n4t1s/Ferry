using System;
using System.IO;
using Ferry.Infrastructure;

namespace Ferry.Tests.Infrastructure;

/// <summary>
/// rere #D-001(b) Phase 1: DeviceIdentity（長期 ECDH 鍵の生成・永続）と base64url 変換を検証する。
///
/// 配線の正しさ（QR/Bridge/signaling のデータ流）は 2 台実機検証に委ねるが、その土台である
/// 「両端が相手の公開鍵から *同一の* PairSecret を導出できる」対称性と「鍵がファイルで安定永続する」
/// ことはここで決定的に固定する。これが崩れると暗号セッション鍵が両端で食い違い復号が全失敗する。
/// </summary>
public class DeviceIdentityTests : IDisposable
{
    private readonly string _dir;

    public DeviceIdentityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "FerryIdentityTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 後始末失敗は無視 */ }
    }

    private string KeyPath(string name) => Path.Combine(_dir, name + ".key");

    // ---- base64url ----

    [Fact]
    public void Base64Urlがラウンドトリップしパディング無しURL安全になること()
    {
        // パディングが出る長さ（length % 3 != 0）を含めて検証する。
        // 空入力は設計上 null（＝鍵なし）に倒すため対象外（別テストで検証）。
        for (var len = 1; len < 40; len++)
        {
            var data = PairCrypto.RandomBytes(len);
            var s = PairCrypto.ToBase64Url(data);
            Assert.DoesNotContain('+', s);
            Assert.DoesNotContain('/', s);
            Assert.DoesNotContain('=', s);
            Assert.Equal(data, PairCrypto.FromBase64Url(s));
        }
    }

    [Fact]
    public void FromBase64Urlが不正入力でnullを返すこと()
    {
        Assert.Null(PairCrypto.FromBase64Url(null));
        Assert.Null(PairCrypto.FromBase64Url(""));
        Assert.Null(PairCrypto.FromBase64Url("!!!not base64!!!"));
    }

    // ---- 鍵の永続 ----

    [Fact]
    public void 公開鍵がファイル永続され再構築で同一になること()
    {
        var path = KeyPath("persist");
        string pk1;
        using (var id1 = new DeviceIdentity(path))
        {
            pk1 = id1.PublicKeyBase64Url;
            Assert.False(string.IsNullOrEmpty(pk1));
        }
        Assert.True(File.Exists(path));

        using var id2 = new DeviceIdentity(path);
        Assert.Equal(pk1, id2.PublicKeyBase64Url); // 同じ鍵ファイルから同じ公開鍵
    }

    [Fact]
    public void 壊れた鍵ファイルは再生成されること()
    {
        var path = KeyPath("corrupt");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 }); // PKCS#8 として不正
        using var id = new DeviceIdentity(path);
        Assert.False(string.IsNullOrEmpty(id.PublicKeyBase64Url)); // 例外を出さず新規生成して動く
    }

    // ---- ECDH 対称性（E2E 暗号の土台）----

    [Fact]
    public void 両端が相手の公開鍵から同一のPairSecretを導出すること()
    {
        using var alice = new DeviceIdentity(KeyPath("alice"));
        using var bob = new DeviceIdentity(KeyPath("bob"));

        const string pairId = "aaaa_bbbb";
        var aliceSide = alice.TryDerivePairSecret(bob.PublicKeyBase64Url, pairId);
        var bobSide = bob.TryDerivePairSecret(alice.PublicKeyBase64Url, pairId);

        Assert.NotNull(aliceSide);
        Assert.NotNull(bobSide);
        Assert.Equal(32, aliceSide!.Length);
        Assert.Equal(aliceSide, bobSide); // ← 両端一致こそが復号成立の前提
    }

    [Fact]
    public void pairIdが違うと別のPairSecretになること()
    {
        using var alice = new DeviceIdentity(KeyPath("a2"));
        using var bob = new DeviceIdentity(KeyPath("b2"));

        var s1 = alice.TryDerivePairSecret(bob.PublicKeyBase64Url, "pair_1");
        var s2 = alice.TryDerivePairSecret(bob.PublicKeyBase64Url, "pair_2");

        Assert.NotNull(s1);
        Assert.NotNull(s2);
        Assert.NotEqual(s1, s2);
    }

    [Fact]
    public void 第三者の鍵では別のPairSecretになること()
    {
        using var alice = new DeviceIdentity(KeyPath("a3"));
        using var bob = new DeviceIdentity(KeyPath("b3"));
        using var mallory = new DeviceIdentity(KeyPath("m3"));

        const string pairId = "aaaa_bbbb";
        var withBob = alice.TryDerivePairSecret(bob.PublicKeyBase64Url, pairId);
        var withMallory = alice.TryDerivePairSecret(mallory.PublicKeyBase64Url, pairId);

        Assert.NotEqual(withBob, withMallory);
    }

    [Fact]
    public void 相手鍵が空や不正ならnullを返すこと()
    {
        using var alice = new DeviceIdentity(KeyPath("a4"));
        Assert.Null(alice.TryDerivePairSecret(null, "p"));
        Assert.Null(alice.TryDerivePairSecret("", "p"));
        Assert.Null(alice.TryDerivePairSecret("!!!garbage!!!", "p"));
    }
}
