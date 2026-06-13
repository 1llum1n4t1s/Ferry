using System;
using System.IO;

namespace Ferry.Util;

/// <summary>
/// 受信ファイルの保存パスを「送信元 OS に依存せず」安全に組み立てるための純関数ヘルパー群。
/// 区切り正規化・パストラバーサル判定・保存先ディレクトリ内包判定をクロス OS（Win/mac/Linux）で
/// 一貫させる。受信側（<see cref="Ferry.Services.TransferService"/> の HandleFileMeta）から使う。
/// </summary>
public static class SafePath
{
    /// <summary>パス区切りを '/' に正規化する。Windows 送信側の '\' を mac/Linux 受信側でも正しく扱うため。</summary>
    public static string NormalizeSeparators(string path)
        => string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');

    /// <summary>
    /// 文字列に制御文字（NUL '\0' を含む C0/C1 制御文字・改行・タブ等）が含まれるかを判定する。
    /// ピア制御のファイル名/相対パスに NUL が混入すると <see cref="Path.GetFullPath"/> 等が
    /// ArgumentException("Null character in path") を投げ、未捕捉だと受信ループ→接続切断（DoS）に至る。
    /// 正規のファイル名に制御文字は現れないため、検出したら不正入力として早期に弾くために使う。
    /// </summary>
    public static bool ContainsControlChar(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return false;
        foreach (var c in s)
            if (char.IsControl(c))
                return true;
        return false;
    }

    /// <summary>
    /// '/' 正規化済み相対パスに親参照 ".." の「パス要素」が含まれるかを判定する。
    /// substring ではなく要素単位なので、"my..file.txt" のような正規名を誤って弾かない。
    /// </summary>
    public static bool HasParentTraversal(string normalizedRelativePath)
    {
        if (string.IsNullOrEmpty(normalizedRelativePath))
            return false;
        foreach (var part in normalizedRelativePath.Split('/'))
            if (part == "..")
                return true;
        return false;
    }

    /// <summary>
    /// '/' 正規化済み相対パスの「先頭要素」が空（先頭 "/" = 絶対パス化けの兆候）または "." のとき true。
    /// 正規の相対パスは先頭が実フォルダ名なので、これらは不正入力として弾く（".." は <see cref="HasParentTraversal"/> が担当）。
    /// 先頭空要素を放置すると保存先がサイレントにフラット化されるため、明示的に拒否する。
    /// </summary>
    public static bool HasUnsafeRoot(string normalizedRelativePath)
    {
        if (string.IsNullOrEmpty(normalizedRelativePath))
            return false;
        var first = normalizedRelativePath.Split('/')[0];
        return first.Length == 0 || first == ".";
    }

    /// <summary>
    /// ピア制御のファイル名からディレクトリ要素を除いた安全なファイル名を返す。
    /// 空 / "." / ".." は不正として null を返す。区切り正規化込みなので、Windows 送信側の
    /// '\' 区切り（例: "sub\file.txt"）でも mac/Linux 受信側で正しく basename ("file.txt") を取り出す。
    /// </summary>
    public static string? SafeFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;
        // NUL 等の制御文字は Path.* で例外を誘発する（受信ループ伝播で接続切断＝DoS）ため不正名として弾く。
        if (ContainsControlChar(fileName))
            return null;
        var name = Path.GetFileName(NormalizeSeparators(fileName));
        return string.IsNullOrEmpty(name) || name == "." || name == ".." ? null : name;
    }

    /// <summary>
    /// candidatePath が baseDir 配下（親方向への脱出なし）に収まるかを判定する。
    /// 文字列 StartsWith ではなく <see cref="Path.GetRelativePath"/> で相対化して判定するため、
    /// 区切り・大小・正規化のクロス OS 差を OS 既定の比較規則に委ねられる
    /// （Windows/macOS=大小無視、Linux=大小区別）。脱出（"../"）・絶対パス化けを弾く。
    /// </summary>
    public static bool IsWithinDirectory(string baseDir, string candidatePath)
    {
        try
        {
            var fullBase = Path.GetFullPath(baseDir);
            var fullCandidate = Path.GetFullPath(candidatePath);
            var rel = Path.GetRelativePath(fullBase, fullCandidate);
            return rel != ".."
                && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !Path.IsPathRooted(rel);
        }
        catch (Exception)
        {
            // NUL 文字混入などで Path.* が ArgumentException を投げるケース。例外を呼び出し元
            // （受信ループ）へ伝播させると接続が切れる（DoS）ため、不正パス＝範囲外（false）として
            // 安全側に倒す。これが最終防御で、どんな不正入力が来ても throw せず必ず拒否に倒れる。
            return false;
        }
    }
}
