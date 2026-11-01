using Ferry.Infrastructure;
using Ferry.Models;
using Ferry.Services;
using NSubstitute;

namespace Ferry.Tests.Services;

public class TransferServiceValidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 並列送信と先頭再送でawait中のチャンク内容とハッシュを保持する(bool cancelFirst)
    {
        var path = Path.GetTempFileName();
        var content = Enumerable.Range(0, TransferProtocol.ChunkSize * 2 + 17)
            .Select(i => (byte)(i / TransferProtocol.ChunkSize + 1)).ToArray();
        await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
        try
        {
            var connection = Substitute.For<IConnectionService>();
            var settings = Substitute.For<ISettingsService>();
            settings.Settings.Returns(new AppSettings());
            using var service = new TransferService(connection, settings);
            using var cts = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();
            TransferItem? firstItem = null;
            service.ProgressChanged += (_, item) =>
            {
                if (item.TransferId == firstId) firstItem = item;
            };
            var chunks = new System.Collections.Concurrent.ConcurrentDictionary<Guid, List<byte[]>>();
            var hashes = new System.Collections.Concurrent.ConcurrentDictionary<Guid, byte[]>();
            var holdFirst = true;
            connection.SendAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var message = ci.ArgAt<byte[]>(1);
                    if (message[0] == TransferProtocol.FileMeta)
                        service.HandleReceivedData(FileChunker.CreateApproveMessage(Guid.Parse(FileChunker.ParseFileMeta(message)!.TransferId)), ci.ArgAt<string>(0));
                    if (message[0] == TransferProtocol.FileHash)
                        hashes[new Guid(message.AsSpan(1, 16))] = message[17..];
                    return Task.CompletedTask;
                });
            connection.SendAsync(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
                .Returns(async ci =>
                {
                    var memory = ci.ArgAt<ReadOnlyMemory<byte>>(1);
                    var id = new Guid(memory.Span.Slice(1, 16));
                    var before = memory.ToArray();
                    if (id == firstId && holdFirst)
                    {
                        holdFirst = false;
                        entered.SetResult();
                        await release.Task.WaitAsync(ci.ArgAt<CancellationToken>(2));
                    }
                    Assert.Equal(before, memory.ToArray());
                    chunks.GetOrAdd(id, _ => []).Add(memory.ToArray());
                });

            var first = service.SendFileAsync(path, requestedTransferId: firstId, peerId: "peer-A", ct: cts.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await service.SendFileAsync(path, requestedTransferId: secondId, peerId: "peer-B", ct: TestContext.Current.CancellationToken);
            if (cancelFirst)
            {
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
                Assert.False(chunks.ContainsKey(firstId));
            }
            else
            {
                release.SetResult();
                await first;
                Assert.Equal(System.Security.Cryptography.SHA256.HashData(content), hashes[firstId]);
                chunks[firstId].Clear();
            }

            // 終了した SendFileAsync は登録を除去するため、再送対象が保持されている条件を用意する。
            var active = (System.Collections.Concurrent.ConcurrentDictionary<Guid, TransferItem>)typeof(TransferService)
                .GetField("_activeTransfers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(service)!;
            active[firstId] = Assert.IsType<TransferItem>(firstItem);
            Assert.True(await service.ResumeTransferAsync(firstId, TestContext.Current.CancellationToken));
            foreach (var id in new[] { firstId, secondId })
            {
                Assert.Equal(new[] { 0, 1, 2 }, chunks[id].Select(m => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(m.AsSpan(17, 4))));
                Assert.Equal(content, chunks[id].SelectMany(m => m.Skip(TransferProtocol.ChunkHeaderSize)).ToArray());
            }
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(content), hashes[secondId]);
        }
        finally { File.Delete(path); }
    }

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
    [InlineData("", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("A123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("a", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", false)]
    public void FileMetaのSHA256は空または64桁hexだけを受理する(string value, bool expected)
    {
        Assert.Equal(expected, TransferService.IsValidExpectedSha256(value));
    }

    [Fact]
    public async Task 短いSHA256を含むFileMetaは承認前にRejectする()
    {
        const string peerId = "peer-B";
        var transferId = Guid.NewGuid();
        var connectionService = Substitute.For<IConnectionService>();
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var rejected = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        connectionService.SendAsync(peerId, Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                rejected.TrySetResult(callInfo.ArgAt<byte[]>(1));
                return Task.CompletedTask;
            });

        using var service = new TransferService(connectionService, settingsService);
        var approvalCount = 0;
        service.ApprovalRequested += (_, _) => Interlocked.Increment(ref approvalCount);
        service.HandleReceivedData(
            FileChunker.CreateFileMetaMessage("invalid.bin", 0, 0, "a", transferId),
            peerId);

        var reject = FileChunker.ParseReject(await rejected.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));

        Assert.NotNull(reject);
        Assert.Equal(transferId, reject.Value.TransferId);
        Assert.Equal("不正な SHA-256", reject.Value.Reason);
        Assert.Equal(0, approvalCount);
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
