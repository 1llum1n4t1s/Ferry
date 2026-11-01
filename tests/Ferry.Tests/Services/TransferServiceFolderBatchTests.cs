using System.Collections.Concurrent;
using System.Security.Cryptography;
using Ferry.Infrastructure;
using Ferry.Models;
using Ferry.Services;
using NSubstitute;

namespace Ferry.Tests.Services;

public class TransferServiceFolderBatchTests
{
    [Fact]
    public async Task 未着の同batchは保存先を保ち同peer別batchと別peerは混在しない()
    {
        using var fixture = new Receiver();
        var batch = Guid.NewGuid();
        var first = await fixture.Finish(fixture.Announce("peer-A", batch, "one.txt")!);
        var separate = await fixture.Finish(fixture.Announce("peer-A", Guid.NewGuid(), "two.txt")!);
        var otherPeer = await fixture.Finish(fixture.Announce("peer-B", batch, "three.txt")!);
        var later = await fixture.Finish(fixture.Announce("peer-A", batch, "four.txt")!);
        Assert.Equal(Path.GetDirectoryName(first.SavedFilePath), Path.GetDirectoryName(later.SavedFilePath));
        Assert.NotEqual(Path.GetDirectoryName(first.SavedFilePath), Path.GetDirectoryName(separate.SavedFilePath));
        Assert.NotEqual(Path.GetDirectoryName(first.SavedFilePath), Path.GetDirectoryName(otherPeer.SavedFilePath));
        fixture.End("peer-A", batch);
        fixture.End("peer-A", batch);
        Assert.Null(fixture.Announce("peer-A", batch, "late.txt"));
        var next = await fixture.Finish(fixture.Announce("peer-A", Guid.NewGuid(), "five.txt")!);
        Assert.NotEqual(Path.GetDirectoryName(first.SavedFilePath), Path.GetDirectoryName(next.SavedFilePath));
    }

    [Fact]
    public async Task Endが先に届いても承認待ち項目の保存先を最後まで保持する()
    {
        using var fixture = new Receiver();
        var batch = Guid.NewGuid();
        var first = fixture.Announce("peer", batch, "one.txt")!;
        var second = fixture.Announce("peer", batch, "two.txt")!;
        fixture.End("other-peer", batch);
        fixture.End("peer", batch);
        await fixture.Finish(first);
        await fixture.Finish(second);
        Assert.Equal(Path.GetDirectoryName(first.SavedFilePath), Path.GetDirectoryName(second.SavedFilePath));
        Assert.Null(fixture.Announce("peer", batch, "late.txt"));
        Assert.False(fixture.Service.HasActiveTransfer);
    }

    [Fact]
    public async Task 孤立batchは30分後に回収しactiveが残るbatchは回収しない()
    {
        using var fixture = new Receiver();
        var orphan = Guid.NewGuid();
        var first = await fixture.Finish(fixture.Announce("peer", orphan, "one.txt")!);
        var liveBatch = Guid.NewGuid();
        var pending = fixture.Announce("peer", liveBatch, "pending.txt")!;
        fixture.Clock.Advance(TimeSpan.FromMinutes(31));
        var recovered = await fixture.Finish(fixture.Announce("peer", orphan, "two.txt")!);
        Assert.NotEqual(Path.GetDirectoryName(first.SavedFilePath), Path.GetDirectoryName(recovered.SavedFilePath));
        await fixture.Finish(pending);
        var later = await fixture.Finish(fixture.Announce("peer", liveBatch, "later.txt")!);
        Assert.Equal(Path.GetDirectoryName(pending.SavedFilePath), Path.GetDirectoryName(later.SavedFilePath));
    }

    [Fact]
    public void unknownEndは状態を作らずpeer上限と全体上限と期限回収が機能する()
    {
        using var fixture = new Receiver();
        for (var i = 0; i < TransferService.MaxFolderBatchesTotal + 1; i++) fixture.End("peer-0", Guid.NewGuid());
        for (var peer = 0; peer < 4; peer++)
        {
            for (var i = 0; i < TransferService.MaxFolderBatchesPerPeer; i++)
            {
                var item = fixture.Announce($"peer-{peer}", Guid.NewGuid(), "pending.txt");
                Assert.NotNull(item);
                fixture.Service.CancelTransfer(item.TransferId.ToString());
            }
            Assert.Null(fixture.Announce($"peer-{peer}", Guid.NewGuid(), "excess.txt"));
        }
        Assert.Null(fixture.Announce("fifth-peer", Guid.NewGuid(), "excess.txt"));
        fixture.Clock.Advance(TimeSpan.FromMinutes(31));
        var afterExpiry = fixture.Announce("fifth-peer", Guid.NewGuid(), "accepted.txt");
        Assert.NotNull(afterExpiry);
        fixture.Service.CancelTransfer(afterExpiry.TransferId.ToString());
        Assert.False(fixture.Service.HasActiveTransfer);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }

    private sealed class Receiver : IDisposable
    {
        public TransferService Service { get; }
        public Clock Clock { get; } = new();
        private TransferItem? _announced;
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<TransferItem>> _finished = new();

        public Receiver()
        {
            var root = Path.Combine(Path.GetTempPath(), "FerryBatch-" + Guid.NewGuid().ToString("N"));
            var connection = Substitute.For<IConnectionService>();
            var settings = Substitute.For<ISettingsService>();
            settings.Settings.Returns(new AppSettings { SaveDirectory = root, EnableNotificationSound = false });
            Service = new TransferService(connection, settings, null, Clock);
            Service.ApprovalRequested += (_, item) => _announced = item;
            Service.FileReceived += (_, item) => _finished[item.TransferId].TrySetResult(item);
        }

        public TransferItem? Announce(string peer, Guid batch, string fileName)
        {
            _announced = null;
            var id = Guid.NewGuid();
            _finished[id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Service.HandleReceivedData(FileChunker.CreateFileMetaMessage(fileName, 0, 0, "", id, "photos/" + fileName, Guid.NewGuid(), batch), peer);
            return _announced;
        }

        public async Task<TransferItem> Finish(TransferItem item)
        {
            Service.ApproveTransfer(item.TransferId.ToString());
            Service.HandleReceivedData(FileChunker.CreateFileHashMessage(item.TransferId, SHA256.HashData([])), item.PeerId);
            return await _finished[item.TransferId].Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        public void End(string peer, Guid batch) => Service.HandleReceivedData(FileChunker.CreateFolderBatchEndMessage(batch), peer);
        public void Dispose() => Service.Dispose();
    }
}
