using System.Security.Cryptography;
using Ferry.Infrastructure;
using Ferry.Models;
using Ferry.Services;
using NSubstitute;

namespace Ferry.Tests.Services;

public class TransferServiceCompletionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(70_000)]
    public async Task 検証ACKまで待機し誤peerと誤IDと誤tokenと早過ぎるACKを無視する(int length)
    {
        using var pair = new Pair();
        pair.ReceiverFilter = message => message[0] != TransferProtocol.FileAck;
        var content = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        pair.SenderFilter = message =>
        {
            if (message[0] == TransferProtocol.FileMeta)
            {
                var meta = FileChunker.ParseFileMeta(message)!;
                pair.Sender.HandleReceivedData(FileChunker.CreateResultAckMessage(true, SHA256.HashData(content), Guid.Parse(meta.TransferId), meta.ResultToken!.Value), "receiver");
            }
            if (message[0] == TransferProtocol.FileChunk)
                pair.Sender.HandleReceivedData(FileChunker.CreateResultAckMessage(true, SHA256.HashData(content), pair.Id, pair.Meta!.ResultToken!.Value), "receiver");
            return true;
        };
        var sending = pair.Send(content);
        var ack = await pair.Ack.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(sending.IsCompleted);
        Assert.Equal(TransferState.InProgress, pair.SentItem!.State);
        var parsed = FileChunker.ParseResultAck(ack)!.Value;
        pair.Sender.HandleReceivedData(ack, "other-peer");
        pair.Sender.HandleReceivedData(FileChunker.CreateResultAckMessage(true, parsed.Hash, Guid.NewGuid(), parsed.ResultToken), "receiver");
        pair.Sender.HandleReceivedData(FileChunker.CreateResultAckMessage(true, parsed.Hash, parsed.TransferId, Guid.NewGuid()), "receiver");
        Assert.False(sending.IsCompleted);
        pair.Sender.HandleReceivedData(ack, "receiver");
        await sending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        pair.Sender.HandleReceivedData(ack, "receiver");
        Assert.Equal(TransferState.Completed, pair.SentItem.State);
        var received = await pair.Received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(content, await File.ReadAllBytesAsync(received.SavedFilePath!, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 受信検証失敗と成功ACKの不正hashを送信成功にしない(bool invalidSuccessHash)
    {
        using var pair = new Pair();
        pair.ReceiverFilter = message =>
        {
            if (message[0] != TransferProtocol.FileAck) return true;
            var result = FileChunker.ParseResultAck(message)!.Value;
            pair.Sender.HandleReceivedData(FileChunker.CreateResultAckMessage(invalidSuccessHash, new byte[32], result.TransferId, result.ResultToken), "receiver");
            return false;
        };
        await Assert.ThrowsAsync<ReceiveVerificationFailedException>(() => pair.Send([1, 2, 3]));
        Assert.Equal(TransferState.Error, pair.SentItem!.State);
    }

    [Fact]
    public async Task 実受信データの破損は両側の検証失敗になる()
    {
        using var pair = new Pair();
        pair.SenderFilter = message =>
        {
            if (message[0] == TransferProtocol.FileChunk) message[^1] ^= 0xFF;
            return true;
        };
        await Assert.ThrowsAsync<ReceiveVerificationFailedException>(() => pair.Send([1, 2, 3]));
        Assert.Equal(TransferState.Error, pair.SentItem!.State);
        await pair.ReceiveFailed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task 最終hash送信の同期コールバックから届く結果も受理する()
    {
        using var pair = new Pair();
        pair.SenderFilter = message =>
        {
            if (message[0] == TransferProtocol.FileHash)
                pair.Sender.HandleReceivedData(FileChunker.CreateResultAckMessage(true, message[17..], pair.Id, pair.Meta!.ResultToken!.Value), "receiver");
            return true;
        };
        await pair.Send([]);
        await pair.Received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(TransferState.Completed, pair.SentItem!.State);
    }

    [Fact]
    public async Task 受信hash計算の入出力失敗も否定結果として送信側へ返す()
    {
        using var pair = new Pair();
        pair.SenderFilter = message =>
        {
            if (message[0] == TransferProtocol.FileHash)
            {
                // 検証時に保存先が利用不能になる境界を、I/Oと状態管理を差し替えずに固定する。
                var states = typeof(TransferService).GetField("_receiveStates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(pair.Receiver)!;
                var state = states.GetType().GetProperty("Item")!.GetValue(states, [pair.Id])!;
                state.GetType().GetProperty("SavePath")!.SetValue(state, Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N"), "file.bin"));
            }
            return true;
        };
        await Assert.ThrowsAsync<ReceiveVerificationFailedException>(() => pair.Send([]));
        await pair.ReceiveFailed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(TransferState.Error, pair.SentItem!.State);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("disconnect")]
    public async Task 結果待ちのタイムアウトと中断後に遅延ACKで復活しない(string ending)
    {
        using var pair = new Pair(ending == "timeout" ? TimeSpan.FromMilliseconds(250) : null);
        pair.ReceiverFilter = message => message[0] != TransferProtocol.FileAck;
        var sending = pair.Send([]);
        var ack = await pair.Ack.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (ending == "cancel") pair.Sender.CancelTransfer(pair.Id.ToString());
        if (ending == "disconnect") pair.SenderConnection.ConnectionLost += Raise.Event<EventHandler<ConnectionLostEventArgs>>(null, new ConnectionLostEventArgs("receiver"));
        if (ending == "timeout") await Assert.ThrowsAsync<ReceiveResultUnknownException>(() => sending);
        else if (ending == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        else await Assert.ThrowsAsync<ReceiveResultUnknownException>(() => sending);
        var terminal = pair.SentItem!.State;
        pair.Sender.HandleReceivedData(ack, "receiver");
        Assert.Equal(terminal, pair.SentItem.State);
        Assert.NotEqual(TransferState.Completed, terminal);
        Assert.False(pair.Sender.HasActiveTransfer);
    }

    [Fact]
    public async Task 同TransferIdの再試行へ前回tokenの結果を流用しない()
    {
        using var pair = new Pair(TimeSpan.FromMilliseconds(200));
        pair.ReceiverFilter = message => message[0] != TransferProtocol.FileAck;
        var first = pair.Send([1]);
        var oldAck = await pair.Ack.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ReceiveResultUnknownException>(() => first);
        pair.Ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = pair.Send([2]);
        var newAck = await pair.Ack.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        pair.Sender.HandleReceivedData(oldAck, "receiver");
        Assert.False(second.IsCompleted);
        pair.Sender.HandleReceivedData(newAck, "receiver");
        await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(TransferState.Completed, pair.SentItem!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resumeも新試行の結果だけを待ち待機中の取消で復活しない(bool cancel)
    {
        using var pair = new Pair(TimeSpan.FromMilliseconds(500));
        pair.ReceiverFilter = message => message[0] != TransferProtocol.FileAck;
        var first = pair.Send([1]);
        var oldAck = await pair.Ack.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ReceiveResultUnknownException>(() => first);
        // 正常送信APIは終了時に除去するため、既存Resume入口の保持済み対象という前提だけを用意する。
        var active = (System.Collections.Concurrent.ConcurrentDictionary<Guid, TransferItem>)typeof(TransferService)
            .GetField("_activeTransfers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(pair.Sender)!;
        active[pair.Id] = pair.SentItem!;
        pair.SentItem!.State = TransferState.Suspended;
        pair.Ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = pair.Sender.ResumeTransferAsync(pair.Id, TestContext.Current.CancellationToken);
        var currentAck = await pair.Ack.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        pair.Sender.HandleReceivedData(oldAck, "receiver");
        Assert.False(resumed.IsCompleted);
        if (cancel) pair.Sender.CancelTransfer(pair.Id.ToString());
        else pair.Sender.HandleReceivedData(currentAck, "receiver");
        Assert.Equal(!cancel, await resumed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        pair.Sender.HandleReceivedData(currentAck, "receiver");
        Assert.Equal(cancel ? TransferState.Cancelled : TransferState.Completed, pair.SentItem.State);
        Assert.False(pair.Sender.HasActiveTransfer);
    }

    [Fact]
    public async Task Resume終端の通知から始まる同IDの次試行を清掃で除去しない()
    {
        using var pair = new Pair();
        await pair.Send([1]);
        var active = (System.Collections.Concurrent.ConcurrentDictionary<Guid, TransferItem>)typeof(TransferService)
            .GetField("_activeTransfers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(pair.Sender)!;
        active[pair.Id] = pair.SentItem!;
        pair.SentItem!.State = TransferState.Suspended;
        pair.ReceiverFilter = message => message[0] != TransferProtocol.FileAck;
        pair.Ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = pair.Sender.ResumeTransferAsync(pair.Id, TestContext.Current.CancellationToken);
        var resumeAck = await pair.Ack.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Task? replacement = null;
        var started = false;
        pair.Sender.ProgressChanged += (_, item) =>
        {
            if (item.State != TransferState.Completed || started) return;
            started = true;
            pair.Ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
            replacement = pair.Send([2]);
        };
        pair.Sender.HandleReceivedData(resumeAck, "receiver");
        Assert.True(await resumed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.NotNull(replacement);
        var nextAck = await pair.Ack.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        pair.Sender.HandleReceivedData(resumeAck, "receiver");
        Assert.True(pair.Sender.HasActiveTransfer);
        Assert.False(replacement.IsCompleted);
        pair.Sender.HandleReceivedData(nextAck, "receiver");
        await replacement.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(pair.Sender.HasActiveTransfer);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 旧形式の送受信とは交渉なしの完了経路を維持する(bool oldReceiver)
    {
        using var pair = new Pair();
        if (oldReceiver)
        {
            pair.ReceiverFilter = message =>
            {
                if (message[0] == TransferProtocol.FileApprove)
                {
                    pair.Sender.HandleReceivedData(message[..17], "receiver");
                    return false;
                }
                return message[0] != TransferProtocol.FileAck;
            };
            await pair.Send([3]);
            Assert.Equal(TransferState.Completed, pair.SentItem!.State);
        }
        else
        {
            var data = new byte[] { 3 };
            pair.Receiver.HandleReceivedData(FileChunker.CreateFileMetaMessage("old.bin", 1, 1, Convert.ToHexStringLower(SHA256.HashData(data)), pair.Id), "sender");
            pair.Receiver.HandleReceivedData(FileChunker.CreateChunkMessage(pair.Id, 0, data), "sender");
            var ack = await pair.Ack.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(34, ack.Length);
            Assert.Equal(1, ack[1]);
        }
    }

    [Theory]
    [InlineData(0L, 300d)]
    [InlineData(16_777_216L, 301d)]
    [InlineData(1_099_511_627_776L, 65_836d)]
    [InlineData(long.MaxValue, 86_400d)]
    public void 受信結果の期限はサイズに応じた有界な猶予を持つ(long size, double seconds)
        => Assert.Equal(seconds, TransferService.ResultTimeoutForSize(size).TotalSeconds);

    private sealed class Pair : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public IConnectionService SenderConnection { get; } = Substitute.For<IConnectionService>();
        public TransferService Sender { get; }
        public TransferService Receiver { get; }
        public Func<byte[], bool>? SenderFilter { get; set; }
        public Func<byte[], bool>? ReceiverFilter { get; set; }
        public TaskCompletionSource<byte[]> Ack { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FileMeta? Meta { get; private set; }
        public TransferItem? SentItem { get; private set; }
        public TransferItem? ReceivedItem { get; private set; }
        public TransferItem? ReceiveError { get; private set; }
        public TaskCompletionSource<TransferItem> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<TransferItem> ReceiveFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "FerryCompletion-" + Guid.NewGuid().ToString("N"));

        public Pair(TimeSpan? timeout = null)
        {
            Directory.CreateDirectory(_root);
            var receiverConnection = Substitute.For<IConnectionService>();
            var senderSettings = Substitute.For<ISettingsService>();
            var receiverSettings = Substitute.For<ISettingsService>();
            senderSettings.Settings.Returns(new AppSettings());
            receiverSettings.Settings.Returns(new AppSettings { SaveDirectory = Path.Combine(_root, "saved"), EnableNotificationSound = false });
            Sender = new TransferService(SenderConnection, senderSettings, timeout);
            Receiver = new TransferService(receiverConnection, receiverSettings);
            Sender.ProgressChanged += (_, item) => SentItem = item;
            Receiver.ApprovalRequested += (_, item) => Receiver.ApproveTransfer(item.TransferId.ToString());
            Receiver.FileReceived += (_, item) => { ReceivedItem = item; Received.TrySetResult(item); };
            Receiver.TransferError += (_, item) => { ReceiveError = item; ReceiveFailed.TrySetResult(item); };
            SenderConnection.SendAsync("receiver", Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(ci => DeliverToReceiver(ci.ArgAt<byte[]>(1).ToArray()));
            SenderConnection.SendAsync("receiver", Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(ci => DeliverToReceiver(ci.ArgAt<ReadOnlyMemory<byte>>(1).ToArray()));
            receiverConnection.SendAsync("sender", Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(ci =>
            {
                var message = ci.ArgAt<byte[]>(1).ToArray();
                if (message[0] == TransferProtocol.FileAck) Ack.TrySetResult(message);
                if (ReceiverFilter?.Invoke(message) != false) Sender.HandleReceivedData(message, "receiver");
                return Task.CompletedTask;
            });
        }

        private Task DeliverToReceiver(byte[] message)
        {
            if (message[0] == TransferProtocol.FileMeta) Meta = FileChunker.ParseFileMeta(message);
            if (SenderFilter?.Invoke(message) != false) Receiver.HandleReceivedData(message, "sender");
            return Task.CompletedTask;
        }

        public Task Send(byte[] content)
        {
            var path = Path.Combine(_root, "input.bin");
            File.WriteAllBytes(path, content);
            return Sender.SendFileAsync(path, requestedTransferId: Id, peerId: "receiver", ct: TestContext.Current.CancellationToken);
        }

        public void Dispose() { Sender.Dispose(); Receiver.Dispose(); }
    }
}
