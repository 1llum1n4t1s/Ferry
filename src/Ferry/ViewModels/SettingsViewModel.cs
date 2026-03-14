using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ferry.Services;

namespace Ferry.ViewModels;

/// <summary>
/// 設定パネルの ViewModel。
/// PC 名、スタートアップ、最小化起動、トレイ格納の設定を管理する。
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private string _displayName = Environment.MachineName;

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
        s.RunAtStartup = RunAtStartup;
        s.StartMinimized = StartMinimized;
        s.MinimizeToTray = MinimizeToTray;
        await _settingsService.SaveAsync();
    }

    partial void OnDisplayNameChanged(string value) => SaveSettingsCommand.Execute(null);
    partial void OnRunAtStartupChanged(bool value) => SaveSettingsCommand.Execute(null);
    partial void OnStartMinimizedChanged(bool value) => SaveSettingsCommand.Execute(null);
    partial void OnMinimizeToTrayChanged(bool value) => SaveSettingsCommand.Execute(null);
}
