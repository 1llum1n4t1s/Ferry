using System;
using System.IO;
using System.Security.Cryptography;

namespace Ferry.Infrastructure;

/// <summary>
/// rere #D-001(b) Phase 1: このデバイスの長期 ECDH(P-256) 鍵ペアを生成・永続する。
///
/// 公開鍵(SubjectPublicKeyInfo, DER)を QR に載せてペア相手へ渡し、相手の公開鍵 × 自分の秘密鍵の
/// ECDH から <see cref="PairCrypto.DerivePairSecret"/> で PairSecret(ルート鍵)を導出する。
///
/// 秘密鍵は %APPDATA%\Ferry\identity.key に PKCS#8 DER の生バイトで保存する
/// （JSON 化しないので AOT SourceGen 不要。ImportPkcs8/ExportPkcs8/Import-ExportSubjectPublicKeyInfo は
/// すべて byte[] ベースでリフレクション不使用 = trim/AOT 安全）。鍵が無い/壊れている場合は新規生成して保存する。
///
/// DeviceId と異なり、この鍵は再生成してもペアが消えるわけではない（PairSecret 未保有のペアは
/// 平文フォールバックするだけ）。よって読み込み失敗時は安全に再生成できる。
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    private readonly ECDiffieHellman _ecdh;

    /// <summary>公開鍵(SubjectPublicKeyInfo, DER)の生バイト。</summary>
    public byte[] PublicKeySpki { get; }

    /// <summary>公開鍵の base64url（パディング無し）表現。QR クエリ（&pk=...）にそのまま載せられる。</summary>
    public string PublicKeyBase64Url { get; }

    public DeviceIdentity(string keyFilePath)
    {
        _ecdh = LoadOrCreate(keyFilePath);
        PublicKeySpki = _ecdh.ExportSubjectPublicKeyInfo();
        PublicKeyBase64Url = PairCrypto.ToBase64Url(PublicKeySpki);
    }

    /// <summary>既定パス（%APPDATA%\Ferry\identity.key、peers.json と同じディレクトリ）で構築する。</summary>
    public static DeviceIdentity CreateDefault()
        => new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Ferry", "identity.key"));

    /// <summary>
    /// 相手の公開鍵(base64url SPKI)と pairId から PairSecret(32B) を導出する。
    /// 相手鍵が空/不正/復号不能なら null を返す（呼び出し側は平文フォールバック）。
    /// </summary>
    public byte[]? TryDerivePairSecret(string? peerPublicKeyBase64Url, string pairId)
    {
        var peerSpki = PairCrypto.FromBase64Url(peerPublicKeyBase64Url);
        if (peerSpki == null) return null;
        try
        {
            var raw = PairCrypto.DeriveRawSharedSecret(_ecdh, peerSpki);
            return PairCrypto.DerivePairSecret(raw, pairId);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"PairSecret 導出に失敗（平文フォールバック）: {ex.Message}", Util.LogLevel.Warning);
            return null;
        }
    }

    private static ECDiffieHellman LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var pkcs8 = File.ReadAllBytes(path);
                var ec = ECDiffieHellman.Create();
                ec.ImportPkcs8PrivateKey(pkcs8, out _);
                return ec;
            }
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"identity.key の読み込みに失敗、鍵を再生成します: {ex.Message}", Util.LogLevel.Warning);
        }
        return CreateAndSave(path);
    }

    private static ECDiffieHellman CreateAndSave(string path)
    {
        var ec = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var pkcs8 = ec.ExportPkcs8PrivateKey();
            // 部分書き込みで壊れた鍵を残さないよう tmp→Move でアトミックに保存する。
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, pkcs8);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // 保存に失敗してもメモリ上の鍵で続行する（次回起動時に再生成 = 既存ペアは平文に落ちるだけ）。
            Util.Logger.Log($"identity.key の保存に失敗（メモリ上の鍵で続行）: {ex.Message}", Util.LogLevel.Warning);
        }
        return ec;
    }

    public void Dispose() => _ecdh.Dispose();
}
