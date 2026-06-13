using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace Ferry.Util;

/// <summary>
/// OS ログイン時の自動起動を、各 OS ネイティブの仕組みで登録/解除する。
/// Windows = レジストリ Run キー / macOS = LaunchAgent plist / Linux = XDG autostart .desktop。
/// いずれの経路も失敗してもアプリ本体の動作は止めない（best-effort）。
/// </summary>
public static class AutoStartManager
{
    /// <summary>レジストリ値名 / .desktop ファイル名のベース。</summary>
    private const string AppName = "Ferry";

    /// <summary>macOS LaunchAgent の Label・逆ドメイン。App.plist の CFBundleIdentifier と揃える。</summary>
    private const string LaunchAgentLabel = "com.1llum1n4t1s.ferry";

    private const string AutoStartRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// 自動起動を登録 (enable=true) / 解除する。enable 時は常に現在の実行ファイルパスで上書きするため、
    /// 起動時に冪等に呼べば Velopack 更新等でパスが変わっても追従できる（self-heal）。
    /// </summary>
    public static void Apply(bool enable)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                ApplyWindows(enable);
            else if (OperatingSystem.IsMacOS())
                ApplyMacOS(enable);
            else if (OperatingSystem.IsLinux())
                ApplyLinux(enable);
        }
        catch (Exception ex)
        {
            Logger.Log($"自動起動の設定に失敗: {ex.Message}", LogLevel.Error);
        }
    }

    // === Windows: HKCU\...\Run レジストリ ===

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegistryKey, writable: true);
        if (key == null)
        {
            Logger.Log("自動起動レジストリキーを開けませんでした", LogLevel.Error);
            return;
        }

        if (enable)
        {
            var exePath = GetExecutablePath();
            if (!string.IsNullOrEmpty(exePath))
            {
                key.SetValue(AppName, $"\"{exePath}\"");
                Logger.Log($"自動起動を登録（レジストリ）: {exePath}");
            }
        }
        else if (key.GetValue(AppName) != null)
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
            Logger.Log("自動起動を解除（レジストリ）");
        }
    }

    // === macOS: ~/Library/LaunchAgents/<label>.plist ===

    private static void ApplyMacOS(bool enable)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents");
        var plistPath = Path.Combine(dir, LaunchAgentLabel + ".plist");

        if (!enable)
        {
            if (File.Exists(plistPath))
            {
                File.Delete(plistPath);
                Logger.Log("自動起動を解除（LaunchAgent）");
            }
            return;
        }

        Directory.CreateDirectory(dir);
        var exe = GetExecutablePath();

        // .app バンドル内バイナリなら .app ルートを `open` で起動する方が Velopack の配置更新に強い。
        // バンドルでなければ実体バイナリを直接 ProgramArguments に置く。
        string[] programArgs;
        var marker = exe.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
        if (marker >= 0)
            programArgs = ["/usr/bin/open", exe[..(marker + 4)]];
        else
            programArgs = [exe];

        var argsXml = new StringBuilder();
        foreach (var a in programArgs)
            argsXml.Append("        <string>").Append(SecurityElement.Escape(a)).Append("</string>\n");

        var plist = $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>{LaunchAgentLabel}</string>
    <key>ProgramArguments</key>
    <array>
{argsXml}    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>ProcessType</key>
    <string>Interactive</string>
</dict>
</plist>
""";
        File.WriteAllText(plistPath, plist);
        Logger.Log($"自動起動を登録（LaunchAgent）: {plistPath}");
    }

    // === Linux: $XDG_CONFIG_HOME/autostart/ferry.desktop ===

    private static void ApplyLinux(bool enable)
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(configHome))
            configHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var dir = Path.Combine(configHome, "autostart");
        var desktopPath = Path.Combine(dir, "ferry.desktop");

        if (!enable)
        {
            if (File.Exists(desktopPath))
            {
                File.Delete(desktopPath);
                Logger.Log("自動起動を解除（autostart .desktop）");
            }
            return;
        }

        Directory.CreateDirectory(dir);
        var exe = GetExecutablePath();
        // XDG Desktop Entry の Exec は独自パーサーで処理される。スペース等の予約文字を含むときだけ
        // ダブルクォートで囲み内部の \ " ` $ をエスケープする（含まない一般パスは無クォートが最も互換）。
        var execValue = exe.IndexOfAny([' ', '\t']) >= 0
            ? "\"" + exe.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\""
            : exe;
        var content = $"""
[Desktop Entry]
Type=Application
Name=Ferry
Comment=P2P File Transfer Application
Exec={execValue}
Icon=ferry
Terminal=false
X-GNOME-Autostart-enabled=true
Hidden=false
""";
        File.WriteAllText(desktopPath, content);
        Logger.Log($"自動起動を登録（autostart .desktop）: {desktopPath}");
    }

    /// <summary>
    /// 自動起動エントリに書く実行ファイルパスを解決する。AppImage 実行時は再配置されても
    /// 安定する $APPIMAGE を優先し、それ以外は現在のプロセスパスを使う。
    /// </summary>
    private static string GetExecutablePath()
    {
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrEmpty(appImage))
            return appImage;
        return Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? string.Empty;
    }
}
