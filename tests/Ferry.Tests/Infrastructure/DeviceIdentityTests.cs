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

    // ---- Sign / Verify (#D-001a Phase B) ----

    [Fact]
    public void Sign_IEEE_P1363_raw64byteを返す()
    {
        using var id = new DeviceIdentity(KeyPath("sign-format"));
        var data = System.Text.Encoding.UTF8.GetBytes("ferry-auth-v1|abc|def|1234567890");
        var sig = id.Sign(data);
        // P-256 の IEEE P1363 raw 形式 = 32 byte r + 32 byte s 連結 = 64 byte 固定。
        // DER だと典型 70-72 byte 可変なのでここで形式違いを検出できる（Web Crypto 互換性の核心）。
        Assert.Equal(64, sig.Length);
    }

    [Fact]
    public void SignVerifyラウンドトリップが同一インスタンスの公開鍵で成功する()
    {
        using var id = new DeviceIdentity(KeyPath("rt-same"));
        var data = System.Text.Encoding.UTF8.GetBytes("hello-ferry-auth");
        var sig = id.Sign(data);
        Assert.True(DeviceIdentity.Verify(id.PublicKeySpki, data, sig));
    }

    [Fact]
    public void Verifyは別鍵の署名を拒否する()
    {
        using var id1 = new DeviceIdentity(KeyPath("rt-other-1"));
        using var id2 = new DeviceIdentity(KeyPath("rt-other-2"));
        var data = System.Text.Encoding.UTF8.GetBytes("ferry");
        var sig1 = id1.Sign(data);
        // id1 の署名を id2 の公開鍵で検証 → 必ず失敗（鍵すり替え攻撃の防御確認）
        Assert.False(DeviceIdentity.Verify(id2.PublicKeySpki, data, sig1));
    }

    [Fact]
    public void Verifyはメッセージ改竄を拒否する()
    {
        using var id = new DeviceIdentity(KeyPath("rt-tampered"));
        var data = System.Text.Encoding.UTF8.GetBytes("ferry-auth-v1|original");
        var tampered = System.Text.Encoding.UTF8.GetBytes("ferry-auth-v1|tampered");
        var sig = id.Sign(data);
        Assert.True(DeviceIdentity.Verify(id.PublicKeySpki, data, sig));
        Assert.False(DeviceIdentity.Verify(id.PublicKeySpki, tampered, sig));
    }

    [Fact]
    public void Signは決定論的ではなく毎回異なる署名を返す()
    {
        // ECDSA は仕様で nonce ランダム → 同じデータでも異なる signature。両方とも verify は通る。
        using var id = new DeviceIdentity(KeyPath("non-det"));
        var data = System.Text.Encoding.UTF8.GetBytes("same-data");
        var sig1 = id.Sign(data);
        var sig2 = id.Sign(data);
        Assert.NotEqual(sig1, sig2);
        Assert.True(DeviceIdentity.Verify(id.PublicKeySpki, data, sig1));
        Assert.True(DeviceIdentity.Verify(id.PublicKeySpki, data, sig2));
    }

    [Fact]
    public void RegenerateAndSaveは旧鍵を破棄し新鍵を生成する()
    {
        var path = KeyPath("regen");
        byte[] oldSpki;
        using (var oldId = new DeviceIdentity(path))
        {
            oldSpki = oldId.PublicKeySpki;
        }
        // ファイル存在を確認してから regenerate
        Assert.True(File.Exists(path));
        using var newId = DeviceIdentity.RegenerateAndSave(path);
        Assert.True(File.Exists(path));  // 新しい鍵で再生成されている
        Assert.NotEqual(oldSpki, newId.PublicKeySpki);  // 鍵が変わったこと
        // 旧鍵の署名は新鍵の公開鍵では verify されない
        // （Q2 clean slate の挙動を固定：DEVICE_PUBKEY_MISMATCH 状態をユニットで再現可能）
        using var anotherSign = new DeviceIdentity(KeyPath("regen-other"));
        var data = System.Text.Encoding.UTF8.GetBytes("clean-slate");
        var sigFromOther = anotherSign.Sign(data);
        Assert.False(DeviceIdentity.Verify(newId.PublicKeySpki, data, sigFromOther));
    }

    [Fact]
    public void RegenerateAndSaveはファイルが存在しなくても新規生成する()
    {
        var path = KeyPath("regen-fresh");
        Assert.False(File.Exists(path));
        using var id = DeviceIdentity.RegenerateAndSave(path);
        Assert.True(File.Exists(path));
        Assert.Equal(91, id.PublicKeySpki.Length); // P-256 SPKI は 91 byte 固定
    }

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
