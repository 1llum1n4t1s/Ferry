using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Ferry.Infrastructure;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// ペアリング済みピアの永続化サービス。
/// %APPDATA%\Ferry\peers.json にペア情報を保存する。
/// </summary>
public sealed class PeerRegistryService : IPeerRegistryService, IDisposable
{
    private readonly string _filePath;
    private readonly List<PairedPeer> _peers = [];
    // Codex 第12弾 #3 (P2) fix: ユーザー起点の unpair が in-flight な peerId を保持する。
    // RemovePeerAsync の前に MarkPendingRemoval で立て、 finally で ClearPendingRemoval する。
    // WritePairRecordWithFallback (責任者経路 / 30s fallback) が IsPendingRemoval を check し、
    // true ならば PUT をスキップして「削除中のペアを resurrect」する race を構造的に塞ぐ。
    // ConcurrentDictionary なので _peersLock とは独立に lock-free で参照できる。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _pendingRemovals = new();
    // Codex 第6弾 #5 (P2): _peers の列挙 / 変更を直列化する。
    // PairSyncService の CheckOnceAsync が GetPairedPeers().ToList() で snapshot を取る間、
    // UI thread や pairing 経路から AddOrUpdatePeerAsync / RemovePeerAsync が走ると
    // InvalidOperationException が出て LoopAsync の outer catch で永久終了し、
    // 以降の remote unpair が反映されなくなる事故を防ぐ。
    private readonly object _peersLock = new();
    // Codex 第7弾 #3 (P2): SaveAsync 自体を直列化して「snapshot 取得→Save」の順序を保つ。
    // 旧実装は snapshot 取得後に lock 外で SaveAsync が並走するため、
    // 新しい snapshot を取った後に古い snapshot の書込が後勝ちする race があり、
    // 例: 新ペアリングの duplicate AddOrUpdate (PairsSsotObserved=false) と SSoT mark (true) が
    // overlap して false で永続化 → unobserved guard で長期間削除遅延、などの事故になっていた。
    private readonly System.Threading.SemaphoreSlim _saveLock = new(1, 1);

