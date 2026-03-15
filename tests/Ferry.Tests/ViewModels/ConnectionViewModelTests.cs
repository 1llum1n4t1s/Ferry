using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Ferry.Models;
using Ferry.Services;
using Ferry.ViewModels;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Ferry.Tests.ViewModels;

/// <summary>
/// ConnectionViewModel のコマンド、状態遷移、プロパティ変更を検証する。
/// Avalonia Dispatcher を使用するメソッド（OnStateChanged, OnRouteChanged, OnPairingCompleted, ClearQrCodeImage）は
/// 単体テスト環境では動作しないためスキップする。
/// </summary>
public class ConnectionViewModelTests : IDisposable
{
    private readonly IConnectionService _connectionService;
    private readonly IQrCodeService _qrCodeService;
    private readonly ISettingsService _settingsService;
    private readonly IPeerRegistryService _peerRegistry;

    public ConnectionViewModelTests()
    {
        _connectionService = Substitute.For<IConnectionService>();
        _qrCodeService = Substitute.For<IQrCodeService>();
        _settingsService = Substitute.For<ISettingsService>();
        _peerRegistry = Substitute.For<IPeerRegistryService>();

        // デフォルトの設定
        _settingsService.Settings.Returns(new AppSettings
        {
            DisplayName = "TestPC",
            BridgePageUrl = "https://example.com/bridge",
        });

        // デフォルトでペアなし
        _peerRegistry.GetPairedPeers().Returns(new List<PairedPeer>());
    }

    private ConnectionViewModel CreateViewModel() =>
        new(_connectionService, _qrCodeService, _settingsService, _peerRegistry);

    public void Dispose() { }

    // === コンストラクタ ===

    [Fact]
    public void コンストラクタ_保存済みピアがPairedPeersにロードされること()
    {
        var peers = new List<PairedPeer>
        {
            new() { PeerId = "peer1", DisplayName = "PC-1" },
            new() { PeerId = "peer2", DisplayName = "PC-2" },
        };
        _peerRegistry.GetPairedPeers().Returns(peers);

        using var vm = CreateViewModel();

        Assert.Equal(2, vm.PairedPeers.Count);
        Assert.Equal("peer1", vm.PairedPeers[0].PeerId);
        Assert.Equal("peer2", vm.PairedPeers[1].PeerId);
    }

    [Fact]
    public void コンストラクタ_ペアなしの場合PairedPeersが空であること()
    {
        using var vm = CreateViewModel();

        Assert.Empty(vm.PairedPeers);
    }

    [Fact]
    public void コンストラクタ_HasPairedPeersがペア数に応じて設定されること()
    {
        var peers = new List<PairedPeer>
        {
            new() { PeerId = "peer1", DisplayName = "PC-1" },
        };
        _peerRegistry.GetPairedPeers().Returns(peers);

        using var vm = CreateViewModel();

        Assert.True(vm.HasPairedPeers);
    }

    [Fact]
    public void コンストラクタ_ペアなしの場合HasPairedPeersがfalseであること()
    {
        using var vm = CreateViewModel();

        Assert.False(vm.HasPairedPeers);
    }

    [Fact]
    public void コンストラクタ_イベントハンドラが登録されること()
    {
        using var vm = CreateViewModel();

        // StateChanged, RouteChanged, PairingCompleted が購読されていることを確認
        // （Dispose で -= するため、購読されていればエラーにならない）
        _connectionService.Received(1).StateChanged += Arg.Any<EventHandler<PeerState>>();
        _connectionService.Received(1).RouteChanged += Arg.Any<EventHandler<ConnectionRoute>>();
        _connectionService.Received(1).PairingCompleted += Arg.Any<EventHandler<PairedPeer>>();
    }

    [Fact]
    public void コンストラクタ_初期状態がDisconnectedであること()
    {
        using var vm = CreateViewModel();

        Assert.Equal(PeerState.Disconnected, vm.ConnectionState);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.Equal(string.Empty, vm.SessionId);
        Assert.Equal(string.Empty, vm.PeerName);
        Assert.False(vm.IsConnecting);
        Assert.Null(vm.SelectedPeer);
    }

    // === StartSessionCommand ===

    [Fact]
    public async Task StartSessionAsync_セッションIDが設定されること()
    {
        _connectionService.StartPairingSessionAsync(Arg.Any<CancellationToken>())
            .Returns("test-session-id");
        // Bitmap を返すと Dispose で Dispatcher が必要になるため null を返す
        _qrCodeService.GenerateQrBitmap(Arg.Any<string>()).Returns((Bitmap?)null);

        using var vm = CreateViewModel();
        await vm.StartSessionCommand.ExecuteAsync(null);

        Assert.Equal("test-session-id", vm.SessionId);
    }

