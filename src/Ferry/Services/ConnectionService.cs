using System;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Infrastructure;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// Firebase シグナリング + WebRTC DataChannel による接続サービスの本実装。
/// ペアリング（Bridge ページ経由の自動マッチング）とオンデマンド接続を管理する。
///
/// 接続モデル:
///   - ファイル送信側が ConnectToPeerAsync を呼び WebRTC Offer を作成
///   - 受信側は StartListeningForConnection で Offer を監視し、自動的に Answer を返す
///   - ICE candidate フィールドは DeviceId 辞書順で決定論的に割り当て（A=小さい方, B=大きい方）
/// </summary>
public sealed class ConnectionService : IConnectionService, IDisposable
{
    private readonly string _databaseUrl;
    private readonly string _deviceId;
    private readonly string _displayName;
    private FirebaseSignaling? _signaling;
    private WebRtcTransport? _transport;
    private string? _currentPairId;
    private CancellationTokenSource? _listeningCts;

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
        // 前回のシグナリングをクリーンアップ
        _signaling?.Dispose();
        _signaling = new FirebaseSignaling(_databaseUrl);

        // セッション登録（DeviceId を安定したセッション ID として使用）
        var sessionId = await _signaling.RegisterSessionAsync(_deviceId, _displayName, ct);

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

        Util.Logger.Log($"ペアリング検知: peer={info.PeerDisplayName}");

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

