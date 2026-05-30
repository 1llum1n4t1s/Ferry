using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Infrastructure;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// Firebase シグナリング + TCP 直接接続 / UDP ホールパンチ / WebSocket リレーによる接続サービス。
/// ペアリング（Bridge ページ経由の自動マッチング）とオンデマンド接続を管理する。
///
/// 接続フロー（優先順位順）:
///   1. TCP 直接接続（同一 LAN、最速）
///   2. UDP ホールパンチ（STUN で NAT 越え P2P、サーバー非経由、成功率 ~80%）
///   3. WebSocket リレー（最終手段、全データが VPS を経由）
/// </summary>
public sealed class ConnectionService : IConnectionService, IDisposable
{
    /// <summary>TCP 直接接続のタイムアウト（秒）。</summary>
    private const int TcpConnectTimeoutSeconds = 5;
    /// <summary>v1.0.38 review fix v10: probe 側全体タイムアウト。
    /// listener の WaitForSdpAsync(5s) + HandleProbeOfferAsync の TCP connect(5s) + Firebase write を
    /// カバーできる長さに設定 (旧 TcpConnectTimeoutSeconds+2=7s では answer 間に合わず Unknown 連発)。</summary>
    private const int ProbeOverallTimeoutSeconds = 15;

    /// <summary>UDP ホールパンチのタイムアウト（秒）。</summary>
    private const int UdpHolePunchTimeoutSeconds = 8;

    /// <summary>Answer 側が TCP 成功を通知する route 値。</summary>
    private const string RouteDirect = "direct";

    /// <summary>Answer 側が TCP 失敗を通知する route 値（STUN/リレーへ遷移）。</summary>
    private const string RouteNeedRelay = "needRelay";

    private readonly string _databaseUrl;
    private readonly string _deviceId;
    private readonly string _displayName;
    private FirebaseSignaling? _signaling;
    private ITransport? _transport;
    private string? _currentPairId;
    private CancellationTokenSource? _listeningCts;

    /// <summary>
    /// v1.0.38 review fix: 現在 StartListeningForConnection で監視中のピア ID。
    /// ProbeRouteAsync 終了時にこの値を使って着信監視を再開する。
    /// ConnectedPeer?.SessionId は接続成立後にしか入らないため復元用には使えない。
    /// </summary>
    private string? _currentListeningPeerId;

    /// <summary>現在着信監視中のピア ID（未監視なら null）。タブ切替で SelectedPeer 外の
    /// ピアを監視中に、そのピアが削除された場合の監視停止判定に VM が使う。</summary>
    public string? CurrentListeningPeerId => _currentListeningPeerId;

    /// <summary>
    /// この pairing watch セッションで既に処理した pairingId。
    /// Firebase 購読時に既存子 (stale な pairings/ エントリ) が replay されても、
    /// 同じ pairing を二重に PairingCompleted へ流さないための重複排除。
    /// StartPairingSessionAsync 開始時にクリアする。
    /// </summary>
    private readonly System.Collections.Generic.HashSet<string> _seenPairingIds = new();

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
    public event EventHandler<string>? StatusMessageChanged;

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
        _seenPairingIds.Clear();

        var sessionId = await _signaling.RegisterSessionAsync(_deviceId, _displayName, ct);

        _signaling.PairingDetected += OnPairingDetected;
        _signaling.StartWatchingPairing();

