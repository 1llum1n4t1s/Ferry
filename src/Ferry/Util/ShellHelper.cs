using System;
using System.Diagnostics;
using System.IO;

namespace Ferry.Util;

/// <summary>OS のシェル連携ヘルパー（ファイラ起動など）。</summary>
public static class ShellHelper
{
    /// <summary>
    /// 指定フォルダを OS の標準ファイルマネージャで開く。パスが空 / 存在しない場合は何もしない。
    /// MainWindow 上部の保存先アドレスバー📂から呼ばれる。
    /// </summary>
    public static void OpenFolder(string? dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            // Process.Start が返す Process は OS プロセスハンドルを保持するため using で即解放する
            // （📂を連打されてもハンドルを枯渇させない。ファイラ自体は独立プロセスとして動き続ける）。
            // 非 Windows は ArgumentList で引数を渡し、.NET に OS 流のエスケープを任せる
            // （パスに二重引用符等が含まれても argv が壊れない）。
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true };
            }
            else
            {
                psi = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
                {
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add(dir);
            }
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            // 失敗ディレクトリを含める：UNC パス / リムーバブルドライブ / xdg-open 不在 等の切り分けが効く。
            // dir はローカル保存先で PII を含むが、本ログはローカル %LOCALAPPDATA% 内のみで持ち出されない。
            Logger.Log($"フォルダを開けませんでした (path={dir}): {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>
    /// 指定 URL を OS の既定ブラウザで開く。https のみ許可（メニュー等の固定 URL 用で、
    /// 任意スキームを Process.Start に流し込まない）。macOS メニューバー「ヘルプ」から呼ばれる。
    /// </summary>
    public static void OpenUrl(string url)
    {
        try
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;

            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                // Windows は UseShellExecute で URL 関連付け（既定ブラウザ）に委譲する
                psi = new ProcessStartInfo(url) { UseShellExecute = true };
            }
            else
            {
                psi = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
                {
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add(url);
            }
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Logger.Log($"URL を開けませんでした (url={url}): {ex.Message}", LogLevel.Warning);
        }
    }
}
