using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ferry.Services;

namespace Ferry.ViewModels;

/// <summary>
/// 設定パネルの ViewModel。
/// PC 名、テーマ、保存先、スタートアップ、最小化起動、トレイ格納の設定を管理する。
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private string _displayName = Environment.MachineName;

    /// <summary>ダークモードが有効か。false の場合ライトモード。</summary>
    [ObservableProperty]
    private bool _isDarkMode = true;

    /// <summary>受信ファイルの保存先ディレクトリ。</summary>
    [ObservableProperty]
    private string _saveDirectory = string.Empty;

    [ObservableProperty]
    private bool _runAtStartup;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _minimizeToTray;

    public SettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadFromSettings();
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
        IsDarkMode = s.ThemeMode != "Light";
        SaveDirectory = s.SaveDirectory;
        RunAtStartup = s.RunAtStartup;
        StartMinimized = s.StartMinimized;
        MinimizeToTray = s.MinimizeToTray;
    }

    /// <summary>
    /// 設定を保存する。プロパティ変更時に自動で呼び出す。
    /// </summary>
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var s = _settingsService.Settings;
        s.DisplayName = DisplayName;
        s.ThemeMode = IsDarkMode ? "Dark" : "Light";
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

    partial void OnIsDarkModeChanged(bool value)
    {
        // テーマを即座に切り替え
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
        }
        SaveSettingsCommand.Execute(null);
    }
}
