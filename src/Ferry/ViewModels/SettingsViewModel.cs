using System;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
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
    private string _displayName = Environment.MachineName;

    /// <summary>テーマモード選択肢の表示名一覧（ロケール連動）。</summary>
    public string[] ThemeOptions => [App.Text("Settings.Theme.System"), App.Text("Settings.Theme.Light"), App.Text("Settings.Theme.Dark")];

    /// <summary>選択中のテーマインデックス（0=System, 1=Light, 2=Dark）。</summary>
    [ObservableProperty]
    private int _selectedThemeIndex;

    /// <summary>選択中のロケールキー。</summary>
    [ObservableProperty]
    private string _selectedLocale = string.Empty;

    /// <summary>受信ファイルの保存先ディレクトリ。</summary>
    [ObservableProperty]
    private string _saveDirectory = string.Empty;

    [ObservableProperty]
    private bool _runAtStartup;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _minimizeToTray;

    /// <summary>チャット履歴の保持日数（0=無期限）。</summary>
    [ObservableProperty]
    private int _chatHistoryRetentionDays = 30;

    /// <summary>チャット履歴保持日数の選択肢。</summary>
    public int[] ChatRetentionOptions => [7, 14, 30, 60, 90];

    // --- 通知設定 ---

    /// <summary>受信サウンドを再生するか。</summary>
    [ObservableProperty]
    private bool _enableNotificationSound = true;

    // --- ファイル転送設定 ---

    /// <summary>受信ファイルの保存先フォルダ。空の場合はダウンロードフォルダ。</summary>
    [ObservableProperty]
    private string _receiveFileSavePath = string.Empty;

    /// <summary>ファイル受信を自動承認するか。</summary>
    [ObservableProperty]
    private bool _autoAcceptFileTransfer = true;

    // --- 外観設定 ---

    /// <summary>テーマ ("dark" / "light" / "system")。</summary>
    [ObservableProperty]
    private string _theme = "dark";

    /// <summary>アクセントカラー (hex)。</summary>
    [ObservableProperty]
    private string _accentColor = "#007AFF";

    /// <summary>フォントサイズ ("small" / "medium" / "large")。</summary>
    [ObservableProperty]
    private string _fontSize = "medium";

    // --- アプリ動作設定 ---

    /// <summary>Windows 起動時に自動起動するか。</summary>
    [ObservableProperty]
    private bool _autoStartWithWindows;

    // === バージョン ===

    /// <summary>バージョン表示テキスト。</summary>
    [ObservableProperty]
    private string _versionText = string.Empty;

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
            RunAtStartup = s.RunAtStartup;
            StartMinimized = s.StartMinimized;
            MinimizeToTray = s.MinimizeToTray;
            ChatHistoryRetentionDays = s.ChatHistoryRetentionDays;
            EnableNotificationSound = s.EnableNotificationSound;
            ReceiveFileSavePath = s.ReceiveFileSavePath;
            AutoAcceptFileTransfer = s.AutoAcceptFileTransfer;
            Theme = s.Theme;
            AccentColor = s.AccentColor;
            FontSize = s.FontSize;
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
        s.RunAtStartup = RunAtStartup;
        s.StartMinimized = StartMinimized;
        s.MinimizeToTray = MinimizeToTray;
        s.ChatHistoryRetentionDays = ChatHistoryRetentionDays;
        s.EnableNotificationSound = EnableNotificationSound;
        s.ReceiveFileSavePath = ReceiveFileSavePath;
        s.AutoAcceptFileTransfer = AutoAcceptFileTransfer;
        s.Theme = Theme;
        s.AccentColor = AccentColor;
        s.FontSize = FontSize;
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

    /// <summary>
    /// 手動更新チェックを実行する。App の更新ダイアログを表示する。
    /// </summary>
    [RelayCommand]
    private void CheckForUpdate()
    {
        if (Application.Current is App app)
            app.Check4Update(true);
    }

    partial void OnDisplayNameChanged(string value) { if (!_isLoading) SaveSettingsCommand.Execute(null); }
    partial void OnRunAtStartupChanged(bool value) { if (!_isLoading) SaveSettingsCommand.Execute(null); }
    partial void OnStartMinimizedChanged(bool value) { if (!_isLoading) SaveSettingsCommand.Execute(null); }
    partial void OnMinimizeToTrayChanged(bool value) { if (!_isLoading) SaveSettingsCommand.Execute(null); }
    partial void OnSaveDirectoryChanged(string value) { if (!_isLoading) SaveSettingsCommand.Execute(null); }
    partial void OnChatHistoryRetentionDaysChanged(int value) { if (!_isLoading) SaveSettingsCommand.Execute(null); }
    partial void OnEnableNotificationSoundChanged(bool value) { if (!_isLoading) SaveSettingsCommand.Execute(null); }
    partial void OnReceiveFileSavePathChanged(string value) { if (!_isLoading) SaveSettingsCommand.Execute(null); }
    partial void OnAutoAcceptFileTransferChanged(bool value) { if (!_isLoading) SaveSettingsCommand.Execute(null); }
    partial void OnThemeChanged(string value) { if (!_isLoading) SaveSettingsCommand.Execute(null); }
    partial void OnAccentColorChanged(string value) { if (!_isLoading) SaveSettingsCommand.Execute(null); }
    partial void OnFontSizeChanged(string value) { if (!_isLoading) SaveSettingsCommand.Execute(null); }
    partial void OnAutoStartWithWindowsChanged(bool value)
    {
        if (!_isLoading)
        {
            // レジストリに自動起動を登録/解除
            if (_settingsService is Services.SettingsService ss)
                ss.SetAutoStart(value);
            SaveSettingsCommand.Execute(null);
        }
    }

    partial void OnSelectedLocaleChanged(string value)
    {
        App.SetLocale(value);
        // テーマ選択肢のテキストを再描画
        OnPropertyChanged(nameof(ThemeOptions));
        if (!_isLoading) SaveSettingsCommand.Execute(null);
    }

    private void LoadVersionInfo()
    {
        var raw = typeof(SettingsViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
        // ビルドメタデータ（'+' 以降）を除去
        VersionText = raw.Contains('+') ? raw.Split('+')[0] : raw;
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        // テーマを即座に切り替え
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = value switch
            {
                1 => ThemeVariant.Light,
                2 => ThemeVariant.Dark,
                _ => ThemeVariant.Default, // OS 追従
            };
        }
        if (!_isLoading) SaveSettingsCommand.Execute(null);
    }
}