        // ペアリングデータとセッションを Firebase から削除（古いデータが残るとペアリング解除後に再検知される）
        await _signaling.CleanupAsync(info.PairingId, ct: default);
    }

    // === 着信接続監視 ===

    /// <summary>
    /// 指定ピアからの接続要求（Offer）をバックグラウンドで監視開始する。
    /// Offer を検知したら自動的に Answer を返して WebRTC 接続を確立する。
    /// ペアリング完了後またはピア選択後に呼び出す。
    /// </summary>
    public void StartListeningForConnection(string peerId)
    {
        StopListeningForConnection();
        _listeningCts = new CancellationTokenSource();
        Util.Logger.Log($"着信接続監視開始: peer={peerId}");
        _ = ListenForIncomingConnectionAsync(peerId, _listeningCts.Token);
    }

    /// <summary>
    /// 着信接続監視を停止する。
    /// </summary>
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
    /// バックグラウンドで Offer をポーリングし、検知したら Answer を返して接続を確立する。
    /// WaitForSdpAsync が内部でポーリングするため、外側のループは接続完了→再待機のためだけに存在する。
    /// </summary>
    private async Task ListenForIncomingConnectionAsync(string peerId, CancellationToken ct)
    {
        var pairId = GeneratePairId(_deviceId, peerId);
        var (myCandidateField, remoteCandidateField) = GetIceCandidateFields(peerId);

        Util.Logger.Log($"着信接続ポーリング開始: pairId={pairId}, myField={myCandidateField}, remoteField={remoteCandidateField}");

        // 起動時点のタイムスタンプ（古い Offer をスキップするため）
        var minCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 接続中・接続済みの場合は待機
                if (State is PeerState.Connected or PeerState.Connecting)
                {
                    await Task.Delay(2000, ct);
                    continue;
                }

                // WaitForSdpAsync は内部でポーリングし、新しい Offer が来るまでブロックする
                // FirebaseSignaling は Offer ポーリング専用
                using var pollingSignaling = new FirebaseSignaling(_databaseUrl);
                var offerSdp = await pollingSignaling.WaitForSdpAsync(pairId, "offer", minCreatedAt: minCreatedAt, ct: ct);

                // 接続中・接続済みの場合は Offer を無視
                if (State is PeerState.Connected or PeerState.Connecting)
                {
                    Util.Logger.Log("着信 Offer を検知したが、既に接続中のためスキップ");
                    minCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    continue;
                }

                Util.Logger.Log($"着信 Offer 検知！ Answer 側として接続開始: pairId={pairId}");
                SetState(PeerState.Connecting);

                // 接続用の FirebaseSignaling（ICE 監視・Answer 送信用）
                _signaling?.Dispose();
                _signaling = new FirebaseSignaling(_databaseUrl);
                DetachTransportEvents();
                _transport?.Dispose();
                _transport = new WebRtcTransport();
                _transport.ChannelOpened += OnChannelOpened;
                _transport.ChannelClosed += OnChannelClosed;
                _transport.DataReceived += OnDataReceived;
                _transport.RouteChanged += OnTransportRouteChanged;
                _currentPairId = pairId;

                // Answer 生成（Vanilla ICE: 全候補収集後に SDP を返す）
                // 注意: ICE candidate 送信ハンドラは Answer 送信後に登録する。
                // CreateAnswerAsync 中に候補が生成されるため、先に登録すると
                // Firebase への同時書き込みが Answer 送信と競合してエラーになる。
                var answerSdp = await _transport.CreateAnswerAsync(offerSdp, ct);
                Util.Logger.Log("SDP Answer 生成完了");

                // Answer SDP を最優先で送信（全候補は SDP に含まれている）
                Util.Logger.Log("SDP Answer 送信中…");
                await _signaling.SendSdpAnswerAsync(pairId, answerSdp, ct);
                Util.Logger.Log("SDP Answer 送信完了");

                // Answer 送信完了後に ICE candidate ハンドラを登録（補助的な trickle ICE）
                SetupIceCandidateSendHandler(pairId, myCandidateField, ct);

                // remote description(offer) 設定済みなので ICE candidate 受信ハンドラを登録
                SetupIceCandidateReceiveHandler(remoteCandidateField);
                _signaling.StartWatchingIceCandidates(pairId, remoteCandidateField);
                Util.Logger.Log("ICE candidate 監視開始");

                // DataChannel 接続待ち
                await WaitForDataChannelAsync(ct);

                ConnectedPeer = new PeerInfo
                {
                    SessionId = peerId,
                    DisplayName = peerId,
                    State = PeerState.Connected,
                };
                SetState(PeerState.Connected);
                Util.Logger.Log("着信接続完了！DataChannel 開通");

                // 処理済みタイムスタンプを更新（次の Offer を待つ）
                minCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // StopListeningForConnection() による正常なキャンセル
                Util.Logger.Log("着信接続監視: 正常キャンセル");
                break;
            }
            catch (OperationCanceledException)
            {
                // DataChannel タイムアウト等 → 状態をリセットしてリトライ
                Util.Logger.Log("着信接続: DataChannel タイムアウト、リトライ", Util.LogLevel.Warning);
                SetState(PeerState.Disconnected);
                minCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                try { await Task.Delay(3000, ct); } catch { break; }
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"着信接続処理エラー: {ex.Message}", Util.LogLevel.Error);
                SetState(PeerState.Disconnected);

                // リトライ間隔
                try { await Task.Delay(3000, ct); } catch { break; }

                // 次回は新しい Offer のみ受け付ける
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
            // Firebase シグナリングを初期化
            _signaling?.Dispose();
            _signaling = new FirebaseSignaling(_databaseUrl);

            // WebRTC トランスポートを初期化
            DetachTransportEvents();
            _transport?.Dispose();
            _transport = new WebRtcTransport();
            _transport.ChannelOpened += OnChannelOpened;
            _transport.ChannelClosed += OnChannelClosed;
            _transport.DataReceived += OnDataReceived;
            _transport.RouteChanged += OnTransportRouteChanged;

            // pairId を両方の DeviceId からソートして一意に生成（両 PC で同じ値になる）
            var pairId = GeneratePairId(_deviceId, peerId);
            _currentPairId = pairId;
            Util.Logger.Log($"pairId 生成: {pairId}");

            // ICE candidate フィールド割り当て（DeviceId 辞書順で決定）
            var (myCandidateField, remoteCandidateField) = GetIceCandidateFields(peerId);
            Util.Logger.Log($"ICE フィールド: my={myCandidateField}, remote={remoteCandidateField}");

            // Initiator 側: 古いシグナリングデータを削除してから Offer を書き込む
            await _signaling.CleanupSignalingDataAsync(pairId, ct);

            // Offer 生成（Vanilla ICE: 全候補収集後に SDP を返す）
            // 注意: ICE candidate 送信ハンドラは Offer 送信後に登録する。
            // CreateOfferAsync 中に候補が生成されるため、先に登録すると
            // Firebase への同時書き込みが Offer 送信と競合してエラーになる。
            var offerSdp = await _transport.CreateOfferAsync(ct);
            Util.Logger.Log("SDP Offer 生成完了、送信中…");
            await _signaling.SendSdpOfferAsync(pairId, offerSdp, ct);
            Util.Logger.Log("SDP Offer 送信完了、Answer 待機中…");

            // Offer 送信完了後に ICE candidate ハンドラを登録（補助的な trickle ICE）
            SetupIceCandidateSendHandler(pairId, myCandidateField, ct);

            // Answer をポーリングで待つ（remote description 設定前に ICE candidate を追加しない）
            var answerSdp = await _signaling.WaitForSdpAsync(pairId, "answer", ct: ct);
            Util.Logger.Log("SDP Answer 受信、リモート設定中…");
            await _transport.SetRemoteAnswerAsync(answerSdp, ct);
            Util.Logger.Log("リモート SDP Answer 設定完了");

            // Answer 設定後にリモート ICE candidate 監視を開始
            // remote description が設定されている状態で addIceCandidate を呼ぶ必要がある
            SetupIceCandidateReceiveHandler(remoteCandidateField);
            _signaling.StartWatchingIceCandidates(pairId, remoteCandidateField);
            Util.Logger.Log("ICE candidate 監視開始");

            // DataChannel 接続待ち
            await WaitForDataChannelAsync(ct);

            ConnectedPeer = new PeerInfo
            {
                SessionId = peerId,
                DisplayName = peerId,
                State = PeerState.Connected,
            };
            SetState(PeerState.Connected);
            Util.Logger.Log("オンデマンド接続完了！DataChannel 開通");
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

    // === 共通ヘルパー ===

    /// <summary>
    /// 自分の ICE candidate を Firebase に送信するハンドラを登録する。
    /// PeerConnection 初期化直後に呼ぶ。
    /// </summary>
    private void SetupIceCandidateSendHandler(string pairId, string myCandidateField, CancellationToken ct)
    {
        _transport!.IceCandidateGenerated += async (_, candidate) =>
        {
            try
            {
                Util.Logger.Log($"ICE candidate 送信: type={candidate.type}, field={myCandidateField}, candidate={candidate.candidate}");
                await _signaling!.SendIceCandidateAsync(pairId, myCandidateField, candidate.candidate, ct);
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"ICE candidate 送信エラー: {ex.Message}", Util.LogLevel.Warning);
            }
        };
    }

    /// <summary>
    /// リモートの ICE candidate を受信して PeerConnection に追加するハンドラを登録する。
    /// remote description 設定後に呼ぶこと（addIceCandidate は remote description が必要）。
    /// </summary>
    private void SetupIceCandidateReceiveHandler(string remoteCandidateField)
    {
        _signaling!.IceCandidateReceived += async (_, candidate) =>
        {
            try
            {
                Util.Logger.Log($"ICE candidate 受信・追加: field={remoteCandidateField}, candidate={candidate}");
                await _transport!.AddIceCandidateAsync(candidate);
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"ICE candidate 追加エラー: {ex.Message}", Util.LogLevel.Warning);
            }
        };
    }

    /// <summary>
    /// DataChannel が開通するまでイベントベースで待機する（タイムアウト 30 秒）。
    /// </summary>
    private async Task WaitForDataChannelAsync(CancellationToken ct)
    {
        Util.Logger.Log("DataChannel 開通待機中…");
        var channelOpenedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConnected(object? s, EventArgs e) => channelOpenedTcs.TrySetResult();
        _transport!.ChannelOpened += OnConnected;
        try
        {
            if (_transport.IsConnected)
                channelOpenedTcs.TrySetResult();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            await using var _ = timeoutCts.Token.Register(() => channelOpenedTcs.TrySetCanceled(timeoutCts.Token));
            await channelOpenedTcs.Task;
        }
        finally
        {
            _transport.ChannelOpened -= OnConnected;
        }
    }

    /// <summary>
    /// DeviceId 辞書順で ICE candidate フィールドを決定する。
    /// 小さい方 → candidatesA, 大きい方 → candidatesB。
    /// </summary>
    private (string myCandidateField, string remoteCandidateField) GetIceCandidateFields(string peerId)
    {
        var isSmallerDeviceId = string.Compare(_deviceId, peerId, StringComparison.Ordinal) < 0;
        return isSmallerDeviceId
            ? ("candidatesA", "candidatesB")
            : ("candidatesB", "candidatesA");
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
        Util.Logger.Log($"DataChannel 切断検知: currentState={State}", Util.LogLevel.Warning);
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

    /// <summary>
    /// _transport のイベントハンドラを安全に解除する（再接続時のハンドラ蓄積を防ぐ）。
    /// </summary>
    private void DetachTransportEvents()
    {
        if (_transport == null) return;
        _transport.ChannelOpened -= OnChannelOpened;
        _transport.ChannelClosed -= OnChannelClosed;
        _transport.DataReceived -= OnDataReceived;
        _transport.RouteChanged -= OnTransportRouteChanged;
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

    public void Dispose()
    {
        StopListeningForConnection();
        _transport?.Dispose();
        _signaling?.Dispose();
    }
}
