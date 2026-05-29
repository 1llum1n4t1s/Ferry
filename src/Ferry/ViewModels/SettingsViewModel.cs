using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ferry.Services;

namespace Ferry.ViewModels;

/// <summary>
/// 設定パネルの ViewModel。
/// PC 名、テーマ、言語、保存先、スタートアップ、最小化起動、トレイ格納の設定を管理する。
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly ISettingsService _settingsService;
    private bool _isLoading;
    private bool _disposed;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = Environment.MachineName;

    /// <summary>テーマモード選択肢の表示名一覧（ロケール連動）。</summary>
    public string[] ThemeOptions => [App.Text("Settings.Theme.System"), App.Text("Settings.Theme.Light"), App.Text("Settings.Theme.Dark")];

    /// <summary>選択中のテーマインデックス（0=System, 1=Light, 2=Dark）。</summary>
    [ObservableProperty]
    public partial int SelectedThemeIndex { get; set; }

    /// <summary>選択中のロケールキー。</summary>
    [ObservableProperty]
    public partial string SelectedLocale { get; set; } = string.Empty;

    /// <summary>受信ファイルの保存先ディレクトリ。</summary>
    [ObservableProperty]
    public partial string SaveDirectory { get; set; } = string.Empty;

    // N-2: 旧 RunAtStartup は AutoStartWithWindows と統合済み

    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; }

    // --- 通知設定 ---

    /// <summary>受信サウンドを再生するか。</summary>
    [ObservableProperty]
    public partial bool EnableNotificationSound { get; set; } = true;

    // --- ファイル転送設定 ---
    // ReceiveFileSavePath は v1.0.38 で SaveDirectory と重複していたため削除済み

    /// <summary>ファイル受信を自動承認するか。</summary>
    [ObservableProperty]
    public partial bool AutoAcceptFileTransfer { get; set; } = true;

    // N-1: 旧 Theme / AccentColor / FontSize は SelectedThemeIndex (ThemeMode) と二重定義のため削除済み

    // --- アプリ動作設定 ---

    /// <summary>Windows 起動時に自動起動するか。</summary>
    [ObservableProperty]
    public partial bool AutoStartWithWindows { get; set; }

    // === バージョン ===

    /// <summary>バージョン表示テキスト (例: "Ferry v1.0.39")。</summary>
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

    public SettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadFromSettings();
        LoadVersionInfo();

        // Lhamiel パターン: App 側の更新チェック状態に追従。購読直後に現状で初期同期する
        // (Settings 画面を開いた瞬間に起動時自動チェックが走っていてもボタンが正しく無効化される)。
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
            AutoStartWithWindows = s.AutoStartWithWindows;
            IgnoredUpdateTag = s.IgnoreUpdateTag ?? string.Empty;
        }
        finally
        {
            _isLoading = false;
        }
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
        App.SetLocale(value);
        // テーマ選択肢のテキストを再描画
        OnPropertyChanged(nameof(ThemeOptions));
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        App.UpdateCheckStateChanged -= OnAppUpdateCheckStateChanged;
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        // テーマを即座に切り替え (N-6: View 側 = App.axaml.cs で RequestedThemeVariant を更新)
        ThemeChangeRequested?.Invoke(this, value);
        SaveIfNotLoading();
    }
}
