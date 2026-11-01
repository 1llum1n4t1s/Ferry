using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ferry;
using Ferry.Infrastructure;
using Ferry.Models;
using Ferry.Services;
using Ferry.ViewModels;

// UIコマンドと実転送サービスを通す。通信境界だけを隔離し、到着順・破損・旧形式を制御する。
internal static class TransferIntegrationChecks
{
    public static async Task<List<CheckResult>> RunAsync(IClassicDesktopStyleApplicationLifetime desktop, string output)
    {
        Dispatcher.UIThread.VerifyAccess();
        var results = new List<CheckResult>();
        async Task Run(string name, Func<Task> check)
        {
            try { await check(); results.Add(new(name, true)); }
            catch (Exception ex) { results.Add(new(name, false, ex.ToString())); }
        }
        await Run("transfer-result-ack-ui", () => CheckAckAsync(desktop, output));
        await Run("transfer-result-failure-ui", () => CheckFailuresAsync(desktop, output));
        await Run("folder-batches-and-legacy-ui", () => CheckFoldersAsync(desktop, output));
        return results;
    }

    private static async Task CheckAckAsync(IClassicDesktopStyleApplicationLifetime desktop, string output)
    {
        await using var pair = new TransferPair(desktop);
        var bytes = Enumerable.Range(0, 65537).Select(index => (byte)(index % 251)).ToArray();
        var path = pair.SourceFile("ack.bin", bytes);
        var acks = new ConcurrentQueue<byte[]>();
        var earlyAckInjected = false;
        pair.Sender.Link.Filter = data =>
        {
            if (data[0] == TransferProtocol.FileMeta)
            {
                var meta = FileChunker.ParseFileMeta(data)!;
                var resultToken = meta.ResultToken ?? throw new InvalidOperationException("新形式メタデータに試行tokenがありません");
                // 正しいID/hashでも、承認・送信完了前のACKを成功扱いしてはいけない。
                pair.Sender.Link.Inject(TransferPair.ReceiverId, FileChunker.CreateResultAckMessage(
                    true, SHA256.HashData(bytes), Guid.Parse(meta.TransferId), resultToken));
                earlyAckInjected = true;
            }
            return data;
        };
        pair.Receiver.Link.Filter = data =>
        {
            if (data[0] != TransferProtocol.FileAck) return data;
            acks.Enqueue(data);
            return null;
        };

        var send = pair.SendAsync(path);
        await WaitAsync(() => acks.TryPeek(out _) && pair.Receiver.Vm.Transfer.Transfers.Any(item => item.State == TransferState.Completed));
        await DrainAsync();
        var sender = pair.Sender.Vm.Transfer.Transfers.Single();
        var receiver = pair.Receiver.Vm.Transfer.Transfers.Single();
        Require(earlyAckInjected && sender.State == TransferState.InProgress && !send.IsCompleted, "早過ぎるACKやACK未着で完了しない");
        Require(File.ReadAllBytes(receiver.SavedFilePath!).SequenceEqual(bytes), "受信保存bytesが送信元と一致");
        using (var saved = File.OpenRead(receiver.SavedFilePath!))
            Require(SHA256.HashData(saved).SequenceEqual(SHA256.HashData(bytes)), "保存済みファイルのSHA-256が一致");
        await pair.CaptureAsync(output, "transfer-before-ack");

        Require(acks.TryPeek(out var ack), "受信結果ACKを捕捉");
        var parsed = FileChunker.ParseResultAck(ack!) ?? throw new InvalidOperationException("相関ACK形式ではありません");
        pair.Sender.Link.Inject("unrelated-peer", ack!);
        pair.Sender.Link.Inject(TransferPair.ReceiverId, FileChunker.CreateResultAckMessage(true, parsed.Hash, Guid.NewGuid(), parsed.ResultToken));
        pair.Sender.Link.Inject(TransferPair.ReceiverId, FileChunker.CreateResultAckMessage(true, parsed.Hash, parsed.TransferId, Guid.NewGuid()));
        await DrainAsync();
        Require(sender.State == TransferState.InProgress && !send.IsCompleted, "誤peer・ID・試行tokenのACKで完了しない");
        pair.Sender.Link.Inject(TransferPair.ReceiverId, ack!);
        await send.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitAsync(() => sender.State == TransferState.Completed && pair.Sender.HasStateText("State.Completed"));
        Require(sender.CompletedAt.HasValue && receiver.State == TransferState.Completed, "正しいACK後に送受信とも完了");
        await pair.CaptureAsync(output, "transfer-after-ack");
    }

