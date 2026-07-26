using System.Text.Json;
using Ferry.Models;

namespace Ferry.Tests.Models;

/// <summary>
/// PairedPeer の ObservableProperty 動作、RouteText 生成、JsonIgnore を検証する。
/// </summary>
public class PairedPeerTests
{
    private static PairedPeer CreatePeer() => new()
    {
        PeerId = "test-peer-id",
        DisplayName = "テスト端末",
    };

    [Fact]
    public void Routeの初期値がUnknownであること()
    {
        var peer = CreatePeer();
        Assert.Equal(ConnectionRoute.Unknown, peer.Route);
    }

    [Fact]
    public void ConnectionStatusTextの初期値が空文字列であること()
    {
        var peer = CreatePeer();
        Assert.Equal(string.Empty, peer.ConnectionStatusText);
    }

    // 経路文言はロケール辞書 (Text.Route.*) 経由になったため、期待値は「絵文字プレフィクス +
    // 経路ごとのリソースキー」で表す。Avalonia の Application が存在しないユニットテストでは
    // App.Text がキー名そのものを返す契約なので、これで「どの経路にどのキーを引いているか」
    // という本来の検証対象（マッピングの取り違え）をそのまま守れる。
    [Theory]
    [InlineData(ConnectionRoute.Direct, "🟢 Text.Route.Lan.Label")]
    [InlineData(ConnectionRoute.StunAssisted, "🟡 Text.Route.P2p.Label")]
    [InlineData(ConnectionRoute.Relay, "🔴 Text.Route.Relay.Label")]
    [InlineData(ConnectionRoute.Unknown, "")]
    public void RouteTextが全enum値に対して正しい文字列を返すこと(ConnectionRoute route, string expected)
    {
        var peer = CreatePeer();
        peer.Route = route;
        Assert.Equal(expected, peer.RouteText);
    }

    [Theory]
    [InlineData(ConnectionRoute.Direct, "Text.Route.Lan.Badge", "Text.Route.Lan.Tooltip")]
    [InlineData(ConnectionRoute.StunAssisted, "Text.Route.P2p.Badge", "Text.Route.P2p.Tooltip")]
    [InlineData(ConnectionRoute.Relay, "Text.Route.Relay.Badge", "Text.Route.Relay.Tooltip")]
    [InlineData(ConnectionRoute.Unknown, "Text.Route.Unknown.Badge", "Text.Route.Unknown.Tooltip")]
    public void 経路バッジのラベルとツールチップがロケールキー経由で解決されること(
        ConnectionRoute route, string expectedLabel, string expectedTooltip)
    {
        var peer = CreatePeer();
        peer.Route = route;
        Assert.Equal(expectedLabel, peer.RouteBadgeLabel);
        Assert.Equal(expectedTooltip, peer.RouteBadgeTooltip);
    }

    [Fact]
    public void RaiseLocalizedTextChangedで経路文言の再評価が通知されること()
    {
        var peer = CreatePeer();
        var changed = new List<string>();
        peer.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        peer.RaiseLocalizedTextChanged();

        Assert.Contains(nameof(PairedPeer.RouteText), changed);
        Assert.Contains(nameof(PairedPeer.RouteBadgeLabel), changed);
        Assert.Contains(nameof(PairedPeer.RouteBadgeTooltip), changed);
    }

    [Fact]
    public void Route変更時にRouteTextのPropertyChangedが発火すること()
    {
        var peer = CreatePeer();
        var changedProperties = new List<string>();
        peer.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        peer.Route = ConnectionRoute.Direct;

        // Route 自体の通知と RouteText の通知の両方が来ること
        Assert.Contains("Route", changedProperties);
        Assert.Contains("RouteText", changedProperties);
    }

    [Fact]
    public void ConnectionStatusText変更時にPropertyChangedが発火すること()
    {
        var peer = CreatePeer();
        var raised = false;
        peer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "ConnectionStatusText") raised = true;
        };

        peer.ConnectionStatusText = "接続中...";
        Assert.True(raised);
        Assert.Equal("接続中...", peer.ConnectionStatusText);
    }

    [Fact]
    public void RouteがJsonIgnoreされてシリアライズに含まれないこと()
    {
        var peer = CreatePeer();
        peer.Route = ConnectionRoute.Direct;
        peer.ConnectionStatusText = "接続済み";

        var json = JsonSerializer.Serialize(peer);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Route, ConnectionStatusText, RouteText はシリアライズされないこと
        Assert.False(root.TryGetProperty("Route", out _), "Route がシリアライズされている");
        Assert.False(root.TryGetProperty("ConnectionStatusText", out _), "ConnectionStatusText がシリアライズされている");
        Assert.False(root.TryGetProperty("RouteText", out _), "RouteText がシリアライズされている");

        // PeerId, DisplayName はシリアライズされること
        Assert.True(root.TryGetProperty("PeerId", out _));
        Assert.True(root.TryGetProperty("DisplayName", out _));
    }

    [Fact]
    public void PairedAtのデフォルトがUTC現在時刻付近であること()
    {
        var before = DateTime.UtcNow;
        var peer = CreatePeer();
        var after = DateTime.UtcNow;

        Assert.InRange(peer.PairedAt, before, after);
    }

    [Fact]
    public void LastTransferAtのデフォルトがnullであること()
    {
        var peer = CreatePeer();
        Assert.Null(peer.LastTransferAt);
    }
}
