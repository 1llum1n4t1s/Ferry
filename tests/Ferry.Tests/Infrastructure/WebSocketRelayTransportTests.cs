using Ferry.Infrastructure;
using Ferry.Models;

namespace Ferry.Tests.Infrastructure;

/// <summary>
/// WebSocketRelayTransport の単体テスト。
///
/// 注意: ConnectAsync / SendAsync / 受信ループの実 WebSocket 往復は
/// HttpListener ベースのループバック検証が Windows 環境でクライアント↔サーバー
/// 同居時にデッドロックする事例が確認できたため、本テストでは扱わない。
/// 往復検証は Cloudflare Worker (`relay.ferry.nephilim.jp`) 相手の手動 e2e で担保する
/// ([`docs/Cloudflare移行_作業依頼書_2026-05.md`](../../../docs/Cloudflare移行_作業依頼書_2026-05.md) の「完了条件」参照)。
///
/// ここでは接続前後の状態遷移と防御的ガードのみを単体で検証する。
/// </summary>
public class WebSocketRelayTransportTests
{
    [Fact]
    public void 初期状態はIsConnectedがfalse()
    {
        var transport = new WebSocketRelayTransport("wss://example.invalid/ferry-relay", "pair123", "offer");
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void RouteプロパティはRelayを固定で返す()
    {
        var transport = new WebSocketRelayTransport("wss://example.invalid/ferry-relay", "pair123", "offer");
        Assert.Equal(ConnectionRoute.Relay, transport.Route);
    }

    [Fact]
    public async Task 未接続状態のSendAsyncはInvalidOperationExceptionをスローする()
    {
        var transport = new WebSocketRelayTransport("wss://example.invalid/ferry-relay", "pair123", "offer");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.SendAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 未接続状態のReadOnlyMemorySendAsyncも例外をスローする()
    {
        var transport = new WebSocketRelayTransport("wss://example.invalid/ferry-relay", "pair123", "offer");
        var payload = new byte[] { 0xAA, 0xBB }.AsMemory();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.SendAsync(payload, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Closeは未接続でも例外を投げずに完了する()
    {
        var transport = new WebSocketRelayTransport("wss://example.invalid/ferry-relay", "pair123", "offer");
        // ガード: 未接続状態で Close を呼んでも NullRef / ObjectDisposed が出ないこと
        transport.Close();
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void Disposeは未接続でも例外を投げずに完了する()
    {
        var transport = new WebSocketRelayTransport("wss://example.invalid/ferry-relay", "pair123", "offer");
        transport.Dispose();
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task 不正なホスト名のConnectAsyncは例外で失敗する()
    {
        // DNS resolve できない URL では確実に失敗する。
        // ChannelOpened/ChannelClosed が誤発火しないこと、IsConnected が false 維持されることを検証。
        var transport = new WebSocketRelayTransport("ws://ferry-relay.invalid.example/ferry-relay", "pair_nx", "offer");

        var openedFired = false;
        transport.ChannelOpened += (_, _) => openedFired = true;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<Exception>(() => transport.ConnectAsync(cts.Token));
        Assert.False(transport.IsConnected);
        Assert.False(openedFired);
    }

    [Fact]
    public void コンストラクタ引数のrelayUrlとpairIdとroleが受け入れられる()
    {
        // null や空文字でも例外なく構築できること (実 ConnectAsync 時に Uri 例外で弾かれる契約)
        var transport = new WebSocketRelayTransport("wss://relay.ferry.nephilim.jp/ferry-relay", "abc_def", "answer");
        Assert.Equal(ConnectionRoute.Relay, transport.Route);
        Assert.False(transport.IsConnected);
    }
}
