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

    public TransferViewModelTests()
    {
        _connectionService = Substitute.For<IConnectionService>();
        _transferService = Substitute.For<ITransferService>();
        _tempDir = Path.Combine(Path.GetTempPath(), $"FerryTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // ConnectionViewModel のスタブ依存
        var qrCodeService = Substitute.For<IQrCodeService>();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Settings.Returns(new AppSettings { DisplayName = "TestPC", BridgePageUrl = "https://example.com" });
        var peerRegistry = Substitute.For<IPeerRegistryService>();
        peerRegistry.GetPairedPeers().Returns(new List<PairedPeer>());
        _connectionViewModel = new ConnectionViewModel(_connectionService, qrCodeService, settingsService, peerRegistry);
    }

    private TransferViewModel CreateViewModel(bool withSelectedPeer = false)
    {
        if (withSelectedPeer)
        {
            _connectionViewModel.SelectedPeer = new PairedPeer { PeerId = "test-peer", DisplayName = "TestPeer" };
        }
        return new TransferViewModel(_connectionService, _transferService, _connectionViewModel);
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
        await _transferService.DidNotReceive().SendFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        await _transferService.DidNotReceive().SendFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        _transferService.SendFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
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
        _transferService.SendFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
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
        Assert.Equal("レジュームに失敗しました", item.ErrorMessage);
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

    [Fact]
    public void ClearHistory_InProgressのアイテムは残ること()
    {
        using var vm = CreateViewModel();
        var inProgress = new TransferItem { FileName = "sending.txt", State = TransferState.InProgress };
        var completed = new TransferItem { FileName = "done.txt", State = TransferState.Completed };
        var error = new TransferItem { FileName = "err.txt", State = TransferState.Error };
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
        using var vm = CreateViewModel();
        var pending = new TransferItem { FileName = "pending.txt", State = TransferState.Pending };
        var completed = new TransferItem { FileName = "done.txt", State = TransferState.Completed };
        vm.Transfers.Add(pending);
        vm.Transfers.Add(completed);

        vm.ClearHistoryCommand.Execute(null);

        Assert.Single(vm.Transfers);
        Assert.Equal("pending.txt", vm.Transfers[0].FileName);
    }

    [Fact]
    public void ClearHistory_CompletedとErrorとCancelledとSuspendedが削除されること()
    {
        using var vm = CreateViewModel();
        vm.Transfers.Add(new TransferItem { FileName = "a.txt", State = TransferState.Completed });
        vm.Transfers.Add(new TransferItem { FileName = "b.txt", State = TransferState.Error });
        vm.Transfers.Add(new TransferItem { FileName = "c.txt", State = TransferState.Cancelled });
        vm.Transfers.Add(new TransferItem { FileName = "d.txt", State = TransferState.Suspended });

        vm.ClearHistoryCommand.Execute(null);

        Assert.Empty(vm.Transfers);
    }

    [Fact]
    public void ClearHistory_空の場合は例外が発生しないこと()
    {
        using var vm = CreateViewModel();
        vm.ClearHistoryCommand.Execute(null);

        Assert.Empty(vm.Transfers);
    }

    // === OnProgressChanged ===

    [Fact]
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

    [Fact]
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

    [Fact]
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
