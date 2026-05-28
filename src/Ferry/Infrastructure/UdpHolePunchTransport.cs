using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Infrastructure;

/// <summary>
/// UDP ホールパンチングによる NAT 越え P2P トランスポート。
/// STUN で外部エンドポイントを取得 → 両側から同時に UDP パケットを送信して NAT に穴を開ける。
/// 信頼性レイヤー（選択的 ACK + フラグメンテーション）で TCP 相当のメッセージ配信を保証する。
///
/// パケットフォーマット:
///   共通ヘッダー: [type: 1byte][seq: 4bytes]
///   DATA 追加:    [msgId: 4bytes][fragIdx: 2bytes][fragCount: 2bytes][data: Nbytes]
///   ACK:          ヘッダーの seq = ACK 対象シーケンス番号
///   PUNCH/PUNCH_ACK: ヘッダーのみ（ペイロードなし）
/// </summary>
public sealed class UdpHolePunchTransport : ITransport
{
    // === パケットフォーマット定数 ===
    private const int PacketHeaderSize = 5;     // type(1) + seq(4)
    private const int FragmentHeaderSize = 8;   // msgId(4) + fragIdx(2) + fragCount(2)
    private const int MaxUdpPayload = 1200;     // MTU 安全マージン
    private const int MaxFragmentData = MaxUdpPayload - PacketHeaderSize - FragmentHeaderSize; // 1187 bytes

    // === プロトコル定数 ===
    private const byte PktPunch = 0x01;
    private const byte PktPunchAck = 0x02;
    private const byte PktData = 0x03;
    private const byte PktAck = 0x04;

    // === 信頼性パラメータ ===
    private const int WindowSize = 128;         // 同時送信可能パケット数
    private const int RetransmitIntervalMs = 100;
    private const int RetransmitTimeoutMs = 300;
    private const int MaxFragments = 16384;     // 1メッセージ最大フラグメント数（攻撃者制御 fragCount による OOM 防止。約19MB相当）

    // === ソケット ===
    private readonly UdpClient _udp;
    private IPEndPoint? _remoteEp;
    private CancellationTokenSource? _loopCts;

    // === 送信状態 ===
    private uint _nextSeq;
    private uint _nextMsgId;
    private readonly SemaphoreSlim _windowSem = new(WindowSize, WindowSize);
    private readonly ConcurrentDictionary<uint, SentPacketInfo> _sentPackets = new();
    private readonly ConcurrentDictionary<uint, PendingMessage> _pendingMessages = new();

    // === 受信状態 ===
    // 実際の受信ロックは ReceivingMessage 単位の `lock (msg)` で行う（HandleData 内）
    private readonly ConcurrentDictionary<uint, ReceivingMessage> _receivingMessages = new();

    public bool IsConnected { get; private set; }
    public ConnectionRoute Route => ConnectionRoute.StunAssisted;

    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler? ChannelOpened;
    public event EventHandler? ChannelClosed;
    public event EventHandler<ConnectionRoute>? RouteChanged;

    public UdpHolePunchTransport()
    {
        _udp = new UdpClient(0, AddressFamily.InterNetwork); // IPv4 で OS にポートを自動割り当て
        _udp.Client.ReceiveBufferSize = 256 * 1024;
        _udp.Client.SendBufferSize = 256 * 1024;
    }

    /// <summary>
    /// 複数の STUN サーバーに順次問い合わせて外部 IP:port を取得する。
    /// 1つのサーバーが応答しなければ次のサーバーにフォールバックする。
    /// このソケットと同じ NAT マッピングを使うため、結果はそのままホールパンチに利用可能。
    /// 先頭は Cloudflare 公開 STUN (Tokyo PoP 近接、Anycast)。Cloudflare 障害時の従として Google 公開 STUN を残す。
    /// 自前 coturn (旧 1llum1n4t1.net VPS) は Cloudflare 移行に伴い撤去済み。
    /// </summary>
    private static readonly (string host, int port)[] StunServers =
    [
        ("stun.cloudflare.com", 3478),
        ("stun.l.google.com", 19302),
    ];

