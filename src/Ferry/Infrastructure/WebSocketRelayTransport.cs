using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Infrastructure;

/// <summary>
/// WebSocket リレーサーバー経由の NAT 越えトランスポート。
/// 同一 LAN でない場合（TCP 直接接続失敗時）のフォールバック。
///
/// プロトコル:
///   1. WebSocket 接続時に pairId を送信してルームに参加
///   2. リレーサーバーが同じ pairId の2クライアント間でメッセージを中継
///   3. メッセージはバイナリフレームでそのまま転送
/// </summary>
public sealed class WebSocketRelayTransport : ITransport
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _receiveCts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly string _relayUrl;
    private readonly string _pairId;
    private readonly string _role; // "offer" or "answer"

    /// <summary>リレールームで相手の参加 ("ready") を待つ絶対上限（秒）= 防御的バックストップ。
    /// 実際の「相手待ち」ポリシー値は呼び出し側 <see cref="Ferry.Services.ConnectionService"/> の
    /// RelayPeerWaitSeconds(15s) が握り、その ct が CreateLinkedTokenSource 連鎖で WaitForReadyAsync に
    /// 伝播する。よって実効待ち = min(この値, 呼び出し側 ct)。この定数は呼び出し側が ct を bound し忘れた
    /// 場合に無限待機へ退行させないための上限なので、ポリシー値より大きい 30s を維持する（裸の 30 で
    /// 「リレー待ち=15s」の意図と齟齬を起こさないよう、関係を明記して名前付き定数化した）。</summary>
    private const int ReadyWaitTimeoutSeconds = 30;

    public bool IsConnected { get; private set; }
    public ConnectionRoute Route => ConnectionRoute.Relay;

    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler? ChannelOpened;
    public event EventHandler? ChannelClosed;
    public event EventHandler<ConnectionRoute>? RouteChanged;

    /// <summary>
    /// WebSocket リレートランスポートを初期化する。
    /// </summary>
    /// <param name="relayUrl">リレーサーバーの WebSocket URL (wss://...)。</param>
    /// <param name="pairId">ペアリング ID（ルームキー）。</param>
    /// <param name="role">役割（"offer" または "answer"）。</param>
    public WebSocketRelayTransport(string relayUrl, string pairId, string role)
    {
        _relayUrl = relayUrl;
        _pairId = pairId;
        _role = role;
    }

    /// <summary>
    /// リレーサーバーに接続し、ルームに参加する。
    /// 相手の接続を待ち、接続確立後に ChannelOpened を発火する。
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Util.Logger.Log($"WebSocket リレー接続開始: {_relayUrl}, pairId={_pairId}, role={_role}");

        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        // リレーサーバーに接続（URL にルーム情報を含める）
        var uri = new Uri($"{_relayUrl}?pairId={Uri.EscapeDataString(_pairId)}&role={Uri.EscapeDataString(_role)}");

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(15));
        await _ws.ConnectAsync(uri, connectCts.Token);

        Util.Logger.Log("WebSocket リレー接続成功");

        // リレーサーバーからの "ready" メッセージを待つ（両者が揃った通知）
        await WaitForReadyAsync(ct);

        IsConnected = true;
        StartReceiveLoop();
        ChannelOpened?.Invoke(this, EventArgs.Empty);
        RouteChanged?.Invoke(this, ConnectionRoute.Relay);

        Util.Logger.Log("WebSocket リレー: 相手と接続確立");
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
        => SendAsync(data.AsMemory(), ct);

    /// <summary>
    /// P-1: ArrayPool 借用バッファをコピーなしで受け取れる ReadOnlyMemory 版。
    /// <see cref="ClientWebSocket.SendAsync(ReadOnlyMemory{byte}, WebSocketMessageType, bool, CancellationToken)"/>
    /// に直接渡すため、ペイロード追加コピーは発生しない。
    /// </summary>
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_ws == null || _ws.State != WebSocketState.Open || !IsConnected)
            throw new InvalidOperationException("リレー接続されていません");

        // ClientWebSocket は同時 SendAsync で InvalidOperationException を投げるため直列化
        await _sendLock.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(data, WebSocketMessageType.Binary, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Close()
    {
        if (_ws == null)
            return;

        Util.Logger.Log("WebSocket リレー接続クローズ");
        IsConnected = false;

        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _receiveCts = null;

        // fire-and-forget で非同期クローズ（sync-over-async のデッドロックを回避）
        if (_ws.State == WebSocketState.Open)
        {
            _ = _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "切断", CancellationToken.None)
                .ContinueWith(_ => { }, TaskScheduler.Default);
        }

        _ws.Dispose();
        _ws = null;
    }

    public void Dispose()
    {
        var wasConnected = IsConnected;
        Close();
        if (wasConnected)
            ChannelClosed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// リレーサーバーから "ready" テキストメッセージを待つ。
    /// 両方のクライアントがルームに参加したことを示す。
    /// </summary>
    private async Task WaitForReadyAsync(CancellationToken ct)
    {
        Util.Logger.Log("WebSocket リレー: 相手の接続待機中…");

        var buffer = new byte[1024];
        using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // 実効待ちは呼び出し側 ct (ConnectionService.RelayPeerWaitSeconds=15s) が先に握る。この CancelAfter は
        // ct が無 bound の場合に無限待機へ退行させないための絶対バックストップ (詳細は定数の doc 参照)。
        readyCts.CancelAfter(TimeSpan.FromSeconds(ReadyWaitTimeoutSeconds));

        while (!readyCts.IsCancellationRequested)
        {
            var result = await _ws!.ReceiveAsync(buffer, readyCts.Token);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                if (text == "ready")
                {
                    Util.Logger.Log("WebSocket リレー: ready 受信");
                    return;
                }
            }
            else if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("リレーサーバーが接続を閉じました");
            }
        }

        throw new OperationCanceledException("リレー相手の接続待機がタイムアウト");
    }

    /// <summary>
    /// バイナリメッセージの受信ループ。
    /// P-7: <see cref="WebSocketReceiveResult.EndOfMessage"/> を確認し、64KB を超えるメッセージが
    /// 複数フレームに分割された場合も <see cref="System.IO.MemoryStream"/> に集約してから配信する。
    /// 集約サイズは <see cref="MaxAggregatedMessageSize"/> で上限を設け、超過時はストリームを破棄して
    /// 警告を出すのみで接続自体は維持する（後続メッセージは引き続き処理される）。
    /// </summary>
    private void StartReceiveLoop()
    {
        _receiveCts = new CancellationTokenSource();
        var ct = _receiveCts.Token;

        _ = Task.Run(() => ReceiveLoopAsync(ct), ct);
    }

    /// <summary>受信集約バッファのサイズ上限。LengthPrefixedStream の MaxMessageSize と揃えてある。</summary>
    private const int MaxAggregatedMessageSize = 16 * 1024 * 1024;

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        // 64KB の固定受信バッファ。1 フレーム分の読み取りに使い回す。
        var frameBuffer = new byte[64 * 1024];
        System.IO.MemoryStream? aggregate = null;
        var overflowDropping = false; // サイズ超過後、EndOfMessage まで読み捨てるためのフラグ

        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(frameBuffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Util.Logger.Log("WebSocket リレー: 相手が切断");
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Binary)
                    continue;

                // P-7: 単一フレーム完結 & 集約バッファ未使用 & 直近の超過破棄もない → ホットパス
                if (result.EndOfMessage && aggregate is null && !overflowDropping)
                {
                    if (result.Count > 0)
                    {
                        // M-4: Buffer.BlockCopy (レガシー API) を Span.ToArray に統一
                        var data = frameBuffer.AsSpan(0, result.Count).ToArray();
                        DataReceived?.Invoke(this, data);
                    }
                    continue;
                }

                // 超過破棄モード: EndOfMessage まで読み飛ばし、後続メッセージで通常モードに復帰
                if (overflowDropping)
                {
                    if (result.EndOfMessage)
                        overflowDropping = false;
                    continue;
                }

                // フラグメント集約モード
                aggregate ??= new System.IO.MemoryStream(result.Count);

                if (aggregate.Length + result.Count > MaxAggregatedMessageSize)
                {
                    Util.Logger.Log(
                        $"WebSocket メッセージサイズ超過: {aggregate.Length + result.Count} > {MaxAggregatedMessageSize}, 破棄",
                        Util.LogLevel.Warning);
                    aggregate.Dispose();
                    aggregate = null;
                    // 残りのフラグメントは EndOfMessage まで読み飛ばす
                    overflowDropping = !result.EndOfMessage;
                    continue;
                }

                aggregate.Write(frameBuffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    var data = aggregate.ToArray();
                    aggregate.Dispose();
                    aggregate = null;
                    DataReceived?.Invoke(this, data);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常なキャンセル
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"WebSocket リレー受信エラー: {ex.Message}", Util.LogLevel.Warning);
        }
        finally
        {
            aggregate?.Dispose();
            if (IsConnected)
            {
                IsConnected = false;
                ChannelClosed?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
