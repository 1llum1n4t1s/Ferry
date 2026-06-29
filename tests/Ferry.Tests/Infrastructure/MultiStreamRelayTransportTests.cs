using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Infrastructure;
using Ferry.Models;

namespace Ferry.Tests.Infrastructure;

/// <summary>
/// マルチストリーム転送 PoC: <see cref="MultiStreamRelayTransport"/> の束ね挙動を Fake ITransport で検証する。
/// 実 WebSocket 往復は扱わず、(a) チャンクの round-robin 分散 (b) 制御フレームの stream[0] 固定
/// (c) 受信集約 (d) ChannelClosed の「全本閉鎖で 1 回」集約 (e) Route=Relay を単体で固定する。
/// </summary>
public class MultiStreamRelayTransportTests
{
    /// <summary>送信を記録するだけの最小 ITransport スタブ。ChannelClosed/DataReceived を手動発火できる。</summary>
    private sealed class FakeTransport : ITransport
    {
        public List<byte[]> Sent { get; } = new();
        public bool IsConnected { get; private set; } = true;
        public ConnectionRoute Route => ConnectionRoute.Relay;
        public string PeerId { get; }

        public event EventHandler<DataReceivedEventArgs>? DataReceived;
        public event EventHandler? ChannelOpened;
        public event EventHandler? ChannelClosed;
        public event EventHandler<ConnectionRoute>? RouteChanged;

        public FakeTransport(string peerId = "peerX") => PeerId = peerId;

        public Task SendAsync(byte[] data, CancellationToken ct = default)
        {
            Sent.Add(data);
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            Sent.Add(data.ToArray());
            return Task.CompletedTask;
        }

        public void RaiseData(byte[] data) => DataReceived?.Invoke(this, new DataReceivedEventArgs(PeerId, data));
        public void RaiseClosed() { IsConnected = false; ChannelClosed?.Invoke(this, EventArgs.Empty); }

        public void Close() => IsConnected = false;
        public void Dispose() { IsConnected = false; }

        // 未使用イベントの警告抑止のための明示参照（C# はイベント未使用で CS0067 を出すため）
        public void TouchUnusedEvents() { ChannelOpened?.Invoke(this, EventArgs.Empty); RouteChanged?.Invoke(this, Route); }
    }

    private static byte[] Chunk(int n) => new byte[] { TransferProtocol.FileChunk, (byte)n };
    private static byte[] Meta() => new byte[] { TransferProtocol.FileMeta, 0x99 };

    [Fact]
    public void コンストラクタ_空stream配列は例外()
    {
        Assert.Throws<ArgumentException>(() => new MultiStreamRelayTransport(Array.Empty<ITransport>(), "peer"));
    }

    [Fact]
    public void RouteはRelay固定_StreamCountとPeerIdを公開()
    {
        var streams = new ITransport[] { new FakeTransport(), new FakeTransport() };
        using var ms = new MultiStreamRelayTransport(streams, "peer1");

        Assert.Equal(ConnectionRoute.Relay, ms.Route);
        Assert.Equal(2, ms.StreamCount);
        Assert.Equal("peer1", ms.PeerId);
        Assert.True(ms.IsConnected);
    }

    [Fact]
    public async Task FileChunkはround_robinで全streamに均等分散()
    {
        var s0 = new FakeTransport();
        var s1 = new FakeTransport();
        var s2 = new FakeTransport();
        using var ms = new MultiStreamRelayTransport(new ITransport[] { s0, s1, s2 }, "peer");

        // 6 チャンク送信 → 3 stream に 2 個ずつ均等に回る
        for (var i = 0; i < 6; i++)
            await ms.SendAsync(Chunk(i), TestContext.Current.CancellationToken);

        Assert.Equal(2, s0.Sent.Count);
        Assert.Equal(2, s1.Sent.Count);
        Assert.Equal(2, s2.Sent.Count);
        // 最初の 3 個が s0,s1,s2 の順に行く（_sendIndex 初期 -1 → 最初の Increment で 0）
        Assert.Equal(0, s0.Sent[0][1]);
        Assert.Equal(1, s1.Sent[0][1]);
        Assert.Equal(2, s2.Sent[0][1]);
    }

