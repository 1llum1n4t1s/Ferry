using System;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Infrastructure;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// Firebase シグナリング + WebRTC DataChannel による接続サービスの本実装。
/// ペアリング（Bridge ページ経由の自動マッチング）とオンデマンド接続を管理する。
/// </summary>
public sealed class ConnectionService : IConnectionService, IDisposable
{
    private readonly string _databaseUrl;
    private readonly string _displayName;
    private FirebaseSignaling? _signaling;
    private WebRtcTransport? _transport;
    private string? _currentPairId;
    private bool _isInitiator;

    public PeerState State { get; private set; } = PeerState.Disconnected;
    public PeerInfo? ConnectedPeer { get; private set; }
    public ConnectionRoute Route { get; private set; } = ConnectionRoute.Unknown;

    public event EventHandler<PeerState>? StateChanged;
    public event EventHandler<ConnectionRoute>? RouteChanged;
    public event EventHandler<PairedPeer>? PairingCompleted;
    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler? ConnectionLost;

    public ConnectionService(string databaseUrl, string displayName)
    {
        _databaseUrl = databaseUrl;
        _displayName = displayName;
    }

    // === ペアリング ===

    public async Task<string> StartPairingSessionAsync(CancellationToken ct = default)
    {
        // 前回のシグナリングをクリーンアップ
        _signaling?.Dispose();
        _signaling = new FirebaseSignaling(_databaseUrl);

        // セッション登録
        var sessionId = await _signaling.RegisterSessionAsync(_displayName, ct);

        // ペアリング検知イベントを登録
        _signaling.PairingDetected += OnPairingDetected;

        // ペアリング監視開始
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

        Util.Logger.Log($"ペアリング検知: peer={info.PeerDisplayName}, initiator={info.IsInitiator}");

        _currentPairId = info.PairingId;
        _isInitiator = info.IsInitiator;

        // ペアリング監視を停止
        _signaling.PairingDetected -= OnPairingDetected;
        _signaling.StopWatching();

        SetState(PeerState.WaitingForMatch);

        // ペアリング完了を通知
        var peer = new PairedPeer
        {
            PeerId = info.PeerId,
            DisplayName = info.PeerDisplayName,
        };
        PairingCompleted?.Invoke(this, peer);

        // シグナリングデータのクリーンアップ
        await _signaling.CleanupAsync(ct: default);
    }

    // === オンデマンド接続 ===

    public async Task ConnectToPeerAsync(string peerId, CancellationToken ct = default)
    {
        SetState(PeerState.Connecting);

        try
        {
            // Firebase シグナリングを初期化（既に接続中なら再利用）
            _signaling?.Dispose();
            _signaling = new FirebaseSignaling(_databaseUrl);

            // WebRTC トランスポートを初期化
            _transport?.Dispose();
            _transport = new WebRtcTransport();

            // DataChannel・経路検出イベント登録
            _transport.ChannelOpened += OnChannelOpened;
            _transport.ChannelClosed += OnChannelClosed;
            _transport.DataReceived += OnDataReceived;
            _transport.RouteChanged += OnTransportRouteChanged;

            // pairId を生成（ペアリング済みの場合、両方の peerId からソートして一意にする）
            var pairId = GeneratePairId(_displayName, peerId);
            _currentPairId = pairId;

            // ICE Candidate を Firebase に送信するイベント
            var myCandidateField = _isInitiator ? "candidatesA" : "candidatesB";
            var remoteCandidateField = _isInitiator ? "candidatesB" : "candidatesA";

            _transport.IceCandidateGenerated += async (_, candidate) =>
            {
                try
                {
                    await _signaling.SendIceCandidateAsync(pairId, myCandidateField, candidate.candidate, ct);
                }
                catch (Exception ex)
                {
                    Util.Logger.Log($"ICE candidate 送信エラー: {ex.Message}", Util.LogLevel.Warning);
                }
            };

            // リモートの ICE Candidate を監視
            _signaling.IceCandidateReceived += async (_, candidate) =>
            {
                try
                {
                    await _transport.AddIceCandidateAsync(candidate, ct);
                }
                catch (Exception ex)
                {
                    Util.Logger.Log($"ICE candidate 追加エラー: {ex.Message}", Util.LogLevel.Warning);
                }
            };
            _signaling.StartWatchingIceCandidates(pairId, remoteCandidateField);

            if (_isInitiator)
            {
                // Offer 側
                var offerSdp = await _transport.CreateOfferAsync(ct);
                await _signaling.SendSdpOfferAsync(pairId, offerSdp, ct);

                // Answer を待つ
                var answerTcs = new TaskCompletionSource<string>();
                _signaling.SdpReceived += (_, sdp) => answerTcs.TrySetResult(sdp);
                _signaling.StartWatchingSdp(pairId, "answer");

                var answerSdp = await answerTcs.Task.WaitAsync(ct);
                await _transport.SetRemoteAnswerAsync(answerSdp, ct);
            }
            else
            {
                // Answer 側: Offer を待つ
                var offerTcs = new TaskCompletionSource<string>();
                _signaling.SdpReceived += (_, sdp) => offerTcs.TrySetResult(sdp);
                _signaling.StartWatchingSdp(pairId, "offer");

                var offerSdp = await offerTcs.Task.WaitAsync(ct);
                var answerSdp = await _transport.CreateAnswerAsync(offerSdp, ct);
                await _signaling.SendSdpAnswerAsync(pairId, answerSdp, ct);
            }

            // DataChannel が開通するまで待つ（タイムアウト 30 秒）
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            while (!_transport.IsConnected)
            {
                await Task.Delay(100, timeoutCts.Token);
            }

            ConnectedPeer = new PeerInfo
            {
                SessionId = peerId,
                DisplayName = peerId,
                State = PeerState.Connected,
            };
            SetState(PeerState.Connected);
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
    }

    private void OnChannelOpened(object? sender, EventArgs e)
    {
        Util.Logger.Log("DataChannel 接続完了");
    }

    private void OnTransportRouteChanged(object? sender, ConnectionRoute route)
    {
        Route = route;
        Util.Logger.Log($"接続経路確定: {route}");
        RouteChanged?.Invoke(this, route);
    }

    private void OnChannelClosed(object? sender, EventArgs e)
    {
        if (State == PeerState.Connected)
        {
            Util.Logger.Log("DataChannel 切断検知", Util.LogLevel.Warning);
            ConnectionLost?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnDataReceived(object? sender, byte[] data)
    {
        DataReceived?.Invoke(this, data);
    }

    private void SetState(PeerState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private static string GeneratePairId(string a, string b)
    {
        return string.Compare(a, b, StringComparison.Ordinal) < 0
            ? $"{a}_{b}"
            : $"{b}_{a}";
    }

    public void Dispose()
    {
        _transport?.Dispose();
        _signaling?.Dispose();
    }
}
