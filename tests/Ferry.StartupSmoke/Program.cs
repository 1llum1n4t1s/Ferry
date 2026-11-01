using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Automation.Peers;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ferry;
using Ferry.Models;
using Ferry.Services;
using Ferry.ViewModels;
using Ferry.Views;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => AppBuilder.Configure<VerificationApp>()
        .UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
}

internal sealed class VerificationApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SetLocale("en_US");
        Dispatcher.UIThread.Post(() => _ = VerifyAsync(desktop));
    }

    private static async Task VerifyAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? [];
        var outputIndex = Array.IndexOf(args, "--output");
        var output = Path.GetFullPath(outputIndex >= 0 ? args[outputIndex + 1] : "window-startup-results");
        Directory.CreateDirectory(output);
        var results = new List<object>();
        var exitCode = 0;
        foreach (var scenario in new[]
        {
            (Name: "normal", Minimized: false, Tray: false, Maximized: false),
            (Name: "minimized", Minimized: true, Tray: false, Maximized: false),
            (Name: "minimized-tray", Minimized: true, Tray: true, Maximized: false),
            (Name: "maximized-minimized", Minimized: true, Tray: false, Maximized: true),
        })
        {
            var settings = new MemorySettings(new AppSettings
            {
                StartMinimized = scenario.Minimized,
                MinimizeToTray = scenario.Tray,
                IsWindowMaximized = scenario.Maximized,
                WindowWidth = 900,
                WindowHeight = 600,
                WindowLeft = 130,
                WindowTop = 140,
                SidebarWidth = 250,
            });
            using var registry = new PeerRegistryService(Path.Combine(output, "window-peers.json"));
            using var vm = CreateSettingsWindowViewModel(settings, registry);
            var window = new VerificationWindow { DataContext = vm };
            desktop.MainWindow = window;
            window.SetSettingsService(settings);
            var beforeShow = window.WindowState.ToString();
            try
            {
                Require(window.Width == 900 && window.Height == 600, "初回表示前のサイズ復元");
                Require(window.WindowStartupLocation == WindowStartupLocation.Manual, "保存座標の初回中央配置による上書き防止");
                window.Show();
                if (!scenario.Maximized)
                    Require(window.Position == new PixelPoint(130, 140), "保存した座標で初回表示");
                await WaitUntilAsync(() => scenario.Minimized
                    ? window.WindowState == WindowState.Minimized
                    : window.IsVisible && window.Bounds.Width > 0);
                // OS の非同期状態通知が収束しても最小化が維持されることを確認する。
                await Task.Delay(400);
                Require((window.WindowState == WindowState.Minimized) == scenario.Minimized, "起動時最小化状態");
                var hiddenToTray = scenario.Minimized && scenario.Tray && !OperatingSystem.IsMacOS();
                Require(window.IsVisible != hiddenToTray, "OS 別のトレイ/Dock 最小化");
                var startupState = window.WindowState.ToString();
                var startupVisible = window.IsVisible;

                // 本体の ShowMainWindow と同じ復帰順序を使う。
                window.ShowInTaskbar = true;
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Show();
                window.Activate();
                await Task.Delay(400);
                Require(window.IsVisible && window.WindowState != WindowState.Minimized, "最小化からの復帰");
                var restoredState = window.WindowState.ToString();
                // Windows は最大化したまま最小化したウィンドウを最大化へ復帰させる。
                if (window.WindowState != WindowState.Normal)
                {
                    window.WindowState = WindowState.Normal;
                    await Task.Delay(400);
                }

                window.Width = 920;
                window.Height = 620;
                window.Hide();
                window.Show();
                await Task.Delay(400);
                Require(window.IsVisible && window.WindowState == WindowState.Normal, "再表示での再最小化防止");
                Require(window.Width == 920 && window.Height == 620, "再表示時のサイズ保持");
                RequireSettingsOnly(window);
                Require(window.GetVisualDescendants().OfType<TextBlock>().Any(text =>
                    text.IsEffectivelyVisible && text.Bounds.Width > 0 && !string.IsNullOrWhiteSpace(text.Text)),
                    "設定画面の文字とレイアウト");

                using var image = new RenderTargetBitmap(new PixelSize(920, 620), new Vector(96, 96));
                image.Render(window);
                image.Save(Path.Combine(output, scenario.Name + ".png"), PngBitmapEncoderOptions.Default);
                results.Add(new { scenario.Name, Passed = true, BeforeShow = beforeShow, StartupState = startupState,
                    StartupVisible = startupVisible, RestoredState = restoredState, window.Width, window.Height });
            }
            catch (Exception ex)
            {
                exitCode = 1;
                results.Add(new { scenario.Name, Passed = false, Error = ex.ToString() });
            }
            finally
            {
                window.AllowClose = true;
                window.Close();
            }
        }
        if (args.Contains("--regressions"))
        {
            var regressionResults = await VerifyLocaleAsync(desktop, output);
            regressionResults.Add(await VerifyTransferEventsAsync(output));
            regressionResults.AddRange(await TransferIntegrationChecks.RunAsync(desktop, output));
            regressionResults.AddRange(await ServiceChecks.RunAsync());
            results.AddRange(regressionResults);
            if (regressionResults.Any(result => !result.Passed)) exitCode = 1;
        }
        var report = new { OS = Environment.OSVersion.ToString(), Runtime = Environment.Version.ToString(),
            Avalonia = typeof(Window).Assembly.GetName().Version?.ToString(), Utc = DateTimeOffset.UtcNow,
            ScreenshotKind = "Avalonia visual tree (not OS compositor)", Results = results,
            TemporaryDirectories = ServiceChecks.TemporaryDirectories.Select(path => new { Path = path, Removed = !Directory.Exists(path) }).ToArray() };
        await File.WriteAllTextAsync(Path.Combine(output, "result.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"起動時ウィンドウ検証: {(exitCode == 0 ? "成功" : "失敗")} / {output}");
        desktop.Shutdown(exitCode);
    }

    private static async Task<List<CheckResult>> VerifyLocaleAsync(IClassicDesktopStyleApplicationLifetime desktop, string output)
    {
        var results = new List<CheckResult>();
        var settings = new MemorySettings(new AppSettings { Locale = "he_IL", IgnoreUpdateTag = "999.0.0" });
        using var registry = new PeerRegistryService(Path.Combine(output, "window-peers.json"));
        using var vm = CreateSettingsWindowViewModel(settings, registry);
        var window = new VerificationWindow { DataContext = vm };
        desktop.MainWindow = window;
        window.SetSettingsService(settings);
        window.Show();
        await Task.Delay(200);
        var dialog = new ConfirmWindow();
        dialog.Show();
        await Task.Delay(100);
        void Check(string name, Action action)
        {
            try { action(); results.Add(new(name, true)); }
            catch (Exception ex) { results.Add(new(name, false, ex.ToString())); }
        }
        try
        {
            Check("rtl-before-window-and-later-dialog", () =>
                Require(window.FlowDirection == Avalonia.Media.FlowDirection.RightToLeft &&
                    dialog.FlowDirection == Avalonia.Media.FlowDirection.RightToLeft, "保存したhe_ILと後発確認画面のRTL"));
            Check("rtl-settings-only", () => RequireSettingsOnly(window));
            using var image = new RenderTargetBitmap(new PixelSize(900, 600), new Vector(96, 96));
            image.Render(window);
            image.Save(Path.Combine(output, "he_IL.png"), PngBitmapEncoderOptions.Default);
            var oldDescription = vm.Settings.IgnoredUpdateTagDisplay;
            vm.Settings.SelectedLocale = "en_US";
            await Task.Delay(150);
            Check("locale-change-existing-windows", () =>
                Require(window.FlowDirection == Avalonia.Media.FlowDirection.LeftToRight &&
                    dialog.FlowDirection == Avalonia.Media.FlowDirection.LeftToRight, "既存ウィンドウのLTR復帰"));
            Check("skipped-tag-localized-binding", () =>
                Require(oldDescription != vm.Settings.IgnoredUpdateTagDisplay &&
                    window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == vm.Settings.IgnoredUpdateTagDisplay),
                    "言語変更後の説明を実画面へ反映"));
            foreach (var locale in new[] { "en_US", "he_IL" })
            {
                vm.Settings.SelectedLocale = locale;
                await Task.Delay(100);
                Check("settings-accessible-names-" + locale, () =>
                {
                    var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
                    var controls = view.GetVisualDescendants().OfType<Control>()
                        .Where(control => control.TemplatedParent == null && control is ToggleSwitch or ComboBox or NumericUpDown or TextBox).ToArray();
                    Require(controls.Length == 11, $"設定入力11個を観測（実際: {controls.Length}: {string.Join(",", controls.Select(control => control.GetType().Name))}）");
                    foreach (var control in controls)
                        Require(!string.IsNullOrWhiteSpace(ControlAutomationPeer.CreatePeerForElement(control)?.GetName()), "設定用途の読み上げ名");
                });
            }
        }
        finally
        {
            dialog.Close();
            window.AllowClose = true;
            window.Close();
            SetLocale("en_US");
        }
        return results;
    }

    private static MainWindowViewModel CreateSettingsWindowViewModel(MemorySettings settings, IPeerRegistryService registry)
    {
        var connection = new StubConnectionService();
        var connectionVm = new ConnectionViewModel(connection, null!, settings, registry, null!);
        var transferVm = new TransferViewModel(connection, new StubTransferService(), connectionVm, settings);
        // 子ViewModelの購読・タイマーは返却先のMainWindowViewModel.Disposeで一度だけ解放する。
        return new(connectionVm, transferVm, new SettingsViewModel(settings)) { IsSettingsMode = true };
    }

    private static void RequireSettingsOnly(Window window)
    {
        Require(window.FindControl<TransferView>("TransferViewControl") is { IsEffectivelyVisible: false }, "設定表示中は転送ビュー非表示");
        Require(window.FindControl<Border>("EmptyView") is { IsEffectivelyVisible: false }, "設定表示中は転送空状態非表示");
    }

    private static async Task<CheckResult> VerifyTransferEventsAsync(string output)
    {
        const string name = "transfer-events-dispatcher";
        try
        {
            Dispatcher.UIThread.VerifyAccess();
            var settings = new MemorySettings(new AppSettings());
            var connection = new CaptureConnection();
            using var registry = new PeerRegistryService(Path.Combine(output, "event-peers.json"));
            using var connectionVm = new ConnectionViewModel(connection, null!, settings, registry, null!);
            using var service = new EventTransferService();
            using var vm = new TransferViewModel(connection, service, connectionVm, settings);
            var active = new TransferItem { FileName = "active.txt", FileSize = 1000, Direction = TransferDirection.Send, State = TransferState.InProgress };
            var completed = new TransferItem { FileName = "completed.txt", FileSize = 1000, State = TransferState.Completed, TransferredBytes = 1000 };
            vm.Transfers.Add(active);
            vm.Transfers.Add(completed);

            // 未知の送信IDは既存行にも履歴件数にも影響しない。
            service.RaiseProgress(new TransferItem { Direction = TransferDirection.Send, State = TransferState.InProgress, TransferredBytes = 500 });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(active.TransferredBytes == 0 && vm.Transfers.Count == 2, "未知IDの送信進捗を無視");

            service.RaiseProgress(new TransferItem { TransferId = completed.TransferId, State = TransferState.InProgress, TransferredBytes = 500 });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(completed.TransferredBytes == 1000 && completed.State == TransferState.Completed, "完了行を遅延進捗で巻き戻さない");

            // 一致する進捗が反映されることで、イベント購読とDispatcher処理の実行も確認する。
            service.RaiseProgress(new TransferItem { TransferId = active.TransferId, State = TransferState.InProgress, TransferredBytes = 500 });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(active.TransferredBytes == 500 && active.State == TransferState.InProgress, "一致IDの進捗を反映");

            service.RaiseError(new TransferItem { Direction = TransferDirection.Send, State = TransferState.Error, ErrorMessage = "unknown-error" });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(active.State == TransferState.InProgress && active.ErrorMessage == null && vm.Transfers.Count == 2, "未知IDの送信エラーを無視");

            service.RaiseError(new TransferItem { TransferId = active.TransferId, Direction = TransferDirection.Send, State = TransferState.Error, ErrorMessage = "matching-error" });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(active.State == TransferState.Error && active.ErrorMessage == "matching-error", "一致IDのエラーを反映");
            return new(name, true);
        }
        catch (Exception ex) { return new(name, false, ex.ToString()); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("ウィンドウ状態の待機に失敗");
            await Task.Delay(20);
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }
}

internal sealed class VerificationWindow : MainWindow
{
    public bool AllowClose { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 検証終了時だけ本体の「×で終了/Hide」を経由せず、この検証用ウィンドウを破棄する。
        if (!AllowClose) base.OnClosing(e);
    }
}

internal sealed class MemorySettings(AppSettings settings) : ISettingsService
{
    public AppSettings Settings { get; } = settings;
    public Task LoadAsync() => Task.CompletedTask;
    public Task SaveAsync() => Task.CompletedTask;
    public void SetAutoStart(bool enabled) { }
}
