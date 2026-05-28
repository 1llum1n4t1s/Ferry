using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            // 破損ファイルを退避して診断用に保全（次回 Save で静かに上書きされるのを防ぐ）
            try
            {
                var backup = _filePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                File.Move(_filePath, backup, overwrite: true);
                Util.Logger.Log($"破損した settings.json を退避しました: {backup}", Util.LogLevel.Warning);

                // rere レビュー #F-009: 破損ファイルから DeviceId だけサルベージ。
                // 旧実装は corrupt 退避するだけで DeviceId は新規採番されていたため、
                // ペア相手側の peers.json に書かれた旧 DeviceId と一致せず「自分は B を見ているが
                // B からは自分が居ない」という壊滅的状態に陥った (CLAUDE.md 既知制限通り)。
                // 退避ファイルから regex で DeviceId フィールドだけ抜き出して新 Settings に
                // 注入することで、JSON 全体は壊れていても ID は救出できる
                try
                {
                    var corruptContent = File.ReadAllText(backup);
                    var match = Regex.Match(corruptContent, "\"DeviceId\"\\s*:\\s*\"([a-fA-F0-9]{32})\"");
                    if (match.Success)
                    {
                        Settings.DeviceId = match.Groups[1].Value.ToLowerInvariant();
                        // CodeRabbit 指摘: MaskIp は IP 形式以外素通し → DeviceId が丸出しだったため
                        // 専用の MaskDeviceId (先頭 4 + ... + 末尾 4) に変更
                        Util.Logger.Log($"破損ファイルから DeviceId をサルベージ: {Util.Logger.MaskDeviceId(Settings.DeviceId)}", Util.LogLevel.Warning);
                        Save(); // 復元した DeviceId で新 settings.json を書き出し
                    }
                }
                catch (Exception salvageEx)
                {
                    Util.Logger.Log($"DeviceId サルベージ失敗: {salvageEx.Message}", Util.LogLevel.Warning);
                }
            }
            catch { /* 退避失敗は無視 */ }
        }
    }

    /// <summary>一時ファイルに書いてからリネームで置換し、書き込み中断による破損を防ぐ。</summary>
    private void WriteAtomic(byte[] json)
    {
        var tmp = _filePath + ".tmp";
        File.WriteAllBytes(tmp, json);
        File.Move(tmp, _filePath, overwrite: true);
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(Settings, AppSettingsJsonContext.Default.AppSettings);
            WriteAtomic(json);
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
            var tmp = _filePath + ".tmp";
            await File.WriteAllBytesAsync(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);
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