    public PeerRegistryService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Ferry",
            "peers.json"))
    {
    }

    /// <summary>
    /// テスト用: ファイルパスを指定してインスタンスを生成する。
    /// </summary>
    public PeerRegistryService(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null) Directory.CreateDirectory(dir);
        Load();
    }

    public IReadOnlyList<PairedPeer> GetPairedPeers()
    {
        // Codex 第6弾 #5 (P2): snapshot を返して呼び出し側の列挙中に
        // _peers が mutate されても InvalidOperationException が出ないようにする。
        lock (_peersLock) return _peers.ToArray();
    }

    public async Task AddOrUpdatePeerAsync(PairedPeer peer)
    {
        lock (_peersLock)
        {
            var existing = _peers.FirstOrDefault(p => p.PeerId == peer.PeerId);
            if (existing != null)
            {
                existing.DisplayName = peer.DisplayName;
                existing.LastTransferAt = peer.LastTransferAt;
                // rere #D-001(b): 再ペアリングで新しい PairSecret を渡されたら更新する
                // （DisplayName のみの更新で既存の鍵を消さないよう、非 null のときだけ上書き）。
                if (!string.IsNullOrEmpty(peer.PairSecret))
                    existing.PairSecret = peer.PairSecret;
            }
            else
            {
                _peers.Add(peer);
            }
        }
        // Codex 第15弾 verify minor: snapshot は PersistAsync 内 (_saveLock 配下) で再取得し reorder で stale 化させない。
        await PersistAsync();
    }

    public async Task<bool> UpdatePeerIfPresentAsync(PairedPeer peer)
    {
        lock (_peersLock)
        {
            var existing = _peers.FirstOrDefault(p => p.PeerId == peer.PeerId);
            // Codex 第15弾 #2 (P2): 存在しないなら insert せず即 false で抜ける。
            // 「FindPeer → AddOrUpdate」2 段操作の隙間で手動 unpair が走ったときに
            // 削除済み peer を再追加しないための update-only API。
            if (existing == null) return false;

            existing.DisplayName = peer.DisplayName;
            existing.LastTransferAt = peer.LastTransferAt;
            existing.PairsSsotObserved = peer.PairsSsotObserved;
            // 非 null のときだけ PairSecret を上書き (AddOrUpdate と同じく既存鍵を消さない)。
            if (!string.IsNullOrEmpty(peer.PairSecret))
                existing.PairSecret = peer.PairSecret;
        }
        await PersistAsync();
        return true;
    }

    /// <summary>Codex P2 fix: PairSyncService の remote unpair 検知から UI を即時更新するための通知。</summary>
    public event EventHandler<string>? PeerRemoved;

    public async Task RemovePeerAsync(string peerId)
    {
        bool removed;
        lock (_peersLock)
        {
            removed = _peers.RemoveAll(p => p.PeerId == peerId) > 0;
        }
        // Codex 第15弾 verify minor: snapshot は PersistAsync 内 (_saveLock 配下) で再取得する。
        await PersistAsync();
        // PeerRemoved は lock 外で発火する (lock 内で event ハンドラが peer registry を再帰的に
        // 触ると deadlock するため)。
        if (removed) PeerRemoved?.Invoke(this, peerId);
    }

    public PairedPeer? FindPeer(string peerId)
    {
        lock (_peersLock) return _peers.FirstOrDefault(p => p.PeerId == peerId);
    }

    // Codex 第12弾 #3 (P2) fix: pending-removal marker の操作。
    public void MarkPendingRemoval(string peerId) => _pendingRemovals.TryAdd(peerId, 0);
    public void ClearPendingRemoval(string peerId) => _pendingRemovals.TryRemove(peerId, out _);
    public bool IsPendingRemoval(string peerId) => _pendingRemovals.ContainsKey(peerId);

    private void Load()
    {
        if (!File.Exists(_filePath)) return;

        try
        {
            var json = File.ReadAllBytes(_filePath);
            var peers = JsonSerializer.Deserialize(json, PeerRegistryJsonContext.Default.ListPairedPeer);
            if (peers != null)
            {
                _peers.AddRange(peers);
            }
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"peers.json の読み込みに失敗: {ex.Message}", Util.LogLevel.Error);
            // 破損ファイルを退避して診断用に保全
            try
            {
                var backup = _filePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                File.Move(_filePath, backup, overwrite: true);
                Util.Logger.Log($"破損した peers.json を退避しました: {backup}", Util.LogLevel.Warning);
            }
            catch { /* 退避失敗は無視 */ }
        }
    }

    // Codex 第7弾 #3 (P2): mutation lock 内で確定済みの payload を SemaphoreSlim で順次 disk に書く。
    // SemaphoreSlim はファイル I/O のみを直列化し、mutation lock とは別オブジェクトなので
    // deadlock リスクは無い (mutation lock を保持したまま SemaphoreSlim を待つ経路は無い)。
    // Codex 第15弾 verify minor (#3/persist reorder と同型) fix: payload を **_saveLock 取得後に _peersLock 内で
    // 再 snapshot** する。 旧実装 (第7弾 #3) は呼び出し側が _peersLock 内で payload を確定 → _saveLock 外で渡して
    // いたが、 SemaphoreSlim は厳密 FIFO 保証が無いため、 並走した 2 つの persist が「新しい snapshot を先に書込 →
    // 古い snapshot を後勝ちで上書き」する極小窓が残っていた。 _saveLock 配下で最新 _peers を再 snapshot すれば、
    // 直列化区間内で常に「最後の書込 = 最新の in-memory 状態」になり reorder 窓が消える。
    private async Task PersistAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            byte[] payload;
            lock (_peersLock) payload = JsonSerializer.SerializeToUtf8Bytes(_peers, PeerRegistryJsonContext.Default.ListPairedPeer);
            // rere #B2-001: アトミック保存(tmp→Move)を共通ヘルパーへ集約
            await Util.AtomicFile.WriteAsync(_filePath, payload);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"peers.json の保存に失敗: {ex.Message}", Util.LogLevel.Error);
        }
        finally
        {
            _saveLock.Release();
        }
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
