using Ferry.Models;
using Ferry.ViewModels;

namespace Ferry.Tests.ViewModels;

/// <summary>
/// 宛先リストの投影ロジック <see cref="ConnectionViewModel.BuildPeerProjection"/> の純関数テスト。
/// Dispatcher 非依存なので VM インスタンス不要。検索・セクション分割・ソートは単純な LINQ 式なので、
/// 退行（11a3394）のあった「選択中ピア keep」だけを固定する。
/// </summary>
public class PeerListProjectionTests
{
    private static PairedPeer Peer(string name, bool online = false)
        => new() { PeerId = name, DisplayName = name, IsOnline = online };

    private static string[] PeerNames(System.Collections.Generic.List<object> projection)
        => projection.OfType<PairedPeer>().Select(p => p.DisplayName).ToArray();

    [Fact]
    public void 検索に一致しなくても選択中ピアkeepは常に表示され無ければ除外される()
    {
        // 退行防止: 転送中に検索ボックスへ非一致文字を打っても選択中ピアが投影から消えないこと
        // （消えると SelectedPeer=null が外部購読者に伝播して転送パネルが空になる HIGH 退行が起きる）。
        var bob = Peer("Bob", online: true);
        var peers = new[] { Peer("Alice", online: true), bob };
        var result = ConnectionViewModel.BuildPeerProjection(peers, "Ali", PeerSortMode.Name, k => k, keep: bob);
        var names = PeerNames(result);
        Assert.Contains("Bob", names);   // 検索 "Ali" に一致しないが keep なので残る
        Assert.Contains("Alice", names);

        // 対比: keep が無ければ非一致ピアは通常どおり除外される
        var withoutKeep = ConnectionViewModel.BuildPeerProjection(peers, "Ali", PeerSortMode.Name, k => k);
        Assert.Equal(new[] { "Alice" }, PeerNames(withoutKeep));
    }
}
