using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Ferry.Infrastructure;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// アプリケーション設定をファイルに永続化するサービス。
/// %APPDATA%\Ferry\settings.json に保存する。
/// </summary>
public sealed class SettingsService : ISettingsService, IDisposable
{
    private readonly string _filePath;

    // Codex 第12弾 verify critical: SaveAsync 内の JsonSerializer.SerializeToUtf8Bytes は
    // SettingsViewModel / MainWindow / ConnectionService.OnPairingDetected 等の各経路から並列で呼ばれる。
    // serialize は AppSettings 内の List/HashSet を foreach で enumerate するため、 別経路の mutation と
    // 重なると "Collection was modified" 例外 → 保存失敗 → in-memory と persisted の desync で
    // SeenPairingIds replay 防御が落ちる。 SemaphoreSlim で SaveAsync 全体 (serialize + write) を
    // 直列化して enumerate と mutation を時間軸上分離する。
    private readonly System.Threading.SemaphoreSlim _saveLock = new(1, 1);

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
            // 初回起動: デフォルト設定を保存して DeviceId を確定させる。
            // 新規インストールは UseCloudflareSignaling 既定 true なので移行済み扱いにしておく
            // （次回起動でマイグレーションが無駄に走るのを防ぐ）。
            Settings.MigratedToCloudflareDefault = true;
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
            MigrateToCloudflareDefaultIfNeeded();
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

    /// <summary>
    /// CF 単独完結移行 Step 5 の一度きりマイグレーション。
    /// 旧 settings.json には <c>UseCloudflareSignaling=false</c> が永続化されているため、コード既定を
    /// true へ反転しただけでは既存クライアントが Firebase 経路に残る。<see cref="AppSettings.MigratedToCloudflareDefault"/>
    /// が未設定（=旧クライアント）なら Cloudflare 既定へ一度だけ引き上げ、フラグを立てて永続化する。
    /// フラグが立った後はユーザーが明示的に false へ戻した値を上書きしない（rollback の可逆性を保つ）。
    /// </summary>
    private void MigrateToCloudflareDefaultIfNeeded()
    {
        if (Settings.MigratedToCloudflareDefault) return;

        // 旧クライアント（永続値 false）からの引き上げのみログに残す。
        // 新規インストールや既に true の早期検証クライアントはフラグだけ立てて静かに保存する。
        bool flipped = !Settings.UseCloudflareSignaling;
        Settings.UseCloudflareSignaling = true;
        Settings.MigratedToCloudflareDefault = true;
        if (flipped)
        {
            Util.Logger.Log(
                "設定マイグレーション: signaling 経路を Cloudflare 既定へ移行しました (UseCloudflareSignaling=true)。" +
                "Firebase へ戻す場合は settings.json で false に設定してください。",
                Util.LogLevel.Info);
        }
        // ここは Load()（コンストラクタ内・単一スレッド初期化経路）からのみ呼ばれるため、
        // SaveAsync の _saveLock を経由しない同期 Save() で安全（並行 SaveAsync を呼ぶ VM/MainWindow/
        // ConnectionService はまだ生成されておらず、List/HashSet enumerate との競合は起こらない）。
        // 起動後の経路からこの helper を呼ぶ場合は SaveAsync 側の直列化を経由させること。
        Save();
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(Settings, AppSettingsJsonContext.Default.AppSettings);
            Util.AtomicFile.Write(_filePath, json);  // rere #B2-001: アトミック保存を共通ヘルパーへ集約
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
        // Codex 第12弾 verify critical fix: SemaphoreSlim で SaveAsync 全体を直列化。
        // serialize の foreach と mutation が同一 List/HashSet 上で衝突しないよう、
        // 「serialize → write」の組を atomic に行う。 mutation 側 (ConnectionService.OnPairingDetected)
        // は List 参照を copy-on-write で差し替える (古い List 参照は in-flight serialize から enumerate 中)。
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(Settings, AppSettingsJsonContext.Default.AppSettings);
            await Util.AtomicFile.WriteAsync(_filePath, json);  // rere #B2-001
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"settings.json の保存に失敗: {ex.Message}", Util.LogLevel.Error);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>SemaphoreSlim をクリーンアップする。 アプリ寿命 = サービス寿命の前提で通常呼ばれないが、 テスト並列実行用に IDisposable 化。</summary>
    public void Dispose() => _saveLock.Dispose();

    // === OS ログイン時の自動起動 ===

    /// <summary>
    /// OS ログイン時の自動起動を登録/解除する。OS 別の実体（Windows=レジストリ /
    /// macOS=LaunchAgent / Linux=XDG autostart）は <see cref="Ferry.Util.AutoStartManager"/> に委譲する。
    /// </summary>
    public void SetAutoStart(bool enable) => Ferry.Util.AutoStartManager.Apply(enable);
}
