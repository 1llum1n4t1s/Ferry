using Ferry.Infrastructure;
using Ferry.Models;
using Ferry.Services;
using NSubstitute;

namespace Ferry.Tests.Services;

public class TransferServiceValidationTests
{
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 10, true)]
    [InlineData(10, 10, true)]
    [InlineData(-1, 10, false)]
    [InlineData(11, 10, false)]
    public void FlowAck数は転送チャンク範囲内だけを受理する(int ackedChunks, int totalChunks, bool expected)
    {
        Assert.Equal(expected, TransferService.IsValidFlowAckCount(ackedChunks, totalChunks));
    }

    [Theory]
    [InlineData(-1L, 0)]
    [InlineData(1024L, 0)]
    public async Task 不正なFileMetaは受信元ピアへ即時Rejectする(long fileSize, int totalChunks)
    {
        const string peerId = "peer-B";
        var transferId = Guid.NewGuid();
        var connectionService = Substitute.For<IConnectionService>();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Settings.Returns(new AppSettings());

        var sentMessage = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        connectionService.SendAsync(peerId, Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                sentMessage.TrySetResult(callInfo.ArgAt<byte[]>(1));
                return Task.CompletedTask;
            });

        using var service = new TransferService(connectionService, settingsService);
        var meta = FileChunker.CreateFileMetaMessage(
            "invalid.bin",
            fileSize,
            totalChunks,
            string.Empty,
            transferId);

        service.HandleReceivedData(meta, peerId);

        var rejectMessage = await sentMessage.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var reject = FileChunker.ParseReject(rejectMessage);

        Assert.NotNull(reject);
        Assert.Equal(transferId, reject.Value.TransferId);
        Assert.Equal("不正なファイルメタデータ", reject.Value.Reason);
        await connectionService.Received(1).SendAsync(peerId, Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        await connectionService.DidNotReceive().SendAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void FileMeta受信だけでは保存先を作らず承認後に作成する()
    {
        const string peerId = "peer-B";
        var tempDir = Path.Combine(Path.GetTempPath(), $"FerryApproval_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var connectionService = Substitute.For<IConnectionService>();
            connectionService.SendAsync(peerId, Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            var settingsService = Substitute.For<ISettingsService>();
            settingsService.Settings.Returns(new AppSettings { SaveDirectory = tempDir });

            using (var service = new TransferService(connectionService, settingsService))
            {
                TransferItem? approval = null;
                service.ApprovalRequested += (_, item) => approval = item;

                var transferId = Guid.NewGuid();
                var meta = FileChunker.CreateFileMetaMessage(
                    "file.bin",
                    0,
                    0,
                    string.Empty,
                    transferId,
                    relativePath: "folder/sub/file.bin");

                service.HandleReceivedData(meta, peerId);

                Assert.NotNull(approval);
                Assert.False(Directory.Exists(Path.Combine(tempDir, "folder")));

                service.ApproveTransfer(transferId.ToString());

                Assert.True(File.Exists(Path.Combine(tempDir, "folder", "sub", "file.bin")));
                service.CancelTransfer(transferId.ToString());
                Assert.False(File.Exists(Path.Combine(tempDir, "folder", "sub", "file.bin")));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task 同一ピアの承認待ちは上限を超えるFileMetaをRejectする()
    {
        const string peerId = "peer-B";
        var connectionService = Substitute.For<IConnectionService>();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Settings.Returns(new AppSettings());

        var rejected = new TaskCompletionSource<(Guid TransferId, string Reason)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connectionService.SendAsync(peerId, Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var parsed = FileChunker.ParseReject(callInfo.ArgAt<byte[]>(1));
                if (parsed is { } reject)
                    rejected.TrySetResult(reject);
                return Task.CompletedTask;
            });

        using var service = new TransferService(connectionService, settingsService);
        var approvalCount = 0;
        service.ApprovalRequested += (_, _) => Interlocked.Increment(ref approvalCount);

        for (var i = 0; i <= TransferService.MaxPendingApprovalsPerPeer; i++)
        {
            var meta = FileChunker.CreateFileMetaMessage(
                $"file-{i}.bin",
                0,
                0,
                string.Empty,
                Guid.NewGuid());
            service.HandleReceivedData(meta, peerId);
        }

        var reject = await rejected.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(TransferService.MaxPendingApprovalsPerPeer, approvalCount);
        Assert.Equal("この送信元の受信承認待ちが上限に達しています", reject.Reason);
    }
}