    private static async Task CheckFailuresAsync(IClassicDesktopStyleApplicationLifetime desktop, string output)
    {
        // 全再試行を破損させ、最後までUIの成功表示へ到達しないことを確認する。
        await using (var pair = new TransferPair(desktop))
        {
            var path = pair.SourceFile("corrupt.bin", new byte[] { 1, 3, 5, 7, 9 });
            pair.Sender.Link.Filter = data =>
            {
                if (data[0] == TransferProtocol.FileChunk) data[^1] ^= 0x01;
                return data;
            };
            await pair.SendAsync(path).WaitAsync(TimeSpan.FromSeconds(15));
            await WaitAsync(() => pair.Receiver.Vm.Transfer.Transfers.Any(item => item.State == TransferState.Error)
                && Directory.GetFiles(pair.Receiver.SaveDirectory, "*", SearchOption.AllDirectories).Length == 0);
            Require(pair.Sender.Vm.Transfer.Transfers.Single().State == TransferState.Error, "hash不一致は送信Error");
            Require(pair.Receiver.Vm.Transfer.Transfers.All(item => item.State != TransferState.Completed), "hash不一致の受信を完了にしない");
            await pair.CaptureAsync(output, "transfer-hash-error");
            await WaitAsync(() => pair.Sender.HasText(pair.Sender.Vm.Transfer.Transfers.Single().StateText), pair.Sender.DescribeDisplay);
        }
        await using (var pair = new TransferPair(desktop, TimeSpan.FromMilliseconds(200)))
        {
            var path = pair.SourceFile("timeout.bin", new byte[] { 2, 4, 6, 8 });
            var held = new ConcurrentQueue<byte[]>();
            var attempts = 0;
            pair.Sender.Link.Filter = data =>
            {
                if (data[0] == TransferProtocol.FileMeta) Interlocked.Increment(ref attempts);
                return data;
            };
            pair.Receiver.Link.Filter = data =>
            {
                if (data[0] != TransferProtocol.FileAck) return data;
                held.Enqueue(data);
                return null;
            };
            await pair.SendAsync(path).WaitAsync(TimeSpan.FromSeconds(15));
            Require(!held.IsEmpty && pair.Sender.Vm.Transfer.Transfers.Single().State == TransferState.Error, "検証ACK欠落は有界でError");
            await WaitAsync(() => pair.Receiver.Vm.Transfer.Transfers.Any(item => item.State == TransferState.Completed));
            Require(attempts == 1 && held.Count == 1, "結果不明時は自動再送しない");
            var saved = Directory.GetFiles(pair.Receiver.SaveDirectory, "*", SearchOption.AllDirectories);
            Require(saved.Length == 1 && File.ReadAllBytes(saved[0]).SequenceEqual(new byte[] { 2, 4, 6, 8 }), "ACK欠落で保存済みファイルを重複させない");
            // timeout後の遅延ACKで終端状態を復活させない。
            while (held.TryDequeue(out var ack)) pair.Sender.Link.Inject(TransferPair.ReceiverId, ack);
            await DrainAsync();
            Require(pair.Sender.Vm.Transfer.Transfers.Single().State == TransferState.Error, "遅延ACKでErrorが完了へ復活しない");
            await WaitAsync(() => pair.Sender.HasText(pair.Sender.Vm.Transfer.Transfers.Single().StateText));
            await pair.CaptureAsync(output, "transfer-ack-timeout");
        }
    }

