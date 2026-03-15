using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Ferry.Infrastructure;
using Ferry.Services;
using Ferry.ViewModels;
using Ferry.Views;

namespace Ferry;

public partial class App : Application
{
    private MainWindow? _mainWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reflection used by Avalonia data validation plugins and ViewLocator")]
#pragma warning disable IL2046 // Avalonia の基底メソッドに属性が付与されていないため抑制
    public override void OnFrameworkInitializationCompleted()
#pragma warning restore IL2046
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();

            // サービス組み立て（コンストラクタで同期的に settings.json を読み込み、DeviceId を永続化）
            var settingsService = new SettingsService();
            var settings = settingsService.Settings;
            // Firebase URL が未設定の場合はデフォルト値を設定して保存
            if (string.IsNullOrEmpty(settings.FirebaseDatabaseUrl))
            {
                settings.FirebaseDatabaseUrl = "https://ferry-edf09-default-rtdb.firebaseio.com";
                settings.BridgePageUrl = "https://ferry-edf09.web.app";
                _ = settingsService.SaveAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Util.Logger.Log($"初期設定の保存に失敗: {t.Exception?.GetBaseException().Message}", Util.LogLevel.Warning);
                }, TaskScheduler.Default);
            }
            var connectionService = new ConnectionService(settings.FirebaseDatabaseUrl, settings.DeviceId, settings.DisplayName);
            var transferService = new StubTransferService();
            var qrCodeService = new QrCodeGenerator();
            var peerRegistry = new PeerRegistryService();

            // テーマを設定から復元
            RequestedThemeVariant = settings.ThemeMode == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;

            var connectionVm = new ConnectionViewModel(connectionService, qrCodeService, settingsService, peerRegistry);
            var transferVm = new TransferViewModel(connectionService, transferService, connectionVm);
            var settingsVm = new SettingsViewModel(settingsService);
            var mainVm = new MainWindowViewModel(connectionVm, transferVm, settingsVm);

            _mainWindow = new MainWindow
            {
                DataContext = mainVm,
            };
            desktop.MainWindow = _mainWindow;

            // トレイアイコン設定（MinimizeToTray 有効時にウィンドウ復帰用）
            var trayIcon = new TrayIcon
            {
                ToolTipText = "Ferry",
                IsVisible = true,
            };
            trayIcon.Clicked += (_, _) =>
            {
                _mainWindow.Show();
                _mainWindow.WindowState = WindowState.Normal;
                _mainWindow.Activate();
            };
            TrayIcon.SetIcons(this, [trayIcon]);

            // 起動時：ペアリング済みピアがあれば最初のピアを宛先として選択（接続はしない）
            // なければ QR コードを表示してペアリング待ち
            if (connectionVm.PairedPeers.Count > 0)
            {
                connectionVm.SelectedPeer = connectionVm.PairedPeers[0];
            }
            else
            {
                connectionVm.StartSessionCommand.Execute(null);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reflection used by Avalonia data validation plugins")]
    private static void DisableAvaloniaDataAnnotationValidation()
    {
        try
        {
            var dataValidationPluginsToRemove =
                BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

            foreach (var plugin in dataValidationPluginsToRemove)
            {
                BindingPlugins.DataValidators.Remove(plugin);
            }
        }
        catch
        {
            // NativeAOT 環境で反射に関する例外が発生する場合がある
        }
    }
}
