using System;
using System.IO;
using Avalonia;
using Ferry.Util;
using Velopack;

namespace Ferry;

internal sealed class Program
{
    /// <summary>
    /// アプリケーションのエントリポイント。Velopack のブートストラップを最初に実行し、
    /// Avalonia アプリを起動する。
    /// </summary>
    /// <remarks>
    /// 【重要】Main は void でなければならない。async Task にすると [STAThread] が無視され、
    /// スレッドが MTA になり、COM ベースの DragDrop が完全に動作しなくなる。
    /// See: https://github.com/AvaloniaUI/Avalonia/issues/12499
    /// </remarks>
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        try
        {
            Logger.Initialize(new LoggerConfig
            {
                LogDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ferry", "logs"),
                FilePrefix = "Ferry",
            });
        }
        catch
        {
            // ログ初期化失敗（権限不足・ディスクフル・フォルダロック等）。
            // TEMP にフォールバックし、それも失敗したらログ無しで起動を継続する
            try
            {
                Logger.Initialize(new LoggerConfig
                {
                    LogDirectory = Path.Combine(Path.GetTempPath(), "Ferry", "logs"),
                    FilePrefix = "Ferry",
                });
            }
            catch { /* ログ無しで続行 */ }
        }
        Logger.LogStartup(args);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