    [Fact]
    public async Task StartSessionAsync_QRコードURL生成に正しいパラメータが渡されること()
    {
        _connectionService.StartPairingSessionAsync(Arg.Any<CancellationToken>())
            .Returns("sid123");
        _qrCodeService.GenerateQrBitmap(Arg.Any<string>()).Returns((Bitmap?)null);

        using var vm = CreateViewModel();
        await vm.StartSessionCommand.ExecuteAsync(null);

        _qrCodeService.Received(1).GenerateQrBitmap(
            Arg.Is<string>(url => url.Contains("sid=sid123") && url.Contains("name=TestPC")));
    }

    [Fact]
    public async Task StartSessionAsync_成功時にWaitingForPairingになること()
    {
        _connectionService.StartPairingSessionAsync(Arg.Any<CancellationToken>())
            .Returns("sid123");
        _qrCodeService.GenerateQrBitmap(Arg.Any<string>()).Returns((Bitmap?)null);

        using var vm = CreateViewModel();
        await vm.StartSessionCommand.ExecuteAsync(null);

        Assert.Equal(PeerState.WaitingForPairing, vm.ConnectionState);
        Assert.Contains("QR コード", vm.StatusText);
    }

    [Fact]
    public async Task StartSessionAsync_例外発生時にErrorになること()
    {
        _connectionService.StartPairingSessionAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("テストエラー"));

        using var vm = CreateViewModel();
        await vm.StartSessionCommand.ExecuteAsync(null);

