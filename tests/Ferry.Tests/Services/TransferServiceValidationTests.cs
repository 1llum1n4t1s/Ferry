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
}
