using System;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;
using Ferry.Services;
using Xunit;

namespace Ferry.Tests.Services;

/// <summary>
/// StubConnectionService のユニットテスト。
/// ペアリングシミュレーション・状態遷移・キャンセル動作を検証する。
/// </summary>
public sealed class StubConnectionServiceTests
{
    // === StartPairingSessionAsync ===

    [Fact]
    public async Task StartPairingSessionAsync_セッションIDを返す()
    {
        var svc = new StubConnectionService();
        var sessionId = await svc.StartPairingSessionAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(sessionId);
        Assert.Equal(8, sessionId.Length); // GUID先頭8文字
    }

    [Fact]
    public async Task StartPairingSessionAsync_状態がWaitingForPairingになる()
    {
        var svc = new StubConnectionService();
        PeerState? receivedState = null;
        svc.StateChanged += (_, s) => receivedState = s;

        await svc.StartPairingSessionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PeerState.WaitingForPairing, svc.State);
        Assert.Equal(PeerState.WaitingForPairing, receivedState);
    }

    [Fact]
    public async Task StartPairingSessionAsync_3秒後にPairingCompletedが発火する()
    {
        var svc = new StubConnectionService();
        var tcs = new TaskCompletionSource<PairedPeer>();
        svc.PairingCompleted += (_, peer) => tcs.TrySetResult(peer);

        await svc.StartPairingSessionAsync(TestContext.Current.CancellationToken);

        // 3秒 (1.5s + 1.5s) + マージン
        var peer = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.NotNull(peer);
        Assert.Equal("スタブPC", peer.DisplayName);
    }

    [Fact]
    public async Task StartPairingSessionAsync_状態遷移の順序が正しい()
    {
        var svc = new StubConnectionService();
        var states = new System.Collections.Generic.List<PeerState>();
        svc.StateChanged += (_, s) => states.Add(s);

        var tcs = new TaskCompletionSource();
        // 最後の状態遷移（Disconnected）で完了とする
        svc.PairingCompleted += (_, _) => { };
        svc.StateChanged += (_, s) =>
        {
            // WaitingForPairing → WaitingForMatch → (PairingCompleted) → Disconnected
            if (states.Count >= 3) tcs.TrySetResult();
        };

        await svc.StartPairingSessionAsync(TestContext.Current.CancellationToken);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 期待: WaitingForPairing → WaitingForMatch → Disconnected
        Assert.Equal(3, states.Count);
        Assert.Equal(PeerState.WaitingForPairing, states[0]);
        Assert.Equal(PeerState.WaitingForMatch, states[1]);
        Assert.Equal(PeerState.Disconnected, states[2]);
    }

    // === CancelPairingAsync ===

    [Fact]
    public async Task CancelPairingAsync_状態がDisconnectedに戻る()
    {
        var svc = new StubConnectionService();
        await svc.StartPairingSessionAsync(TestContext.Current.CancellationToken);
        await svc.CancelPairingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PeerState.Disconnected, svc.State);
    }

    [Fact]
    public async Task CancelPairingAsync_PairingCompletedが発火しない()
    {
        var svc = new StubConnectionService();
        var fired = false;
        svc.PairingCompleted += (_, _) => fired = true;

        await svc.StartPairingSessionAsync(TestContext.Current.CancellationToken);
        // すぐキャンセル
        await svc.CancelPairingAsync(TestContext.Current.CancellationToken);

        // シミュレーション完了を待つ時間分待って、発火しないことを確認
        await Task.Delay(4000, TestContext.Current.CancellationToken);
        Assert.False(fired);
    }

    // === ConnectToPeerAsync ===

    [Fact]
    public async Task ConnectToPeerAsync_即座にConnectedになる()
    {
        var svc = new StubConnectionService();
        PeerState? receivedState = null;
        svc.StateChanged += (_, s) => receivedState = s;

        await svc.ConnectToPeerAsync("test-peer", TestContext.Current.CancellationToken);

        Assert.Equal(PeerState.Connected, svc.State);
        Assert.Equal(PeerState.Connected, receivedState);
    }

    [Fact]
    public async Task ConnectToPeerAsync_ConnectedPeerが設定される()
    {
        var svc = new StubConnectionService();
        await svc.ConnectToPeerAsync("test-peer", TestContext.Current.CancellationToken);

        Assert.NotNull(svc.ConnectedPeer);
        Assert.Equal("test-peer", svc.ConnectedPeer.SessionId);
        Assert.Equal("スタブデバイス", svc.ConnectedPeer.DisplayName);
    }

    [Fact]
    public async Task ConnectToPeerAsync_RouteがDirectになる()
    {
        var svc = new StubConnectionService();
        ConnectionRoute? receivedRoute = null;
        svc.RouteChanged += (_, r) => receivedRoute = r;

        await svc.ConnectToPeerAsync("test-peer", TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionRoute.Direct, svc.Route);
        Assert.Equal(ConnectionRoute.Direct, receivedRoute);
    }

    // === DisconnectAsync ===

    [Fact]
    public async Task DisconnectAsync_状態がリセットされる()
    {
        var svc = new StubConnectionService();
        await svc.ConnectToPeerAsync("test-peer", TestContext.Current.CancellationToken);
        await svc.DisconnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PeerState.Disconnected, svc.State);
        Assert.Null(svc.ConnectedPeer);
        Assert.Equal(ConnectionRoute.Unknown, svc.Route);
    }

    // === SendAsync ===

    [Fact]
    public async Task SendAsync_例外を投げない()
    {
        var svc = new StubConnectionService();
        var ex = await Record.ExceptionAsync(() => svc.SendAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken));
        Assert.Null(ex);
    }

    // === Stage 2: 単数 ConnectedPeer の便宜値 / Stage 5: peerId 指定の SendAsync / DisconnectAsync 既定実装 ===

    /// <summary>Stage 2: 未接続時の <see cref="IConnectionService.ConnectedPeers"/> は『同一インスタンス』の
    /// 空辞書を返すこと（IConnectionService の interface static field キャッシュにより呼出ごと割当が起きない）。</summary>
    [Fact]
    public void ConnectedPeers_未接続時はキャッシュ済の空辞書を返すこと()
    {
        IConnectionService svc = new StubConnectionService();
        var a = svc.ConnectedPeers;
        var b = svc.ConnectedPeers;

        Assert.NotNull(a);
        Assert.Empty(a);
        // PR #12 review fix: 呼出ごとに new Dictionary しない（同一参照キャッシュ）
        Assert.Same(a, b);
    }

    /// <summary>Stage 2: 未接続時の <see cref="IConnectionService.ListeningPeerIds"/> は空であること。</summary>
    [Fact]
    public void ListeningPeerIds_未接続時は空であること()
    {
        IConnectionService svc = new StubConnectionService();
        Assert.Empty(svc.ListeningPeerIds);
    }

    /// <summary>Stage 2: <see cref="IConnectionService.RouteOf"/> の既定実装は ConnectedPeer の SessionId が
    /// 一致するときだけ <see cref="IConnectionService.Route"/> を返し、それ以外は <see cref="ConnectionRoute.Unknown"/>。</summary>
    [Fact]
    public async Task RouteOf_既定実装はConnectedPeer一致時のみRouteを返すこと()
    {
        // RouteOf は IConnectionService の interface default 実装なので interface 経由で呼ぶ
        IConnectionService svc = new StubConnectionService();
        // 未接続時はどの peerId でも Unknown
        Assert.Equal(ConnectionRoute.Unknown, svc.RouteOf("any-peer"));

        await svc.ConnectToPeerAsync("connected-peer", TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionRoute.Direct, svc.RouteOf("connected-peer"));
        Assert.Equal(ConnectionRoute.Unknown, svc.RouteOf("other-peer"));
    }

    /// <summary>Stage 5: peerId 指定の <see cref="IConnectionService.SendAsync(string, byte[], System.Threading.CancellationToken)"/>
    /// は既定実装で旧 API <c>SendAsync(byte[])</c> に委譲して例外を投げないこと（テスト/旧経路互換）。</summary>
    [Fact]
    public async Task SendAsync_peerId指定版_既定実装が旧APIへフォールバックすること()
    {
        IConnectionService svc = new StubConnectionService();
        var ex = await Record.ExceptionAsync(() => svc.SendAsync("any-peer", new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken));
        Assert.Null(ex);
    }

    [Fact]
    public async Task DisconnectAsync_peerId指定版_一致する接続だけを切断すること()
    {
        IConnectionService svc = new StubConnectionService();
        await svc.ConnectToPeerAsync("peer-X", TestContext.Current.CancellationToken);
        Assert.Equal(PeerState.Connected, svc.State);

        await svc.DisconnectAsync("peer-Y", TestContext.Current.CancellationToken);

        Assert.Equal(PeerState.Connected, svc.State);
        Assert.Equal("peer-X", svc.ConnectedPeer?.SessionId);

        await svc.DisconnectAsync("peer-X", TestContext.Current.CancellationToken);

        Assert.Equal(PeerState.Disconnected, svc.State);
        Assert.Null(svc.ConnectedPeer);
    }
}
