using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Platform;
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

/// <summary>ロケール選択肢の表示用レコード。</summary>
public record LocaleItem(string Key, string DisplayName);

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private ResourceDictionary? _activeLocale;
    private ISettingsService? _settingsService;

    /// <summary>TransferService のインスタンス（MainWindow からのアクセス用）。</summary>
    public ITransferService? TransferService { get; private set; }

    /// <summary>自動更新の配信元 URL（Cloudflare R2 ferry-updates 経由）。</summary>
    private const string UpdateBaseUrl = "https://ferry.nephilim.jp";

    /// <summary>サポートされているロケール一覧。</summary>
    public static readonly string[] SupportedLocales =
    [
        "en_US", "ja_JP", "zh_CN", "zh_TW", "de_DE", "fr_FR", "es_ES",
        "it_IT", "pt_BR", "ru_RU", "uk_UA", "id_ID", "fil_PH", "ta_IN", "ko_KR",
        "la_VA", "sa_IN", "he_IL"
    ];

    /// <summary>ロケール表示名（ネイティブ言語名）。</summary>
    public static readonly Dictionary<string, string> LocaleDisplayNames = new()
    {
        ["en_US"] = "English",
        ["ja_JP"] = "日本語",
        ["zh_CN"] = "简体中文",
        ["zh_TW"] = "繁體中文",
        ["de_DE"] = "Deutsch",
        ["fr_FR"] = "Français",
        ["es_ES"] = "Español",
        ["it_IT"] = "Italiano",
        ["pt_BR"] = "Português (Brasil)",
        ["ru_RU"] = "Русский",
        ["uk_UA"] = "Українська",
        ["id_ID"] = "Bahasa Indonesia",
        ["fil_PH"] = "Tagalog",
        ["ta_IN"] = "தமிழ்",
        ["ko_KR"] = "한국어",
        ["la_VA"] = "Latina",
        ["sa_IN"] = "संस्कृतम्",
        ["he_IL"] = "עברית עתיקה"
    };

    /// <summary>ロケール選択肢一覧。</summary>
    public static readonly LocaleItem[] LocaleOptions = SupportedLocales
        .Select(l => new LocaleItem(l, LocaleDisplayNames.GetValueOrDefault(l, l)))
        .ToArray();

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

            // Windows ファイアウォールルールの確認・追加（初回のみ UAC ダイアログ表示）
            FirewallHelper.EnsureFirewallRule();

            // サービス組み立て（コンストラクタで同期的に settings.json を読み込み、DeviceId を永続化）
            _settingsService = new SettingsService();
            var settingsService = (SettingsService)_settingsService;
            var settings = settingsService.Settings;
            // 各接続先 URL が未設定の場合はデフォルト値を設定して保存
            var needsSave = false;
            if (string.IsNullOrEmpty(settings.FirebaseDatabaseUrl))
            {
                settings.FirebaseDatabaseUrl = "https://ferry-edf09-default-rtdb.firebaseio.com";
                needsSave = true;
            }
            if (string.IsNullOrEmpty(settings.BridgePageUrl))
            {
                settings.BridgePageUrl = "https://ferry-edf09.web.app";
                needsSave = true;
            }
            if (string.IsNullOrEmpty(settings.RelayUrl))
            {
                settings.RelayUrl = "wss://1llum1n4t1.net/ferry-relay";
                needsSave = true;
            }
            if (string.IsNullOrEmpty(settings.SaveDirectory))
            {
                settings.SaveDirectory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                needsSave = true;
            }
            if (needsSave)
            {
                _ = settingsService.SaveAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Util.Logger.Log($"初期設定の保存に失敗: {t.Exception?.GetBaseException().Message}", Util.LogLevel.Warning);
                }, TaskScheduler.Default);
            }
            var connectionService = new ConnectionService(settings.FirebaseDatabaseUrl, settings.DeviceId, settings.DisplayName);
            if (!string.IsNullOrEmpty(settings.RelayUrl))
                connectionService.RelayUrl = settings.RelayUrl;
            var transferService = new TransferService(connectionService, settingsService);
            TransferService = transferService;
            var qrCodeService = new QrCodeGenerator();
            var peerRegistry = new PeerRegistryService();

            // ロケールを設定から復元（未設定ならシステムロケールを自動検出）
            var locale = string.IsNullOrEmpty(settings.Locale) ? DetectDefaultLocale() : settings.Locale;
            SetLocale(locale);

            // テーマを設定から復元
            RequestedThemeVariant = settings.ThemeMode switch
            {
                "Light" => ThemeVariant.Light,
                "Dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default, // "System" = OS 追従
            };

            var connectionVm = new ConnectionViewModel(connectionService, qrCodeService, settingsService, peerRegistry);
            var transferVm = new TransferViewModel(connectionService, transferService, connectionVm);
            var settingsVm = new SettingsViewModel(settingsService);
            var mainVm = new MainWindowViewModel(connectionVm, transferVm, settingsVm);

            _mainWindow = new MainWindow
            {
                DataContext = mainVm,
            };
            _mainWindow.SetSettingsService(settingsService);
            desktop.MainWindow = _mainWindow;

            // トレイアイコン設定（MinimizeToTray 有効時にウィンドウ復帰用）
            var trayIcon = new TrayIcon
            {
                ToolTipText = "Ferry",
                IsVisible = true,
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Ferry/icon/app.ico"))),
                Menu = CreateTrayMenu(),
            };
            trayIcon.Clicked += (_, _) => ShowMainWindow();
            TrayIcon.SetIcons(this, [trayIcon]);

            // 起動時：ペアリング済みピアがあれば最初のピアを宛先として選択
            // ピアがいなければ AddMemberWindow ダイアログを自動表示
            if (connectionVm.PairedPeers.Count > 0)
            {
                connectionVm.SelectedPeer = connectionVm.PairedPeers[0];
            }
            else
            {
                // ピア未登録時はメンバー追加ダイアログを自動表示
                _mainWindow.Loaded += async (_, _) =>
                {
                    connectionVm.StartSessionCommand.Execute(null);
                    var dialog = new AddMemberWindow { DataContext = connectionVm };
                    await dialog.ShowDialog(_mainWindow);
                };
            }

            // 起動時の自動更新チェック（更新がある場合のみダイアログ表示）
            if (_settingsService!.Settings.Check4UpdatesOnStartup)
                Check4Update(false);
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

    /// <summary>
    /// ロケールを切り替える。
    /// </summary>
    /// <param name="localeKey">ロケールキー（"ja_JP", "en_US" など）</param>
    public static void SetLocale(string localeKey)
    {
        if (Current is not App app ||
            app.Resources[localeKey] is not ResourceDictionary targetLocale ||
            targetLocale == app._activeLocale)
            return;

        if (app._activeLocale != null)
            app.Resources.MergedDictionaries.Remove(app._activeLocale);

        app.Resources.MergedDictionaries.Add(targetLocale);
        app._activeLocale = targetLocale;
    }

    /// <summary>
    /// リソースからローカライズ済みテキストを取得する。
    /// </summary>
    /// <param name="key">リソースキー（"Text." プレフィックスなし）</param>
    /// <param name="args">フォーマット引数</param>
    /// <returns>ローカライズ済み文字列</returns>
    public static string Text(string key, params object[] args)
    {
        var fmt = Current?.FindResource($"Text.{key}") as string;
        if (string.IsNullOrWhiteSpace(fmt))
            return $"Text.{key}";

        if (args == null || args.Length == 0)
            return fmt;

        return string.Format(fmt, args);
    }

    /// <summary>
    /// システムのカルチャからデフォルトロケールを検出する。
    /// </summary>
    public static string DetectDefaultLocale()
    {
        var culture = CultureInfo.CurrentUICulture;
        var name = culture.Name.Replace('-', '_');

        // 完全一致
        if (SupportedLocales.Contains(name))
            return name;

        // 言語部分のみで一致（例: "ja" → "ja_JP"）
        var lang = culture.TwoLetterISOLanguageName;
        var match = SupportedLocales.FirstOrDefault(l => l.StartsWith(lang + "_", StringComparison.OrdinalIgnoreCase));
        return match ?? "en_US";
    }

    /// <summary>メインウィンドウを表示・復帰する。</summary>
    private void ShowMainWindow()
    {
        if (_mainWindow == null) return;
        _mainWindow.ShowInTaskbar = true;
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    /// <summary>トレイアイコンの右クリックメニューを作成する。</summary>
    private NativeMenu CreateTrayMenu()
    {
        var menu = new NativeMenu();

        var showItem = new NativeMenuItem(Text("Tray.ShowWindow"));
        showItem.Click += (_, _) => ShowMainWindow();
        menu.Add(showItem);

        menu.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem(Text("Tray.Exit"));
        exitItem.Click += (_, _) =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        };
        menu.Add(exitItem);

        return menu;
    }

    // === 自動更新チェック（Komorebi パターン） ===

    /// <summary>
    /// 更新チェックを実行する。更新がある場合はダイアログを表示する。
    /// </summary>
    /// <param name="manually">ユーザーが手動で実行した場合は true（結果を常に表示）。</param>
    public void Check4Update(bool manually = false)
    {
        Task.Run(async () =>
        {
            try
            {
                var source = new Velopack.Sources.SimpleWebSource(UpdateBaseUrl);
                var mgr = new Velopack.UpdateManager(source);

                if (!mgr.IsInstalled)
                {
                    if (manually)
                        ShowSelfUpdateResult(new Models.AlreadyUpToDate());
                    return;
                }

                var newVersion = await mgr.CheckForUpdatesAsync();
                if (newVersion == null)
                {
                    if (manually)
                        ShowSelfUpdateResult(new Models.AlreadyUpToDate());
                    return;
                }

                // 自動チェック時はユーザーが無視指定したバージョンをスキップ
                if (!manually)
                {
                    var s = _settingsService?.Settings;
                    var newTag = $"v{newVersion.TargetFullRelease.Version}";
                    if (s != null && newTag == s.IgnoreUpdateTag)
                        return;
                }

                ShowSelfUpdateResult(new Models.VelopackUpdate(mgr, newVersion));
            }
            catch (Exception e)
            {
                Util.Logger.Log($"更新チェック失敗: {e.Message}", Util.LogLevel.Warning);
                if (manually)
                    ShowSelfUpdateResult(new Models.SelfUpdateFailed(e));
            }
        });
    }

    /// <summary>更新結果ダイアログを表示する。</summary>
    private void ShowSelfUpdateResult(object data)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_mainWindow == null) return;

            var vm = new ViewModels.SelfUpdateViewModel { Data = data };
            var window = new Views.SelfUpdateWindow { DataContext = vm };
            window.ShowDialog(_mainWindow);
        });
    }

    /// <summary>指定バージョンの更新通知を無視するよう設定に記録する。</summary>
    public void IgnoreUpdateVersion(string tag)
    {
        var s = _settingsService?.Settings;
        if (s == null) return;
        s.IgnoreUpdateTag = tag;
        _ = _settingsService!.SaveAsync();
    }
}
