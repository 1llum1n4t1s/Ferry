using System;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ferry.Services;
using Velopack;
using Velopack.Sources;

namespace Ferry.ViewModels;

/// <summary>
/// 設定パネルの ViewModel。
/// PC 名、テーマ、言語、保存先、スタートアップ、最小化起動、トレイ格納の設定を管理する。
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;

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

    // === バージョン・更新 ===

    /// <summary>バージョン表示テキスト。</summary>
    [ObservableProperty]
    private string _versionText = string.Empty;

    /// <summary>更新チェック中かどうか。</summary>
    [ObservableProperty]
    private bool _isCheckingUpdate;

    /// <summary>更新チェック結果のステータステキスト。</summary>
    [ObservableProperty]
    private string _updateStatusText = string.Empty;

    /// <summary>GitHub Releases の更新元リポジトリ URL。</summary>
    private const string GitHubRepoUrl = "https://github.com/1llum1n4t1s/Ferry";

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
        var s = _settingsService.Settings;
        DisplayName = s.DisplayName;
        SelectedThemeIndex = s.ThemeMode switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0, // "System" またはその他
        };
        SelectedLocale = string.IsNullOrEmpty(s.Locale) ? App.DetectDefaultLocale() : s.Locale;
        SaveDirectory = s.SaveDirectory;
        RunAtStartup = s.RunAtStartup;
        StartMinimized = s.StartMinimized;
        MinimizeToTray = s.MinimizeToTray;
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

    partial void OnDisplayNameChanged(string value) => SaveSettingsCommand.Execute(null);
    partial void OnRunAtStartupChanged(bool value) => SaveSettingsCommand.Execute(null);
    partial void OnStartMinimizedChanged(bool value) => SaveSettingsCommand.Execute(null);
    partial void OnMinimizeToTrayChanged(bool value) => SaveSettingsCommand.Execute(null);
    partial void OnSaveDirectoryChanged(string value) => SaveSettingsCommand.Execute(null);

    partial void OnSelectedLocaleChanged(string value)
    {
        App.SetLocale(value);
        // テーマ選択肢のテキストを再描画
        OnPropertyChanged(nameof(ThemeOptions));
        SaveSettingsCommand.Execute(null);
    }

    private void LoadVersionInfo()
    {
        var raw = typeof(SettingsViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
        // ビルドメタデータ（'+' 以降）を除去
        VersionText = raw.Contains('+') ? raw.Split('+')[0] : raw;
    }

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        if (IsCheckingUpdate) return;

        IsCheckingUpdate = true;
        UpdateStatusText = App.Text("Update.Checking");

        try
        {
            var source = new GithubSource(GitHubRepoUrl, string.Empty, false);
            var options = new UpdateOptions { ExplicitChannel = "win" };
            var mgr = new UpdateManager(source, options);

            if (!mgr.IsInstalled)
            {
                UpdateStatusText = App.Text("Update.DevEnvironment");
                return;
            }

            var newVersion = await mgr.CheckForUpdatesAsync();
            if (newVersion != null)
            {
                UpdateStatusText = App.Text("Update.Downloading", newVersion.TargetFullRelease.Version);
                await mgr.DownloadUpdatesAsync(newVersion);
                UpdateStatusText = App.Text("Update.Applying");
                mgr.ApplyUpdatesAndRestart(newVersion);
            }
            else
            {
                UpdateStatusText = App.Text("Update.UpToDate");
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = App.Text("Update.Error", ex.Message);
            Util.Logger.Log($"手動更新チェック失敗: {ex.Message}", Util.LogLevel.Warning);
        }
        finally
        {
            IsCheckingUpdate = false;
        }
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
        SaveSettingsCommand.Execute(null);
    }
}