    [Fact]
    public async Task 制御フレーム_FileMetaは常にstream0へ固定()
    {
        var s0 = new FakeTransport();
        var s1 = new FakeTransport();
        using var ms = new MultiStreamRelayTransport(new ITransport[] { s0, s1 }, "peer");

        // チャンクを挟んでも FileMeta は必ず stream[0]
        await ms.SendAsync(Chunk(0), TestContext.Current.CancellationToken); // s0
        await ms.SendAsync(Meta(), TestContext.Current.CancellationToken);   // s0 (固定)
        await ms.SendAsync(Chunk(1), TestContext.Current.CancellationToken); // s1
        await ms.SendAsync(Meta(), TestContext.Current.CancellationToken);   // s0 (固定)

        // FileMeta 2 個は両方 s0、チャンクは s0/s1 に 1 個ずつ
        var metasOnS0 = s0.Sent.FindAll(b => b[0] == TransferProtocol.FileMeta).Count;
        var metasOnS1 = s1.Sent.FindAll(b => b[0] == TransferProtocol.FileMeta).Count;
        Assert.Equal(2, metasOnS0);
        Assert.Equal(0, metasOnS1);
    }

    [Fact]
    public async Task 空メッセージはstream0へ()
    {
        var s0 = new FakeTransport();
        var s1 = new FakeTransport();
        using var ms = new MultiStreamRelayTransport(new ITransport[] { s0, s1 }, "peer");

        await ms.SendAsync(Array.Empty<byte>(), TestContext.Current.CancellationToken);

        Assert.Single(s0.Sent);
        Assert.Empty(s1.Sent);
    }

    [Fact]
    public void 受信_inner1本のDataReceivedが集約DataReceivedへ貫通()
    {
        var s0 = new FakeTransport("peerA");
        var s1 = new FakeTransport("peerA");
        using var ms = new MultiStreamRelayTransport(new ITransport[] { s0, s1 }, "peerA");

        var received = new List<(string peer, byte[] data)>();
        ms.DataReceived += (_, e) => received.Add((e.PeerId, e.Data));

        s0.RaiseData(new byte[] { 1, 2, 3 });
        s1.RaiseData(new byte[] { 4, 5 });

        Assert.Equal(2, received.Count);
        Assert.All(received, r => Assert.Equal("peerA", r.peer));
        Assert.Equal(new byte[] { 1, 2, 3 }, received[0].data);
        Assert.Equal(new byte[] { 4, 5 }, received[1].data);
    }

    [Fact]
    public void ChannelClosed_1本閉鎖では発火せず_全本閉鎖で1回だけ発火()
    {
        var s0 = new FakeTransport();
        var s1 = new FakeTransport();
        var s2 = new FakeTransport();
        using var ms = new MultiStreamRelayTransport(new ITransport[] { s0, s1, s2 }, "peer");

        var closedCount = 0;
        ms.ChannelClosed += (_, _) => closedCount++;

        s0.RaiseClosed();
        Assert.Equal(0, closedCount); // まだ 2 本生存
        Assert.True(ms.IsConnected);

        s1.RaiseClosed();
        Assert.Equal(0, closedCount); // まだ 1 本生存
        Assert.True(ms.IsConnected);

        s2.RaiseClosed();
        Assert.Equal(1, closedCount); // 全本閉鎖 → 1 回発火
        Assert.False(ms.IsConnected);
    }

    [Fact]
    public async Task 全本閉鎖後のSendAsyncは例外()
    {
        var s0 = new FakeTransport();
        using var ms = new MultiStreamRelayTransport(new ITransport[] { s0 }, "peer");

        s0.RaiseClosed();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ms.SendAsync(Chunk(0), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Dispose_接続中なら集約ChannelClosedを1回補償発火()
    {
        var s0 = new FakeTransport();
        var s1 = new FakeTransport();
        var ms = new MultiStreamRelayTransport(new ITransport[] { s0, s1 }, "peer");

        var closedCount = 0;
        ms.ChannelClosed += (_, _) => closedCount++;

        ms.Dispose();

        Assert.Equal(1, closedCount);
        Assert.False(ms.IsConnected);
    }

    [Fact]
    public void Dispose_inner購読を解除して以後の受信を貫通させない()
    {
        var s0 = new FakeTransport();
        var ms = new MultiStreamRelayTransport(new ITransport[] { s0 }, "peer");

        var received = 0;
        ms.DataReceived += (_, _) => received++;

        ms.Dispose();
        s0.RaiseData(new byte[] { 1 }); // Dispose 後の inner 受信は集約へ来ない

        Assert.Equal(0, received);
    }
}