    private static async Task CheckFoldersAsync(IClassicDesktopStyleApplicationLifetime desktop, string output)
    {
        await using var pair = new TransferPair(desktop);
        Directory.CreateDirectory(Path.Combine(pair.Receiver.SaveDirectory, "photos"));
        var expected = Enumerable.Range(1, 11).ToDictionary(index => $"file-{index:D2}.txt", index => (byte)index);
        var sources = expected.Select(entry => pair.SourceFile("first/photos/" + entry.Key, new[] { entry.Value })).ToArray();
        var heldMeta = new ConcurrentQueue<byte[]>();
        var metas = 0;
        pair.Sender.Link.Filter = data =>
        {
            if (data[0] != TransferProtocol.FileMeta || Interlocked.Increment(ref metas) == 1) return data;
            heldMeta.Enqueue(data);
            return null;
        };
        var send = pair.SendAsync(Path.GetDirectoryName(sources[0])!);
        await WaitAsync(() => Volatile.Read(ref metas) == 11 && heldMeta.Count == 10
            && pair.Receiver.Vm.Transfer.Transfers.Count(item => item.State == TransferState.Completed) == 1);
        var firstSaved = pair.Receiver.Vm.Transfer.Transfers.Single(item => item.State == TransferState.Completed).SavedFilePath!;
        var heldAtGap = heldMeta.Count;
        Require(!send.IsCompleted && metas == 11 && heldAtGap == 10, "先行1件完了で10件窓を越す11番目が投入され、後続10件は未着");
        pair.Sender.Link.Filter = null;
        while (heldMeta.TryDequeue(out var meta)) pair.Receiver.Link.Inject(TransferPair.SenderId, meta);
        await send.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitAsync(() => pair.Receiver.Vm.Transfer.Transfers.Count(item => item.State == TransferState.Completed) == 11);
        var firstBatch = pair.Receiver.Vm.Transfer.Transfers.ToArray();
        Require(firstBatch.All(item => Path.GetDirectoryName(item.SavedFilePath!) == Path.GetDirectoryName(firstSaved)), "同batchは先行完了後の後続も同じ保存先");
        Require(firstBatch.Select(item => Path.GetFileName(item.SavedFilePath!)).Order().SequenceEqual(expected.Keys.Order())
            && firstBatch.All(item => File.ReadAllBytes(item.SavedFilePath!).SequenceEqual(new[] { expected[Path.GetFileName(item.SavedFilePath!)] })),
            "同batchの11ファイルを欠落・重複なく同じ内容で保存");

        var second = pair.SourceFile("second/photos/file-01.txt", new byte[] { 99 });
        await pair.SendAsync(Path.GetDirectoryName(second)!).WaitAsync(TimeSpan.FromSeconds(10));
        await WaitAsync(() => pair.Receiver.Vm.Transfer.Transfers.Count(item => item.State == TransferState.Completed) == 12);
        var other = pair.Receiver.Vm.Transfer.Transfers.Single(item => !firstBatch.Contains(item));
        Require(Path.GetDirectoryName(other.SavedFilePath!) != Path.GetDirectoryName(firstSaved), "同rootの別batchは保存先を分離");
        Require(File.ReadAllBytes(other.SavedFilePath!).SequenceEqual(new byte[] { 99 })
            && firstBatch.All(item => File.ReadAllBytes(item.SavedFilePath!).SequenceEqual(new[] { expected[Path.GetFileName(item.SavedFilePath!)] })),
            "別batchで既存ファイルを上書きしない");

        // 新しいoptionalフィールドを知らないpeerをwire境界で模擬する。旧バイナリ実行とは区別する。
        var legacy = pair.SourceFile("legacy/photos/old.txt", new byte[] { 4, 5 });
        var legacyApprovals = 0;
        var legacyAcks = 0;
        pair.Sender.Link.Filter = data =>
        {
            if (data[0] == TransferProtocol.FolderBatchEnd) return null;
            if (data[0] != TransferProtocol.FileMeta) return data;
            var meta = FileChunker.ParseFileMeta(data)!;
            return FileChunker.CreateFileMetaMessage(meta.FileName, meta.FileSize, meta.TotalChunks, meta.Sha256,
                Guid.Parse(meta.TransferId), meta.RelativePath);
        };
        pair.Receiver.Link.Filter = data =>
        {
            if (data[0] == TransferProtocol.FileApprove) { Require(data.Length == 17, "旧形式Approveでtoken echoなし"); Interlocked.Increment(ref legacyApprovals); }
            if (data[0] == TransferProtocol.FileAck) { Require(data.Length == 34, "旧形式ACKのprefix互換"); Interlocked.Increment(ref legacyAcks); }
            return data;
        };
        await pair.SendAsync(Path.GetDirectoryName(legacy)!).WaitAsync(TimeSpan.FromSeconds(10));
        await WaitAsync(() => legacyAcks > 0 && pair.Receiver.Vm.Transfer.Transfers.Count(item => item.State == TransferState.Completed) == 13);
        var legacyItem = pair.Receiver.Vm.Transfer.Transfers.Single(item => item.FileName == "old.txt" || item.FileName.EndsWith("/old.txt", StringComparison.Ordinal));
        Require(legacyApprovals > 0 && File.ReadAllBytes(legacyItem.SavedFilePath!).SequenceEqual(new byte[] { 4, 5 }), "旧形式peerと保存・hash検証まで互換");
        Require(pair.Sender.Vm.Transfer.Transfers.All(item => item.State == TransferState.Completed), "folderと旧形式の全送信が完了");
        Console.WriteLine($"フォルダ統合: gap時Meta={metas}, 保留={heldAtGap}, 同batch保存={firstBatch.Length}, 別batch/旧形式込み={pair.Receiver.Vm.Transfer.Transfers.Count}");
        await pair.CaptureAsync(output, "transfer-folder-batches");
    }

