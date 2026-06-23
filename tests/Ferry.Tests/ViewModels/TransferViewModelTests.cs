using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;
using Ferry.Services;
using Ferry.ViewModels;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Ferry.Tests.ViewModels;

/// <summary>
/// TransferViewModel のファイル送信、レジューム、進捗更新、イベントハンドリングを検証する。
/// </summary>
public class TransferViewModelTests : IDisposable
{
    private readonly IConnectionService _connectionService;
    private readonly ITransferService _transferService;
    private readonly ConnectionViewModel _connectionViewModel;
    private readonly string _tempDir;

    private readonly ISettingsService _settingsService;

    public TransferViewModelTests()
    {
        _connectionService = Substitute.For<IConnectionService>();
        _transferService = Substitute.For<ITransferService>();
        _tempDir = Path.Combine(Path.GetTempPath(), $"FerryTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // ConnectionViewModel のスタブ依存
        var qrCodeService = Substitute.For<IQrCodeService>();
        _settingsService = Substitute.For<ISettingsService>();
        _settingsService.Settings.Returns(new AppSettings { DisplayName = "TestPC" });
        var peerRegistry = Substitute.For<IPeerRegistryService>();
        peerRegistry.GetPairedPeers().Returns(new List<PairedPeer>());
        // rere #B1-001: presence ファクトリはスタブ。テストは StartPresenceMonitoring を呼ばないので Create は走らない。
        var presenceFactory = Substitute.For<IPresenceServiceFactory>();
        _connectionViewModel = new ConnectionViewModel(_connectionService, qrCodeService, _settingsService, peerRegistry, presenceFactory);
    }

    private TransferViewModel CreateViewModel(bool withSelectedPeer = false)
    {
        if (withSelectedPeer)
        {
            _connectionViewModel.SelectedPeer = new PairedPeer { PeerId = "test-peer", DisplayName = "TestPeer" };
            var peerInfo = new PeerInfo { SessionId = "test-peer", DisplayName = "TestPeer", State = PeerState.Connected };
            // v1.0.47: EnsureConnectedAsync が ConnectedPeer.SessionId == peer.PeerId を要求するため、
            // 接続済みシナリオでは ConnectedPeer も同じ SessionId を返すよう mock を整える。
            _connectionService.ConnectedPeer.Returns(peerInfo);
            // 複数ペア対応 Stage 5: EnsureConnectedToPeerAsync は ConnectedPeers 集合で接続済みを判定する。
            _connectionService.ConnectedPeers.Returns(new Dictionary<string, PeerInfo> { ["test-peer"] = peerInfo });
        }
        // v1.0.38: TransferViewModel が ISettingsService に依存するようになった (AutoAccept チェック用)
        return new TransferViewModel(_connectionService, _transferService, _connectionViewModel, _settingsService);
    }

    /// <summary>v1.0.38 review nitpick: AutoAccept=true で UI を経由せず即承認、PendingApprovals に積まれないこと。</summary>
    [Fact(Skip = "UI スレッド (Dispatcher.UIThread) を必要とするためテスト環境では Skip。実機で検証")]
    public void OnApprovalRequested_AutoAccept有効時はPendingApprovalsに積まれず即ApproveTransferが呼ばれること()
    {
        _settingsService.Settings.Returns(new AppSettings { AutoAcceptFileTransfer = true, DisplayName = "TestPC" });
        var vm = CreateViewModel(withSelectedPeer: true);
        var item = new TransferItem { TransferId = Guid.NewGuid(), FileName = "a.txt", FileSize = 100 };

        _transferService.ApprovalRequested += Raise.Event<EventHandler<TransferItem>>(_transferService, item);

        Assert.Empty(vm.PendingApprovals);
        _transferService.Received(1).ApproveTransfer(item.TransferId.ToString());
    }

    /// <summary>v1.0.38 review nitpick: AutoAccept=false で従来通り PendingApprovals に積まれること。</summary>
    [Fact(Skip = "UI スレッド (Dispatcher.UIThread) を必要とするためテスト環境では Skip。実機で検証")]
    public void OnApprovalRequested_AutoAccept無効時はPendingApprovalsに積まれApproveTransferは呼ばれないこと()
    {
        _settingsService.Settings.Returns(new AppSettings { AutoAcceptFileTransfer = false, DisplayName = "TestPC" });
        var vm = CreateViewModel(withSelectedPeer: true);
        var item = new TransferItem { TransferId = Guid.NewGuid(), FileName = "b.txt", FileSize = 200 };

        _transferService.ApprovalRequested += Raise.Event<EventHandler<TransferItem>>(_transferService, item);

        Assert.Single(vm.PendingApprovals);
        _transferService.DidNotReceive().ApproveTransfer(Arg.Any<string>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    /// <summary>一時ファイルを作成して絶対パスを返す。</summary>
    private string CreateTempFile(string name = "test.txt", int sizeBytes = 100)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    // === SendFilesAsync ===

    [Fact]
    public async Task SendFilesAsync_未接続時は何もしないこと()
    {
        _connectionService.State.Returns(PeerState.Disconnected);
        var filePath = CreateTempFile();

        using var vm = CreateViewModel();
        await vm.SendFilesCommand.ExecuteAsync(new[] { filePath });

        Assert.Empty(vm.Transfers);
        await _transferService.DidNotReceive().SendFileAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendFilesAsync_空配列の場合は何もしないこと()
    {
        _connectionService.State.Returns(PeerState.Connected);

        using var vm = CreateViewModel(withSelectedPeer: true);
        await vm.SendFilesCommand.ExecuteAsync(Array.Empty<string>());

        Assert.Empty(vm.Transfers);
    }

    [Fact]
    public async Task SendFilesAsync_存在しないファイルパスはスキップされること()
    {
        _connectionService.State.Returns(PeerState.Connected);

        using var vm = CreateViewModel(withSelectedPeer: true);
        await vm.SendFilesCommand.ExecuteAsync(new[] { @"C:\nonexistent\file.txt" });

        Assert.Empty(vm.Transfers);
        await _transferService.DidNotReceive().SendFileAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendFilesAsync_正常送信でTransferItemがCompletedになること()
    {
        _connectionService.State.Returns(PeerState.Connected);
        var filePath = CreateTempFile("send.txt", 200);

        using var vm = CreateViewModel(withSelectedPeer: true);
        await vm.SendFilesCommand.ExecuteAsync(new[] { filePath });

        Assert.Single(vm.Transfers);
        var item = vm.Transfers[0];
        Assert.Equal("send.txt", item.FileName);
        Assert.Equal(200, item.FileSize);
        Assert.Equal(TransferDirection.Send, item.Direction);
        Assert.Equal(TransferState.Completed, item.State);
        Assert.Equal(200, item.TransferredBytes);
    }

    [Fact]
    public async Task SendFilesAsync_例外発生時にTransferItemがErrorになること()
    {
        _connectionService.State.Returns(PeerState.Connected);
        var filePath = CreateTempFile("error.txt");
        _transferService.SendFileAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("ディスクエラー"));

        using var vm = CreateViewModel(withSelectedPeer: true);
        await vm.SendFilesCommand.ExecuteAsync(new[] { filePath });

        Assert.Single(vm.Transfers);
        var item = vm.Transfers[0];
        Assert.Equal(TransferState.Error, item.State);
        Assert.Equal("ディスクエラー", item.ErrorMessage);
    }

    [Fact]
    public async Task SendFilesAsync_複数ファイルが順番に送信されること()
    {
        _connectionService.State.Returns(PeerState.Connected);
        var file1 = CreateTempFile("a.txt", 100);
        var file2 = CreateTempFile("b.txt", 200);

        using var vm = CreateViewModel(withSelectedPeer: true);
        await vm.SendFilesCommand.ExecuteAsync(new[] { file1, file2 });

        Assert.Equal(2, vm.Transfers.Count);
        Assert.Equal("a.txt", vm.Transfers[0].FileName);
        Assert.Equal("b.txt", vm.Transfers[1].FileName);
        Assert.All(vm.Transfers, t => Assert.Equal(TransferState.Completed, t.State));
    }

    [Fact]
    public async Task SendFilesAsync_存在しないファイルと存在するファイルが混在する場合は存在するもののみ送信すること()
    {
        _connectionService.State.Returns(PeerState.Connected);
        var validFile = CreateTempFile("valid.txt", 50);

        using var vm = CreateViewModel(withSelectedPeer: true);
        await vm.SendFilesCommand.ExecuteAsync(new[] { @"C:\nonexistent.txt", validFile });

        Assert.Single(vm.Transfers);
        Assert.Equal("valid.txt", vm.Transfers[0].FileName);
    }

    [Fact]
    public async Task SendFilesAsync_全送信完了後にIsTransferringがfalseになること()
    {
        _connectionService.State.Returns(PeerState.Connected);
        var filePath = CreateTempFile();

        using var vm = CreateViewModel(withSelectedPeer: true);
        await vm.SendFilesCommand.ExecuteAsync(new[] { filePath });

        Assert.False(vm.IsTransferring);
    }

    [Fact]
    public async Task SendFilesAsync_一部エラーでもIsTransferringは最終的にfalseになること()
    {
        _connectionService.State.Returns(PeerState.Connected);
        var filePath = CreateTempFile();
        _transferService.SendFileAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("err"));

        using var vm = CreateViewModel(withSelectedPeer: true);
        await vm.SendFilesCommand.ExecuteAsync(new[] { filePath });

        // Error 状態なので InProgress は存在しない → IsTransferring = false
        Assert.False(vm.IsTransferring);
    }

    // === ResumeTransferAsync ===

    [Fact]
    public async Task ResumeTransferAsync_Suspended状態のアイテムのみ対象であること()
    {
        _transferService.ResumeTransferAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        using var vm = CreateViewModel();
        var suspendedItem = new TransferItem
        {
            FileName = "resume.txt",
            FileSize = 1000,
            State = TransferState.Suspended,
        };
        vm.Transfers.Add(suspendedItem);

        await vm.ResumeTransferCommand.ExecuteAsync(suspendedItem.TransferId);

        Assert.Equal(TransferState.Completed, suspendedItem.State);
        Assert.Equal(1000, suspendedItem.TransferredBytes);
    }

    [Fact]
    public async Task ResumeTransferAsync_Suspended以外の状態のアイテムは無視されること()
    {
        using var vm = CreateViewModel();
        var errorItem = new TransferItem
        {
            FileName = "err.txt",
            State = TransferState.Error,
        };
        vm.Transfers.Add(errorItem);

        await vm.ResumeTransferCommand.ExecuteAsync(errorItem.TransferId);

        // 状態は変わらない
        Assert.Equal(TransferState.Error, errorItem.State);
        await _transferService.DidNotReceive().ResumeTransferAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeTransferAsync_存在しないTransferIdの場合は何もしないこと()
    {
        using var vm = CreateViewModel();

        await vm.ResumeTransferCommand.ExecuteAsync(Guid.NewGuid());

        await _transferService.DidNotReceive().ResumeTransferAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeTransferAsync_失敗時にErrorになること()
    {
        _transferService.ResumeTransferAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);

        using var vm = CreateViewModel();
        var item = new TransferItem { FileName = "fail.txt", State = TransferState.Suspended };
        vm.Transfers.Add(item);

        await vm.ResumeTransferCommand.ExecuteAsync(item.TransferId);

        Assert.Equal(TransferState.Error, item.State);
        // ローカライズ後: テスト環境では App.Text() がキーを返す場合がある
        Assert.True(
            item.ErrorMessage == "レジュームに失敗しました" || item.ErrorMessage == "Text.Transfer.ResumeFailed",
            $"ErrorMessage should be resume failed text, but was: {item.ErrorMessage}");
    }

    [Fact]
    public async Task ResumeTransferAsync_例外発生時にErrorになること()
    {
        _transferService.ResumeTransferAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("ネットワークエラー"));

        using var vm = CreateViewModel();
        var item = new TransferItem { FileName = "exc.txt", State = TransferState.Suspended };
        vm.Transfers.Add(item);

        await vm.ResumeTransferCommand.ExecuteAsync(item.TransferId);

        Assert.Equal(TransferState.Error, item.State);
        Assert.Equal("ネットワークエラー", item.ErrorMessage);
    }

    [Fact]
    public async Task ResumeTransferAsync_完了後にIsTransferringが更新されること()
    {
        _transferService.ResumeTransferAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        using var vm = CreateViewModel();
        var item = new TransferItem { FileName = "done.txt", FileSize = 500, State = TransferState.Suspended };
        vm.Transfers.Add(item);

        await vm.ResumeTransferCommand.ExecuteAsync(item.TransferId);

        Assert.False(vm.IsTransferring);
    }

    // === ClearHistory ===

    // v1.0.47: ClearHistory は宛先（選択中ピア）の終端アイテムのみ削除する。
    // テストでは withSelectedPeer:true で "test-peer" を選び、各アイテムに同じ PeerId を付与して検証する。
    private const string TestPeerId = "test-peer";

    [Fact]
    public void ClearHistory_InProgressのアイテムは残ること()
    {
        using var vm = CreateViewModel(withSelectedPeer: true);
        var inProgress = new TransferItem { FileName = "sending.txt", State = TransferState.InProgress, PeerId = TestPeerId };
        var completed = new TransferItem { FileName = "done.txt", State = TransferState.Completed, PeerId = TestPeerId };
        var error = new TransferItem { FileName = "err.txt", State = TransferState.Error, PeerId = TestPeerId };
        vm.Transfers.Add(inProgress);
        vm.Transfers.Add(completed);
        vm.Transfers.Add(error);

        vm.ClearHistoryCommand.Execute(null);

        Assert.Single(vm.Transfers);
        Assert.Equal("sending.txt", vm.Transfers[0].FileName);
        Assert.Equal(TransferState.InProgress, vm.Transfers[0].State);
    }

    [Fact]
    public void ClearHistory_Pendingのアイテムも残ること()
    {
        using var vm = CreateViewModel(withSelectedPeer: true);
        var pending = new TransferItem { FileName = "pending.txt", State = TransferState.Pending, PeerId = TestPeerId };
        var completed = new TransferItem { FileName = "done.txt", State = TransferState.Completed, PeerId = TestPeerId };
        vm.Transfers.Add(pending);
        vm.Transfers.Add(completed);

        vm.ClearHistoryCommand.Execute(null);

        Assert.Single(vm.Transfers);
        Assert.Equal("pending.txt", vm.Transfers[0].FileName);
    }

    [Fact]
    public void ClearHistory_CompletedとErrorとCancelledとSuspendedが削除されること()
    {
        using var vm = CreateViewModel(withSelectedPeer: true);
        vm.Transfers.Add(new TransferItem { FileName = "a.txt", State = TransferState.Completed, PeerId = TestPeerId });
        vm.Transfers.Add(new TransferItem { FileName = "b.txt", State = TransferState.Error, PeerId = TestPeerId });
        vm.Transfers.Add(new TransferItem { FileName = "c.txt", State = TransferState.Cancelled, PeerId = TestPeerId });
        vm.Transfers.Add(new TransferItem { FileName = "d.txt", State = TransferState.Suspended, PeerId = TestPeerId });

        vm.ClearHistoryCommand.Execute(null);

        Assert.Empty(vm.Transfers);
    }

    [Fact]
    public void ClearHistory_別ピアのアイテムは残ること()
    {
        using var vm = CreateViewModel(withSelectedPeer: true);
        vm.Transfers.Add(new TransferItem { FileName = "mine.txt", State = TransferState.Completed, PeerId = TestPeerId });
        vm.Transfers.Add(new TransferItem { FileName = "other.txt", State = TransferState.Completed, PeerId = "other-peer" });

        vm.ClearHistoryCommand.Execute(null);

        Assert.Single(vm.Transfers);
        Assert.Equal("other.txt", vm.Transfers[0].FileName);
    }

    [Fact]
    public void ClearHistory_空の場合は例外が発生しないこと()
    {
        using var vm = CreateViewModel(withSelectedPeer: true);
        vm.ClearHistoryCommand.Execute(null);

        Assert.Empty(vm.Transfers);
    }

    // === Stage 6: per-peer 集計 (PairedPeer.IsTransferring / ActiveTransferCount) ===

    /// <summary>Stage 6: 進行中転送が peer A に 2 件、peer B に 1 件、peer C に 0 件のとき、
    /// 各 PairedPeer の IsTransferring / ActiveTransferCount が正しく集計されること。
    /// 集計トリガは ResumeTransferAsync 経由（Suspended→Completed で RecomputeIsTransferring が走る）で起こす。</summary>
    [Fact]
    public async Task RecomputePerPeerTransferCounts_進行中件数がPeerIdで集計されること()
    {
        // PairedPeer A/B/C を ConnectionViewModel.PairedPeers に登録
        var peerA = new PairedPeer { PeerId = "peer-A", DisplayName = "PeerA" };
        var peerB = new PairedPeer { PeerId = "peer-B", DisplayName = "PeerB" };
        var peerC = new PairedPeer { PeerId = "peer-C", DisplayName = "PeerC" };
        _connectionViewModel.PairedPeers.Add(peerA);
        _connectionViewModel.PairedPeers.Add(peerB);
        _connectionViewModel.PairedPeers.Add(peerC);

        // ResumeTransferAsync が走ると最後に RecomputeIsTransferring 経由で集計が走る。
        // テスト対象の peer/state 構成を事前に Transfers に並べておく。
        _transferService.ResumeTransferAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        using var vm = CreateViewModel();
        vm.Transfers.Add(new TransferItem { FileName = "a1.txt", PeerId = "peer-A", State = TransferState.InProgress });
        vm.Transfers.Add(new TransferItem { FileName = "a2.txt", PeerId = "peer-A", State = TransferState.InProgress });
        vm.Transfers.Add(new TransferItem { FileName = "b1.txt", PeerId = "peer-B", State = TransferState.InProgress });
        // peer-C には進行中なし
        vm.Transfers.Add(new TransferItem { FileName = "a-done.txt", PeerId = "peer-A", State = TransferState.Completed });

        // ResumeTransferAsync 経由で RecomputeIsTransferring → RecomputePerPeerTransferCounts を発火させる。
        // Suspended な dummy item を Resume 完了 (Completed) させると、最後の RecomputeIsTransferring で集計が走る。
        var trigger = new TransferItem { FileName = "trigger.txt", PeerId = "peer-A", FileSize = 1, State = TransferState.Suspended };
        vm.Transfers.Add(trigger);
        await vm.ResumeTransferCommand.ExecuteAsync(trigger.TransferId);

        // 集計結果を検証。trigger は最終的に Completed なので peer-A は依然 InProgress 2 件のまま。
        Assert.Equal(2, peerA.ActiveTransferCount);
        Assert.True(peerA.IsTransferring);
        Assert.Equal(1, peerB.ActiveTransferCount);
        Assert.True(peerB.IsTransferring);
        Assert.Equal(0, peerC.ActiveTransferCount);
        Assert.False(peerC.IsTransferring);
    }

    /// <summary>Stage 6: 全ての進行中転送が完了したら全 peer の IsTransferring が false に戻ること。</summary>
    [Fact]
    public async Task RecomputePerPeerTransferCounts_全完了で全PeerのIsTransferringがfalseに戻ること()
    {
        var peerA = new PairedPeer { PeerId = "peer-A", DisplayName = "PeerA", IsTransferring = true, ActiveTransferCount = 3 };
        var peerB = new PairedPeer { PeerId = "peer-B", DisplayName = "PeerB", IsTransferring = true, ActiveTransferCount = 1 };
        _connectionViewModel.PairedPeers.Add(peerA);
        _connectionViewModel.PairedPeers.Add(peerB);

        _transferService.ResumeTransferAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        using var vm = CreateViewModel();
        // 全て Completed/Error/Cancelled — InProgress 0 件で集計
        vm.Transfers.Add(new TransferItem { FileName = "a1.txt", PeerId = "peer-A", State = TransferState.Completed });
        vm.Transfers.Add(new TransferItem { FileName = "b1.txt", PeerId = "peer-B", State = TransferState.Error });

        // Resume 経由で集計を発火
        var trigger = new TransferItem { FileName = "t.txt", PeerId = "peer-A", FileSize = 1, State = TransferState.Suspended };
        vm.Transfers.Add(trigger);
        await vm.ResumeTransferCommand.ExecuteAsync(trigger.TransferId);

        Assert.Equal(0, peerA.ActiveTransferCount);
        Assert.False(peerA.IsTransferring);
        Assert.Equal(0, peerB.ActiveTransferCount);
        Assert.False(peerB.IsTransferring);
    }

    // === OnProgressChanged ===

    [Fact(Skip = "Avalonia Dispatcher が必要")]
    public void OnProgressChanged_TransferIdで照合して更新されること()
    {
        using var vm = CreateViewModel();
        var item = new TransferItem
        {
            FileName = "progress.txt",
            FileSize = 1000,
            State = TransferState.InProgress,
            TransferredBytes = 0,
        };
        vm.Transfers.Add(item);

        // イベントを発火
        var progressItem = new TransferItem
        {
            TransferredBytes = 500,
        };
        // TransferId を合わせる
        typeof(TransferItem).GetProperty(nameof(TransferItem.TransferId))!
            .SetValue(progressItem, item.TransferId);

        _transferService.ProgressChanged += Raise.Event<EventHandler<TransferItem>>(null, progressItem);

        Assert.Equal(500, item.TransferredBytes);
    }

    [Fact]
    public void OnProgressChanged_TransferIdが一致しない場合は更新されないこと()
    {
        using var vm = CreateViewModel();
        var item = new TransferItem
        {
            FileName = "no-match.txt",
            FileSize = 1000,
            State = TransferState.InProgress,
            TransferredBytes = 0,
        };
        vm.Transfers.Add(item);

        var progressItem = new TransferItem
        {
            TransferredBytes = 500,
        };
        // 異なる TransferId（デフォルトで新しい GUID が生成される）

        _transferService.ProgressChanged += Raise.Event<EventHandler<TransferItem>>(null, progressItem);

        Assert.Equal(0, item.TransferredBytes);
    }

    [Fact]
    public void OnProgressChanged_InProgress以外の状態では更新されないこと()
    {
        using var vm = CreateViewModel();
        var item = new TransferItem
        {
            FileName = "completed.txt",
            FileSize = 1000,
            State = TransferState.Completed,
            TransferredBytes = 1000,
        };
        vm.Transfers.Add(item);

        var progressItem = new TransferItem
        {
            TransferredBytes = 500,
        };
        typeof(TransferItem).GetProperty(nameof(TransferItem.TransferId))!
            .SetValue(progressItem, item.TransferId);

        _transferService.ProgressChanged += Raise.Event<EventHandler<TransferItem>>(null, progressItem);

        // Completed なので更新されない
        Assert.Equal(1000, item.TransferredBytes);
    }

    // === OnFileReceived ===

    [Fact(Skip = "Avalonia Dispatcher が必要")]
    public void OnFileReceived_コレクションに追加されること()
    {
        using var vm = CreateViewModel();
        var receivedItem = new TransferItem
        {
            FileName = "received.txt",
            FileSize = 2000,
            Direction = TransferDirection.Receive,
            State = TransferState.Completed,
        };

        _transferService.FileReceived += Raise.Event<EventHandler<TransferItem>>(null, receivedItem);

        Assert.Single(vm.Transfers);
        Assert.Equal("received.txt", vm.Transfers[0].FileName);
        Assert.Equal(TransferDirection.Receive, vm.Transfers[0].Direction);
    }

    // === OnTransferError ===

    [Fact(Skip = "Avalonia Dispatcher が必要")]
    public void OnTransferError_該当アイテムのステータスが更新されること()
    {
        using var vm = CreateViewModel();
        var item = new TransferItem
        {
            FileName = "error.txt",
            State = TransferState.InProgress,
        };
        vm.Transfers.Add(item);

        var errorItem = new TransferItem
        {
            ErrorMessage = "転送中断",
        };
        typeof(TransferItem).GetProperty(nameof(TransferItem.TransferId))!
            .SetValue(errorItem, item.TransferId);

        _transferService.TransferError += Raise.Event<EventHandler<TransferItem>>(null, errorItem);

        Assert.Equal(TransferState.Error, item.State);
        Assert.Equal("転送中断", item.ErrorMessage);
    }

    [Fact]
    public void OnTransferError_TransferIdが一致しない場合は更新されないこと()
    {
        using var vm = CreateViewModel();
        var item = new TransferItem
        {
            FileName = "safe.txt",
            State = TransferState.InProgress,
        };
        vm.Transfers.Add(item);

        var errorItem = new TransferItem
        {
            ErrorMessage = "エラー",
        };

        _transferService.TransferError += Raise.Event<EventHandler<TransferItem>>(null, errorItem);

        Assert.Equal(TransferState.InProgress, item.State);
        Assert.Null(item.ErrorMessage);
    }

    // === Dispose ===

    [Fact]
    public void Dispose_イベントハンドラが解除されること()
    {
        var vm = CreateViewModel();
        vm.Dispose();

        _transferService.Received(1).ProgressChanged -= Arg.Any<EventHandler<TransferItem>>();
        _transferService.Received(1).FileReceived -= Arg.Any<EventHandler<TransferItem>>();
        _transferService.Received(1).TransferError -= Arg.Any<EventHandler<TransferItem>>();
    }

    [Fact]
    public void Dispose_二重呼び出しでも例外が発生しないこと()
    {
        var vm = CreateViewModel();
        vm.Dispose();
        vm.Dispose(); // 2回目でも例外なし
    }
}
