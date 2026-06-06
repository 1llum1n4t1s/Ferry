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
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open", $"\"{dir}\"") { UseShellExecute = false }
                    : new ProcessStartInfo("xdg-open", $"\"{dir}\"") { UseShellExecute = false };
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            // 失敗ディレクトリを含める：UNC パス / リムーバブルドライブ / xdg-open 不在 等の切り分けが効く。
            // dir はローカル保存先で PII を含むが、本ログはローカル %LOCALAPPDATA% 内のみで持ち出されない。
            Logger.Log($"フォルダを開けませんでした (path={dir}): {ex.Message}", LogLevel.Warning);
        }
    }
}
