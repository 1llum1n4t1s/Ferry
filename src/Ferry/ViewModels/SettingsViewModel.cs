using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ferry.Services;

namespace Ferry.ViewModels;

/// <summary>
/// 設定パネルの ViewModel。
/// PC 名、テーマ、言語、保存先、スタートアップ、最小化起動、トレイ格納の設定を管理する。
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    /// <summary>帯域制限の即時反映に使う。デザイナー用 ctor では null。</summary>
    private readonly ITransferService? _transferService;
    private bool _isLoading;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = Environment.MachineName;

    // テーマ選択肢の表示テキストは SettingsView.axaml の ComboBoxItem.Content が
    // DynamicResource (Text.Settings.Theme.System/Light/Dark) で直接引く形に統一済み。
    // 旧 ItemsSource={Binding ThemeOptions} 方式は、indexer Replace でも Avalonia ComboBox の
    // SelectedItem 内部状態が外れて「言語切替でテーマ選択が外れる」症状が再発していたため撤去。

    /// <summary>選択中のテーマインデックス（0=System, 1=Light, 2=Dark）。</summary>
    [ObservableProperty]
    public partial int SelectedThemeIndex { get; set; }

    /// <summary>選択中のロケールキー。</summary>
    [ObservableProperty]
    public partial string SelectedLocale { get; set; } = string.Empty;

    /// <summary>ロケール ComboBox の選択肢。</summary>
    public IReadOnlyList<LocaleItem> LocaleOptions => App.LocaleOptions;

    /// <summary>ロケール ComboBox の選択項目。<see cref="SelectedLocale"/>（キー文字列）と相互に同期する。
    /// 同じ値の代入では ObservableProperty が変更通知を出さないため、双方向同期はここで自然に収束する。</summary>
    [ObservableProperty]
    public partial LocaleItem? SelectedLocaleItem { get; set; }

    partial void OnSelectedLocaleItemChanged(LocaleItem? value)
    {
        if (value != null) SelectedLocale = value.Key;
    }

    /// <summary>受信ファイルの保存先ディレクトリ。</summary>
    [ObservableProperty]
    public partial string SaveDirectory { get; set; } = string.Empty;

    // N-2: 旧 RunAtStartup は AutoStartWithWindows と統合済み

    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; }

    /// <summary>
    /// トレイ最小化の設定行を表示するか。macOS は「最小化 = Dock 格納」が OS 慣習で
    /// MinimizeToTray は no-op（MainWindow の WindowState 監視が <c>!IsMacOS()</c> で除外済み）。
    /// そのため mac では行ごと隠し、mac で通じない「タスクトレイ」表現の露出を避ける。
    /// OS は実行中に変わらないので通知不要のプレーンプロパティ（AOT トリム安全）。
    /// </summary>
    public bool IsTrayMinimizeSupported => !OperatingSystem.IsMacOS();

    // --- 通知設定 ---

    /// <summary>受信サウンドを再生するか。</summary>
    [ObservableProperty]
    public partial bool EnableNotificationSound { get; set; } = true;

    // --- ファイル転送設定 ---
    // ReceiveFileSavePath は v1.0.38 で SaveDirectory と重複していたため削除済み

    /// <summary>ファイル受信を自動承認するか。</summary>
    [ObservableProperty]
    public partial bool AutoAcceptFileTransfer { get; set; } = true;

    // rere #D-001(b): 旧 EnableSecureChannel トグルは v1.0.48 で撤去（常時 ON 化）。
    // ConnectionService 側で PairSecret 保有時のみ自動的に暗号チャネルを起動する。

    /// <summary>アップロード帯域制限 (KB/s)。0 で無制限。NumericUpDown.Value (decimal?) とバインド。</summary>
    [ObservableProperty]
    public partial decimal UploadKBps { get; set; }

    /// <summary>ダウンロード帯域制限 (KB/s)。0 で無制限。NumericUpDown.Value (decimal?) とバインド。</summary>
    [ObservableProperty]
    public partial decimal DownloadKBps { get; set; }

    // 旧「同時並列転送数」設定 (ParallelTransferCount ComboBox) は撤去済み。
    // 並列本数は TransferViewModel.MaxParallelSends の内部固定（最大 10）で、
    // 設定画面には読み取り専用の「自動（最大 10）」表示だけを残す。

    // N-1: 旧 Theme / AccentColor / FontSize は SelectedThemeIndex (ThemeMode) と二重定義のため削除済み

    // --- アプリ動作設定 ---

    /// <summary>Windows 起動時に自動起動するか。</summary>
    [ObservableProperty]
    public partial bool AutoStartWithWindows { get; set; }

    // === バージョン ===

    /// <summary>バージョン表示テキスト (例: "Ferry v1.0.51")。</summary>
    [ObservableProperty]
    public partial string VersionText { get; set; } = string.Empty;

    /// <summary>
    /// 更新チェックが進行中か。Lhamiel と同じ設計で
    /// <see cref="App.UpdateCheckStateChanged"/> イベントから駆動され、UI 側は
    /// <c>IsEnabled="{Binding !IsCheckingUpdate}"</c> でボタンを無効化する (並走実行抑止 + 視覚 FB)。
    /// </summary>
    [ObservableProperty]
    public partial bool IsCheckingUpdate { get; set; }

    /// <summary>「このバージョンを無視」が設定されているタグ (例: "1.0.40")。空文字なら未設定。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIgnoredUpdateTag), nameof(IgnoredUpdateTagDisplay))]
    public partial string IgnoredUpdateTag { get; set; } = string.Empty;

    /// <summary>スキップ中バージョンが存在するか (UI の IsVisible バインド用)。</summary>
    public bool HasIgnoredUpdateTag => !string.IsNullOrEmpty(IgnoredUpdateTag);

    /// <summary>「バージョン X.Y.Z をスキップ中」表示テキスト。ロケール対応。</summary>
    public string IgnoredUpdateTagDisplay =>
        HasIgnoredUpdateTag
            ? App.Text("Settings.Version.SkippedVersion", IgnoredUpdateTag)
            : string.Empty;

    public SettingsViewModel(ISettingsService settingsService, ITransferService? transferService = null)
    {
        _settingsService = settingsService;
        _transferService = transferService;
        LoadFromSettings();
        LoadVersionInfo();

        // App 側の更新チェック状態に追従。購読直後に現状で初期同期する
        // (Settings 画面を開いた瞬間に起動時自動チェックが走っていてもボタンが正しく無効化される)。
        // 本 VM は起動時に 1 度だけ生成されプロセス終了まで生きる単一インスタンス
        // (App.OnFrameworkInitializationCompleted で生成 → MainWindowViewModel.Settings が保持) なので、
        // static イベントへの購読はプロセス寿命と一致する。明示解除 (Dispose) は不要。
        App.UpdateCheckStateChanged += OnAppUpdateCheckStateChanged;
        IsCheckingUpdate = App.IsUpdateCheckInProgress;
    }

    /// <summary>デザイナー用。</summary>
    public SettingsViewModel()
    {
        _settingsService = null!;
    }

    private void LoadFromSettings()
    {
        _isLoading = true;
        try
        {
            var s = _settingsService.Settings;
            DisplayName = s.DisplayName;
            SelectedThemeIndex = s.ThemeMode switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0, // "System" またはその他
            };
            SelectedLocale = string.IsNullOrEmpty(s.Locale) ? App.DetectDefaultLocale() : s.Locale;
            SaveDirectory = string.IsNullOrEmpty(s.SaveDirectory)
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                : s.SaveDirectory;
            StartMinimized = s.StartMinimized;
            MinimizeToTray = s.MinimizeToTray;
            EnableNotificationSound = s.EnableNotificationSound;
            AutoAcceptFileTransfer = s.AutoAcceptFileTransfer;
            UploadKBps = Math.Max(0, s.UploadKBps);
            DownloadKBps = Math.Max(0, s.DownloadKBps);
            AutoStartWithWindows = s.AutoStartWithWindows;

            // インストール済みバージョンが skip 対象に追い付いた/追い越したら、その skip 設定は陳腐化しているので
            // 自動でクリアする。古い PC で過去に「このバージョンをスキップ」した後に手動更新すると、既に通り過ぎた
            // バージョン (例: v1.0.20) を「スキップ中」と表示し続けて混乱を招くため (UX 修正)。
            var loadedTag = s.IgnoreUpdateTag ?? string.Empty;
            if (!string.IsNullOrEmpty(loadedTag) && IsObsoleteIgnoreTag(loadedTag, AppVersion.Value))
            {
                Util.Logger.Log(
                    $"陳腐化した IgnoreUpdateTag を自動クリア: skip={loadedTag} <= installed={AppVersion.Value}",
                    Util.LogLevel.Info);
                loadedTag = string.Empty;
                s.IgnoreUpdateTag = string.Empty; // 共有 Settings を即クリア (更新チェック consumer も空を見る)
                _ = SaveClearedTagAsync();        // ディスクへも永続化 (観測付き fire-and-forget)
            }
            IgnoredUpdateTag = loadedTag;
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>
    /// skip 対象タグが現在のインストール済みバージョン以下 (= 既に通過済み) で陳腐化しているか判定する。
    /// パース不能なタグは陳腐化扱いにせず保持する (誤クリア防止)。先頭 'v' は許容する。
    /// </summary>
    public static bool IsObsoleteIgnoreTag(string ignoreTag, string currentVersion)
    {
        return System.Version.TryParse(ignoreTag.TrimStart('v', 'V'), out var ignored)
            && System.Version.TryParse(currentVersion.TrimStart('v', 'V'), out var current)
            && ignored <= current;
    }

    /// <summary>陳腐化 IgnoreUpdateTag の自動クリアをディスクへ永続化する (観測付き fire-and-forget)。</summary>
    private async Task SaveClearedTagAsync()
    {
        try { await _settingsService.SaveAsync(); }
        catch (Exception ex) { Util.Logger.LogException("IgnoreUpdateTag 自動クリアの永続化に失敗", ex); }
    }

    private static string ThemeIndexToMode(int index) => index switch
    {
        1 => "Light",
        2 => "Dark",
        _ => "System",
    };

    /// <summary>
    /// 設定を保存する。プロパティ変更時に自動で呼び出す。
    /// </summary>
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var s = _settingsService.Settings;
        s.DisplayName = DisplayName;
        s.ThemeMode = ThemeIndexToMode(SelectedThemeIndex);
        s.Locale = SelectedLocale;
        s.SaveDirectory = SaveDirectory;
        s.StartMinimized = StartMinimized;
        s.MinimizeToTray = MinimizeToTray;
        s.EnableNotificationSound = EnableNotificationSound;
        s.AutoAcceptFileTransfer = AutoAcceptFileTransfer;
        s.UploadKBps = (int)Math.Max(0m, UploadKBps);
        s.DownloadKBps = (int)Math.Max(0m, DownloadKBps);
        s.AutoStartWithWindows = AutoStartWithWindows;
        await _settingsService.SaveAsync();
    }

    /// <summary>
    /// 保存先フォルダ選択ダイアログを開く。ViewModel から直接ダイアログは開けないため、
    /// View 側のイベントハンドラで呼び出す。
    /// </summary>
    [RelayCommand]
    private void BrowseSaveDirectory()
    {
        // View 側で処理（SettingsPanel.axaml.cs の BrowseSaveDirectoryRequested イベント経由）
        BrowseSaveDirectoryRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>保存先フォルダ選択ダイアログを要求するイベント。</summary>
    public event EventHandler? BrowseSaveDirectoryRequested;

    /// <summary>テーマ切替要求イベント (App.axaml.cs で購読し RequestedThemeVariant を更新)。</summary>
    public event EventHandler<int>? ThemeChangeRequested;

    /// <summary>更新チェック要求イベント (App.axaml.cs で購読)。</summary>
    public event EventHandler? UpdateCheckRequested;

    /// <summary>
    /// 手動更新チェックを要求する。実際の Check4Update 実行は App 側に委譲 (N-6: MVVM 厳密化)。
    /// </summary>
    [RelayCommand]
    private void CheckForUpdate()
    {
        UpdateCheckRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>ロード中でなければ設定を保存する。各プロパティ変更ハンドラから呼び出す共通ヘルパー。
    /// RelayCommand 生成 Command.Execute(null) ではなく直接メソッドを fire-and-forget で呼ぶ
    /// （内部呼び出しに Command 経由は不要、N-20 正規化）。</summary>
    private void SaveIfNotLoading() { if (!_isLoading) _ = SaveSettingsAsync(); }

    partial void OnDisplayNameChanged(string value) => SaveIfNotLoading();
    partial void OnStartMinimizedChanged(bool value) => SaveIfNotLoading();
    partial void OnMinimizeToTrayChanged(bool value) => SaveIfNotLoading();
    partial void OnSaveDirectoryChanged(string value) => SaveIfNotLoading();
    partial void OnEnableNotificationSoundChanged(bool value) => SaveIfNotLoading();
    partial void OnAutoAcceptFileTransferChanged(bool value) => SaveIfNotLoading();

    partial void OnUploadKBpsChanged(decimal value)
    {
        if (_isLoading) return;
        // VM の値を先に Settings へ反映してから SyncRateLimits を呼び、進行中の転送に即時反映する。
        // SaveSettingsAsync 内でもう一度書き込まれるが冪等なので問題なし。
        _settingsService.Settings.UploadKBps = (int)Math.Max(0m, value);
        _transferService?.SyncRateLimits();
        _ = SaveSettingsAsync();
    }

    partial void OnDownloadKBpsChanged(decimal value)
    {
        if (_isLoading) return;
        _settingsService.Settings.DownloadKBps = (int)Math.Max(0m, value);
        _transferService?.SyncRateLimits();
        _ = SaveSettingsAsync();
    }

    partial void OnAutoStartWithWindowsChanged(bool value)
    {
        if (!_isLoading)
        {
            // レジストリに自動起動を登録/解除（インターフェース経由、N-21 正規化）
            _settingsService.SetAutoStart(value);
            _ = SaveSettingsAsync();
        }
    }

    partial void OnSelectedLocaleChanged(string value)
    {
        // ComboBox 側の選択項目へ反映（設定復元など、キー文字列側から変わった経路の同期）
        SelectedLocaleItem = App.LocaleOptions.FirstOrDefault(l => l.Key == value);

        // ロケール差し替えは MergedDictionaries.Remove → Add 方式 (App.SetLocale)。
        // Theme ComboBox は ComboBoxItem.Content="{DynamicResource ...}" 直接参照で、
        // ItemsSource を触らないので SelectedIndex は不変。VM 側で再評価は不要。
        App.SetLocale(value);
        SaveIfNotLoading();
    }

    private void LoadVersionInfo()
    {
        // N-9: Reflection (`AssemblyInformationalVersion`) を Native AOT 安全な static 定数に置換
        // v1.0.38: バージョン文字列を「Ferry v1.0.38」形式に整形 (Lhamiel と同等)
        VersionText = $"Ferry v{AppVersion.Value}";
    }

    /// <summary>
    /// <see cref="App.UpdateCheckStateChanged"/> 遷移を UI スレッドに marshal して
    /// <see cref="IsCheckingUpdate"/> を更新する。バックグラウンドスレッドから呼ばれうるため
    /// Dispatcher.UIThread.Post で必ず UI スレッドへ送る (Lhamiel と同じパターン)。
    /// </summary>
    private void OnAppUpdateCheckStateChanged(bool inProgress)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsCheckingUpdate = inProgress);
    }

    /// <summary>
    /// 「このバージョンを無視」で保存された <see cref="AppSettings.IgnoreUpdateTag"/> を取り消すコマンド。
    /// バージョンセクションの「取り消し」ボタンから呼ばれる、誤クリックの復旧導線。
    /// 取り消し後は次回の自動 / 手動チェックでそのバージョンも通知対象に戻る。
    /// </summary>
    [RelayCommand]
    private async Task ClearIgnoredUpdateTagAsync()
    {
        if (string.IsNullOrEmpty(IgnoredUpdateTag)) return;
        try
        {
            _settingsService.Settings.IgnoreUpdateTag = string.Empty;
            await _settingsService.SaveAsync();
            IgnoredUpdateTag = string.Empty;
            Util.Logger.Log("IgnoreUpdateTag をユーザー操作によりクリアしました", Util.LogLevel.Warning);
        }
        catch (Exception ex)
        {
            Util.Logger.LogException("IgnoreUpdateTag のクリアに失敗", ex);
        }
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        // テーマを即座に切り替え (N-6: View 側 = App.axaml.cs で RequestedThemeVariant を更新)
        ThemeChangeRequested?.Invoke(this, value);
        SaveIfNotLoading();
    }
}
