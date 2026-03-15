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

    public IReadOnlyList<PairedPeer> GetPairedPeers() => _peers.AsReadOnly();

    public async Task AddOrUpdatePeerAsync(PairedPeer peer)
    {
        var existing = _peers.FirstOrDefault(p => p.PeerId == peer.PeerId);
        if (existing != null)
        {
            existing.DisplayName = peer.DisplayName;
            existing.LastTransferAt = peer.LastTransferAt;
        }
        else
        {
            _peers.Add(peer);
        }
        await SaveAsync();
    }

    public async Task RemovePeerAsync(string peerId)
    {
        _peers.RemoveAll(p => p.PeerId == peerId);
        await SaveAsync();
    }

    public PairedPeer? FindPeer(string peerId)
    {
        return _peers.FirstOrDefault(p => p.PeerId == peerId);
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
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(_peers, PeerRegistryJsonContext.Default.ListPairedPeer);
            await File.WriteAllBytesAsync(_filePath, json);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"peers.json の保存に失敗: {ex.Message}", Util.LogLevel.Error);
        }
    }
}
