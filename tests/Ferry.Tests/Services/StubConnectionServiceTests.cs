using System;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;
using Ferry.Services;

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
        var sessionId = await svc.StartPairingSessionAsync();

        Assert.NotNull(sessionId);
        Assert.Equal(8, sessionId.Length); // GUID先頭8文字
    }

    [Fact]
    public async Task StartPairingSessionAsync_状態がWaitingForPairingになる()
    {
        var svc = new StubConnectionService();
        PeerState? receivedState = null;
        svc.StateChanged += (_, s) => receivedState = s;

        await svc.StartPairingSessionAsync();

        Assert.Equal(PeerState.WaitingForPairing, svc.State);
        Assert.Equal(PeerState.WaitingForPairing, receivedState);
    }

    [Fact]
    public async Task StartPairingSessionAsync_3秒後にPairingCompletedが発火する()
    {
        var svc = new StubConnectionService();
        var tcs = new TaskCompletionSource<PairedPeer>();
        svc.PairingCompleted += (_, peer) => tcs.TrySetResult(peer);

        await svc.StartPairingSessionAsync();

        // 3秒 (1.5s + 1.5s) + マージン
        var peer = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

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

        await svc.StartPairingSessionAsync();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

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
        await svc.StartPairingSessionAsync();
        await svc.CancelPairingAsync();

        Assert.Equal(PeerState.Disconnected, svc.State);
    }

    [Fact]
    public async Task CancelPairingAsync_PairingCompletedが発火しない()
    {
        var svc = new StubConnectionService();
        var fired = false;
        svc.PairingCompleted += (_, _) => fired = true;

        await svc.StartPairingSessionAsync();
        // すぐキャンセル
        await svc.CancelPairingAsync();

        // シミュレーション完了を待つ時間分待って、発火しないことを確認
        await Task.Delay(4000);
        Assert.False(fired);
    }

    // === ConnectToPeerAsync ===

    [Fact]
    public async Task ConnectToPeerAsync_即座にConnectedになる()
    {
        var svc = new StubConnectionService();
        PeerState? receivedState = null;
        svc.StateChanged += (_, s) => receivedState = s;

        await svc.ConnectToPeerAsync("test-peer");

        Assert.Equal(PeerState.Connected, svc.State);
        Assert.Equal(PeerState.Connected, receivedState);
    }

    [Fact]
    public async Task ConnectToPeerAsync_ConnectedPeerが設定される()
    {
        var svc = new StubConnectionService();
        await svc.ConnectToPeerAsync("test-peer");

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

        await svc.ConnectToPeerAsync("test-peer");

        Assert.Equal(ConnectionRoute.Direct, svc.Route);
        Assert.Equal(ConnectionRoute.Direct, receivedRoute);
    }

    // === DisconnectAsync ===

    [Fact]
    public async Task DisconnectAsync_状態がリセットされる()
    {
        var svc = new StubConnectionService();
        await svc.ConnectToPeerAsync("test-peer");
        await svc.DisconnectAsync();

        Assert.Equal(PeerState.Disconnected, svc.State);
        Assert.Null(svc.ConnectedPeer);
        Assert.Equal(ConnectionRoute.Unknown, svc.Route);
    }

    // === SendAsync ===

    [Fact]
    public async Task SendAsync_例外を投げない()
    {
        var svc = new StubConnectionService();
        var ex = await Record.ExceptionAsync(() => svc.SendAsync(new byte[] { 1, 2, 3 }));
        Assert.Null(ex);
    }
}
