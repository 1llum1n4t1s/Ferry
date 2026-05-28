using System;
using System.IO;
using System.Threading.Tasks;
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

        // rere レビュー #F-003: 未捕捉例外をログに残す。
        // 旧実装は AppDomain.UnhandledException / TaskScheduler.UnobservedTaskException
        // のいずれも登録しておらず、`_ = Task.Run(...)` 系のクラッシュが silent kill 経路を作り、
        // 「アプリが突然消えた、ログ見ても何も残ってない」ユーザー報告の原因到達を不能にしていた。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                // CodeRabbit 指摘: 例外メッセージ / StackTrace に IP アドレスが含まれうるので
                // MaskIp で末尾オクテットを伏せてからログ出力 (PII 保護)
                var raw = $"FATAL UnhandledException (terminating={e.IsTerminating}): {ex?.GetType().Name} - {ex?.Message}\n{ex?.StackTrace}";
                Logger.Log(Logger.MaskIp(raw), LogLevel.Error);
            }
            catch { /* ログ自体が落ちる経路は何もできない */ }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                // CodeRabbit 指摘: 同上、MaskIp 経由で PII 保護
                var raw = $"UnobservedTaskException: {e.Exception.GetType().Name} - {e.Exception.Message}\n{e.Exception.StackTrace}";
                Logger.Log(Logger.MaskIp(raw), LogLevel.Error);
                e.SetObserved(); // プロセス終了を阻止 (TaskScheduler が default で AppDomain 終了させるのを避ける)
            }
            catch { }
        };

        Logger.LogStartup(args);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
