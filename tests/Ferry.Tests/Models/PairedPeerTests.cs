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

    [Theory]
    [InlineData(ConnectionRoute.Direct, "🟢 LAN 直接")]
    [InlineData(ConnectionRoute.StunAssisted, "🟡 P2P（STUN）")]
    [InlineData(ConnectionRoute.Relay, "🔴 リレー（TURN）")]
    [InlineData(ConnectionRoute.Unknown, "")]
    public void RouteTextが全enum値に対して正しい文字列を返すこと(ConnectionRoute route, string expected)
    {
        var peer = CreatePeer();
        peer.Route = route;
        Assert.Equal(expected, peer.RouteText);
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