    private static async Task DrainAsync() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static async Task WaitAsync(Func<bool> condition, Func<string>? diagnostic = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { while (!condition()) await Task.Delay(10, timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && diagnostic != null)
        { throw new TimeoutException(diagnostic()); }
        await DrainAsync();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class TransferPair : IAsyncDisposable
    {
        public const string SenderId = "integration-sender";
        public const string ReceiverId = "integration-receiver";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Ferry-Integration-" + Guid.NewGuid().ToString("N"));
        private readonly List<Task> _sends = [];
        public TransferEnd Sender { get; }
        public TransferEnd Receiver { get; }
        public TransferPair(IClassicDesktopStyleApplicationLifetime desktop, TimeSpan? resultTimeout = null)
        {
            Directory.CreateDirectory(_root);
            ServiceChecks.TemporaryDirectories.Enqueue(_root);
            Sender = new TransferEnd(_root, SenderId, ReceiverId, resultTimeout);
            Receiver = new TransferEnd(_root, ReceiverId, SenderId, null);
            Sender.Link.Other = Receiver.Link;
            Receiver.Link.Other = Sender.Link;
            desktop.MainWindow = Sender.Window;
            Sender.Window.Show();
            Receiver.Window.Show();
        }
        public string SourceFile(string relative, byte[] bytes)
        {
            var path = Path.Combine(_root, "source", relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        public Task SendAsync(params string[] paths)
        {
            var send = Sender.Vm.Transfer.SendFilesCommand.ExecuteAsync(paths);
            _sends.Add(send);
            return send;
        }
        public async Task CaptureAsync(string output, string name)
        {
            await DrainAsync();
            foreach (var (side, end) in new[] { ("send", Sender), ("receive", Receiver) })
            {
                Require(end.Window.Bounds.Width > 0 && end.Window.Bounds.Height > 0, "転送画面の実レイアウト");
                using var image = new RenderTargetBitmap(new PixelSize(900, 600), new Vector(96, 96));
                image.Render(end.Window);
                image.Save(Path.Combine(output, name + "-" + side + ".png"), PngBitmapEncoderOptions.Default);
            }
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                // 途中assert失敗でも保留中のコマンドを取り残さず、実サービスの取消経路で終了させる。
                if (_sends.Any(send => !send.IsCompleted))
                    foreach (var item in Sender.Vm.Transfer.Transfers.ToArray())
                        Sender.Vm.Transfer.CancelTransferCommand.Execute(item.TransferId);
                await Task.WhenAll(_sends).WaitAsync(TimeSpan.FromSeconds(5));
                await DrainAsync();
            }
            finally
            {
                Receiver.Dispose();
                Sender.Dispose();
                Directory.Delete(_root, true);
            }
        }
    }

    private sealed class TransferEnd : IDisposable
    {
        public DuplexConnection Link { get; }
        public TransferService Service { get; }
        public MainWindowViewModel Vm { get; }
        public VerificationWindow Window { get; }
        public string SaveDirectory { get; }
        private readonly PeerRegistryService _registry;
        public TransferEnd(string root, string id, string remote, TimeSpan? resultTimeout)
        {
            SaveDirectory = Path.Combine(root, id);
            Directory.CreateDirectory(SaveDirectory);
            var settings = new MemorySettings(new AppSettings
            {
                SaveDirectory = SaveDirectory, AutoAcceptFileTransfer = false, EnableNotificationSound = false,
                WindowWidth = 900, WindowHeight = 600, SidebarWidth = 250,
            });
            Link = new DuplexConnection(id, remote);
            _registry = new PeerRegistryService(Path.Combine(SaveDirectory, "unused-peers.json"));
            var connectionVm = new ConnectionViewModel(Link, null!, settings, _registry, null!);
            var peer = new PairedPeer { PeerId = remote, DisplayName = remote };
            connectionVm.PairedPeers.Add(peer);
            connectionVm.SelectedPeer = peer;
            Service = resultTimeout.HasValue
                ? (TransferService)typeof(TransferService).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(IConnectionService), typeof(ISettingsService), typeof(TimeSpan?) }, null)!.Invoke(new object?[] { Link, settings, resultTimeout })
                : new TransferService(Link, settings);
            var transferVm = new TransferViewModel(Link, Service, connectionVm, settings);
            Vm = new MainWindowViewModel(connectionVm, transferVm, new SettingsViewModel(settings));
            // 実承認イベント→Dispatcher→実承認コマンドを通す（AutoAcceptは使わない）。
            Service.ApprovalRequested += (_, item) => Dispatcher.UIThread.Post(() => transferVm.ApproveTransferCommand.Execute(item.TransferId));
            Window = new VerificationWindow { DataContext = Vm, Width = 900, Height = 600 };
            Window.SetSettingsService(settings);
        }
        public bool HasStateText(string key) => HasText(App.Text(key));
        public bool HasText(string value) => Window.GetVisualDescendants().OfType<TextBlock>()
            .Any(text => text.IsEffectivelyVisible && text.Text == value);
        public string DescribeDisplay() => "モデル: " + string.Join(" | ", Vm.Transfer.Transfers.Select(item => $"{item.State}: {item.StateText}"))
            + " / 表示: " + string.Join(" | ", Window.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible && text.DataContext is TransferItem).Select(text => text.Text));
        public void Dispose()
        {
            Window.AllowClose = true;
            Window.Close();
            Vm.Dispose();
            Service.Dispose();
            _registry.Dispose();
        }
    }

