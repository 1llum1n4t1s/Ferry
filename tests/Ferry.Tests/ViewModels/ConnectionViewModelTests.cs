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
public class ConnectionViewModelTests
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
        });

        // デフォルトでペアなし
        _peerRegistry.GetPairedPeers().Returns(new List<PairedPeer>());
    }

    private ConnectionViewModel CreateViewModel() =>
        // rere #B1-001: presence ファクトリはスタブ（テストは StartPresenceMonitoring を呼ばない）。
        new(_connectionService, _qrCodeService, _settingsService, _peerRegistry, Substitute.For<IPresenceServiceFactory>());

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

    /// <summary>PairedPeer.WentOnline に登録されているハンドラ数（field-like event の backing field を反射で読む）。</summary>
    private static int WentOnlineHandlerCount(PairedPeer peer)
    {
        var field = typeof(PairedPeer).GetField(nameof(PairedPeer.WentOnline),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (field!.GetValue(peer) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    [Fact]
    public void PairedPeersの追加と削除でWentOnline購読が1件ずつ張られ外れること()
    {
        var initial = new PairedPeer { PeerId = "peer1", DisplayName = "PC-1" };
        _peerRegistry.GetPairedPeers().Returns(new List<PairedPeer> { initial });

        using var vm = CreateViewModel();
        // 初期ロード分は ctor が直接購読する（二重購読すると Online エッジで経路 Probe が 2 回走る）
        Assert.Equal(1, WentOnlineHandlerCount(initial));

        var added = new PairedPeer { PeerId = "peer2", DisplayName = "PC-2" };
        vm.PairedPeers.Add(added);
        Assert.Equal(1, WentOnlineHandlerCount(added));

        vm.PairedPeers.Remove(added);
        Assert.Equal(0, WentOnlineHandlerCount(added));

        vm.PairedPeers.Remove(initial);
        Assert.Equal(0, WentOnlineHandlerCount(initial));
    }

    // === StartSessionCommand ===

    [Fact]
    public async Task StartSessionAsync_コピー用コードに短命nonceを含めること()
    {
        const string deviceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string nonce = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        _connectionService.StartPairingSessionAsync(Arg.Any<CancellationToken>()).Returns(deviceId);
        _connectionService.LastPairingNonce.Returns(nonce);
        _qrCodeService.GenerateQrBitmap(Arg.Any<string>()).Returns((Bitmap?)null);

        using var vm = CreateViewModel();
        await vm.StartSessionCommand.ExecuteAsync(null);

        Assert.Equal($"{deviceId}.{nonce}", vm.PairingCode);
    }

    [Fact]
    public async Task StartSessionAsync_同じdeviceIdで再入してもPairingCode変更を通知すること()
    {
        const string deviceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string firstNonce = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string secondNonce = "cccccccccccccccccccccccccccccccc";
        _connectionService.StartPairingSessionAsync(Arg.Any<CancellationToken>()).Returns(deviceId);
        _connectionService.LastPairingNonce.Returns(firstNonce, secondNonce);
        _qrCodeService.GenerateQrBitmap(Arg.Any<string>()).Returns((Bitmap?)null);

        using var vm = CreateViewModel();
        await vm.StartSessionCommand.ExecuteAsync(null);
        var notifications = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionViewModel.PairingCode))
                notifications++;
        };

        await vm.StartSessionCommand.ExecuteAsync(null);

        Assert.Equal($"{deviceId}.{secondNonce}", vm.PairingCode);
        Assert.True(notifications >= 1);
    }

    [Fact]
    public async Task StartSessionAsync_QRコードURLはCFBridgeを宛先にsidと端末名のパラメータを持つこと()
    {
        _connectionService.StartPairingSessionAsync(Arg.Any<CancellationToken>())
            .Returns("sid123");
        _qrCodeService.GenerateQrBitmap(Arg.Any<string>()).Returns((Bitmap?)null);

        using var vm = CreateViewModel();
        await vm.StartSessionCommand.ExecuteAsync(null);

        // CF 単独完結: QR の宛先は常に CF 版 Bridge（relay Worker の Static Assets）固定
        _qrCodeService.Received(1).GenerateQrBitmap(
            Arg.Is<string>(url => url != null
                && url.StartsWith(Ferry.AppConstants.CfBridgePageUrl)
                && url.Contains("sid=sid123")
                && url.Contains("name=TestPC")));
    }

    // === RemovePeerAsync ===

    [Fact]
    public async Task RemovePeerAsync_ピアがコレクションとレジストリから削除され複数残る間はStartSessionしないこと()
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
        await _peerRegistry.Received(1).RemovePeerAsync("peer1");
        // ペアが残っているので新規ペアリングセッションは始めない
        await _connectionService.DidNotReceive().StartPairingSessionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemovePeerAsync_選択中ピアを削除したら対象ピアだけDisconnectされること()
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
        await _connectionService.Received(1).DisconnectAsync("peer1", Arg.Any<CancellationToken>());
        await _connectionService.DidNotReceive().DisconnectAsync(Arg.Any<CancellationToken>());
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
        await _connectionService.Received(1).DisconnectAsync("peer2", Arg.Any<CancellationToken>());
        await _connectionService.DidNotReceive().DisconnectAsync(Arg.Any<CancellationToken>());
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
        Assert.Equal(string.Empty, vm.SessionId);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.Null(vm.QrCodeImage);
        // 選択中ピアが無いときは全体切断（ピア指定なし）を呼ぶ
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
        await _connectionService.Received(1).DisconnectAsync("peer1", Arg.Any<CancellationToken>());
        await _connectionService.DidNotReceive().DisconnectAsync(Arg.Any<CancellationToken>());
    }

    // === ConnectToSelectedPeerAsync（転送開始時のオンデマンド接続） ===

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

        await vm.ConnectToSelectedPeerAsync(TestContext.Current.CancellationToken);

        // ConnectToPeerAsync は呼ばれない
        await _connectionService.DidNotReceive().ConnectToPeerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectToSelectedPeerAsync_別ピア接続中でも全切断せず対象へ接続すること()
    {
        var peer = new PairedPeer { PeerId = "peer2", DisplayName = "PC-2" };
        _peerRegistry.GetPairedPeers().Returns(new List<PairedPeer> { peer });
        _connectionService.State.Returns(PeerState.Connected);
        _connectionService.ConnectedPeer.Returns(new PeerInfo { SessionId = "peer1" });
        _connectionService.ConnectToPeerAsync("peer2", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        using var vm = CreateViewModel();
        vm.ConnectionState = PeerState.Connected;
        vm.SelectedPeer = peer;
        _connectionService.ClearReceivedCalls();

        await vm.ConnectToSelectedPeerAsync(TestContext.Current.CancellationToken);

        await _connectionService.Received(1).ConnectToPeerAsync("peer2", Arg.Any<CancellationToken>());
        await _connectionService.DidNotReceive().DisconnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ピア選択時は宛先を記憶するだけで接続しないこと()
    {
        var peer = new PairedPeer { PeerId = "peer1", DisplayName = "PC-1" };
        var peers = new List<PairedPeer> { peer };
        _peerRegistry.GetPairedPeers().Returns(peers);

        using var vm = CreateViewModel();
        vm.SelectedPeer = peer;

        // 接続は行われない
        Assert.Equal(PeerState.Disconnected, vm.ConnectionState);
        await _connectionService.DidNotReceive().ConnectToPeerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectToSelectedPeerAsync_オンデマンド接続で例外発生時にステータスがクリアされること()
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
        await Assert.ThrowsAsync<Exception>(
            () => vm.ConnectToSelectedPeerAsync(TestContext.Current.CancellationToken));

        Assert.Equal(string.Empty, peer.ConnectionStatusText);
        Assert.Equal(PeerState.Disconnected, vm.ConnectionState);
    }

    [Fact]
    public void 選択解除は直前ピアの着信監視だけを停止すること()
    {
        var peer = new PairedPeer { PeerId = "peer1", DisplayName = "PC-1" };
        _peerRegistry.GetPairedPeers().Returns(new List<PairedPeer> { peer });

        using var vm = CreateViewModel();
        vm.SelectedPeer = peer;
        _connectionService.ClearReceivedCalls();

        vm.SelectedPeer = null;

        _connectionService.Received(1).StopListeningForConnection("peer1");
        _connectionService.DidNotReceive().StopListeningForConnection();
    }

}
