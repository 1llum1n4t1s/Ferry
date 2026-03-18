using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Ferry.Infrastructure;
using Ferry.Models;
using Microsoft.Win32;

namespace Ferry.Services;

/// <summary>
/// アプリケーション設定をファイルに永続化するサービス。
/// %APPDATA%\Ferry\settings.json に保存する。
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly string _filePath;

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Ferry",
            "settings.json"))
    {
    }

    /// <summary>
    /// テスト用: ファイルパスを指定してインスタンスを生成する。
    /// </summary>
    public SettingsService(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null) Directory.CreateDirectory(dir);
        Load();
    }

    /// <summary>
    /// コンストラクタから同期的に呼び出す。
    /// </summary>
    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            // 初回起動: デフォルト設定を保存して DeviceId を確定させる
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllBytes(_filePath);
            var loaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
            if (loaded != null)
            {
                Settings = loaded;
            }
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"settings.json の読み込みに失敗: {ex.Message}", Util.LogLevel.Error);
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(Settings, AppSettingsJsonContext.Default.AppSettings);
            File.WriteAllBytes(_filePath, json);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"settings.json の保存に失敗: {ex.Message}", Util.LogLevel.Error);
        }
    }

    public Task LoadAsync()
    {
        Load();
        return Task.CompletedTask;
    }

    public async Task SaveAsync()
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(Settings, AppSettingsJsonContext.Default.AppSettings);
            await File.WriteAllBytesAsync(_filePath, json);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"settings.json の保存に失敗: {ex.Message}", Util.LogLevel.Error);
        }
    }

    // === Windows 自動起動（レジストリ） ===

    private const string AutoStartRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartValueName = "Ferry";

    /// <summary>
    /// Windows 起動時の自動起動をレジストリに登録/解除する。
    /// </summary>
    public void SetAutoStart(bool enable)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegistryKey, writable: true);
            if (key == null)
            {
                Util.Logger.Log("自動起動レジストリキーを開けませんでした", Util.LogLevel.Error);
                return;
            }

            if (enable)
            {
                // 実行ファイルのパスを登録
                var exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AutoStartValueName, $"\"{exePath}\"");
                    Util.Logger.Log($"自動起動を登録: {exePath}");
                }
            }
            else
            {
                // 登録を解除
                if (key.GetValue(AutoStartValueName) != null)
                {
                    key.DeleteValue(AutoStartValueName, throwOnMissingValue: false);
                    Util.Logger.Log("自動起動を解除");
                }
            }
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"自動起動の設定に失敗: {ex.Message}", Util.LogLevel.Error);
        }
    }
}
