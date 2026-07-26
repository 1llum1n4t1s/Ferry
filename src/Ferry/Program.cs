using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Ferry.Util;
using Velopack;

namespace Ferry;

internal sealed partial class Program
{
    private const string AppUserModelId = "velopack.Ferry";

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
        if (OperatingSystem.IsWindows())
        {
            TrySetCurrentProcessAppUserModelId();
        }

        VelopackApp.Build().Run();

        // 多重起動防止（v1.0.47）: 既に起動済みなら既存ウィンドウを前面化させて、このプロセスは即終了する。
        // Velopack の install/update フック (Run()) より後に判定し、更新処理自体は阻害しない。
        if (!SingleInstanceGuard.TryAcquire())
            return;

        try
        {
            Logger.Initialize(new LoggerConfig
            {
                // OS 別のログ配置（mac=~/Library/Logs/Ferry 等）は Util.AppPaths に集約。
                LogDirectory = Util.AppPaths.GetLogDirectory(),
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
#if DEBUG
            // DevTools インフラを有効化する。実際の接続は App.Initialize の
            // AttachDeveloperTools が行う（両方必要）。パッケージ自体が Debug 限定参照なので
            // Release / Native AOT 発行には一切入らない。
            .WithDeveloperTools()
#endif
            .LogToTrace();

    private static void TrySetCurrentProcessAppUserModelId()
    {
        try { _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch { /* シェル連携の失敗だけで起動を止めない */ }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);
}
