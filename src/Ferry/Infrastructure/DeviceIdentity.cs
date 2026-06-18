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
    /// rere #D-001(a) Phase B: 与えられたデータに対し ECDSA P-256 SHA-256 で署名する。
    /// Workers の Web Crypto API と互換を取るため、署名形式は **IEEE P1363 raw 64 byte**
    /// （32B r + 32B s 連結）で出力する（既定の DER は不採用：Web Crypto verify は IEEE P1363 を期待）。
    ///
    /// 既存の ECDH 鍵パラメータ（同一の P-256 鍵）を ECDSA に再エクスポートして署名する。
    /// 同一鍵を ECDH と ECDSA で兼用するのは NIST 推奨外だが、実装簡素化と既存資産流用のため受容。
    /// 設計詳細は docs/design/firebase-auth-pair-ssot.md §3a.3 / §4.1 参照。
    /// </summary>
    public byte[] Sign(byte[] data)
    {
        var ecParams = _ecdh.ExportParameters(includePrivateParameters: true);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(ecParams);
        return ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>
    /// 公開鍵だけで署名を検証する純関数（Workers 側 verify ロジックの C# 対応物・主にテスト用）。
    /// signature は <see cref="Sign"/> と同じ IEEE P1363 raw 形式を受ける。
    /// </summary>
    public static bool Verify(byte[] publicKeySpki, byte[] data, byte[] signature)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
        return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>
    /// rere #D-001(a) Phase B / Q2 採用案: identity.key を破棄して新規鍵を生成する（clean slate）。
    /// Workers `/auth/token` が 401 DEVICE_PUBKEY_MISMATCH を返したとき、ユーザー承認を経て呼ばれる。
    /// 既存ペアは PairSecret/Firebase auth とも整合が取れなくなるため、呼び出し側で peers.json も
    /// 同時に空配列で reset し、新規ペアリングからやり直す前提。
    /// </summary>
    public static DeviceIdentity RegenerateAndSave(string keyFilePath)
    {
        // CodeRabbit 指摘: File.Delete が失敗したのに警告だけ出して続行すると、`new DeviceIdentity(path)` が
        // 既存ファイルから古い鍵を読み込み「再生成された風で実は同じ鍵」状態になる。これは clean slate UI の
        // 意図に反し、次回 /auth/token でまた DEVICE_PUBKEY_MISMATCH を引き起こす致命的バグ。削除失敗時は
        // 例外を伝搬して clean slate UI 側に「失敗 → 後で再試行 / 手動で削除」と促す。
        if (File.Exists(keyFilePath))
        {
            File.Delete(keyFilePath);
        }
        return new DeviceIdentity(keyFilePath);  // 不在なので CreateAndSave が走る
    }

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
            // Codex 第6弾 #6 (P2): identity.key 永続化失敗時は ephemeral key で続行せず例外を伝播する。
            // 旧実装は silent in-memory continued で続行していたため、書込不可な profile (権限なし / ロック)
            // で起動すると in-memory key が Workers に bind され、次起動で別 key が生成されて
            // /auth/token が DEVICE_PUBKEY_MISMATCH を返し clean-slate UI が無条件発火していた。
            // 生成した ec は呼出側で受けないため、ここで明示 Dispose してから throw する。
            ec.Dispose();
            Util.Logger.Log($"identity.key の永続化に失敗: {ex.Message}", Util.LogLevel.Error);
            throw new InvalidOperationException(
                $"identity.key の永続化に失敗しました ({path})。プロファイルの書込権限を確認してください。", ex);
        }
        return ec;
    }

    public void Dispose() => _ecdh.Dispose();
}
