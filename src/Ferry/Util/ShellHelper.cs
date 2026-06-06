using System;
using System.Diagnostics;
using System.IO;

namespace Ferry.Util;

/// <summary>OS のシェル連携ヘルパー（ファイラ起動など）。</summary>
public static class ShellHelper
{
    /// <summary>
    /// 指定フォルダを OS の標準ファイルマネージャで開く。パスが空 / 存在しない場合は何もしない。
    /// MainWindow の保存先アドレスバー📂と TransferView の「受信フォルダを開く」📂が共用する。
    /// </summary>
    public static void OpenFolder(string? dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", $"\"{dir}\"") { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{dir}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Logger.Log($"フォルダを開けませんでした: {ex.Message}", LogLevel.Warning);
        }
    }
}
