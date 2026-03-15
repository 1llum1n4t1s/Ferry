using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Ferry.Infrastructure;
using Ferry.Models;

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
}
