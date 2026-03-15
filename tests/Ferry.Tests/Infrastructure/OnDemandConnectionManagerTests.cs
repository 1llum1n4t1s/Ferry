using System;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Infrastructure;
using Ferry.Models;
using Ferry.Services;
using NSubstitute;

namespace Ferry.Tests.Infrastructure;

/// <summary>
/// OnDemandConnectionManager のユニットテスト。
/// IConnectionService / IPeerRegistryService をモックして接続管理ロジックを検証する。
/// </summary>
public sealed class OnDemandConnectionManagerTests : IDisposable
{
    private readonly IConnectionService _connectionService;
    private readonly IPeerRegistryService _peerRegistry;
    private readonly OnDemandConnectionManager _manager;

    public OnDemandConnectionManagerTests()
    {
        _connectionService = Substitute.For<IConnectionService>();
        _peerRegistry = Substitute.For<IPeerRegistryService>();
        _manager = new OnDemandConnectionManager(_connectionService, _peerRegistry);
    }

    public void Dispose() => _manager.Dispose();

    // === EnsureConnectedAsync ===

    [Fact]
    public async Task EnsureConnectedAsync_未接続時に接続を実行する()
    {
        _connectionService.State.Returns(PeerState.Disconnected);

        await _manager.EnsureConnectedAsync("peer1");

        await _connectionService.Received(1).ConnectToPeerAsync("peer1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureConnectedAsync_同じピアに接続済みならスキップ()
    {
        _connectionService.State.Returns(PeerState.Connected);
        _connectionService.ConnectedPeer.Returns(new PeerInfo { SessionId = "peer1" });

        await _manager.EnsureConnectedAsync("peer1");

        // ConnectToPeerAsync は呼ばれない
        await _connectionService.DidNotReceive().ConnectToPeerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // DisconnectAsync も呼ばれない
        await _connectionService.DidNotReceive().DisconnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureConnectedAsync_別ピアに接続済みなら切断してから新接続()
    {
        _connectionService.State.Returns(PeerState.Connected);
        _connectionService.ConnectedPeer.Returns(new PeerInfo { SessionId = "peer1" });

        await _manager.EnsureConnectedAsync("peer2");

        // 旧接続を切断
        Received.InOrder(() =>
        {
            _connectionService.DisconnectAsync(Arg.Any<CancellationToken>());
            _connectionService.ConnectToPeerAsync("peer2", Arg.Any<CancellationToken>());
        });
    }

    // === NotifyTransferStarted / NotifyTransferCompleted ===

    [Fact]
    public async Task NotifyTransferCompleted後にアイドルタイムアウトで自動切断される()
    {
        _connectionService.State.Returns(PeerState.Disconnected);
        _manager.IdleTimeoutSeconds = 1; // テスト用に1秒に短縮

        await _manager.EnsureConnectedAsync("peer1");
        _manager.NotifyTransferStarted();
        _manager.NotifyTransferCompleted();

        // アイドルタイマーが発火するまで待つ
        await Task.Delay(1500);

        await _connectionService.Received().DisconnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyTransferStarted中はアイドルタイムアウトで切断されない()
    {
        _connectionService.State.Returns(PeerState.Disconnected);
        _manager.IdleTimeoutSeconds = 1;

        await _manager.EnsureConnectedAsync("peer1");
        _manager.NotifyTransferStarted();

        // 転送中に待つ → 切断されないはず
        await Task.Delay(1500);

        // ConnectToPeerAsync の1回分のみで、DisconnectAsync は呼ばれない
        await _connectionService.DidNotReceive().DisconnectAsync(Arg.Any<CancellationToken>());
    }

    // === OnConnectionLost ===

    [Fact]
    public async Task OnConnectionLost_転送中なら再接続を試行する()
    {
        _connectionService.State.Returns(PeerState.Disconnected);
        _manager.MaxReconnectAttempts = 1;

        await _manager.EnsureConnectedAsync("peer1");
        _manager.NotifyTransferStarted();

        // 再接続成功をシミュレート
        _connectionService.State.Returns(PeerState.Connected);

        var reconnectedFired = false;
        _manager.Reconnected += (_, _) => reconnectedFired = true;

        // ConnectionLost イベントを発火
        _connectionService.ConnectionLost += Raise.Event();

        // 指数バックオフ (1秒) + マージン
        await Task.Delay(2000);

        await _connectionService.Received().ConnectToPeerAsync("peer1", Arg.Any<CancellationToken>());
        Assert.True(reconnectedFired);
    }

    [Fact]
    public async Task OnConnectionLost_非転送中なら再接続しない()
    {
        _connectionService.State.Returns(PeerState.Disconnected);

        await _manager.EnsureConnectedAsync("peer1");
        // NotifyTransferStarted を呼ばない → _isTransferring = false

        // 接続呼び出しカウンタをリセット
        _connectionService.ClearReceivedCalls();

        // ConnectionLost を発火
        _connectionService.ConnectionLost += Raise.Event();

        await Task.Delay(500);

        // 再接続は呼ばれない
        await _connectionService.DidNotReceive().ConnectToPeerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnConnectionLost_全試行失敗でReconnectFailedが発火する()
    {
        _connectionService.State.Returns(PeerState.Disconnected);
        _manager.MaxReconnectAttempts = 2;

        await _manager.EnsureConnectedAsync("peer1");
        _manager.NotifyTransferStarted();

        // 再接続は常に失敗（State が Connected にならない）
        _connectionService.State.Returns(PeerState.Disconnected);

        var failedFired = false;
        _manager.ReconnectFailed += (_, _) => failedFired = true;

        _connectionService.ConnectionLost += Raise.Event();

        // 指数バックオフ: 1s + 2s + マージン
        await Task.Delay(5000);

        Assert.True(failedFired);
    }

    [Fact]
    public async Task OnConnectionLost_再接続で例外が発生しても全試行を続ける()
    {
        _connectionService.State.Returns(PeerState.Disconnected);
        _manager.MaxReconnectAttempts = 2;

        await _manager.EnsureConnectedAsync("peer1");
        _manager.NotifyTransferStarted();

        // ConnectToPeerAsync が例外を投げる
        _connectionService.ConnectToPeerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("接続失敗")));

        var failedFired = false;
        _manager.ReconnectFailed += (_, _) => failedFired = true;

        _connectionService.ConnectionLost += Raise.Event();

        // 指数バックオフ: 1s + 2s + マージン
        await Task.Delay(5000);

        Assert.True(failedFired);
    }

    // === Dispose ===

    [Fact]
    public async Task Dispose_イベントハンドラが解除される()
    {
        // Dispose 後に ConnectionLost を発火しても再接続されない
        _connectionService.State.Returns(PeerState.Disconnected);

        await _manager.EnsureConnectedAsync("peer1");
        _manager.NotifyTransferStarted();
        _manager.Dispose();

        _connectionService.ClearReceivedCalls();

        // Dispose 後なのでイベントハンドラは解除済み
        // NSubstitute の Raise.Event は登録済みハンドラのみ呼ぶ
        // Dispose で -= しているため、呼ばれない
        _connectionService.ConnectionLost += Raise.Event();

        // 再接続は試行されない（ハンドラ解除済み）
        _connectionService.DidNotReceive().ConnectToPeerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
