using System.Text.Json;
using Ferry.Models;

namespace Ferry.Tests.Models;

/// <summary>
/// PairedPeer の ObservableProperty 動作、経路バッジ、JsonIgnore を検証する。
/// </summary>
public class PairedPeerTests
{
    private static PairedPeer CreatePeer() => new()
    {
        PeerId = "test-peer-id",
        DisplayName = "テスト端末",
    };

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

        Assert.Contains(nameof(PairedPeer.RouteBadgeLabel), changed);
        Assert.Contains(nameof(PairedPeer.RouteBadgeTooltip), changed);
    }

    [Fact]
    public void Route変更時に経路バッジのPropertyChangedが発火すること()
    {
        var peer = CreatePeer();
        var changedProperties = new List<string>();
        peer.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        peer.Route = ConnectionRoute.Direct;

        Assert.Contains(nameof(PairedPeer.Route), changedProperties);
        Assert.Contains(nameof(PairedPeer.RouteBadgeLabel), changedProperties);
        Assert.Contains(nameof(PairedPeer.RouteBadgeTooltip), changedProperties);
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

        // 接続経路と状態表示、経路バッジはシリアライズされないこと
        Assert.False(root.TryGetProperty("Route", out _), "Route がシリアライズされている");
        Assert.False(root.TryGetProperty("ConnectionStatusText", out _), "ConnectionStatusText がシリアライズされている");
        Assert.False(root.TryGetProperty(nameof(PairedPeer.RouteBadgeLabel), out _), "RouteBadgeLabel がシリアライズされている");
        Assert.False(root.TryGetProperty(nameof(PairedPeer.RouteBadgeTooltip), out _), "RouteBadgeTooltip がシリアライズされている");

        // PeerId, DisplayName はシリアライズされること
        Assert.True(root.TryGetProperty("PeerId", out _));
        Assert.True(root.TryGetProperty("DisplayName", out _));
    }

}
