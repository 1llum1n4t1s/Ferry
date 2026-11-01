using System.Reflection;
using Ferry.Infrastructure;
using Ferry.Services;
using NSubstitute;

namespace Ferry.Tests.Services;

public class ConnectionServiceSendTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task 全送信overloadが接続と暗号準備を確認し旧ペア互換を保つ(int overload)
    {
        const string self = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string peer = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        using var service = new ConnectionService(self, "TestPC", signalingFactory: () => Substitute.For<ISignalingService>());
        var transport = Substitute.For<ITransport>();
        transport.IsConnected.Returns(true);
        var sent = new List<byte[]>();
        transport.SendAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            sent.Add(ci.ArgAt<byte[]>(0).ToArray());
            return Task.CompletedTask;
        });
        transport.SendAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            sent.Add(ci.ArgAt<ReadOnlyMemory<byte>>(0).ToArray());
            return Task.CompletedTask;
        });

        // 公開送信入口を通すため、ネットワーク接続だけを確立済み session に置き換える。
        var sessionType = typeof(ConnectionService).GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
        var session = Activator.CreateInstance(sessionType, peer)!;
        void Set(string name, object? value) => sessionType.GetProperty(name)!.SetValue(session, value);
        Set("Transport", transport);
        var sessions = typeof(ConnectionService).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        sessions.GetType().GetMethod("TryAdd")!.Invoke(sessions, [peer, session]);
        typeof(ConnectionService).GetField("_transport", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, transport);
        byte[] payload = [1, 2, 3, 4];
        Task Send(CancellationToken ct) => overload switch
        {
            0 => service.SendAsync(payload, ct),
            1 => service.SendAsync((ReadOnlyMemory<byte>)payload, ct),
            2 => service.SendAsync(peer, payload, ct),
            _ => service.SendAsync(peer, (ReadOnlyMemory<byte>)payload, ct),
        };

        transport.IsConnected.Returns(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Send(TestContext.Current.CancellationToken));
        transport.IsConnected.Returns(true);
        var channel = new SecureChannel(self, peer, new byte[32], true);
        channel.Start();
        Set("SecureChannel", channel);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Set("SecureReadyTcs", ready);
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            var cancelled = Send(cts.Token);
            Assert.False(cancelled.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        }
        var waiting = Send(TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        ready.SetResult(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => waiting);
        Assert.Empty(sent);

        var legacy = new SecureChannel(self, peer, null, true);
        legacy.Start();
        Set("SecureChannel", legacy);
        await Send(TestContext.Current.CancellationToken);
        Assert.Equal(payload, Assert.Single(sent));
        sent.Clear();

        var remote = new SecureChannel(peer, self, new byte[32], true);
        channel = new SecureChannel(self, peer, new byte[32], true);
        var toRemote = new Queue<byte[]>(channel.Start().Send);
        var toLocal = new Queue<byte[]>(remote.Start().Send);
        while (toLocal.Count > 0 || toRemote.Count > 0)
        {
            if (toLocal.TryDequeue(out var local))
                foreach (var frame in channel.OnFrame(local).Send) toRemote.Enqueue(frame);
            if (toRemote.TryDequeue(out var frameForRemote))
                foreach (var frame in remote.OnFrame(frameForRemote).Send) toLocal.Enqueue(frame);
        }
        Assert.True(channel.IsSecure);
        Set("SecureChannel", channel);
        await Send(TestContext.Current.CancellationToken);
        var envelope = Assert.Single(sent);
        Assert.NotEqual(payload, envelope);
        Assert.Equal(payload, Assert.Single(remote.OnFrame(envelope).Deliver));
    }
}
