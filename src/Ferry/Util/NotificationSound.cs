using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ferry.Util;

/// <summary>
/// 受信完了などの通知音を OS ネイティブの手段で 1 回鳴らすヘルパー。best-effort（失敗は握りつぶす）。
/// Win=`MessageBeep`(user32) / mac=`afplay` / Linux=`canberra-gtk-play`→`paplay`。
/// System.Media.SystemSounds/SoundPlayer は Windows 専用アセンブリ(System.Windows.Extensions)依存で
/// クロスプラットフォーム net10.0 から参照できないため、Windows は user32 の MessageBeep を P/Invoke する。
/// P/Invoke は実行時遅延解決なので非 Windows の AOT publish を壊さない（呼び出しは OS ガードで Windows 限定）。
/// 各経路ともプロセス起動/呼び出しは非ブロッキング。
/// </summary>
public static partial class NotificationSound
{
    /// <summary>通知音を 1 回鳴らす。鳴らせない環境では静かに no-op。</summary>
    public static void Play()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                PlayWindows();
            else if (OperatingSystem.IsMacOS())
                TryStart("afplay", ["/System/Library/Sounds/Glass.aiff"], requireFile: true);
            else if (OperatingSystem.IsLinux())
                PlayLinux();
        }
        catch (Exception ex)
        {
            Logger.Log($"通知音の再生に失敗（無視）: {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>MB_ICONASTERISK(0x40) でシステムの「メッセージ（情報）」音を非同期再生する。</summary>
    [SupportedOSPlatform("windows")]
    private static void PlayWindows() => MessageBeep(0x00000040);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool MessageBeep(uint uType);

    private static void PlayLinux()
    {
        // freedesktop の標準イベント音。canberra-gtk-play が無ければ paplay でサンプル音を試す。
        if (TryStart("canberra-gtk-play", ["-i", "message-new-instant"]))
            return;
        TryStart("paplay", ["/usr/share/sounds/freedesktop/stereo/message-new-instant.oga"], requireFile: true);
    }

    /// <summary>
    /// プロセスを起動して即デタッチ（出力待ちなし、ハンドルは即解放）。
    /// コマンド不在（ENOENT）やファイル不在のときは false を返して握りつぶす（フォールバック継続用）。
    /// </summary>
    private static bool TryStart(string fileName, string[] args, bool requireFile = false)
    {
        // 第 1 引数がファイルパス前提の経路では、存在しなければ起動自体を諦める（無音 no-op）。
        if (requireFile && args.Length > 0 && !File.Exists(args[0]))
            return false;

        try
        {
            var psi = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p != null;
        }
        catch
        {
            return false;
        }
    }
}
