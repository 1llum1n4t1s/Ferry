using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// rere #D-001(a) Phase B §6.3: Firebase pairs/{pairId} DELETE が失敗（オフライン等）したときの
/// 再試行キュー。`%APPDATA%\Ferry\pending-pair-deletes.json` に永続。
///
/// 起動時 + アプリ前面復帰時に <see cref="ProcessAsync"/> を呼んで queue の各アイテムを retry する。
/// backoff: 1min, 5min, 30min, 2h, 12h。RetryCount >= 5 で打ち切り（Warning ログ + queue から除去）。
/// </summary>
public sealed class PendingPairDeleteQueue
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private List<PendingPairDelete> _items = [];

    public PendingPairDeleteQueue()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Ferry",
            "pending-pair-deletes.json"))
    {
    }

    public PendingPairDeleteQueue(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null) Directory.CreateDirectory(dir);
        Load();
    }

    /// <summary>キューに新しい削除リトライアイテムを追加（または既存をリセット）。</summary>
    public async Task EnqueueAsync(string pairId)
    {
        lock (_lock)
        {
            var existing = _items.FirstOrDefault(i => i.PairId == pairId);
            if (existing != null)
            {
                existing.LastRetryAtMs = 0;  // 即時 retry 対象に戻す
                existing.RetryCount = 0;
            }
            else
            {
                _items.Add(new PendingPairDelete { PairId = pairId, LastRetryAtMs = 0, RetryCount = 0 });
            }
        }
        await SaveAsync();
    }

    /// <summary>キュー内の全アイテムを処理する。各アイテムについて delete callback を呼び、成功なら除去・失敗なら retry 情報を更新。</summary>
    public async Task ProcessAsync(Func<string, Task<bool>> deleteCallback)
    {
        List<PendingPairDelete> snapshot;
        lock (_lock) snapshot = [.. _items];
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var changed = false;
        foreach (var item in snapshot)
        {
            if (item.LastRetryAtMs > 0 && now - item.LastRetryAtMs < BackoffMs(item.RetryCount)) continue;
            bool ok;
            try { ok = await deleteCallback(item.PairId); }
            catch { ok = false; }
            lock (_lock)
            {
                if (ok)
                {
                    _items.RemoveAll(i => i.PairId == item.PairId);
                    changed = true;
                }
                else
                {
                    var current = _items.FirstOrDefault(i => i.PairId == item.PairId);
                    if (current != null)
                    {
                        current.RetryCount++;
                        current.LastRetryAtMs = now;
                        // Codex P2 fix (第2弾): 5 回打ち切りは廃止。オフライン/未認証で 2h 以内に復旧しないと
                        // 永久に相手側ペアが残ってしまっていた。失敗回数が増えても 24h 上限の backoff で
                        // 永続 retry し続ける。手動削除に逃げる代替案より、ユーザーの「ペア消したい」意図を
                        // 諦めずに反映する方が UX/信頼モデル上正しい。
                        changed = true;
                    }
                }
            }
        }
        if (changed) await SaveAsync();
    }

    private static long BackoffMs(int retryCount) => retryCount switch
    {
        0 => 0,                  // 即時
        1 => 60_000,             // 1min
        2 => 300_000,            // 5min
        3 => 1_800_000,          // 30min
        4 => 7_200_000,          // 2h
        5 => 43_200_000,         // 12h
        _ => 86_400_000,         // 24h (以降は 24h 毎に retry し続ける)
    };

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var bytes = File.ReadAllBytes(_filePath);
            var items = JsonSerializer.Deserialize(bytes, PendingDeleteJsonContext.Default.ListPendingPairDelete);
            if (items != null) _items = items;
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"pending-pair-deletes.json の読み込みに失敗: {ex.Message}", Util.LogLevel.Warning);
            try
            {
                File.Move(_filePath, _filePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            }
            catch { /* 退避失敗は無視 */ }
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            List<PendingPairDelete> snapshot;
            lock (_lock) snapshot = [.. _items];
            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, PendingDeleteJsonContext.Default.ListPendingPairDelete);
            await Util.AtomicFile.WriteAsync(_filePath, json);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"pending-pair-deletes.json の保存に失敗: {ex.Message}", Util.LogLevel.Warning);
        }
    }

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }
}

[JsonSerializable(typeof(List<PendingPairDelete>))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class PendingDeleteJsonContext : JsonSerializerContext { }
