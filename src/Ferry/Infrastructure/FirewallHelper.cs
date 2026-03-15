using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ferry.Infrastructure;

/// <summary>
/// Windows ファイアウォールに Ferry の受信許可ルールを自動登録するヘルパー。
/// 初回起動時に UAC 昇格プロンプトを表示し、ルールがなければ追加する。
/// </summary>
public static class FirewallHelper
{
    private const string RuleName = "Ferry P2P File Transfer";

    /// <summary>
    /// Windows 環境でのみ、ファイアウォールルールの有無を確認し、
    /// なければ UAC 昇格で netsh を実行して追加する。
    /// </summary>
    public static void EnsureFirewallRule()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        try
        {
            if (RuleExists())
            {
                Util.Logger.Log("ファイアウォールルール確認済み");
                return;
            }

            Util.Logger.Log("ファイアウォールルールが未登録、追加を試行…");
            AddRule();
        }
        catch (Exception ex)
        {
            // ファイアウォール設定に失敗してもアプリ起動は続行する
            Util.Logger.Log($"ファイアウォールルール設定エラー: {ex.Message}", Util.LogLevel.Warning);
        }
    }

    /// <summary>
    /// netsh で TCP ルールの存在を確認する（昇格不要）。
    /// 旧バージョンの UDP ルールは無視する。
    /// </summary>
    private static bool RuleExists()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = $"advfirewall firewall show rule name=\"{RuleName}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process == null) return false;

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);

        // TCP ルールが存在するか確認（旧 UDP ルールとの区別）
        return output.Contains(RuleName, StringComparison.OrdinalIgnoreCase)
               && output.Contains("TCP", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// UAC 昇格で netsh を実行し、受信許可ルールを追加する。
    /// ユーザーに UAC ダイアログが表示される。
    /// </summary>
    private static void AddRule()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Util.Logger.Log("実行ファイルパスを取得できないためファイアウォールルール追加をスキップ", Util.LogLevel.Warning);
            return;
        }

        // netsh コマンドで TCP 受信許可ルールを追加（LAN 内 P2P 直接接続用）
        var arguments = $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP program=\"{exePath}\" description=\"Ferry - P2P file transfer\"";

        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            // Verb = "runas" は UseShellExecute = true の場合のみ有効
            // → 昇格は cmd /c 経由で行う
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                Util.Logger.Log("ファイアウォールルール追加プロセスの起動に失敗", Util.LogLevel.Warning);
                return;
            }

            process.WaitForExit(10000);

            if (process.ExitCode == 0)
            {
                Util.Logger.Log("ファイアウォールルール追加成功 ✓");
            }
            else
            {
                Util.Logger.Log($"ファイアウォールルール追加失敗: 終了コード {process.ExitCode}", Util.LogLevel.Warning);
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: ユーザーが UAC ダイアログで「いいえ」を選択
            Util.Logger.Log("ファイアウォールルール追加: ユーザーがキャンセル", Util.LogLevel.Warning);
        }
    }
}