        SetState(PeerState.WaitingForPairing);
        return sessionId;
    }

    /// <summary>
    /// アプリ内 URL 貼り付けによるペアリング (Bridge ページを経由しない、カメラ無し PC 同士向け)。
    /// 相手 PC の招待リンク (https://ferry-edf09.web.app/?sid=...&amp;name=...) を受け取り、
    /// 自セッションを sidA、URL から取得したセッションを sidB として
    /// Firebase の pairings/ に書き込む。両 PC は StartWatchingPairing で成立を検知する。
    /// </summary>
    /// <param name="peerInviteUrl">相手 PC に表示されているペアリングリンク。</param>
    /// <returns>ペアリング成功時 true。URL 形式不正 / 自己 URL / セッション未存在の場合は false + 理由メッセージ。</returns>
    public async Task<(bool Success, string Message)> PairFromUrlAsync(string peerInviteUrl, CancellationToken ct = default)
    {
        if (_signaling == null)
            return (false, "ペアリング待機を開始してから実行してください。");

        if (string.IsNullOrWhiteSpace(peerInviteUrl))
            return (false, "URL が空です。");

        // URL から sid / name を抽出 (System.Web.HttpUtility を避け、手動パース)
        string? sidB = null;
        string? nameB = null;
        try
        {
            var uri = new Uri(peerInviteUrl.Trim());
            var query = uri.Query.TrimStart('?');
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=');
                if (idx < 0) continue;
                var key = Uri.UnescapeDataString(pair[..idx]);
                var value = Uri.UnescapeDataString(pair[(idx + 1)..]);
                if (key == "sid") sidB = value;
                else if (key == "name") nameB = value;
            }
        }
        catch
        {
            return (false, "Ferry のペアリングリンクではありません。URL を確認してください。");
        }

        if (string.IsNullOrEmpty(sidB))
            return (false, "URL に sid パラメータが含まれていません。");

        if (sidB == _deviceId)
            return (false, "これは自分の PC の URL です。もう片方の PC のリンクを貼り付けてください。");

        // 相手セッションの存在を確認
        var (exists, displayName) = await _signaling.CheckSessionAsync(sidB, ct);
        if (!exists)
            return (false, "ペアリング先のセッションが見つかりません。相手の PC でアプリが起動していることを確認してください。");

        // pairings/ に書き込み → 両 PC の StartWatchingPairing が成立を検知する
        var resolvedNameB = string.IsNullOrEmpty(nameB) ? displayName ?? "PC-B" : nameB;
        await _signaling.SubmitPairingAsync(_deviceId, _displayName, sidB, resolvedNameB, ct);
        return (true, $"「{_displayName}」と「{resolvedNameB}」をペアリングしました。");
    }

    /// <summary>
    /// v1.0.38: ペアリングコード (32 文字 hex = sessionId) を直接受け取ってペアリングする。
    /// URL を貼り付ける旧 PairFromUrlAsync と違ってブラウザで開かれる事故が起きない。
    /// </summary>
    public async Task<(bool Success, string Message)> PairFromCodeAsync(string code, CancellationToken ct = default)
    {
        if (_signaling == null)
            return (false, "ペアリング待機を開始してから実行してください。");
        if (string.IsNullOrWhiteSpace(code))
            return (false, "コードが空です。");

        var sidB = code.Trim();

        // 32 文字 hex (Guid "N" 形式) の検証
        if (!Guid.TryParseExact(sidB, "N", out _))
            return (false, "Ferry のペアリングコードではありません (32 文字の英数字)。");

        if (sidB == _deviceId)
            return (false, "これは自分の PC のコードです。もう片方の PC のコードを貼り付けてください。");

        // 相手セッションの存在確認
        var (exists, displayName) = await _signaling.CheckSessionAsync(sidB, ct);
        if (!exists)
            return (false, "ペアリング先のセッションが見つかりません。相手の PC でアプリが起動していることを確認してください。");

        var resolvedNameB = displayName ?? "PC-B";
        await _signaling.SubmitPairingAsync(_deviceId, _displayName, sidB, resolvedNameB, ct);
        return (true, $"「{_displayName}」と「{resolvedNameB}」をペアリングしました。");
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

    private void OnPairingDetected(object? sender, PairingInfo info)
    {
        // async void のため例外は捕捉しないとプロセスを巻き込む。全体を try-catch で保護する
        try
        {
            // 他タスクの Dispose と競合しないようローカルにキャプチャしてから使う
            var sig = _signaling;
            if (sig == null) return;

            // 同一 pairing の重複検知（購読時の既存子 replay 含む）はスキップ。
            // watch はここでは止めない。stale な pairings/ エントリ (過去に成立済みで Firebase に
            // 1 時間残るもの) を拾っても watcher を生かしておき、新規デバイスとのペアリングを
            // 検知し続けられるようにする。成立確定 (新規ピア) 時に VM が StopPairingWatch を呼ぶ。
            if (!_seenPairingIds.Add(info.PairingId)) return;

            Util.Logger.Log($"ペアリング検知: peer={info.PeerDisplayName}");

            var peer = new PairedPeer
            {
                PeerId = info.PeerId,
                DisplayName = info.PeerDisplayName,
            };
            PairingCompleted?.Invoke(this, peer);

            // pairings/{pairingId} は **削除しない** 。即削除すると、もう片方の PC が Firebase の
            // InsertOrUpdate イベントを受け取る前にエントリが消え、ペアリング検知漏れが起きる
            // (片方の画面に相手が表示されない症状)。CreatedAt ベースで GitHub Actions が
            // 1 時間後に自動掃除するため、即時 cleanup は不要。
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"ペアリング検知処理エラー: {ex.Message}", Util.LogLevel.Warning);
        }
    }

    /// <summary>
    /// pairing watch を停止する。新規ペアリング成立確定時に VM (OnPairingCompleted) が呼ぶ。
    /// stale/既知ピアの再検知では呼ばず、watcher を生かしたままにする。
    /// </summary>
    public void StopPairingWatch()
    {
        var sig = _signaling;
        if (sig == null) return;
        sig.PairingDetected -= OnPairingDetected;
        sig.StopWatching();
    }

    // === 着信接続監視 ===

    public void StartListeningForConnection(string peerId)
    {
        StopListeningForConnection();
        _listeningCts = new CancellationTokenSource();
        _currentListeningPeerId = peerId;  // v1.0.38 review fix: 監視中のピア ID を保持
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
            _currentListeningPeerId = null;  // v1.0.38 review fix: クリア
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
        // v1.0.38 review fix v14 / v15 (Codex P2 #3318349010): 既に処理した probe nonce を記録。
        // sender 側 finally で削除されるが、削除前にこちらが poll するとダブル処理になるため。
        // 旧 minProbeCreatedAt (cross-device clock 比較) は時計差で fresh offer を捨てる回帰を
        // 招いていたため撤去。per-nonce key + 本 HashSet のみで stale dedupe する。
        var processedProbeNonces = new System.Collections.Generic.HashSet<string>();

        // ポーリング用 Firebase クライアントはループ全体で再利用する
        // （毎反復で new すると接続/TLS ハンドシェイクの churn が発生するため）
        using var pollingSignaling = new FirebaseSignaling(_databaseUrl);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (State is PeerState.Connected or PeerState.Connecting)
                {
                    await Task.Delay(2000, ct);
                    continue;
                }

                // v1.0.38 review fix v14: per-nonce key 化により複数の同時 probe offer を全て処理。
                // ReadProbeOffersAsync は probeOffers/<nonce>/ 配下を全部 OnceAsync で取得し、
                // (nonce, sdp) のリストを返す。自分発 (From=self) と既処理 nonce はスキップ
                var probeOffers = await pollingSignaling.ReadProbeOffersAsync(pairId, ct);
                foreach (var (probeNonce, probeOfferJson) in probeOffers)
                {
                    if (processedProbeNonces.Contains(probeNonce)) continue;
                    var probeOffer = DeserializeConnectionInfo(probeOfferJson);
                    if (probeOffer == null || probeOffer.From == _deviceId) continue;
                    try
                    {
                        await HandleProbeOfferAsync(probeOffer, pairId, probeNonce, ct);
                        processedProbeNonces.Add(probeNonce);
                    }
                    catch (Exception ex)
                    {
                        Util.Logger.Log($"Probe offer 処理エラー (nonce={probeNonce}): {ex.Message}", Util.LogLevel.Warning);
                    }
                }
                // 過剰な memory 占有を避けるため、最大 100 nonce で打ち切り (5min cooldown と整合)
                if (processedProbeNonces.Count > 100) processedProbeNonces.Clear();

                // 通常 offer は WaitForSdpAsync で長く待つが、最大 5 秒で抜けて probe offer 側も
                // 定期的にチェックできるようにする (両者の polling を交互に進める)
                using var offerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                offerCts.CancelAfter(TimeSpan.FromSeconds(5));
                string offerJson;
                try
                {
                    offerJson = await pollingSignaling.WaitForSdpAsync(pairId, "offer", minCreatedAt: minCreatedAt, ct: offerCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 5 秒タイムアウト → 次の iteration で probe + offer 両方再 polling
                    continue;
                }

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

                // v1.0.38 review fix v2: 自分自身が送信した offer は無視する (保険)
                if (offer.From == _deviceId)
                {
                    Util.Logger.Log($"自己 offer を無視: pairId={pairId}");
                    minCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    continue;
                }

                Util.Logger.Log($"着信接続情報検知！ Answer 側として接続開始: pairId={pairId}, ips=[{string.Join(", ", offer.Ips.Select(Util.Logger.MaskIp))}], port={offer.Port}");
                SetState(PeerState.Connecting);
                StatusMessageChanged?.Invoke(this, "Status.Phase.TcpConnecting");

                _signaling?.Dispose();
                var sig = new FirebaseSignaling(_databaseUrl);
                _signaling = sig;
                _currentPairId = pairId;

                // ① TCP 直接接続を試行
                var connected = await TryTcpConnectAsync(offer.Ips, offer.Port, ct);

                // TCP 結果を即座に Answer として送信（Offer 側が待機中）
                var answerInfo = new ConnectionInfo
                {
                    Ips = TcpDirectTransport.GetLocalIpAddresses(),
                    Port = 0,
                    Connected = connected,
                    Route = connected ? RouteDirect : RouteNeedRelay,
                };
                var answerJson = SerializeConnectionInfo(answerInfo);
                await sig.SendSdpAnswerAsync(pairId, answerJson, ct);

                // ② TCP 失敗時: UDP ホールパンチを試行
                if (!connected && !string.IsNullOrEmpty(offer.ExternalIp) && offer.ExternalPort > 0)
                {
                    StatusMessageChanged?.Invoke(this, "Status.Phase.UdpHolePunch");
                    connected = await TryUdpHolePunchAnswerAsync(offer, pairId, ct);
                }

                // ③ UDP 失敗時: WebSocket リレーにフォールバック
                if (!connected)
                {
                    StatusMessageChanged?.Invoke(this, "Status.Phase.Relay");
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

    /// <summary>
    /// v1.0.38 review fix v5: Task の例外を観察して握りつぶす helper。
    /// 既に完了している task はそのまま、未完了の task は cancel 後の完了を待つ。
    /// background loop 累積防止 (disposed Firebase client への polling 等を確実に止める)。
    /// </summary>
    private static async Task ObserveTaskAsync(Task task)
    {
        try { await task; }
        catch { /* キャンセル / 失敗を握りつぶす (呼び出し側が既に結果判定済み) */ }
    }

    /// <summary>
    /// v1.0.38 review fix #6: probe 専用の応答処理。
    /// 通常の ListenForIncomingConnectionAsync 内フローと違って、TCP 接続試行だけ走らせて
    /// 結果を answer に書く。transport は確立せず即切断、State も変更しない。
    /// </summary>
    private async Task HandleProbeOfferAsync(ConnectionInfo offer, string pairId, string nonce, CancellationToken ct)
    {
        Util.Logger.Log($"Probe offer 受信: pairId={pairId}, nonce={nonce}, TCP 試行のみで応答");
        var probeSig = new FirebaseSignaling(_databaseUrl);
        var connected = false;
        TcpDirectTransport? tcpTransport = null;

        try
        {
            if (offer.Ips.Length > 0 && offer.Port > 0)
            {
                tcpTransport = new TcpDirectTransport();
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(TimeSpan.FromSeconds(TcpConnectTimeoutSeconds));
                try
                {
                    await tcpTransport.ConnectAsync(offer.Ips, offer.Port, probeCts.Token);
                    connected = true;
                }
                catch
                {
                    // TCP 失敗 = 別ネットワーク。probe 側で StunAssisted / Relay 推定にフォールバック
                }
            }

            var answerInfo = new ConnectionInfo
            {
                Ips = TcpDirectTransport.GetLocalIpAddresses(),
                Port = 0,
                Probe = true,
                Connected = connected,
                Route = connected ? RouteDirect : RouteNeedRelay,
                From = _deviceId,
                // v1.0.38 review fix v12: 受信した offer の Nonce をそのまま echo する。
                // probe sender 側がこの nonce を見て「自分宛 answer か / 相手の別 probe か」を判別する
                // v14: per-nonce key 化により nonce 一致は key で保証されるが、互換 / デバッグのため payload にも残す
                Nonce = offer.Nonce,
            };
            // v1.0.38 review fix v14: per-nonce key 配下の answer に書き込む (旧 v4 の単一 slot 撤去)
            await probeSig.SendProbeAnswerAsync(pairId, nonce, SerializeConnectionInfo(answerInfo), ct);
        }
        finally
        {
            try { tcpTransport?.Close(); tcpTransport?.Dispose(); } catch { }
            probeSig.Dispose();
        }
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

            // ① TCP リスナー起動 → offer 送信（STUN なし）
            StatusMessageChanged?.Invoke(this, "Status.Phase.TcpPreparing");
            var tcpTransport = new TcpDirectTransport();
            var port = tcpTransport.StartListener();

            var localIps = TcpDirectTransport.GetLocalIpAddresses();
            var offerInfo = new ConnectionInfo
            {
                Ips = localIps,
                Port = port,
                RelayUrl = RelayUrl,
                From = _deviceId,  // v1.0.38 review fix v2: 通常 offer にも From をセット (受信側が自己 offer を無視するため)
            };
            var offerJson = SerializeConnectionInfo(offerInfo);
            Util.Logger.Log($"接続情報送信: ips=[{string.Join(", ", localIps.Select(Util.Logger.MaskIp))}], port={port}");
            await _signaling.SendSdpOfferAsync(pairId, offerJson, ct);

            // ② TCP accept + Answer ポーリングを同時待機
            StatusMessageChanged?.Invoke(this, "Status.Phase.TcpConnecting");
            //    Answer が TCP 結果を通知してくるので、固定タイムアウト不要
            var tcpAcceptTask = tcpTransport.AcceptAsync(ct);
            var answerTask = _signaling.WaitForSdpAsync(pairId, "answer", ct: ct);

            // どちらか先に完了した方で判断
            var completedTask = await Task.WhenAny(tcpAcceptTask, answerTask);

            var connected = false;

            if (completedTask == tcpAcceptTask && tcpAcceptTask.IsCompletedSuccessfully)
            {
                // TCP 接続成功（LAN 内）
                Util.Logger.Log("TCP 直接接続成功");
                connected = true;
                _transport = tcpTransport;
                AttachTransportEvents();

                // Answer ポーリングも完了を待つ（確認応答）
                await answerTask;
                Util.Logger.Log("接続確認応答受信");
            }
            else
            {
                // Answer が先に到着 → route を確認
                string? answerJson = null;
                try
                {
                    answerJson = await answerTask;
                }
                catch (Exception ex)
                {
                    Util.Logger.Log($"Answer 受信エラー: {ex.Message}", Util.LogLevel.Error);
                }

                tcpTransport.Dispose(); // TCP は不要

                if (answerJson != null)
                {
                    Util.Logger.Log("Answer が TCP 失敗報告 → STUN/UDP ホールパンチ試行");
                    StatusMessageChanged?.Invoke(this, "Status.Phase.StunQuery");

                    // ③ STUN + UDP ホールパンチを試行
                    var udpTransport = new UdpHolePunchTransport();
                    var stunResult = await udpTransport.GetExternalEndpointAsync(ct: ct);

                    if (stunResult != null)
                    {
                        Util.Logger.Log($"STUN 外部エンドポイント取得: {Util.Logger.MaskIp(stunResult.Value.ip)}:{stunResult.Value.port}");

                        // 外部エンドポイントを offer に追加送信
                        var updatedOffer = new ConnectionInfo
                        {
                            Ips = localIps,
                            Port = port,
                            ExternalIp = stunResult.Value.ip,
                            ExternalPort = stunResult.Value.port,
                            RelayUrl = RelayUrl,
                            From = _deviceId,  // v1.0.38 review fix v2
                        };
                        await _signaling.SendSdpOfferAsync(pairId, SerializeConnectionInfo(updatedOffer), ct);

                        StatusMessageChanged?.Invoke(this, "Status.Phase.UdpHolePunch");
                        connected = await TryUdpHolePunchOfferAsync(udpTransport, pairId, ct);
                    }
                    else
                    {
                        Util.Logger.Log("STUN 外部エンドポイント取得失敗（UDP ホールパンチ不可）");
                        udpTransport.Dispose();
                    }

                    // ④ WebSocket リレーにフォールバック
                    if (!connected)
                    {
                        StatusMessageChanged?.Invoke(this, "Status.Phase.Relay");
                        var relayConnected = await TryRelayConnectAsync(pairId, "offer", ct);
                        if (!relayConnected)
                            throw new InvalidOperationException("全ての接続方法が失敗しました");
                    }
                }
            }

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

    /// <summary>
    /// 軽量プローブで経路だけ判定する（データチャンネルは確立せず即切断）。
    /// メインの <see cref="_signaling"/> / <see cref="_transport"/> / <see cref="State"/> には触らず、
    /// 一時的な FirebaseSignaling と transport を使う。着信監視 (StartListeningForConnection) は
    /// Probe 中だけ停止して、競合を避ける。
    /// TCP/UDP のみ実試行、リレーは推定（Cloudflare Workers の DO duration コスト回避）。
    /// </summary>
    public async Task<ConnectionRoute> ProbeRouteAsync(string peerId, CancellationToken ct = default)
    {
        // v1.0.38 review fix #5: 既に同じピアと通常接続中 / 接続試行中なら現 Route を返す。
        // 他のピアに接続中の場合は他ピアの Route が混入するので Unknown を返してスキップ。
        if (State is PeerState.Connecting or PeerState.Connected)
        {
            if (ConnectedPeer?.SessionId == peerId)
            {
                Util.Logger.Log($"Probe スキップ: 同じピアに接続中 → 現 Route={Route} を返す");
                return Route;
            }
            Util.Logger.Log($"Probe スキップ: 別のピアに接続中 (current={ConnectedPeer?.SessionId}, probe={peerId})");
            return ConnectionRoute.Unknown;
        }

        Util.Logger.Log($"経路 Probe 開始: peer={peerId}");

        // v1.0.38 review fix v2: probe 中も listening を維持する。
        // 自分の probe offer は ConnectionInfo.From=_deviceId で識別して
        // ListenForIncomingConnectionAsync が自己 offer として無視するので競合しない。
        // これで probe 中に selected peer の real transfer offer を見落とすバグが解消する

        FirebaseSignaling? probeSig = null;
        TcpDirectTransport? tcpTransport = null;
        UdpHolePunchTransport? udpTransport = null;
        var pairId = GeneratePairId(_deviceId, peerId);

        // v1.0.38 review fix v12: 自分の probe を識別する nonce。bidirectional 同時 probe で
        // 相手の probe の answer を誤認しないよう、answer 側が echo する nonce を発行する。
        // v15 review fix (Codex P2 #3318349010): 旧 `probeCreatedAt` (cross-device clock 比較
        // 用 cutoff) は撤去。per-nonce key (毎 probe 新規 GUID) で stale answer は構造的に隔離
        // されるため、答え側 PC の時計が遅れていても fresh answer を正しく拾えるようになった。
        var probeNonce = Guid.NewGuid().ToString("N")[..16];

        try
        {
            probeSig = new FirebaseSignaling(_databaseUrl);
            // v1.0.38 review fix v12: probe では RegisterSessionAsync を呼ばない。
            // 旧実装は sessions/{deviceId} を書き込んでいたため、probe 中の peer に対して
            // 別デバイスが PairFromCodeAsync で deviceId を入力すると CheckSessionAsync が
            // 通過してしまい、一方的 pairing 記録が残る (この peer は pairings/ entry を listen
            // していないので異常状態) というセキュリティバグがあった。
            // probe は signaling/<pairId>/probeOffer に書き込むだけで動くので session 不要。

            // v1.0.38 review fix v4: probe を専用ノード (signaling/<pairId>/probeOffer +
            // probeAnswer + probeCreatedAt + probeAnswerCreatedAt) に完全分離。
            // 通常の offer / answer / createdAt スロットを一切触らないので、
            // 同じ pair で real connection と probe が同時に走っても互いに干渉しない

            // ① TCP リスナー起動 → probe 専用 offer 送信
            tcpTransport = new TcpDirectTransport();
            var port = tcpTransport.StartListener();
            var localIps = TcpDirectTransport.GetLocalIpAddresses();
            var offerInfo = new ConnectionInfo
            {
                Ips = localIps,
                Port = port,
                RelayUrl = RelayUrl,
                Probe = true,
                From = _deviceId,  // listening 側で probeOffer.From == _deviceId は無視 (自己 probe スキップ)
                Nonce = probeNonce,  // v12: answer 側に echo してもらって自分宛 answer かを判別
            };
            // v1.0.38 review fix v14: per-nonce key 化 — probeOffers/<nonce>/{data, createdAt}
            await probeSig.SendProbeOfferAsync(pairId, probeNonce, SerializeConnectionInfo(offerInfo), ct);

            // ② TCP accept + probe answer ポーリングを同時待機
            //   UDP ホールパンチ probe は撤去 (TCP 失敗時は StunAssisted 推定)
            // v1.0.38 review fix v10: タイムアウトを listener の最悪ケースに合わせて延長。
            //   listener 側 polling iteration 内訳 (worst case):
            //     - WaitForSdpAsync(offer, 5s) で待機中に probe offer 到着 → 5s 後 next iter
            //     - TryReadProbeOfferAsync で検出 (即時)
            //     - HandleProbeOfferAsync の TCP connect 5s (TcpConnectTimeoutSeconds)
            //     - probeSig.SendProbeAnswerAsync (Firebase write 1-2s)
            //   合計 ~13s なので 15s に設定 (旧 7s では非 LAN ピアで answer が間に合わずに Unknown)
            using var stageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stageCts.CancelAfter(TimeSpan.FromSeconds(ProbeOverallTimeoutSeconds));
            var tcpAcceptTask = tcpTransport.AcceptAsync(stageCts.Token);
            // v1.0.38 review fix v14: 自分の nonce 配下の answer のみ待つ
            // (他 probe の answer は別 key なので絶対に混入しない)
            // v15 review fix (Codex P2 #3318349010): cross-device clock 比較は撤廃。
            // nonce が毎 probe で fresh GUID なので、probeAnswers/<nonce> に entries が存在する
            // = 自分宛 fresh answer と確定する (stale answer の混入経路がない)。
            var answerTask = probeSig.WaitForProbeAnswerAsync(pairId, probeNonce, ct: stageCts.Token);

            Task completedTask;
            try
            {
                completedTask = await Task.WhenAny(tcpAcceptTask, answerTask);
            }
            catch (OperationCanceledException)
            {
                // v1.0.38 review fix v7: タイムアウト = answer 来てない = 相手がこちらを listening していない可能性あり。
                // 経路をテストできていないので StunAssisted 推定ではなく Unknown を返す
                Util.Logger.Log("Probe: TCP/Answer 待機タイムアウト → Unknown (相手が listening していない可能性)");
                return ConnectionRoute.Unknown;
            }

            // v1.0.38 review fix v5: 負け task をキャンセルして観察する (background loop 累積防止)。
            stageCts.Cancel();

            ConnectionRoute resultRoute;
            if (completedTask == tcpAcceptTask && tcpAcceptTask.IsCompletedSuccessfully)
            {
                Util.Logger.Log("Probe: TCP 直接接続成功 → Direct");
                resultRoute = ConnectionRoute.Direct;
            }
            else
            {
                // Answer 側が TCP 結果を通知
                string? answerJson = null;
                try { answerJson = await answerTask; }
                catch { /* Answer 取得失敗 → Unknown (相手が listening していない可能性) */ }

                if (answerJson != null)
                {
                    var answer = DeserializeConnectionInfo(answerJson);
                    // v1.0.38 review fix v14: nonce 一致は key path (probeAnswers/<nonce>) で既に保証済み。
                    // payload 内 Nonce 検証は二重防護のため残す (互換 / デバッグ用) が、key 検証で十分
                    if (answer?.Nonce != null && answer.Nonce != probeNonce)
                    {
                        Util.Logger.Log($"Probe: Answer nonce 異常 (key={probeNonce}, payload={answer.Nonce}) → Unknown");
                        resultRoute = ConnectionRoute.Unknown;
                    }
                    else if (answer?.Route == RouteDirect)
                    {
                        Util.Logger.Log("Probe: Answer 側で TCP 成功通知 → Direct");
                        resultRoute = ConnectionRoute.Direct;
                    }
                    else
                    {
                        // v1.0.38 review fix v7: Answer 側が TCP 失敗を実際に報告した時のみ StunAssisted 推定
                        // (relay 可能性もあるが実 transfer で確定 / overwrite される)
                        Util.Logger.Log("Probe: Answer 側で TCP 失敗報告 → StunAssisted 推定 (実 transfer で確定)");
                        resultRoute = ConnectionRoute.StunAssisted;
                    }
                }
                else
                {
                    // v1.0.38 review fix v7: Answer 未到着 = 相手がこちらを listening していない =
                    // 経路がテストされていない → Unknown を返す (StunAssisted 誤表示を避ける)
                    Util.Logger.Log("Probe: Answer 未到着 → Unknown (経路未テスト)");
                    resultRoute = ConnectionRoute.Unknown;
                }
            }

            // 負け task が disposed Firebase client を polling し続けないよう observe (例外は握りつぶす)
            await ObserveTaskAsync(tcpAcceptTask);
            await ObserveTaskAsync(answerTask);

            return resultRoute;
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"Probe 例外: {ex.Message} → Unknown", Util.LogLevel.Warning);
            return ConnectionRoute.Unknown;
        }
        finally
        {
            // 確立した transport は全て即切断（データチャンネルは使わない）
            try { tcpTransport?.Close(); tcpTransport?.Dispose(); } catch { }
            try { udpTransport?.Close(); udpTransport?.Dispose(); } catch { }

            // v1.0.38 review fix v14: 自分の per-nonce probe ノードを即時 cleanup する。
            // 旧 v7 は単一スロットだったので「live signaling を壊さないため省略」していたが、
            // v14 では per-nonce key なので自分のノードだけ消せば他に影響なし。
            // 残骸蓄積防止 + listener 側の "既処理 nonce" set のフラッシュタイミング短縮
            if (probeSig != null)
            {
                try { await probeSig.CleanupProbeAsync(pairId, probeNonce); }
                catch (Exception ex) { Util.Logger.Log($"Probe cleanup エラー: {ex.Message}", Util.LogLevel.Warning); }
            }
            probeSig?.Dispose();

            // v1.0.38 review fix v2: listening は probe 中も停止していないので再開不要
            Util.Logger.Log($"経路 Probe 終了: peer={peerId}");
        }
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (_transport == null || !_transport.IsConnected)
            throw new InvalidOperationException("接続されていません");

        await _transport.SendAsync(data, ct);
    }

    /// <summary>
    /// P-1: ArrayPool 借用バッファをコピーなしで transport の Memory 版に流す送信パス。
    /// 1GB 転送で約 1GB の Gen0 alloc 削減（チャンクメッセージごとの new byte[] 解消）。
    /// </summary>
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
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
    /// UDP ホールパンチを試行する（Offer 側）。
    /// Answer 側の外部エンドポイントを Firebase からポーリングし、取得後にホールパンチを実行する。
    /// </summary>
    private async Task<bool> TryUdpHolePunchOfferAsync(UdpHolePunchTransport udpTransport, string pairId, CancellationToken ct)
    {
        try
        {
            Util.Logger.Log("UDP ホールパンチ（Offer 側）: Answer の外部エンドポイント待機中…");

            // Answer 側の外部エンドポイントをポーリング（最大 10 秒待機）
            using var epCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            epCts.CancelAfter(TimeSpan.FromSeconds(10));

            string endpointStr;
            try
            {
                endpointStr = await _signaling!.WaitForEndpointAsync(pairId, "answer", epCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Util.Logger.Log("Answer の外部エンドポイント取得タイムアウト", Util.LogLevel.Warning);
                udpTransport.Dispose();
                return false;
            }

            var parts = endpointStr.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var remotePort))
            {
                Util.Logger.Log($"Answer エンドポイントのパース失敗: {endpointStr}", Util.LogLevel.Warning);
                udpTransport.Dispose();
                return false;
            }

            // ホールパンチ実行
            using var punchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            punchCts.CancelAfter(TimeSpan.FromSeconds(UdpHolePunchTimeoutSeconds));

            await udpTransport.HolePunchAsync(parts[0], remotePort, punchCts.Token);

            DetachTransportEvents();
            _transport?.Dispose();
            _transport = udpTransport;
            AttachTransportEvents();
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Util.Logger.Log("UDP ホールパンチ（Offer 側）タイムアウト", Util.LogLevel.Warning);
            udpTransport.Dispose();
            return false;
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"UDP ホールパンチ（Offer 側）失敗: {ex.Message}", Util.LogLevel.Warning);
            udpTransport.Dispose();
            return false;
        }
    }

    /// <summary>
    /// UDP ホールパンチを試行する（Answer 側）。
    /// STUN で自身の外部エンドポイントを取得し、Firebase に書き込んでからホールパンチを実行する。
    /// </summary>
    private async Task<bool> TryUdpHolePunchAnswerAsync(ConnectionInfo offer, string pairId, CancellationToken ct)
    {
        UdpHolePunchTransport? udpTransport = null;
        try
        {
            Util.Logger.Log("UDP ホールパンチ（Answer 側）開始: STUN クエリ実行中…");
            udpTransport = new UdpHolePunchTransport();
            var stunResult = await udpTransport.GetExternalEndpointAsync(ct: ct);

            if (stunResult == null)
            {
                Util.Logger.Log("STUN 外部エンドポイント取得失敗", Util.LogLevel.Warning);
                udpTransport.Dispose();
                return false;
            }

            Util.Logger.Log($"STUN 外部エンドポイント取得: {Util.Logger.MaskIp(stunResult.Value.ip)}:{stunResult.Value.port}");

            // 自身の外部エンドポイントを Firebase に書き込み（Offer 側が読む）
            await _signaling!.SendEndpointAsync(pairId, "answer", $"{stunResult.Value.ip}:{stunResult.Value.port}", ct);

            // Offer 側の外部エンドポイントに向けてホールパンチ実行
            using var punchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            punchCts.CancelAfter(TimeSpan.FromSeconds(UdpHolePunchTimeoutSeconds));

            await udpTransport.HolePunchAsync(offer.ExternalIp!, offer.ExternalPort, punchCts.Token);

            DetachTransportEvents();
            _transport?.Dispose();
            _transport = udpTransport;
            AttachTransportEvents();
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Util.Logger.Log("UDP ホールパンチ（Answer 側）タイムアウト", Util.LogLevel.Warning);
            udpTransport?.Dispose();
            return false;
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"UDP ホールパンチ（Answer 側）失敗: {ex.Message}", Util.LogLevel.Warning);
            udpTransport?.Dispose();
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

    /// <summary>STUN で取得した外部 IP アドレス（UDP ホールパンチ用）。</summary>
    [JsonPropertyName("externalIp")]
    public string? ExternalIp { get; set; }

    /// <summary>STUN で取得した外部ポート番号（UDP ホールパンチ用）。</summary>
    [JsonPropertyName("externalPort")]
    public int ExternalPort { get; set; }

    /// <summary>WebSocket リレーサーバーの URL（NAT 越え用フォールバック）。</summary>
    [JsonPropertyName("relayUrl")]
    public string? RelayUrl { get; set; }

    /// <summary>接続確認フラグ（Answer 側が true を返す）。</summary>
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    /// <summary>
    /// Answer 側が TCP 接続結果を通知するフィールド。
    /// "direct" = TCP 成功、"needRelay" = TCP 失敗（Offer 側は STUN/リレーへ遷移）。
    /// </summary>
    [JsonPropertyName("route")]
    public string? Route { get; set; }

    /// <summary>
    /// v1.0.38 review fix #6: 経路 Probe 用 offer/answer を識別するフラグ。
    /// true の場合、相手側 (ListenForIncomingConnectionAsync) は通常の transport 確立ではなく
    /// TCP 接続試行のみで answer を返し、_transport / State を変更しない。
    /// これで Probe が通常接続フローに混入する事故を防ぐ。
    /// </summary>
    [JsonPropertyName("probe")]
    public bool Probe { get; set; }

    /// <summary>
    /// v1.0.38 review fix v2: offer の送信元 deviceId。
    /// 同じ pairId を共有する自分の probe offer を ListenForIncomingConnectionAsync が
    /// 自分で拾わないように区別する用 (From == 自分の deviceId なら無視)。
    /// これで probe 中も listening を停止する必要がなくなり、
    /// "probe 中に selected peer の real offer を見落とす" バグが解消する。
    /// </summary>
    [JsonPropertyName("from")]
    public string? From { get; set; }

    /// <summary>
    /// v1.0.38 review fix v12: 双方向同時 probe の race を防ぐための probe 識別子。
    /// 単一スロット (probeOffer/probeAnswer) を両側が共有しているので、
    /// 後勝ち上書きで自分の offer が消され、相手の answer を自分の answer と誤認するレースが起きる。
    /// 修正: 自分の probe ごとに nonce を発行し、answer 側はこれを echo する。
    /// 受信側 (probe sender) は answer.Nonce が自分の発行 nonce と一致するときだけ採用、
    /// 不一致なら Unknown (= 相手の別 probe の応答を拾った状態)。
    /// </summary>
    [JsonPropertyName("nonce")]
    public string? Nonce { get; set; }
}

/// <summary>
/// Native AOT 対応の JSON シリアライザコンテキスト。
/// </summary>
[JsonSerializable(typeof(ConnectionInfo))]
internal sealed partial class ConnectionInfoJsonContext : JsonSerializerContext;
