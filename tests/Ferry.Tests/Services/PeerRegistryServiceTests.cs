using System;
using System.IO;
using System.Threading.Tasks;
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
    public void コンストラクタ_ファイルが存在しない場合_空リストで初期化()
    {
        var svc = CreateService();
        Assert.Empty(svc.GetPairedPeers());
    }

    [Fact]
    public async Task コンストラクタ_既存ファイルからピアを読み込む()
    {
        // 事前にデータを保存
        var svc1 = CreateService();
        await svc1.AddOrUpdatePeerAsync(CreatePeer());

        // 新しいインスタンスで読み込み
        var svc2 = CreateService();
        Assert.Single(svc2.GetPairedPeers());
        Assert.Equal("peer1", svc2.GetPairedPeers()[0].PeerId);
    }

    [Fact]
    public void コンストラクタ_不正なJSONファイル_例外を投げずに空リストで初期化()
    {
        File.WriteAllText(_filePath, "{ invalid json }}}");
        var svc = CreateService();
        Assert.Empty(svc.GetPairedPeers());
    }

    // === AddOrUpdatePeerAsync ===

    [Fact]
    public async Task AddOrUpdatePeerAsync_新規ピアを追加()
    {
        var svc = CreateService();
        var peer = CreatePeer();

        await svc.AddOrUpdatePeerAsync(peer);

        Assert.Single(svc.GetPairedPeers());
        Assert.Equal("peer1", svc.GetPairedPeers()[0].PeerId);
        Assert.Equal("テストPC", svc.GetPairedPeers()[0].DisplayName);
    }

    [Fact]
    public async Task AddOrUpdatePeerAsync_既存ピアのDisplayNameとLastTransferAtを更新()
    {
        var svc = CreateService();
        var peer = CreatePeer();
        await svc.AddOrUpdatePeerAsync(peer);

        var updated = new PairedPeer
        {
            PeerId = "peer1",
            DisplayName = "更新後の名前",
            LastTransferAt = new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc),
        };
        await svc.AddOrUpdatePeerAsync(updated);

        // 1件のまま更新される
        Assert.Single(svc.GetPairedPeers());
        Assert.Equal("更新後の名前", svc.GetPairedPeers()[0].DisplayName);
        Assert.Equal(new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc), svc.GetPairedPeers()[0].LastTransferAt);
    }

    [Fact]
    public async Task AddOrUpdatePeerAsync_複数ピアを追加()
    {
        var svc = CreateService();
        await svc.AddOrUpdatePeerAsync(CreatePeer("peer1", "PC-A"));
        await svc.AddOrUpdatePeerAsync(CreatePeer("peer2", "PC-B"));

        Assert.Equal(2, svc.GetPairedPeers().Count);
    }

    [Fact]
    public async Task AddOrUpdatePeerAsync_ファイルに永続化される()
    {
        var svc = CreateService();
        await svc.AddOrUpdatePeerAsync(CreatePeer());

        // ファイルが存在することを確認
        Assert.True(File.Exists(_filePath));

        // 新インスタンスで読み直して確認
        var svc2 = CreateService();
        Assert.Single(svc2.GetPairedPeers());
    }

    // === RemovePeerAsync ===

    [Fact]
    public async Task RemovePeerAsync_存在するピアを削除()
    {
        var svc = CreateService();
        await svc.AddOrUpdatePeerAsync(CreatePeer());

        await svc.RemovePeerAsync("peer1");

        Assert.Empty(svc.GetPairedPeers());
    }

    [Fact]
    public async Task RemovePeerAsync_存在しないPeerIdでも例外を投げない()
    {
        var svc = CreateService();
        await svc.AddOrUpdatePeerAsync(CreatePeer());

        // 存在しない peerId で呼び出し → 例外なし
        var ex = await Record.ExceptionAsync(() => svc.RemovePeerAsync("nonexistent"));
        Assert.Null(ex);

        // 既存ピアは残る
        Assert.Single(svc.GetPairedPeers());
    }

    [Fact]
    public async Task RemovePeerAsync_削除がファイルに反映される()
    {
        var svc = CreateService();
        await svc.AddOrUpdatePeerAsync(CreatePeer());
        await svc.RemovePeerAsync("peer1");

        var svc2 = CreateService();
        Assert.Empty(svc2.GetPairedPeers());
    }

    // === FindPeer ===

    [Fact]
    public async Task FindPeer_存在するピアを返す()
    {
        var svc = CreateService();
        await svc.AddOrUpdatePeerAsync(CreatePeer());

        var found = svc.FindPeer("peer1");
        Assert.NotNull(found);
        Assert.Equal("テストPC", found.DisplayName);
    }

    [Fact]
    public void FindPeer_存在しないPeerIdでnullを返す()
    {
        var svc = CreateService();
        Assert.Null(svc.FindPeer("nonexistent"));
    }

    // === GetPairedPeers ===

    [Fact]
    public void GetPairedPeers_読み取り専用リストを返す()
    {
        var svc = CreateService();
        var peers = svc.GetPairedPeers();

        // IReadOnlyList なので Add 等のメソッドが使えないことを型で保証
        Assert.IsAssignableFrom<IReadOnlyList<PairedPeer>>(peers);
    }
}
