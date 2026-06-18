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
public sealed class PeerRegistryService : IPeerRegistryService
{
    private readonly string _filePath;
    private readonly List<PairedPeer> _peers = [];
    // Codex 第6弾 #5 (P2): _peers の列挙 / 変更を直列化する。
    // PairSyncService の CheckOnceAsync が GetPairedPeers().ToList() で snapshot を取る間、
    // UI thread や pairing 経路から AddOrUpdatePeerAsync / RemovePeerAsync が走ると
    // InvalidOperationException が出て LoopAsync の outer catch で永久終了し、
    // 以降の remote unpair が反映されなくなる事故を防ぐ。
    private readonly object _peersLock = new();

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
        // SaveAsync は serialize+ファイル I/O なので lock 外で実行 (mutation は lock 内で完了済み)。
        await SaveAsync();
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
        await SaveAsync();
        // PeerRemoved は lock 外で発火する (lock 内で event ハンドラが peer registry を再帰的に
        // 触ると deadlock するため)。
        if (removed) PeerRemoved?.Invoke(this, peerId);
    }

    public PairedPeer? FindPeer(string peerId)
    {
        lock (_peersLock) return _peers.FirstOrDefault(p => p.PeerId == peerId);
    }

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

    private async Task SaveAsync()
    {
        try
        {
            // Codex 第6弾 #5 (P2): _peers を直接 serialize すると並行 mutation で例外になりうる。
            // lock 内で snapshot を取ってから lock 外で I/O を実行する。
            // (AOT JsonContext は List<PairedPeer> のみ登録済みのためそのまま使う。)
            List<PairedPeer> snapshot;
            lock (_peersLock) snapshot = [.. _peers];
            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, PeerRegistryJsonContext.Default.ListPairedPeer);
            // rere #B2-001: アトミック保存(tmp→Move)を共通ヘルパーへ集約
            await Util.AtomicFile.WriteAsync(_filePath, json);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"peers.json の保存に失敗: {ex.Message}", Util.LogLevel.Error);
        }
    }
}
