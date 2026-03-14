using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
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

            // サービス組み立て
            var settingsService = new StubSettingsService();
            var settings = settingsService.Settings;
            var connectionService = new ConnectionService(settings.FirebaseDatabaseUrl, settings.DisplayName);
            var transferService = new StubTransferService();
            var qrCodeService = new QrCodeGenerator();
            var peerRegistry = new PeerRegistryService();

            var connectionVm = new ConnectionViewModel(connectionService, qrCodeService, settingsService, peerRegistry);
            var transferVm = new TransferViewModel(connectionService, transferService);
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

            // 起動時に自動でセッション開始（ペアリング済みピアがなければ QR コード表示）
            if (connectionVm.PairedPeers.Count == 0)
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
