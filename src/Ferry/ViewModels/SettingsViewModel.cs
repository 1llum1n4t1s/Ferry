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
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private bool _isLoading;

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

    /// <summary>受信ファイルの保存先フォルダ。空の場合はダウンロードフォルダ。</summary>
    [ObservableProperty]
    public partial string ReceiveFileSavePath { get; set; } = string.Empty;

    /// <summary>ファイル受信を自動承認するか。</summary>
    [ObservableProperty]
    public partial bool AutoAcceptFileTransfer { get; set; } = true;

    // N-1: 旧 Theme / AccentColor / FontSize は SelectedThemeIndex (ThemeMode) と二重定義のため削除済み

    // --- アプリ動作設定 ---

    /// <summary>Windows 起動時に自動起動するか。</summary>
    [ObservableProperty]
    public partial bool AutoStartWithWindows { get; set; }

    // === バージョン ===

    /// <summary>バージョン表示テキスト。</summary>
    [ObservableProperty]
    public partial string VersionText { get; set; } = string.Empty;

    public SettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadFromSettings();
        LoadVersionInfo();
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
            ReceiveFileSavePath = s.ReceiveFileSavePath;
            AutoAcceptFileTransfer = s.AutoAcceptFileTransfer;
            AutoStartWithWindows = s.AutoStartWithWindows;
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
        s.ReceiveFileSavePath = ReceiveFileSavePath;
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
    partial void OnReceiveFileSavePathChanged(string value) => SaveIfNotLoading();
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
        VersionText = AppVersion.Value;
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        // テーマを即座に切り替え (N-6: View 側 = App.axaml.cs で RequestedThemeVariant を更新)
        ThemeChangeRequested?.Invoke(this, value);
        SaveIfNotLoading();
    }
}
