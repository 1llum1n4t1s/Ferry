using Ferry.Models;
using Ferry.Services;

namespace Ferry.Tests.Services;

/// <summary>
/// PeerRegistryService のユニットテスト。
/// テストごとに一時ディレクトリを使い、永続化ファイルの干渉を防ぐ。
/// </summary>
public sealed class PeerRegistryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public PeerRegistryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FerryTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "peers.json");
    }

    public void Dispose()
    {
        // テスト後に一時ディレクトリを削除
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private PeerRegistryService CreateService() => new(_filePath);

    private static PairedPeer CreatePeer(string id = "peer1", string name = "テストPC") =>
        new() { PeerId = id, DisplayName = name };

    // === コンストラクタ ===

    [Fact]
    public void コンストラクタ_不正なJSONファイル_例外を投げずに空リストで初期化()
    {
        File.WriteAllText(_filePath, "{ invalid json }}}");
        var svc = CreateService();
        Assert.Empty(svc.GetPairedPeers());

        // 破損ファイルは .corrupt-* へ退避され、元の名前は空く（AGENTS.md の保存契約）
        Assert.False(File.Exists(_filePath));
        Assert.Single(Directory.GetFiles(_tempDir, "peers.json.corrupt-*"));
    }

    // === AddOrUpdatePeerAsync ===

    [Fact]
    public async Task AddOrUpdatePeerAsync_ファイルに永続化される()
    {
        var svc = CreateService();
        var peer = CreatePeer();
        // 接続経路・状態表示は実行時の状態で、peers.json へは書かれない（[JsonIgnore]）
        peer.Route = ConnectionRoute.Direct;
        peer.ConnectionStatusText = "接続済み";
        peer.PairSecret = "secret-key";
        await svc.AddOrUpdatePeerAsync(peer);

        // ファイルが存在することを確認
        Assert.True(File.Exists(_filePath));
        var json = File.ReadAllText(_filePath);
        Assert.Contains("\"PeerId\"", json);
        Assert.DoesNotContain("\"Route\"", json);
        Assert.DoesNotContain("\"ConnectionStatusText\"", json);
        Assert.DoesNotContain("\"RouteBadgeLabel\"", json);
        Assert.DoesNotContain("\"RouteBadgeTooltip\"", json);

        // 同一 PeerId の再追加は挿入ではなく既存の更新（DisplayName / LastTransferAt）。
        // PairSecret が空の更新では既存鍵を消さない。
        var lastTransfer = new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc);
        await svc.AddOrUpdatePeerAsync(new PairedPeer { PeerId = "peer1", DisplayName = "更新後", LastTransferAt = lastTransfer });
        var updated = Assert.Single(svc.GetPairedPeers());
        Assert.Equal("更新後", updated.DisplayName);
        Assert.Equal(lastTransfer, updated.LastTransferAt);
        Assert.Equal("secret-key", updated.PairSecret);

        // 新インスタンスで読み直して確認
        var svc2 = CreateService();
        var reloaded = Assert.Single(svc2.GetPairedPeers());
        Assert.Equal("peer1", reloaded.PeerId);
        Assert.Equal("更新後", reloaded.DisplayName);
        Assert.Equal(lastTransfer, reloaded.LastTransferAt);
        Assert.Equal("secret-key", reloaded.PairSecret);
    }

    // === RemovePeerAsync ===

    [Fact]
    public async Task RemovePeerAsync_削除がファイルに反映される()
    {
        var svc = CreateService();
        await svc.AddOrUpdatePeerAsync(CreatePeer());
        await svc.RemovePeerAsync("peer1");

        var svc2 = CreateService();
        Assert.Empty(svc2.GetPairedPeers());
    }

    // === UpdatePeerIfPresentAsync (Codex 第15弾 #2) ===

    [Fact]
    public async Task UpdatePeerIfPresentAsync_不在ピアはinsertせずfalseを返す()
    {
        // 「FindPeer → AddOrUpdate」の隙間で unpair された peer を resurrect しないための核心挙動:
        // 存在しない peer に対しては何も追加せず false を返す。
        var svc = CreateService();

        var ghost = new PairedPeer { PeerId = "removed", DisplayName = "幽霊", PairsSsotObserved = true };
        var result = await svc.UpdatePeerIfPresentAsync(ghost);

        Assert.False(result);
        Assert.Empty(svc.GetPairedPeers());
    }

    [Fact]
    public async Task UpdatePeerIfPresentAsync_空のPairSecretは既存鍵を消さない()
    {
        var svc = CreateService();
        await svc.AddOrUpdatePeerAsync(new PairedPeer { PeerId = "peer1", DisplayName = "PC-A", PairSecret = "secret-key" });

        // PairSecret を空にした更新でも既存鍵は維持される (AddOrUpdate と同じ規約)。
        var result = await svc.UpdatePeerIfPresentAsync(
            new PairedPeer { PeerId = "peer1", DisplayName = "PC-A2", PairSecret = null, PairsSsotObserved = true });

        Assert.True(result);
        Assert.Single(svc.GetPairedPeers());
        Assert.Equal("secret-key", svc.GetPairedPeers()[0].PairSecret);
        Assert.Equal("PC-A2", svc.GetPairedPeers()[0].DisplayName);
        Assert.True(svc.GetPairedPeers()[0].PairsSsotObserved);
    }
}
