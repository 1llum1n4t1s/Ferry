using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Ferry.Infrastructure;
using Ferry.Models;
using Ferry.Services;

// 実サービスの入口から保存・取消・切断までを通す、隔離した再現シナリオ。
internal static class ServiceChecks
{
    public static async Task<List<CheckResult>> RunAsync()
    {
        var results = new List<CheckResult>();
        async Task Run(string name, Func<Task> scenario)
        {
            try { await scenario(); results.Add(new(name, true)); }
            catch (Exception ex) { results.Add(new(name, false, ex.ToString())); }
        }
        await Run("corrupt-settings-identity", () => Task.Run(CheckIdentity));
        await Run("secondary-peer-cancel", () => Task.Run(CheckCancel));
        await Run("secondary-peer-flowack-after-cancel", () => Task.Run(CheckLateFlowAck));
        foreach (var ending in new[] { "disconnect", "reject", "cancel" })
            await Run("approve-overlap-" + ending, () => Task.Run(() => CheckApprovalOverlap(ending)));
        await Run("folder-sequential-approval", CheckFolder);
        await Run("legacy-buffer-before-approval", CheckBufferedChunk);
        await Run("udp-cancel-then-send", CheckUdp);
        return results;
    }

    private static void CheckIdentity()
    {
        foreach (var content in new[] { "", "{\"DeviceId\":", "null" })
        {
            using var directory = new ScratchDirectory();
            var path = Path.Combine(directory.Path, "settings.json");
            const string expected = "1234567890abcdef1234567890abcdef";
            File.WriteAllText(path, content);
            File.WriteAllText(Path.Combine(directory.Path, "device-id"), expected);
            using (var service = new SettingsService(path))
            {
                Require(service.WasCorrupted && service.Settings.DeviceId == expected, "破損設定で副本のID保持");
                service.SaveAsync().GetAwaiter().GetResult();
            }
            using var reload = new SettingsService(path);
            Require(reload.Settings.DeviceId == expected, "再保存・再起動後もID保持");
            Require(Directory.GetFiles(directory.Path, "*.corrupt-*").Length == 1, "破損原本の退避");
        }
        using var salvage = new ScratchDirectory();
        var salvagePath = Path.Combine(salvage.Path, "settings.json");
        const string original = "abcdef1234567890abcdef1234567890";
        File.WriteAllText(salvagePath, "{\"DeviceId\":\"" + original + "\", BROKEN");
        File.WriteAllText(Path.Combine(salvage.Path, "device-id"), new string('1', 32));
        using var salvaged = new SettingsService(salvagePath);
        Require(salvaged.Settings.DeviceId == original, "原本からのID救済を副本より優先");
    }

