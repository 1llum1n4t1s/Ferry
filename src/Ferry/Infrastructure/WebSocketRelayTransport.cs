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
    private readonly string _relayUrl;
    private readonly string _pairId;
    private readonly string _role; // "offer" or "answer"

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

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (_ws == null || _ws.State != WebSocketState.Open || !IsConnected)
            throw new InvalidOperationException("リレー接続されていません");

        await _ws.SendAsync(data, WebSocketMessageType.Binary, true, ct);
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

        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "切断", CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
        }
        catch
        {
            // クローズ時のエラーは無視
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
        readyCts.CancelAfter(TimeSpan.FromSeconds(30));

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
    /// </summary>
    private void StartReceiveLoop()
    {
        _receiveCts = new CancellationTokenSource();
        var ct = _receiveCts.Token;

        _ = Task.Run(async () =>
        {
            // WebSocket フレームの最大サイズ（チャンクサイズ + ヘッダー分の余裕）
            var buffer = new byte[64 * 1024];

            try
            {
                while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
                {
                    var result = await _ws.ReceiveAsync(buffer, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Util.Logger.Log("WebSocket リレー: 相手が切断");
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Binary && result.Count > 0)
                    {
                        var data = new byte[result.Count];
                        Buffer.BlockCopy(buffer, 0, data, 0, result.Count);
                        DataReceived?.Invoke(this, data);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常なキャンセル
            }
            catch (WebSocketException ex)
            {
                Util.Logger.Log($"WebSocket リレー受信エラー: {ex.Message}", Util.LogLevel.Warning);
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"WebSocket リレー受信エラー: {ex.Message}", Util.LogLevel.Warning);
            }
            finally
            {
                if (IsConnected)
                {
                    IsConnected = false;
                    ChannelClosed?.Invoke(this, EventArgs.Empty);
                }
            }
        }, ct);
    }
}
