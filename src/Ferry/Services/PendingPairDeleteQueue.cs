using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// rere #D-001(a) Phase B §6.3: Firebase pairs/{pairId} DELETE が失敗（オフライン等）したときの
/// 再試行キュー。`%APPDATA%\Ferry\pending-pair-deletes.json` に永続。
///
/// 起動時 + アプリ前面復帰時に <see cref="ProcessAsync"/> を呼んで queue の各アイテムを retry する。
/// backoff: 1min → 5min → 30min → 2h → 12h → 以降 24h 毎（<see cref="BackoffMs"/>）。
/// 打ち切りは無し（Codex 第2弾 fix）。オフライン/未認証が長引いても相手側ペアを確実に消すため、
/// 成功するまで 24h 上限の backoff で retry し続ける。
/// </summary>
public sealed class PendingPairDeleteQueue : IDisposable
{
    private readonly string _filePath;
    private readonly object _lock = new();
    // Codex P2 fix (第7弾 #5): SaveAsync を lock 外で実行していたため、 ProcessAsync retry 失敗時 SaveAsync と
    // 再ペアリングの RemoveAsync SaveAsync が並走すると古い non-empty snapshot が後勝ちして queue が resurrect し、
    // 後の retry が新ペアを誤削除する race があった。 PeerRegistryService 同様 SemaphoreSlim で書込をシリアライズし、
    // snapshot は lock 内で確定（payload 化）してから _saveLock 配下で書き出す。
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    // Codex P2 fix (第10弾 #2): in-flight cancel marker。 ProcessAsync の callback await 中に
    // RemoveAsync(pairId) が呼ばれたとき、 reserve で既に _items に居ない item の cancellation を
    // in-flight callback まで伝播させるための signal。 ProcessAsync は復帰時に _inFlight を check し、
    // cancelled なら queue に戻さない (= stale Firebase DELETE 後の retry resurrect を防ぐ)。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _inFlight = new();
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
        // Codex 第15弾 verify minor: payload は PersistAsync 内 (_saveLock 配下) で再 snapshot する。
        await PersistAsync();
    }

    /// <summary>
    /// Codex P2 fix (第4弾): 同じ pairId で再ペアリングが成立したときに queued delete を取り消す。
    /// 残しておくと後で queue retry が新ペアの pairs ノードを誤削除し remote unpair を引き起こす。
    /// pairs/{pairId} の PutPairAsync 成功時に呼ぶ。
    /// </summary>
    public async Task RemoveAsync(string pairId)
    {
        bool changed;
        bool cancelledInFlight = false;
        lock (_lock)
        {
            changed = _items.RemoveAll(i => i.PairId == pairId) > 0;
            // Codex P2 fix (第10弾 #2 + verify follow-up): in-flight callback にも cancellation signal を送る。
            // ProcessAsync の TryRemove と本 assignment は **同一 _lock 配下** で実行するため atomic に
            // インターリーブしない (= marker leak / cancellation lost が無い)。 ContainsKey で「in-flight 中の
            // pairId のみ」true 化することで、 既に終わった pairId に新規 entry を leak させない。
            if (_inFlight.ContainsKey(pairId))
            {
                _inFlight[pairId] = true;
                cancelledInFlight = true;
            }
        }
        // Codex P2 fix (第11弾 #2): changed か cancelledInFlight どちらでも Persist する。
        // 旧実装は _items.RemoveAll が 0 (= 既に reserve 済) のとき Persist skip していたため、
        // in-flight cancellation marker はメモリに記録されるものの、 アプリ終了 / background retry 中断で
        // 次起動時に cancellation 情報が失われ、 disk 上の queue に古い pairId が残って次起動の
        // ProcessAsync で誤って新ペアの Firebase pairs ノードを削除する race があった。
        // Codex 第15弾 verify minor: 最新 _items の snapshot は PersistAsync 内 (_saveLock 配下) で確定する。
        if (changed || cancelledInFlight) await PersistAsync();
    }

    /// <summary>キュー内の全アイテムを処理する。各アイテムについて delete callback を呼び、成功なら除去・失敗なら retry 情報を更新。</summary>
    public async Task ProcessAsync(Func<string, Task<bool>> deleteCallback)
    {
        List<PendingPairDelete> snapshot;
        lock (_lock) snapshot = [.. _items];
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var item in snapshot)
        {
            if (item.LastRetryAtMs > 0 && now - item.LastRetryAtMs < BackoffMs(item.RetryCount)) continue;
            // Codex P2 fix (第9弾 #3): lock 内で item を queue から一時 reserve (in-flight) してから callback を呼ぶ。
            // 旧実装 (第6弾 #3) は stillQueued check 後 lock release → callback 実行中に RemoveAsync が走っても
            // 止められず、stale retry が新ペアを誤削除する race が残っていた。reserve すれば callback 実行中の
            // RemoveAsync は「既に queue にない」状態の no-op になり race フリー。callback 失敗時は retry 情報を
            // 更新して queue に戻す (callback 中に他者 Enqueue があった場合の defensive 重複チェック付き)。
            PendingPairDelete reserved;
            lock (_lock)
            {
                var idx = _items.FindIndex(i => i.PairId == item.PairId);
                if (idx < 0)
                {
                    Util.Logger.Log($"pending delete pairId={item.PairId} は再ペアリングで取消済み → retry skip", Util.LogLevel.Debug);
                    continue;
                }
                reserved = _items[idx];
                _items.RemoveAt(idx);
                // Codex P2 fix (第10弾 #2): in-flight 登録。 callback await 中の RemoveAsync が
                // 値 true (cancelled) に書き換えれば、 復帰時に queue 復活させない判断ができる。
                _inFlight[reserved.PairId] = false;
            }
            bool ok;
            try { ok = await deleteCallback(reserved.PairId); }
            catch { ok = false; }
            // Codex P2 fix (第10弾 #2 + verify follow-up): cancelled marker の評価と callback 失敗時の
            // queue 戻しを **同一 _lock 配下で atomic に** 実行する。 cancelled==true なら RemoveAsync が
            // in-flight 中に走った = 再ペア成立。 callback の成否に関わらず queue 復活させず結果無視
            // (= stale Firebase DELETE 後の retry resurrect を防ぐ)。
            // Codex 第15弾 #3 (P2) fix: in-flight marker は **persist の後** に落とす。 旧実装はこの lock 内で
            // TryRemove して marker を即落とし、 queue の最新 state は batch footer (全 item 処理後に 1 度) でしか
            // 永続化しなかった。 そのため「delete 成功 → 同一 device と再ペア → footer の persist 前にアプリ終了」で、
            // RemoveAsync(pairId) が _items にも _inFlight にも当該 pairId を見つけられず Persist を skip し、
            // disk に古い pairId が残存 → 次起動の ProcessAsync が再生成済み pairs/{pairId} を誤削除する race が
            // あった。 ここでは marker を保持したまま cancelled を peek し、 reserve 由来の削除 (or 失敗時の戻し) を
            // 反映した payload を確定 → persist → その後に marker を落とす。 これで「marker drop 時点で disk は
            // 最新 state を反映済み」を保証し、 RemoveAsync は persist 完了まで marker を観測できる。
            bool cancelled;
            lock (_lock)
            {
                cancelled = _inFlight.TryGetValue(reserved.PairId, out var c) && c;
                if (!cancelled && !ok)
                {
                    reserved.RetryCount++;
                    reserved.LastRetryAtMs = now;
                    // Codex P2 fix (第2弾): 5 回打ち切りは廃止。オフライン/未認証で 2h 以内に復旧しないと
                    // 永久に相手側ペアが残ってしまっていた。失敗回数が増えても 24h 上限の backoff で
                    // 永続 retry し続ける。手動削除に逃げる代替案より、ユーザーの「ペア消したい」意図を
                    // 諦めずに反映する方が UX/信頼モデル上正しい。
                    // reserve した item を queue に戻す。callback 中に他者が同 pairId で Enqueue していた場合は
                    // skip (重複を避ける defensive チェック)。
                    if (!_items.Any(i => i.PairId == reserved.PairId))
                    {
                        _items.Add(reserved);
                    }
                }
            }
            // marker を落とす前に最新 state を disk へ確定する (= 上記 #3 fix の core)。
            // snapshot は PersistAsync 内 (_saveLock 配下) で再取得され、 reorder で stale 化しない (第15弾 verify minor)。
            await PersistAsync();
            lock (_lock) { _inFlight.TryRemove(reserved.PairId, out _); }
            // 成功時は既に reserve で _items から消えているので追加処理不要
        }
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

    // Codex 第15弾 verify minor (#3/persist reorder) fix: payload は **_saveLock 取得後に _lock 内で再 snapshot** する。
    // 旧実装 (第7弾 #5) は呼び出し側が _lock 内で payload を確定 → _saveLock 外で渡していたため、 SemaphoreSlim は
    // 厳密 FIFO 保証が無く、 異なる pairId の 2 つの persist が「新しい snapshot を先に書込 → 古い snapshot を
    // 後勝ちで上書き」する極小窓があった (item ごと persist 化で書込頻度 = reorder 機会が増える)。 _saveLock 配下で
    // 最新 _items を再 snapshot すれば、 直列化区間内で常に「最後の書込 = 最新の in-memory 状態」が保証され、
    // disk が stale snapshot で固定されて次起動時に再ペア済み pairs を誤削除する窓が消える。
    private async Task PersistAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            byte[] payload;
            lock (_lock) payload = JsonSerializer.SerializeToUtf8Bytes(_items, PendingDeleteJsonContext.Default.ListPendingPairDelete);
            await Util.AtomicFile.WriteAsync(_filePath, payload);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"pending-pair-deletes.json の保存に失敗: {ex.Message}", Util.LogLevel.Warning);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    /// <summary>
    /// Codex 第7弾 verify critical: SemaphoreSlim を Dispose してリソースリークを防ぐ。
    /// アプリ寿命と同等の単一インスタンスだが、 テスト並列実行で複数生成される経路に備えて IDisposable 化。
    /// </summary>
    public void Dispose()
    {
        _saveLock.Dispose();
    }
}

[JsonSerializable(typeof(List<PendingPairDelete>))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class PendingDeleteJsonContext : JsonSerializerContext { }