        Assert.Equal(PeerState.Error, vm.ConnectionState);
        Assert.Contains("テストエラー", vm.StatusText);
    }

    [Fact]
    public async Task StartSessionAsync_完了後にIsConnectingがfalseになること()
    {
        _connectionService.StartPairingSessionAsync(Arg.Any<CancellationToken>())
            .Returns("sid123");
        _qrCodeService.GenerateQrBitmap(Arg.Any<string>()).Returns((Bitmap?)null);

        using var vm = CreateViewModel();
        await vm.StartSessionCommand.ExecuteAsync(null);

        Assert.False(vm.IsConnecting);
    }

    [Fact]
    public async Task StartSessionAsync_例外発生時もIsConnectingがfalseになること()
    {
        _connectionService.StartPairingSessionAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("fail"));

        using var vm = CreateViewModel();
        await vm.StartSessionCommand.ExecuteAsync(null);

        Assert.False(vm.IsConnecting);
    }

    // === RemovePeerAsync ===

    [Fact]
    public async Task RemovePeerAsync_ピアがコレクションから削除されること()
    {
        var peers = new List<PairedPeer>
        {
            new() { PeerId = "peer1", DisplayName = "PC-1" },
            new() { PeerId = "peer2", DisplayName = "PC-2" },
        };
        _peerRegistry.GetPairedPeers().Returns(peers);

        using var vm = CreateViewModel();
        await vm.RemovePeerCommand.ExecuteAsync("peer1");

        Assert.Single(vm.PairedPeers);
        Assert.Equal("peer2", vm.PairedPeers[0].PeerId);
    }

    [Fact]
    public async Task RemovePeerAsync_レジストリのRemovePeerAsyncが呼ばれること()
    {
        var peers = new List<PairedPeer>
        {
            new() { PeerId = "peer1", DisplayName = "PC-1" },
        };
        _peerRegistry.GetPairedPeers().Returns(peers);

        using var vm = CreateViewModel();
        await vm.RemovePeerCommand.ExecuteAsync("peer1");

        await _peerRegistry.Received(1).RemovePeerAsync("peer1");
    }

    [Fact]
    public async Task RemovePeerAsync_存在しないpeerIdでも例外が発生しないこと()
    {
        using var vm = CreateViewModel();

        // 例外が発生しないことを確認
        await vm.RemovePeerCommand.ExecuteAsync("nonexistent");

        await _peerRegistry.Received(1).RemovePeerAsync("nonexistent");
    }

    [Fact]
    public async Task RemovePeerAsync_HasPairedPeersが更新されること()
    {
        var peers = new List<PairedPeer>
        {
            new() { PeerId = "peer1", DisplayName = "PC-1" },
        };
        _peerRegistry.GetPairedPeers().Returns(peers);

        using var vm = CreateViewModel();
        Assert.True(vm.HasPairedPeers);

        await vm.RemovePeerCommand.ExecuteAsync("peer1");

        Assert.False(vm.HasPairedPeers);
    }

    [Fact]
    public async Task RemovePeerAsync_選択中ピアを削除したらSelectedPeerがnullになりDisconnectが呼ばれること()
    {
        var peer = new PairedPeer { PeerId = "peer1", DisplayName = "PC-1" };
        var peers = new List<PairedPeer> { peer };
        _peerRegistry.GetPairedPeers().Returns(peers);
        _connectionService.State.Returns(PeerState.Disconnected);

        using var vm = CreateViewModel();
        // SelectedPeer を直接設定（OnSelectedPeerChanged で ConnectToSelectedPeerAsync が呼ばれるが、
        // State が Disconnected でない場合のみ接続を試みる）
        vm.SelectedPeer = peer;

        await vm.RemovePeerCommand.ExecuteAsync("peer1");

        Assert.Null(vm.SelectedPeer);
        await _connectionService.Received().DisconnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemovePeerAsync_選択中でないピアを削除してもSelectedPeerは変わらないこと()
    {
        var peer1 = new PairedPeer { PeerId = "peer1", DisplayName = "PC-1" };
        var peer2 = new PairedPeer { PeerId = "peer2", DisplayName = "PC-2" };
        var peers = new List<PairedPeer> { peer1, peer2 };
        _peerRegistry.GetPairedPeers().Returns(peers);
        _connectionService.State.Returns(PeerState.Disconnected);

        using var vm = CreateViewModel();
        vm.SelectedPeer = peer1;

        await vm.RemovePeerCommand.ExecuteAsync("peer2");

        Assert.Equal(peer1, vm.SelectedPeer);
    }

    [Fact]
    public async Task RemovePeerAsync_全ピア削除でStartSessionCommandが発火すること()
    {
        var peer = new PairedPeer { PeerId = "peer1", DisplayName = "PC-1" };
        var peers = new List<PairedPeer> { peer };
        _peerRegistry.GetPairedPeers().Returns(peers);
        _connectionService.StartPairingSessionAsync(Arg.Any<CancellationToken>())
            .Returns("new-session");
        _qrCodeService.GenerateQrBitmap(Arg.Any<string>()).Returns((Bitmap?)null);

        using var vm = CreateViewModel();
        await vm.RemovePeerCommand.ExecuteAsync("peer1");

        // StartSessionCommand が実行されたことを確認（StartPairingSessionAsync が呼ばれる）
        await _connectionService.Received().StartPairingSessionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemovePeerAsync_複数ピアが残っている場合StartSessionは発火しないこと()
    {
        var peers = new List<PairedPeer>
        {
            new() { PeerId = "peer1", DisplayName = "PC-1" },
            new() { PeerId = "peer2", DisplayName = "PC-2" },
        };
        _peerRegistry.GetPairedPeers().Returns(peers);

        using var vm = CreateViewModel();
        await vm.RemovePeerCommand.ExecuteAsync("peer1");

        // StartPairingSessionAsync が呼ばれないこと
        await _connectionService.DidNotReceive().StartPairingSessionAsync(Arg.Any<CancellationToken>());
    }

    // === DisconnectAsync ===

    [Fact]
    public async Task DisconnectAsync_全状態がリセットされること()
    {
        _connectionService.StartPairingSessionAsync(Arg.Any<CancellationToken>())
            .Returns("sid123");
        _qrCodeService.GenerateQrBitmap(Arg.Any<string>()).Returns((Bitmap?)null);

        using var vm = CreateViewModel();
        // まずセッションを開始して状態を変更
        await vm.StartSessionCommand.ExecuteAsync(null);
        Assert.Equal(PeerState.WaitingForPairing, vm.ConnectionState);

        // 切断
        await vm.DisconnectCommand.ExecuteAsync(null);

        Assert.Equal(PeerState.Disconnected, vm.ConnectionState);
        Assert.Equal(string.Empty, vm.PeerName);
        Assert.Equal(string.Empty, vm.SessionId);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.Null(vm.QrCodeImage);
    }

    [Fact]
    public async Task DisconnectAsync_ConnectionServiceのDisconnectAsyncが呼ばれること()
    {
        using var vm = CreateViewModel();
        await vm.DisconnectCommand.ExecuteAsync(null);

        await _connectionService.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectAsync_選択中ピアのConnectionStatusTextがクリアされること()
    {
        var peer = new PairedPeer { PeerId = "peer1", DisplayName = "PC-1" };
        var peers = new List<PairedPeer> { peer };
        _peerRegistry.GetPairedPeers().Returns(peers);
        _connectionService.State.Returns(PeerState.Disconnected);

        using var vm = CreateViewModel();
        vm.SelectedPeer = peer;
        peer.ConnectionStatusText = "接続中";

        await vm.DisconnectCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, peer.ConnectionStatusText);
    }

    // === ConnectToSelectedPeerAsync（OnSelectedPeerChanged 経由） ===

    [Fact]
    public async Task ConnectToSelectedPeerAsync_WaitingForPairing中はスキップされること()
    {
        var peer = new PairedPeer { PeerId = "peer1", DisplayName = "PC-1" };
        var peers = new List<PairedPeer> { peer };
        _peerRegistry.GetPairedPeers().Returns(peers);
        _connectionService.StartPairingSessionAsync(Arg.Any<CancellationToken>())
            .Returns("sid");
        _qrCodeService.GenerateQrBitmap(Arg.Any<string>()).Returns((Bitmap?)null);

        using var vm = CreateViewModel();
        // WaitingForPairing 状態にする
        await vm.StartSessionCommand.ExecuteAsync(null);
        Assert.Equal(PeerState.WaitingForPairing, vm.ConnectionState);

        // SelectedPeer を設定
        vm.SelectedPeer = peer;

        // ConnectToPeerAsync は呼ばれないはず
        await _connectionService.DidNotReceive().ConnectToPeerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectToSelectedPeerAsync_同一ピア接続済みの場合スキップされること()
    {
        var peer = new PairedPeer { PeerId = "peer1", DisplayName = "PC-1" };
        var peers = new List<PairedPeer> { peer };
        _peerRegistry.GetPairedPeers().Returns(peers);
        _connectionService.State.Returns(PeerState.Connected);
        _connectionService.ConnectedPeer.Returns(new PeerInfo { SessionId = "peer1" });

        using var vm = CreateViewModel();
        // Connected 状態にして同一ピア情報を設定
        vm.ConnectionState = PeerState.Connected;

        vm.SelectedPeer = peer;

        // 少し待つ（fire-and-forget のため）
        await Task.Delay(100);

        // ConnectToPeerAsync は呼ばれない
        await _connectionService.DidNotReceive().ConnectToPeerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ピア選択時は宛先を記憶するだけで接続しないこと()
    {
        var peer = new PairedPeer { PeerId = "peer1", DisplayName = "PC-1" };
        var peers = new List<PairedPeer> { peer };
        _peerRegistry.GetPairedPeers().Returns(peers);

        using var vm = CreateViewModel();
        vm.SelectedPeer = peer;

        // 接続は行われない
        Assert.Equal("PC-1", vm.PeerName);
        Assert.Equal(PeerState.Disconnected, vm.ConnectionState);
    }

    [Fact]
    public async Task ConnectToSelectedPeerAsync_オンデマンド接続で例外発生時にオフライン表示になること()
    {
        var peer = new PairedPeer { PeerId = "peer1", DisplayName = "PC-1" };
        var peers = new List<PairedPeer> { peer };
        _peerRegistry.GetPairedPeers().Returns(peers);
        _connectionService.State.Returns(PeerState.Disconnected);
        _connectionService.ConnectToPeerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("接続失敗"));

        using var vm = CreateViewModel();
        vm.SelectedPeer = peer;

        // オンデマンド接続を明示的に呼び出す
        await Assert.ThrowsAsync<Exception>(() => vm.ConnectToSelectedPeerAsync());

        Assert.Equal("オフライン", peer.ConnectionStatusText);
        Assert.Equal(PeerState.Disconnected, vm.ConnectionState);
        Assert.False(vm.IsConnecting);
    }

    [Fact]
    public void SelectedPeerにnullを設定しても接続は試みられないこと()
    {
        using var vm = CreateViewModel();
        vm.SelectedPeer = null;

        _connectionService.DidNotReceive().ConnectToPeerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // === Dispatcher が必要なメソッドのスキップ ===

    [Fact(Skip = "Avalonia Dispatcher が必要")]
    public void OnStateChanged_UIスレッドで状態が更新されること() { }

    [Fact(Skip = "Avalonia Dispatcher が必要")]
    public void OnRouteChanged_UIスレッドで経路テキストが更新されること() { }

    [Fact(Skip = "Avalonia Dispatcher が必要")]
    public void OnPairingCompleted_UIスレッドでピアが追加されること() { }

    [Fact(Skip = "Avalonia Dispatcher が必要 - ClearQrCodeImage 内で Dispatcher.UIThread.Post を使用")]
    public void Dispose_イベントハンドラが解除されること() { }

    // === AddNewPeerCommand ===

    [Fact]
    public async Task AddNewPeerAsync_StartSessionAsyncが呼ばれること()
    {
        _connectionService.StartPairingSessionAsync(Arg.Any<CancellationToken>())
            .Returns("new-sid");
        _qrCodeService.GenerateQrBitmap(Arg.Any<string>()).Returns((Bitmap?)null);

        using var vm = CreateViewModel();
        await vm.AddNewPeerCommand.ExecuteAsync(null);

        await _connectionService.Received().StartPairingSessionAsync(Arg.Any<CancellationToken>());
    }
}