    public async Task<(string ip, int port)?> GetExternalEndpointAsync(CancellationToken ct = default)
    {
        foreach (var (host, port) in StunServers)
        {
            try
            {
                var result = await StunClient.GetExternalEndpointAsync(_udp, host, port, ct);
                if (result != null)
                {
                    Util.Logger.Log($"STUN 成功: {host}:{port} → {Util.Logger.MaskIp(result.Value.ip)}:{result.Value.port}");
                    return result;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // 外部キャンセルはそのまま伝播
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"STUN 失敗: {host}:{port} → {ex.Message}", Util.LogLevel.Warning);
            }
        }

        Util.Logger.Log("全 STUN サーバーに接続失敗", Util.LogLevel.Warning);
        return null;
    }

    /// <summary>
    /// UDP ホールパンチングを実行して相手との接続を確立する。
    /// 両側が同時にこのメソッドを呼ぶことで NAT に穴を開ける。
    /// </summary>
    /// <param name="remoteIp">相手の外部 IP。</param>
    /// <param name="remotePort">相手の外部ポート。</param>
    /// <param name="ct">キャンセルトークン（タイムアウト制御用）。</param>
    public async Task HolePunchAsync(string remoteIp, int remotePort, CancellationToken ct = default)
    {
        _remoteEp = new IPEndPoint(IPAddress.Parse(remoteIp), remotePort);
        Util.Logger.Log($"UDP ホールパンチ開始: {Util.Logger.MaskIp(remoteIp)}:{remotePort}");

        // 受信ループを先に起動（PUNCH_ACK を受け取るため）
        StartBackgroundLoops();

        // PUNCH パケットを 200ms 間隔で送信し続ける
        var punchPacket = MakeHeaderOnlyPacket(PktPunch, 0);

        while (!ct.IsCancellationRequested && !IsConnected)
        {
            try
            {
                await _udp.SendAsync(punchPacket, punchPacket.Length, _remoteEp);
            }
            catch (SocketException ex)
            {
                Util.Logger.Log($"PUNCH 送信エラー: {ex.Message}", Util.LogLevel.Warning);
            }

            try
            {
                await Task.Delay(200, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (!IsConnected)
            throw new OperationCanceledException("UDP ホールパンチ失敗");

        Util.Logger.Log("UDP ホールパンチ成功！");
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
        => SendAsync(data.AsMemory(), ct);

    /// <summary>
    /// P-1: ArrayPool 借用バッファを受け取り、MTU フラグメント化して送信する。
    /// 信頼性レイヤー（再送用）でフラグメント byte[] は保持が必要なため、
    /// フラグメント化時点で再送用配列にコピーする（入力バッファは即解放可能）。
    /// </summary>
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (!IsConnected || _remoteEp == null)
            throw new InvalidOperationException("接続されていません");

        var msgId = Interlocked.Increment(ref _nextMsgId);
        var fragments = FragmentMessage(data.Span, (uint)msgId);
        var pending = new PendingMessage(fragments.Length);
        _pendingMessages[(uint)msgId] = pending;

        // 各フラグメントをウィンドウ制御付きで送信
        foreach (var frag in fragments)
        {
            await _windowSem.WaitAsync(ct);

            var seq = (uint)Interlocked.Increment(ref _nextSeq);
            // シーケンス番号をパケットに書き込み（Slice で範囲を明示）
            BinaryPrimitives.WriteUInt32BigEndian(frag.AsSpan(1, 4), seq);

            var info = new SentPacketInfo(seq, (uint)msgId, frag);
            _sentPackets[seq] = info;

            try
            {
                await _udp.SendAsync(frag, frag.Length, _remoteEp);
            }
            catch (SocketException ex)
            {
                Util.Logger.Log($"UDP 送信エラー: {ex.Message}", Util.LogLevel.Warning);
            }
        }

        // 全フラグメントの ACK 完了を待つ
        await pending.Completion.Task.WaitAsync(ct);
    }

    public void Close()
    {
        if (!IsConnected && _loopCts == null) return;

        Util.Logger.Log("UDP 接続クローズ");
        IsConnected = false;

        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;

        try { _udp.Close(); } catch { }

        // 未完成メッセージ・未 ACK パケットを解放（切断時のメモリ滞留を防止）
        _receivingMessages.Clear();
        _sentPackets.Clear();
        _pendingMessages.Clear();
    }

    public void Dispose()
    {
        var wasConnected = IsConnected;
        Close();
        _udp.Dispose();
        _windowSem.Dispose();
        if (wasConnected)
            ChannelClosed?.Invoke(this, EventArgs.Empty);
    }

    // === バックグラウンドループ ===

    private void StartBackgroundLoops()
    {
        if (_loopCts != null) return;
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;

        _ = Task.Run(() => ReceiveLoopAsync(ct), ct);
        _ = Task.Run(() => RetransmitLoopAsync(ct), ct);
    }

    /// <summary>受信ループ: UDP パケットを読み取ってタイプ別にディスパッチする。</summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udp.ReceiveAsync(ct);
                }
                catch (SocketException)
                {
                    break;
                }

                var packet = result.Buffer;
                if (packet.Length < PacketHeaderSize) continue;

                var type = packet[0];

                switch (type)
                {
                    case PktPunch:
                        HandlePunch(result.RemoteEndPoint);
                        break;
                    case PktPunchAck:
                        HandlePunchAck(result.RemoteEndPoint);
                        break;
                    case PktData:
                        HandleData(packet);
                        break;
                    case PktAck:
                        HandleAck(packet);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Util.Logger.Log($"UDP 受信ループエラー: {ex.Message}", Util.LogLevel.Warning);
        }
        finally
        {
            if (IsConnected)
            {
                IsConnected = false;
                ChannelClosed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// 再送ループ: 未 ACK パケットを定期的に再送する。
    /// P-5: <see cref="Task.Delay(int, CancellationToken)"/> を <see cref="PeriodicTimer"/> に置換し、
    /// ループあたりの Task allocation を撲滅。再送タイムアウト超過のパケットを 1 周期で一括スキャンする。
    /// （RTT 推定は導入せず固定値のまま。動作中の信頼性レイヤーへの侵襲を最小化）
    /// </summary>
    private async Task RetransmitLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(RetransmitIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_remoteEp == null) continue;

                var now = Environment.TickCount64;
                foreach (var kvp in _sentPackets)
                {
                    var info = kvp.Value;
                    if (now - info.SentAtTick > RetransmitTimeoutMs)
                    {
                        info.SentAtTick = now;
                        try
                        {
                            await _udp.SendAsync(info.Packet, info.Packet.Length, _remoteEp);
                        }
                        catch (OperationCanceledException)
                        {
                            // 全体キャンセル → ループ全体を終了
                            return;
                        }
                        catch
                        {
                            // rere レビュー #C1-001: 旧 `break` だと一時的な ENETUNREACH /
                            // EHOSTUNREACH / ObjectDisposedException で再送タイマー全停止 →
                            // _windowSem.WaitAsync が永久 hang。個別失敗は次のパケットへ continue する
                            continue;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    // === パケットハンドラ ===

    private void HandlePunch(IPEndPoint from)
    {
        // 相手の PUNCH を受信 → PUNCH_ACK を返す
        _remoteEp = from;
        SendHeaderOnlyPacketFireAndForget(PktPunchAck, 0, from);

        if (!IsConnected)
        {
            Util.Logger.Log($"UDP PUNCH 受信: {from} → PUNCH_ACK 返送");
            SetConnected();
        }
    }

    private void HandlePunchAck(IPEndPoint from)
    {
        _remoteEp = from;
        if (!IsConnected)
        {
            Util.Logger.Log($"UDP PUNCH_ACK 受信: {from} → 接続確立");
            SetConnected();
        }
    }

    private void HandleData(byte[] packet)
    {
        if (packet.Length < PacketHeaderSize + FragmentHeaderSize) return;

        // P-4: BinaryPrimitives.Read* を 1 度の AsSpan() からスライスして読む
        var packetSpan = packet.AsSpan();
        var seq = BinaryPrimitives.ReadUInt32BigEndian(packetSpan.Slice(1, 4));
        var msgId = BinaryPrimitives.ReadUInt32BigEndian(packetSpan.Slice(5, 4));
        var fragIdx = BinaryPrimitives.ReadUInt16BigEndian(packetSpan.Slice(9, 2));
        var fragCount = BinaryPrimitives.ReadUInt16BigEndian(packetSpan.Slice(11, 2));

        // 攻撃者制御の生値を弾く（OOM・配列外アクセス防止）
        if (fragCount == 0 || fragCount > MaxFragments || fragIdx >= fragCount) return;

        // P-4: ACK は 5 バイトの固定長ヘッダのみ。byte[] を新規確保せず、fire-and-forget の
        // 内部で ArrayPool 借用 → SendAsync(ReadOnlyMemory) → Return で alloc を 0 に近づける。
        if (_remoteEp != null)
            SendHeaderOnlyPacketFireAndForget(PktAck, seq, _remoteEp);

        // フラグメントデータを抽出
        var dataOffset = PacketHeaderSize + FragmentHeaderSize;
        var fragData = packetSpan.Slice(dataOffset).ToArray();

        // メッセージ再構築
        var msg = _receivingMessages.GetOrAdd(msgId, _ => new ReceivingMessage(fragCount));

        lock (msg)
        {
            // 確保済み配列長で再検証（同一 msgId で異なる fragCount を送る攻撃を防ぐ）
            if (fragIdx >= msg.Received.Length) return;
            if (msg.Received[fragIdx]) return; // 重複パケット

            msg.Fragments[fragIdx] = fragData;
            msg.Received[fragIdx] = true;
            msg.ReceivedCount++;

            if (msg.ReceivedCount != msg.TotalFragments) return;
        }

        // 全フラグメント受信完了 → メッセージを再構築して配信
        _receivingMessages.TryRemove(msgId, out _);

        // 1パスでサイズ計算とコピーを同時実行
        var totalSize = 0;
        for (var i = 0; i < msg.TotalFragments; i++)
            totalSize += msg.Fragments[i].Length;

        var fullData = new byte[totalSize];
        var offset = 0;
        for (var i = 0; i < msg.TotalFragments; i++)
        {
            msg.Fragments[i].CopyTo(fullData.AsSpan(offset));
            offset += msg.Fragments[i].Length;
        }

        DataReceived?.Invoke(this, fullData);
    }

    private void HandleAck(byte[] packet)
    {
        if (packet.Length < PacketHeaderSize) return;
        var ackedSeq = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(1, 4));

        if (!_sentPackets.TryRemove(ackedSeq, out var info)) return;

        // ウィンドウスロットを解放
        try { _windowSem.Release(); } catch (SemaphoreFullException) { }

        // メッセージの全フラグメント ACK チェック
        if (_pendingMessages.TryGetValue(info.MsgId, out var pending))
        {
            if (Interlocked.Increment(ref pending.AckedCount) == pending.TotalFragments)
            {
                pending.Completion.TrySetResult();
                _pendingMessages.TryRemove(info.MsgId, out _);
            }
        }
    }

    private void SetConnected()
    {
        IsConnected = true;
        ChannelOpened?.Invoke(this, EventArgs.Empty);
        RouteChanged?.Invoke(this, ConnectionRoute.StunAssisted);
    }

    // === フラグメンテーション ===

    /// <summary>メッセージをフラグメントに分割し、送信可能なパケットを生成する。
    /// P-1: 入力を <see cref="ReadOnlySpan{T}"/> に変更し、ArrayPool 借用バッファをコピーなしで読めるようにした。
    /// 生成するパケット byte[] は再送ループで保持する必要があるためそのまま byte[] のまま。</summary>
    private static byte[][] FragmentMessage(ReadOnlySpan<byte> data, uint msgId)
    {
        var fragCount = (data.Length + MaxFragmentData - 1) / MaxFragmentData;
        if (fragCount == 0) fragCount = 1; // 空メッセージでも1フラグメント

        var packets = new byte[fragCount][];

        for (var i = 0; i < fragCount; i++)
        {
            var fragOffset = i * MaxFragmentData;
            var fragLen = Math.Min(MaxFragmentData, data.Length - fragOffset);

            var packet = new byte[PacketHeaderSize + FragmentHeaderSize + fragLen];
            var packetSpan = packet.AsSpan();
            packetSpan[0] = PktData;
            // seq は送信時に書き込む（ここでは 0）
            BinaryPrimitives.WriteUInt32BigEndian(packetSpan.Slice(5, 4), msgId);
            BinaryPrimitives.WriteUInt16BigEndian(packetSpan.Slice(9, 2), (ushort)i);
            BinaryPrimitives.WriteUInt16BigEndian(packetSpan.Slice(11, 2), (ushort)fragCount);
            data.Slice(fragOffset, fragLen).CopyTo(packetSpan.Slice(PacketHeaderSize + FragmentHeaderSize));

            packets[i] = packet;
        }

        return packets;
    }

    // === バイナリヘルパー ===

    /// <summary>
    /// ヘッダーのみのパケット（PUNCH / PUNCH_ACK 用、5 byte）を生成する。
    /// HolePunchAsync ループで使い回されるため byte[] のまま保持できる用途向け。
    /// </summary>
    private static byte[] MakeHeaderOnlyPacket(byte type, uint seq)
    {
        var packet = new byte[PacketHeaderSize];
        packet[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1, 4), seq);
        return packet;
    }

    /// <summary>
    /// P-4: ACK / PUNCH_ACK 等の 5 バイト固定パケットを ArrayPool 借用バッファで送信する。
    /// 受信ループのホットパスで <c>new byte[5]</c> を毎回 alloc するのを避けるため、
    /// 借用 → <see cref="UdpClient.SendAsync(ReadOnlyMemory{byte}, IPEndPoint, CancellationToken)"/>
    /// → 完了後 Return の fire-and-forget で発射する。
    /// 失敗時のログは握りつぶす（ACK は冪等で再送ループ側がリカバリするため）。
    /// </summary>
    private void SendHeaderOnlyPacketFireAndForget(byte type, uint seq, IPEndPoint to)
    {
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(PacketHeaderSize);
        rented[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(rented.AsSpan(1, 4), seq);

        // Rent は要求以上の長さを返すため、明示的に PacketHeaderSize 分だけスライスして渡す。
        _ = SendAndReturnAsync(_udp, new ReadOnlyMemory<byte>(rented, 0, PacketHeaderSize), to, rented);

        static async Task SendAndReturnAsync(UdpClient udp, ReadOnlyMemory<byte> payload, IPEndPoint to, byte[] rented)
        {
            try
            {
                // .NET 6+ の ReadOnlyMemory オーバーロード。借用バッファを直接渡せる。
                await udp.SendAsync(payload, to);
            }
            catch
            {
                // ACK 送信失敗は呼び出し側で握りつぶす（再送ループでリカバリ）
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    // === 内部クラス ===

    private sealed class SentPacketInfo
    {
        public readonly uint Seq;
        public readonly uint MsgId;
        public readonly byte[] Packet;
        public long SentAtTick;

        public SentPacketInfo(uint seq, uint msgId, byte[] packet)
        {
            Seq = seq;
            MsgId = msgId;
            Packet = packet;
            SentAtTick = Environment.TickCount64;
        }
    }

    private sealed class PendingMessage
    {
        public readonly int TotalFragments;
        public int AckedCount;
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingMessage(int totalFragments) => TotalFragments = totalFragments;
    }

    private sealed class ReceivingMessage
    {
        public readonly int TotalFragments;
        public readonly byte[][] Fragments;
        public readonly bool[] Received;
        public int ReceivedCount;

        public ReceivingMessage(int totalFragments)
        {
            TotalFragments = totalFragments;
            Fragments = new byte[totalFragments][];
            Received = new bool[totalFragments];
        }
    }
}
