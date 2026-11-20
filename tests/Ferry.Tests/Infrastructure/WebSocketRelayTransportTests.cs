using Ferry.Infrastructure;
using Ferry.Models;

namespace Ferry.Tests.Infrastructure;

/// <summary>
/// WebSocketRelayTransport の単体テスト。
///
/// 注意: ConnectAsync / SendAsync / 受信ループの実 WebSocket 往復は
/// HttpListener ベースのループバック検証が Windows 環境でクライアント↔サーバー
/// 同居時にデッドロックする事例が確認できたため、本テストでは扱わない。
/// 往復検証は実 relay Worker (`watashiba.kagayoi.com`) 相手の実機確認で担保する
/// (運用と障害切り分けは [`docs/operations/runbook.md`](../../../docs/operations/runbook.md) 参照)。
///
/// ここでは Bearer 取得失敗時に WebSocket 接続前で失敗する防御的ガードと、受信バッファ容量を単体で検証する。
/// </summary>
public class WebSocketRelayTransportTests
{
    [Fact]
    public async Task Bearer取得デリゲート未設定ならWebSocket接続前に失敗する()
    {
        var transport = new WebSocketRelayTransport(
            "ws://ferry-relay.invalid.example/ferry-relay", "pair_nx", "offer");
        var openedFired = false;
        transport.ChannelOpened += (_, _) => openedFired = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Contains("cfToken", ex.Message);
        Assert.False(transport.IsConnected);
        Assert.False(openedFired);

        // 未接続のままでも Dispose は例外を投げない
        transport.Dispose();
    }

    [Theory]
    [InlineData("   ")]
    public async Task Bearerが空ならWebSocket接続前に失敗する(string token)
    {
        var transport = new WebSocketRelayTransport(
            "ws://ferry-relay.invalid.example/ferry-relay", "pair_nx", "offer",
            bearerTokenAsync: () => Task.FromResult(token));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Contains("cfToken", ex.Message);
        Assert.False(transport.IsConnected);
        transport.Dispose();
    }

    [Fact]
    public async Task Bearer取得例外ならWebSocket接続前に失敗する()
    {
        var transport = new WebSocketRelayTransport(
            "ws://ferry-relay.invalid.example/ferry-relay", "pair_nx", "offer",
            bearerTokenAsync: () => throw new HttpRequestException("auth failed"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Contains("cfToken", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.False(transport.IsConnected);
        transport.Dispose();
    }

    [Fact]
    public void 受信フレームバッファは暗号封筒込みのチャンクメッセージ1個を単一フレームで収容できる()
    {
        // リレー受信のホットパス (単一フレーム完結) が実際に通ることを固定する。
        // バッファがチャンクメッセージより小さいと EndOfMessage=false になり、全チャンクが
        // MemoryStream 集約パスへ落ちて 1GB あたり数万回の追加確保とコピーが発生する
        // (旧実装の 64KB 固定バッファがこの状態だった)。ChunkSize 変更時の回帰も検出する。
        var chunkMessageSize = TransferProtocol.ChunkHeaderSize + TransferProtocol.ChunkSize;
        var encryptedSize = chunkMessageSize + PairCrypto.EnvelopeOverhead;

        Assert.True(
            WebSocketRelayTransport.ReceiveFrameBufferSize >= encryptedSize,
            $"受信バッファ {WebSocketRelayTransport.ReceiveFrameBufferSize} は暗号化チャンク {encryptedSize} 以上である必要がある");
    }
}
