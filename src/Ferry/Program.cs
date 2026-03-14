using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Ferry.Util;
using Velopack;
using Velopack.Sources;

namespace Ferry;

internal sealed class Program
{
    /// <summary>
    /// GitHub Releases の更新元リポジトリ URL。Velopack がここから releases.win.json を取得する。
    /// </summary>
    private const string GitHubRepoUrl = "https://github.com/1llum1n4t1s/Ferry";

    /// <summary>
    /// アプリケーションのエントリポイント。Velopack のブートストラップを最初に実行し、
    /// GitHub Releases に最新版があれば強制更新してから Avalonia アプリを起動する。
    /// </summary>
    [STAThread]
    public static async Task Main(string[] args)
    {
        VelopackApp.Build().Run();

        Logger.Initialize(new LoggerConfig
        {
            LogDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ferry", "logs"),
            FilePrefix = "Ferry",
        });
        Logger.LogStartup(args);

        await TryForceUpdateAsync(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// GitHub Releases に最新版があればダウンロード・適用・再起動する。更新がなければ何もしない。
    /// </summary>
    private static async Task TryForceUpdateAsync(string[] args)
    {
        try
        {
            var source = new GithubSource(GitHubRepoUrl, string.Empty, false);
            var options = new UpdateOptions { ExplicitChannel = "win" };
            var mgr = new UpdateManager(source, options);
            var newVersion = await mgr.CheckForUpdatesAsync();
            if (newVersion != null)
            {
                await mgr.DownloadUpdatesAsync(newVersion);
                mgr.ApplyUpdatesAndRestart(newVersion, args);
            }
        }
        catch
        {
            // ネットワークエラーなどで更新チェックに失敗した場合はアプリを起動する
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
