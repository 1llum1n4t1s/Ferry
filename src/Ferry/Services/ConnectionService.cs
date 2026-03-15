using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Infrastructure;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// Firebase シグナリング + TCP 直接接続 / WebSocket リレーによる接続サービス。
/// ペアリング（Bridge ページ経由の自動マッチング）とオンデマンド接続を管理する。
///
/// 接続モデル:
///   - ファイル送信側（Offer 側）が ConnectToPeerAsync を呼び、TCP リスナーを起動して接続情報を送信
///   - 受信側（Answer 側）は StartListeningForConnection で接続情報を監視し、TCP 接続を確立
///   - TCP 直接接続失敗時は WebSocket リレーにフォールバック
/// </summary>
public sealed class ConnectionService : IConnectionService, IDisposable
{
    /// <summary>TCP 直接接続のタイムアウト（秒）。</summary>
    private const int TcpConnectTimeoutSeconds = 5;

    private readonly string _databaseUrl;
    private readonly string _deviceId;
    private readonly string _displayName;
    private FirebaseSignaling? _signaling;
    private ITransport? _transport;
    private string? _currentPairId;
    private CancellationTokenSource? _listeningCts;

    /// <summary>WebSocket リレーサーバーの URL。null の場合はリレーなし（TCP 直接のみ）。</summary>
    public string? RelayUrl { get; set; }

    public PeerState State { get; private set; } = PeerState.Disconnected;
    public PeerInfo? ConnectedPeer { get; private set; }
    public ConnectionRoute Route { get; private set; } = ConnectionRoute.Unknown;

    public event EventHandler<PeerState>? StateChanged;
    public event EventHandler<ConnectionRoute>? RouteChanged;
    public event EventHandler<PairedPeer>? PairingCompleted;
    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler? ConnectionLost;

    public ConnectionService(string databaseUrl, string deviceId, string displayName)
    {
        _databaseUrl = databaseUrl;
        _deviceId = deviceId;
        _displayName = displayName;
    }

    // === ペアリング ===

    public async Task<string> StartPairingSessionAsync(CancellationToken ct = default)
    {
        _signaling?.Dispose();
        _signaling = new FirebaseSignaling(_databaseUrl);

        var sessionId = await _signaling.RegisterSessionAsync(_deviceId, _displayName, ct);

        _signaling.PairingDetected += OnPairingDetected;
        _signaling.StartWatchingPairing();

        SetState(PeerState.WaitingForPairing);
        return sessionId;
    }

    public async Task CancelPairingAsync(CancellationToken ct = default)
    {
        if (_signaling != null)
        {
            _signaling.PairingDetected -= OnPairingDetected;
            await _signaling.CleanupAsync(ct: ct);
            _signaling.Dispose();
            _signaling = null;
        }
        SetState(PeerState.Disconnected);
    }

    private async void OnPairingDetected(object? sender, PairingInfo info)
    {
        if (_signaling == null) return;

        Util.Logger.Log($"ペアリング検知: peer={info.PeerDisplayName}");

        _signaling.PairingDetected -= OnPairingDetected;
        _signaling.StopWatching();

        SetState(PeerState.WaitingForMatch);

        var peer = new PairedPeer
        {
            PeerId = info.PeerId,
            DisplayName = info.PeerDisplayName,
        };
        PairingCompleted?.Invoke(this, peer);

        await _signaling.CleanupAsync(info.PairingId, ct: default);
    }

    // === 着信接続監視 ===

    public void StartListeningForConnection(string peerId)
    {
        StopListeningForConnection();
        _listeningCts = new CancellationTokenSource();
        Util.Logger.Log($"着信接続監視開始: peer={peerId}");
        _ = ListenForIncomingConnectionAsync(peerId, _listeningCts.Token);
    }

    public void StopListeningForConnection()
    {
        if (_listeningCts != null)
        {
            Util.Logger.Log("着信接続監視停止");
            _listeningCts.Cancel();
            _listeningCts.Dispose();
            _listeningCts = null;
        }
    }