    private static void CheckCancel()
    {
        using var directory = new ScratchDirectory();
        var connection = new CaptureConnection();
        using var service = CreateReceiver(connection, directory.Path);
        var item = Announce(service, "secondary", "cancel.txt");
        service.ApproveTransfer(item.TransferId.ToString());
        service.CancelTransfer(item.TransferId.ToString());
        var reject = connection.Sent.Single(message => message.Data[0] == TransferProtocol.FileReject);
        Require(reject.Peer == "secondary", "Rejectの送信先は受信元");
        Require(!service.HasActiveTransfer && item.State == TransferState.Cancelled, "取消後の状態回収");
        Require(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories).Length == 0, "部分ファイル削除");
    }

    private static void CheckApprovalOverlap(string ending)
    {
        using var directory = new ScratchDirectory();
        var connection = new CaptureConnection();
        using var service = CreateReceiver(connection, directory.Path);
        var item = Announce(service, "secondary", "overlap.txt");
        var terminals = 0;
        service.TransferError += (_, _) => Interlocked.Increment(ref terminals);
        var gate = (Lock)Field(service, "_savePathGate");
        Exception? threadError = null;
        var approval = new Thread(() =>
        {
            try { service.ApproveTransfer(item.TransferId.ToString()); }
            catch (Exception ex) { threadError = ex; }
        }) { IsBackground = true };
        using (gate.EnterScope())
        {
            approval.Start();
            Require(SpinWait.SpinUntil(() => (approval.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)), "承認が実ファイル作成ゲートで待機");
            if (ending == "disconnect") connection.Drop("secondary");
            else if (ending == "reject") service.HandleReceivedData(FileChunker.CreateRejectMessage(item.TransferId, "CANARY"), "secondary");
            else service.CancelTransfer(item.TransferId.ToString());
        }
        Require(approval.Join(TimeSpan.FromSeconds(5)), "承認スレッド終了");
        if (threadError != null) throw threadError;
        Require(!service.HasActiveTransfer && item.State == TransferState.Cancelled, "終了後の再登録防止");
        Require(terminals == 1, "終端通知は1回");
        Require(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories).Length == 0, "作成途中のファイル回収");
        Require(!connection.Sent.Any(message => message.Data[0] == TransferProtocol.FileApprove), "終了後の承認送信防止");
    }

    private static void CheckLateFlowAck()
    {
        using var directory = new ScratchDirectory();
        var connection = new CaptureConnection();
        var settings = new MemorySettings(new AppSettings { SaveDirectory = directory.Path, DownloadKBps = 1, EnableNotificationSound = false });
        using var service = new TransferService(connection, settings);
        var item = Announce(service, "secondary", "late-flowack.txt");
        service.ApproveTransfer(item.TransferId.ToString());
        var bucketGate = (SemaphoreSlim)Field(Field(service, "_downloadBucket"), "_gate");
        var receive = new Thread(() => service.HandleReceivedData(FileChunker.CreateChunkMessage(item.TransferId, 0, Canary), "secondary")) { IsBackground = true };
        bucketGate.Wait();
        try
        {
            receive.Start();
            Require(SpinWait.SpinUntil(() => (receive.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)), "書込み後の帯域制限待ちを固定");
            service.CancelTransfer(item.TransferId.ToString());
        }
        finally { bucketGate.Release(); }
        Require(receive.Join(TimeSpan.FromSeconds(5)), "受信処理終了");
        var flow = connection.Sent.Single(message => message.Data[0] == TransferProtocol.FileFlowAck);
        Require(flow.Peer == "secondary", "索引回収後のFlowAckも受信元へ送る");
        Require(item.State == TransferState.Cancelled && !service.HasActiveTransfer, "取消終端の保持");
    }

    private static async Task CheckBufferedChunk()
    {
        using var directory = new ScratchDirectory();
        var connection = new CaptureConnection();
        using var service = CreateReceiver(connection, directory.Path);
        var item = Announce(service, "secondary", "legacy-buffer.txt");
        service.HandleReceivedData(FileChunker.CreateChunkMessage(item.TransferId, 0, Canary), "secondary");
        service.ApproveTransfer(item.TransferId.ToString());
        await Wait(() => item.State is TransferState.Completed or TransferState.Error);
        Require(item.State == TransferState.Completed && File.ReadAllBytes(item.SavedFilePath!).SequenceEqual(Canary), "先行した旧形式チャンクを承認後に保存");
    }

    private static async Task CheckFolder()
    {
        using var directory = new ScratchDirectory();
        var connection = new CaptureConnection();
        using var service = CreateReceiver(connection, directory.Path);
        var first = Announce(service, "secondary", "one.txt", "photos/one.txt");
        var second = Announce(service, "secondary", "two.txt", "photos/two.txt");
        await Finish(service, first);
        await Finish(service, second);
        Require(Path.GetDirectoryName(first.SavedFilePath) == Path.GetDirectoryName(second.SavedFilePath), "個別承認でも同じroot");
        var unrelated = Announce(service, "other", "pending.txt", "elsewhere/pending.txt");
        var next = Announce(service, "secondary", "three.txt", "photos/three.txt");
        await Finish(service, next);
        Require(Path.GetDirectoryName(next.SavedFilePath) != Path.GetDirectoryName(first.SavedFilePath), "別peerの待機は次回rootを混在させない");
        service.CancelTransfer(unrelated.TransferId.ToString());
        Require(!service.HasActiveTransfer, "全転送終端");
    }

    private static async Task Finish(TransferService service, TransferItem item)
    {
        service.ApproveTransfer(item.TransferId.ToString());
        service.HandleReceivedData(FileChunker.CreateChunkMessage(item.TransferId, 0, Canary), item.PeerId);
        await Wait(() => item.State is TransferState.Completed or TransferState.Error);
        Require(item.State == TransferState.Completed && File.ReadAllBytes(item.SavedFilePath!).SequenceEqual(Canary), "保存内容と受信検証");
    }

    private static async Task CheckUdp()
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new UdpHolePunchTransport();
        // 外部STUN/接続認可には接続せず、隔離した通常DATA/ACKの送信経路だけを実行する。
        var endpoint = (IPEndPoint)peer.Client.LocalEndPoint!;
        SetField(transport, "_remoteEp", endpoint);
        SetField(transport, "_isConnected", true);
        var ack = 0;
        var received = 0;
        using var peerCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var loop = Task.Run(async () =>
        {
            try
            {
                while (!peerCts.IsCancellationRequested)
                {
                    var packet = await peer.ReceiveAsync(peerCts.Token);
                    if (packet.Buffer[0] != 3) continue;
                    Interlocked.Increment(ref received);
                    if (Volatile.Read(ref ack) == 0) continue;
                    var response = packet.Buffer[..5];
                    response[0] = 4;
                    // 本体の受信ループ初期化を伴わないfixtureなので、ACK入口だけを呼ぶ。
                    typeof(UdpHolePunchTransport).GetMethod("HandleAck", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(transport, [response]);
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            using var cancel = new CancellationTokenSource();
            var send = transport.SendAsync(new byte[1187 * 200], cancel.Token);
            await Wait(() => Volatile.Read(ref received) >= 128);
            cancel.Cancel();
            try { await send; throw new InvalidOperationException("取消が伝播しませんでした"); }
            catch (OperationCanceledException) { }
            Volatile.Write(ref ack, 1);
            await transport.SendAsync(Canary).WaitAsync(TimeSpan.FromSeconds(2));
            Require(((SemaphoreSlim)Field(transport, "_windowSem")).CurrentCount == 128, "後続送信後にwindow枠を全回収");
        }
        finally
        {
            peerCts.Cancel();
            await loop;
        }
    }

    private static readonly byte[] Canary = Encoding.UTF8.GetBytes("FERRY_GOGO_CANARY");
    private static TransferService CreateReceiver(CaptureConnection connection, string path) =>
        new(connection, new MemorySettings(new AppSettings { SaveDirectory = path, AutoAcceptFileTransfer = false, EnableNotificationSound = false }));
    private static TransferItem Announce(TransferService service, string peer, string name, string? relative = null)
    {
        TransferItem? item = null;
        void Capture(object? _, TransferItem value) => item = value;
        service.ApprovalRequested += Capture;
        service.HandleReceivedData(FileChunker.CreateFileMetaMessage(name, Canary.Length, 1,
            Convert.ToHexStringLower(SHA256.HashData(Canary)), Guid.NewGuid(), relative), peer);
        service.ApprovalRequested -= Capture;
        return item ?? throw new InvalidOperationException("承認待ちが作成されませんでした");
    }
    private static object Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void SetField(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static async Task Wait(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ScratchDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Ferry-Gogo-" + Guid.NewGuid().ToString("N"));
        public ScratchDirectory() { Directory.CreateDirectory(Path); TemporaryDirectories.Enqueue(Path); }
        public void Dispose() => Directory.Delete(Path, true);
    }
    public static ConcurrentQueue<string> TemporaryDirectories { get; } = new();
}

internal sealed record CheckResult(string Name, bool Passed, string? Error = null);

#pragma warning disable CS0067
internal sealed class CaptureConnection : IConnectionService
{
    public PeerState State => PeerState.Connected;
    public PeerInfo ConnectedPeer { get; } = new() { SessionId = "primary", DisplayName = "CANARY_PRIMARY" };
    public ConnectionRoute Route => ConnectionRoute.Direct;
    public ConcurrentQueue<(string Peer, byte[] Data)> Sent { get; } = new();
    public event EventHandler<PeerState>? StateChanged;
    public event EventHandler<ConnectionRoute>? RouteChanged;
    public event EventHandler<PairedPeer>? PairingCompleted;
    public event EventHandler<DataReceivedEventArgs>? DataReceived;
    public event EventHandler<ConnectionLostEventArgs>? ConnectionLost;
    public event EventHandler<string>? StatusMessageChanged;
    public event EventHandler<string>? RemoteUnpairDetected;
    public void Drop(string peer) => ConnectionLost?.Invoke(this, new(peer));
    public Task SendAsync(byte[] data, CancellationToken ct = default) => SendAsync("primary", data, ct);
    public Task SendAsync(string peer, byte[] data, CancellationToken ct = default) { Sent.Enqueue((peer, data)); return Task.CompletedTask; }
    public Task<string> StartPairingSessionAsync(CancellationToken ct = default) => Task.FromResult("CANARY");
    public Task CancelPairingAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ConnectToPeerAsync(string peer, CancellationToken ct = default) => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task DisconnectAsync(string peer, CancellationToken ct = default) => Task.CompletedTask;
    public void StartListeningForConnection(string peer) { }
    public void StopListeningForConnection() { }
    public void StopListeningForConnection(string peer) { }
}
