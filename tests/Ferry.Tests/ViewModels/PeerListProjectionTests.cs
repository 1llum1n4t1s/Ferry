using System;
using System.Linq;
using Ferry.Models;
using Ferry.ViewModels;

namespace Ferry.Tests.ViewModels;

/// <summary>
/// 宛先リストの投影ロジック <see cref="ConnectionViewModel.BuildPeerProjection"/>（検索 / セクション分割 / ソート）の
/// 純関数テスト。Dispatcher 非依存なので VM インスタンス不要。見出しラベルは恒等関数で解決する。
/// </summary>
public class PeerListProjectionTests
{
    private static PairedPeer Peer(string name, bool online = false, bool pinned = false,
        ConnectionRoute route = ConnectionRoute.Unknown, bool transferring = false, int active = 0,
        DateTime? lastTransfer = null)
        => new()
        {
            PeerId = name,
            DisplayName = name,
            IsOnline = online,
            IsPinned = pinned,
            Route = route,
            IsTransferring = transferring,
            ActiveTransferCount = active,
            LastTransferAt = lastTransfer,
        };

    private static System.Collections.Generic.List<object> Build(
        System.Collections.Generic.IEnumerable<PairedPeer> peers, string? search = null, PeerSortMode mode = PeerSortMode.Name)
        => ConnectionViewModel.BuildPeerProjection(peers, search, mode, k => k);

    private static string[] PeerNames(System.Collections.Generic.List<object> projection)
        => projection.OfType<PairedPeer>().Select(p => p.DisplayName).ToArray();

    private static string[] SectionLabels(System.Collections.Generic.List<object> projection)
        => projection.OfType<PeerListSection>().Select(h => h.Label).ToArray();

    [Fact]
    public void 検索は表示名の部分一致大文字小文字無視でフィルタする()
    {
        var peers = new[] { Peer("Alice", online: true), Peer("Bob", online: true), Peer("alfred", online: true) };
        var result = Build(peers, search: "AL");
        // "Alice" と "alfred" が一致（大文字小文字無視）。名前順で alfred < Alice。
        Assert.Equal(new[] { "alfred", "Alice" }, PeerNames(result));
        Assert.DoesNotContain("Bob", PeerNames(result));
    }

    [Fact]
    public void 空白のみの検索は全件を返す()
    {
        var peers = new[] { Peer("A", online: true), Peer("B", online: true) };
        Assert.Equal(2, PeerNames(Build(peers, search: "   ")).Length);
    }

    [Fact]
    public void 検索に一致しなくても選択中ピアkeepは常に表示される()
    {
        // 退行防止: 転送中に検索ボックスへ非一致文字を打っても選択中ピアが投影から消えないこと
        // （消えると SelectedPeer=null が外部購読者に伝播して転送パネルが空になる HIGH 退行が起きる）。
        var bob = Peer("Bob", online: true);
        var peers = new[] { Peer("Alice", online: true), bob };
        var result = ConnectionViewModel.BuildPeerProjection(peers, "Ali", PeerSortMode.Name, k => k, keep: bob);
        var names = PeerNames(result);
        Assert.Contains("Bob", names);   // 検索 "Ali" に一致しないが keep なので残る
        Assert.Contains("Alice", names);
    }

    [Fact]
    public void keepが無ければ非一致ピアは通常どおり除外される()
    {
        var peers = new[] { Peer("Alice", online: true), Peer("Bob", online: true) };
        var names = PeerNames(Build(peers, search: "Ali"));  // keep=null
        Assert.Equal(new[] { "Alice" }, names);
    }

    [Fact]
    public void ピンオンラインオフラインの3セクションに分かれ空セクションは見出しごと省略される()
    {
        var peers = new[]
        {
            Peer("P", online: false, pinned: true),  // pinned（offline だがピンセクションへ）
            Peer("O1", online: true),
            Peer("O2", online: true),
            // 非ピンの offline は無し → オフライン見出しは省略
        };
        var result = Build(peers);
        Assert.Equal(new[] { "Peer.Section.Pinned", "Peer.Section.Online" }, SectionLabels(result));
        Assert.IsType<PeerListSection>(result[0]);
        Assert.Equal("P", ((PairedPeer)result[1]).DisplayName);
    }

    [Fact]
    public void ピン留めはオンライン状態を問わずピンセクションへ入る()
    {
        var peers = new[]
        {
            Peer("PinnedOnline", online: true, pinned: true),
            Peer("PlainOnline", online: true),
            Peer("PlainOffline", online: false),
        };
        var result = Build(peers);
        Assert.Equal(new[] { "Peer.Section.Pinned", "Peer.Section.Online", "Peer.Section.Offline" }, SectionLabels(result));
        // 先頭セクション = ピン、その直後の行が PinnedOnline
        Assert.Equal("PinnedOnline", ((PairedPeer)result[1]).DisplayName);
        // 残りはオンライン/オフラインに分かれる
        Assert.Contains("PlainOnline", PeerNames(result));
        Assert.Contains("PlainOffline", PeerNames(result));
    }

    [Fact]
    public void 名前順ソートは表示名昇順大文字小文字無視()
    {
        var peers = new[] { Peer("Charlie", online: true), Peer("alice", online: true), Peer("Bob", online: true) };
        Assert.Equal(new[] { "alice", "Bob", "Charlie" }, PeerNames(Build(peers, mode: PeerSortMode.Name)));
    }

    [Fact]
    public void 転送中優先ソートは転送中の相手を上に積む()
    {
        var peers = new[]
        {
            Peer("Idle", online: true, transferring: false),
            Peer("Busy", online: true, transferring: true, active: 2),
        };
        Assert.Equal(new[] { "Busy", "Idle" }, PeerNames(Build(peers, mode: PeerSortMode.Transferring)));
    }

    [Fact]
    public void 経路順ソートはLANP2PRelay未確定の順()
    {
        var peers = new[]
        {
            Peer("R", online: true, route: ConnectionRoute.Relay),
            Peer("D", online: true, route: ConnectionRoute.Direct),
            Peer("S", online: true, route: ConnectionRoute.StunAssisted),
        };
        Assert.Equal(new[] { "D", "S", "R" }, PeerNames(Build(peers, mode: PeerSortMode.Route)));
    }

    [Fact]
    public void 最終転送順ソートは新しい順()
    {
        var peers = new[]
        {
            Peer("Old", online: true, lastTransfer: new DateTime(2020, 1, 1)),
            Peer("New", online: true, lastTransfer: new DateTime(2026, 1, 1)),
            Peer("Never", online: true, lastTransfer: null),
        };
        Assert.Equal(new[] { "New", "Old", "Never" }, PeerNames(Build(peers, mode: PeerSortMode.LastTransfer)));
    }
}