    private sealed class DuplexConnection(string localId, string remoteId) : IConnectionService
    {
        public DuplexConnection Other { get; set; } = null!;
        public Func<byte[], byte[]?>? Filter { get; set; }
        public PeerState State => PeerState.Connected;
        public PeerInfo ConnectedPeer { get; } = new() { SessionId = remoteId, DisplayName = remoteId, State = PeerState.Connected };
        public IReadOnlyDictionary<string, PeerInfo> ConnectedPeers => new Dictionary<string, PeerInfo> { [remoteId] = ConnectedPeer };
        public ConnectionRoute Route => ConnectionRoute.Direct;
        public event EventHandler<DataReceivedEventArgs>? DataReceived;
        public event EventHandler<PeerState>? StateChanged { add { } remove { } }
        public event EventHandler<ConnectionRoute>? RouteChanged { add { } remove { } }
        public event EventHandler<PairedPeer>? PairingCompleted { add { } remove { } }
        public event EventHandler<ConnectionLostEventArgs>? ConnectionLost { add { } remove { } }
        public event EventHandler<string>? StatusMessageChanged { add { } remove { } }
        public event EventHandler<string>? RemoteUnpairDetected { add { } remove { } }
        public void Inject(string peer, byte[] data) => DataReceived?.Invoke(this, new(peer, data));
        public Task SendAsync(byte[] data, CancellationToken ct = default) => SendAsync(remoteId, data, ct);
        public Task SendAsync(string peerId, byte[] data, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (peerId != remoteId) throw new InvalidOperationException("fixtureの宛先取り違え");
            var copy = data.ToArray();
            var filtered = Filter == null ? copy : Filter(copy);
            if (filtered != null) Other.Inject(localId, filtered);
            return Task.CompletedTask;
        }
        public Task<string> StartPairingSessionAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task CancelPairingAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ConnectToPeerAsync(string peerId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(string peerId, CancellationToken ct = default) => Task.CompletedTask;
        public void StartListeningForConnection(string peerId) { }
        public void StopListeningForConnection() { }
        public void StopListeningForConnection(string peerId) { }
    }
}