    /// <summary>
    /// バックグラウンドで Offer（接続情報）をポーリングし、
    /// 検知したら TCP 接続 / WebSocket リレー接続を確立する。
    /// </summary>
    private async Task ListenForIncomingConnectionAsync(string peerId, CancellationToken ct)
    {
        var pairId = GeneratePairId(_deviceId, peerId);
        Util.Logger.Log($"着信接続ポーリング開始: pairId={pairId}");

        var minCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (State is PeerState.Connected or PeerState.Connecting)
                {
                    await Task.Delay(2000, ct);
                    continue;
                }

                // Offer（接続情報 JSON）をポーリングで待つ
                using var pollingSignaling = new FirebaseSignaling(_databaseUrl);
                var offerJson = await pollingSignaling.WaitForSdpAsync(pairId, "offer", minCreatedAt: minCreatedAt, ct: ct);

                if (State is PeerState.Connected or PeerState.Connecting)
                {
                    Util.Logger.Log("着信接続情報を検知したが、既に接続中のためスキップ");
                    minCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    continue;
                }

                var offer = DeserializeConnectionInfo(offerJson);
                if (offer == null)
                {
                    Util.Logger.Log("着信接続情報のパースに失敗", Util.LogLevel.Warning);
                    minCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    continue;
                }

                Util.Logger.Log($"着信接続情報検知！ Answer 側として接続開始: pairId={pairId}, ips=[{string.Join(", ", offer.Ips)}], port={offer.Port}");
                SetState(PeerState.Connecting);

                _signaling?.Dispose();
                _signaling = new FirebaseSignaling(_databaseUrl);
                _currentPairId = pairId;

                // TCP 直接接続を試行
                var connected = await TryTcpConnectAsync(offer.Ips, offer.Port, ct);

                // TCP 失敗時: WebSocket リレーにフォールバック
                if (!connected)
                {
                    connected = await TryRelayConnectAsync(pairId, "answer", ct);
                }

                if (!connected)
                {
                    Util.Logger.Log("全接続方法が失敗", Util.LogLevel.Error);
                    SetState(PeerState.Disconnected);
                    minCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    try { await Task.Delay(3000, ct); } catch { break; }
                    continue;
                }

                // 接続成功 → Answer（確認応答）を送信
                var answerInfo = new ConnectionInfo
                {
                    Ips = TcpDirectTransport.GetLocalIpAddresses(),
                    Port = 0, // Answer 側はリスナーを開かない
                    Connected = true,
                };
                var answerJson = SerializeConnectionInfo(answerInfo);
                await _signaling.SendSdpAnswerAsync(pairId, answerJson, ct);

                ConnectedPeer = new PeerInfo
                {
                    SessionId = peerId,
                    DisplayName = peerId,
                    State = PeerState.Connected,
                };
                SetState(PeerState.Connected);
                Util.Logger.Log($"着信接続完了！ 経路: {_transport?.Route}");

                minCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Util.Logger.Log("着信接続監視: 正常キャンセル");
                break;
            }
            catch (OperationCanceledException)
            {
                Util.Logger.Log("着信接続: タイムアウト、リトライ", Util.LogLevel.Warning);
                SetState(PeerState.Disconnected);
                minCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                try { await Task.Delay(3000, ct); } catch { break; }
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"着信接続処理エラー: {ex.Message}", Util.LogLevel.Error);
                SetState(PeerState.Disconnected);
                try { await Task.Delay(3000, ct); } catch { break; }
                minCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }

        Util.Logger.Log("着信接続ポーリング終了");
    }

    // === オンデマンド接続（送信側が呼ぶ） ===

    public async Task ConnectToPeerAsync(string peerId, CancellationToken ct = default)
    {
        Util.Logger.Log($"オンデマンド接続開始: peer={peerId}, deviceId={_deviceId}");
        SetState(PeerState.Connecting);

        // 着信監視を一時停止（自分の Offer を自分で拾わないように）
        StopListeningForConnection();

        try
        {
            _signaling?.Dispose();
            _signaling = new FirebaseSignaling(_databaseUrl);

            DetachTransportEvents();
            _transport?.Dispose();
            _transport = null;

            var pairId = GeneratePairId(_deviceId, peerId);
            _currentPairId = pairId;
            Util.Logger.Log($"pairId 生成: {pairId}");

            // 古いシグナリングデータを削除
            await _signaling.CleanupSignalingDataAsync(pairId, ct);

            // TCP リスナーを起動して待ち受け
            var tcpTransport = new TcpDirectTransport();
            var port = tcpTransport.StartListener();

            // 接続情報を Firebase に送信
            var localIps = TcpDirectTransport.GetLocalIpAddresses();
            var offerInfo = new ConnectionInfo
            {
                Ips = localIps,
                Port = port,
                RelayUrl = RelayUrl,
            };
            var offerJson = SerializeConnectionInfo(offerInfo);
            Util.Logger.Log($"接続情報送信: ips=[{string.Join(", ", localIps)}], port={port}");
            await _signaling.SendSdpOfferAsync(pairId, offerJson, ct);
            Util.Logger.Log("接続情報送信完了、相手の接続待機中…");

            // TCP 接続受入を待つ（タイムアウト付き）
            var connected = false;
            try
            {
                using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                acceptCts.CancelAfter(TimeSpan.FromSeconds(TcpConnectTimeoutSeconds));
                await tcpTransport.AcceptAsync(acceptCts.Token);
                connected = true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Util.Logger.Log("TCP 直接接続タイムアウト → リレーにフォールバック", Util.LogLevel.Warning);
                tcpTransport.Dispose();
            }

            if (connected)
            {
                // TCP 直接接続成功
                _transport = tcpTransport;
                AttachTransportEvents();
            }
            else
            {
                // WebSocket リレーにフォールバック
                var relayConnected = await TryRelayConnectAsync(pairId, "offer", ct);
                if (!relayConnected)
                    throw new InvalidOperationException("全ての接続方法が失敗しました");
            }

            // Answer（確認応答）をポーリングで待つ
            var answerJson = await _signaling.WaitForSdpAsync(pairId, "answer", ct: ct);
            Util.Logger.Log($"接続確認応答受信");

            ConnectedPeer = new PeerInfo
            {
                SessionId = peerId,
                DisplayName = peerId,
                State = PeerState.Connected,
            };
            SetState(PeerState.Connected);
            Util.Logger.Log($"オンデマンド接続完了！ 経路: {_transport?.Route}");
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"接続エラー: {ex.Message}", Util.LogLevel.Error);
            SetState(PeerState.Error);
            throw;
        }
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (_transport == null || !_transport.IsConnected)
            throw new InvalidOperationException("接続されていません");

        await _transport.SendAsync(data, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        Util.Logger.Log("切断処理開始");
        StopListeningForConnection();
        DetachTransportEvents();
        _transport?.Close();
        _transport?.Dispose();
        _transport = null;

        if (_signaling != null)
        {
            await _signaling.CleanupAsync(_currentPairId, ct);
            _signaling.Dispose();
            _signaling = null;
        }

        _currentPairId = null;
        ConnectedPeer = null;
        Route = ConnectionRoute.Unknown;
        SetState(PeerState.Disconnected);
        Util.Logger.Log("切断処理完了");
    }

    // === 接続ヘルパー ===

    /// <summary>
    /// TCP 直接接続を試行する（Answer 側が使用）。
    /// </summary>
    private async Task<bool> TryTcpConnectAsync(string[] ips, int port, CancellationToken ct)
    {
        if (ips.Length == 0 || port <= 0)
        {
            Util.Logger.Log("TCP 接続情報が不正（IP なしまたはポート 0）", Util.LogLevel.Warning);
            return false;
        }

        try
        {
            var tcpTransport = new TcpDirectTransport();

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(TcpConnectTimeoutSeconds));

            await tcpTransport.ConnectAsync(ips, port, connectCts.Token);

            DetachTransportEvents();
            _transport?.Dispose();
            _transport = tcpTransport;
            AttachTransportEvents();
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Util.Logger.Log("TCP 直接接続タイムアウト", Util.LogLevel.Warning);
            return false;
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"TCP 直接接続失敗: {ex.Message}", Util.LogLevel.Warning);
            return false;
        }
    }

    /// <summary>
    /// WebSocket リレー接続を試行する。
    /// </summary>
    private async Task<bool> TryRelayConnectAsync(string pairId, string role, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(RelayUrl))
        {
            Util.Logger.Log("リレーURL 未設定のためフォールバック不可", Util.LogLevel.Warning);
            return false;
        }

        try
        {
            Util.Logger.Log($"WebSocket リレー接続試行: role={role}");
            var relayTransport = new WebSocketRelayTransport(RelayUrl, pairId, role);

            using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            relayCts.CancelAfter(TimeSpan.FromSeconds(30));

            await relayTransport.ConnectAsync(relayCts.Token);

            DetachTransportEvents();
            _transport?.Dispose();
            _transport = relayTransport;
            AttachTransportEvents();
            return true;
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"WebSocket リレー接続失敗: {ex.Message}", Util.LogLevel.Warning);
            return false;
        }
    }

    // === イベントハンドラ ===

    private void AttachTransportEvents()
    {
        if (_transport == null) return;
        _transport.ChannelOpened += OnChannelOpened;
        _transport.ChannelClosed += OnChannelClosed;
        _transport.DataReceived += OnDataReceived;
        _transport.RouteChanged += OnTransportRouteChanged;
    }

    private void DetachTransportEvents()
    {
        if (_transport == null) return;
        _transport.ChannelOpened -= OnChannelOpened;
        _transport.ChannelClosed -= OnChannelClosed;
        _transport.DataReceived -= OnDataReceived;
        _transport.RouteChanged -= OnTransportRouteChanged;
    }

    private void OnChannelOpened(object? sender, EventArgs e)
    {
        Util.Logger.Log("データチャネル接続完了");
    }

    private void OnTransportRouteChanged(object? sender, ConnectionRoute route)
    {
        Route = route;
        Util.Logger.Log($"接続経路確定: {route}");
        RouteChanged?.Invoke(this, route);
    }

    private void OnChannelClosed(object? sender, EventArgs e)
    {
        Util.Logger.Log($"データチャネル切断検知: currentState={State}", Util.LogLevel.Warning);
        if (State == PeerState.Connected)
        {
            ConnectionLost?.Invoke(this, EventArgs.Empty);
            SetState(PeerState.Disconnected);
        }
    }

    private void OnDataReceived(object? sender, byte[] data)
    {
        DataReceived?.Invoke(this, data);
    }

    private void SetState(PeerState state)
    {
        Util.Logger.Log($"状態遷移: {State} → {state}");
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private static string GeneratePairId(string a, string b)
    {
        return string.Compare(a, b, StringComparison.Ordinal) < 0
            ? $"{a}_{b}"
            : $"{b}_{a}";
    }

    // === 接続情報のシリアライズ ===

    private static string SerializeConnectionInfo(ConnectionInfo info)
    {
        return JsonSerializer.Serialize(info, ConnectionInfoJsonContext.Default.ConnectionInfo);
    }

    private static ConnectionInfo? DeserializeConnectionInfo(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ConnectionInfoJsonContext.Default.ConnectionInfo);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"接続情報 JSON パースエラー: {ex.Message}", Util.LogLevel.Warning);
            return null;
        }
    }

    public void Dispose()
    {
        StopListeningForConnection();
        _transport?.Dispose();
        _signaling?.Dispose();
    }
}

/// <summary>
/// Firebase 経由で交換する接続情報。
/// SDP の代わりに IP:port 情報を交換する。
/// </summary>
public sealed class ConnectionInfo
{
    /// <summary>ローカル IP アドレス群（LAN 内の全 IPv4 アドレス）。</summary>
    [JsonPropertyName("ips")]
    public string[] Ips { get; set; } = [];

    /// <summary>TCP リスナーのポート番号。</summary>
    [JsonPropertyName("port")]
    public int Port { get; set; }

    /// <summary>WebSocket リレーサーバーの URL（NAT 越え用フォールバック）。</summary>
    [JsonPropertyName("relayUrl")]
    public string? RelayUrl { get; set; }

    /// <summary>接続確認フラグ（Answer 側が true を返す）。</summary>
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }
}

/// <summary>
/// Native AOT 対応の JSON シリアライザコンテキスト。
/// </summary>
[JsonSerializable(typeof(ConnectionInfo))]
internal sealed partial class ConnectionInfoJsonContext : JsonSerializerContext;
