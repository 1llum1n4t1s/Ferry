using System;
using System.IO;

namespace Ferry.Util;

/// <summary>
/// アプリのデータ配置パスを OS ごとに解決するヘルパー。
/// 現状はログ出力先のみを集約する（settings.json / peers.json は DeviceId・ペア情報の
/// 移行リスクがあるため従来の <see cref="Environment.SpecialFolder.ApplicationData"/> 配置を維持）。
/// </summary>
public static class AppPaths
{
    private const string AppFolder = "Ferry";

    /// <summary>
    /// ログ出力ディレクトリを解決する。
    /// macOS は慣習に従い <c>~/Library/Logs/Ferry</c>（Console.app から見える・常に存在し書込可）。
    /// .NET の <see cref="Environment.SpecialFolder.LocalApplicationData"/> は macOS で
    /// <c>~/.local/share</c>（隠し・非慣習）へ解決され、環境次第で空文字を返すと相対パス化して
    /// 書込失敗 → TEMP 退避と「どこに出ているか分からない」状態を招くため、明示パスに寄せる。
    /// Windows は <c>%LOCALAPPDATA%\Ferry\logs</c>、Linux は XDG の <c>~/.local/share/Ferry/logs</c>。
    /// </summary>
    public static string GetLogDirectory()
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
                return Path.Combine(home, "Library", "Logs", AppFolder);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // GetFolderPath が空を返す環境（一部の Unix 起動文脈）で相対パス化するのを防ぐ保険。
        if (string.IsNullOrEmpty(localAppData))
            localAppData = Path.GetTempPath();
        return Path.Combine(localAppData, AppFolder, "logs");
    }
}
