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

    /// <summary>UDP ホールパンチ: Offer 側が Answer の外部エンドポイントを待つ時間（秒）。
    /// ⚠ 必ず <see cref="OfferV2PollSeconds"/> より長くすること。Answer 側は ExternalIp 付き offer-v2 を
    /// 最大 OfferV2PollSeconds 読み直してから自分の endpoint を publish するため、
    /// 待ち(10s) ＜ ポーリング(8s) になると endpoint 到着前に Offer 側が諦め、cross-NAT で必ず
    /// リレー転落する（CLAUDE.md 記載の構造バグ class）。rere #B2-002: 直書きを定数化し依存を明文化。</summary>
    private const int OfferEndpointWaitSeconds = 10;
    /// <summary>UDP ホールパンチ: Answer 側が ExternalIp 付き offer-v2 を読み直すポーリング時間（秒）。
    /// 必ず <see cref="OfferEndpointWaitSeconds"/> より短く保つこと（上記参照）。</summary>
    private const int OfferV2PollSeconds = 8;
    /// <summary>接続確認応答（補助情報）の打ち切り時間（秒）。endpoint 待ちとは別意味だが現状同値。</summary>
    private const int AnswerConfirmWaitSeconds = 10;

    /// <summary>Offer 側が「TCP accept または answer のどちらか」を待つ全体タイムアウト（秒）。
    /// 旧コードは answerCts に CancelAfter を設定せず WhenAny に渡していたため、相手が無応答
    /// (オフライン / 旧バージョン非互換 / シグナリング不成立) かつ TCP も来ない場合に WhenAny が
    /// 永久待機し、UI が「待機中」のまま固着していた (実機ログ 2026-06-17 で確認)。answer は通常 ~6s で
    /// 届く (相手の TCP 5s + offer-v2 ポーリング + Firebase write を含む) ので、余裕を持たせて 20s。
    /// 超過時は answerTask が cancel されて WhenAny を抜け、answerJson==null 経路で明示エラーに落とす
    /// (相手不在なのでリレーも来ず、即エラーの方が体感が良い。answer が来た上での UDP 失敗は別途リレーへ)。</summary>
    private const int OfferAnswerWaitSeconds = 20;

    /// <summary>WebSocket リレーで「相手が同じ部屋に来る」のを待つ上限（秒）。双方が UDP タイムアウト(8s)後に
    /// ほぼ同時にリレーへ来るため十数秒で足りる。旧 30s は、片側だけ UDP が非対称成功して相手がリレーに来ない
    /// ケースで 30s 丸ごと空振りし、その後の接続リトライ (NAT が温まり UDP が即成功) までの体感待ちを 50s 超に
    /// していた (実機ログ 2026-06-17)。15s に短縮して空振り時の浪費を半減する (正規のリレー合流は数秒で済む)。</summary>
    private const int RelayPeerWaitSeconds = 15;

    /// <summary>rere #D-003: role調停の deferral 判定で「ペア相手の offer が新しい」とみなす上限(ms)。
    /// この時間内に作られた offer なら相手は今まさに接続を試みていると判断して譲歩する。
    /// 過去に放置された古い offer (cleanup 漏れ/Firebase 6h cleanup 待ち) で誤って譲歩しないための鮮度ゲート。</summary>
    private const int RoleDeferFreshnessMs = 60_000;

    /// <summary>rere PR#8 #F2: role調停で listener に委譲した後、listener が実際に接続を確立 (State=Connected)
    /// するまで待つ上限(秒)。委譲先が answerer 経路で TCP(5s)→UDP(8s)→relay を試す全体時間をカバーする値
    /// (<see cref="ProbeOverallTimeoutSeconds"/> と同等)。時間内に確立できなければ通常 offerer 経路へ
    /// フォールバックする (per-sender ノード #D-003 で同時 offer は安全なので回帰しない)。</summary>
    private const int RoleDeferListenTimeoutSeconds = 15;

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
    /// <summary>rere PR#8 #F2: 着信監視タスクの参照。role調停フォールバック時に Cancel 後 *完了まで* await して
    /// listener と本体のテアダウン競合 (確立直後の _transport をサイレント破壊) を防ぐために保持する。</summary>
    private Task? _listeningTask;

    /// <summary>
    /// オンデマンド接続 (ConnectToPeerAsync) の所有 CTS と直列化ゲート。
    /// 再送等で接続フローが多重起動すると、同一 pairId に offer 側フロー (answer ポーラー →
    /// STUN → endpoint 待ち → リレー offer 接続) が並走し、二重 answer 受信 → 二重リレー接続 →
    /// 409 / _transport 上書き Dispose で接続が崩壊する。所有 CTS で in-flight (孤児ポーラー含む) を
    /// 中断し、SemaphoreSlim で本体を直列化して、同時に 1 本だけ走るようにする。
    /// </summary>
    private CancellationTokenSource? _connectCts;
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    /// <summary>
    /// 現在の State=Connecting を立てたのが着信監視ループ (listener) かどうか。
    /// listener のキャンセル復旧ブロックは、自分が立てた Connecting だけを Disconnected に
    /// 巻き戻すためにこれを確認する。ConnectToPeerAsync が Connecting を立て直した後に
    /// listener の遅延キャンセル処理が走っても、オンデマンド接続側の状態と transport を
    /// 誤って破棄しないようにする (同時接続競合時の踏み潰し防止)。
    /// </summary>
    private volatile bool _connectingByListener;

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
    /// v1.0.38: ペアリングコード (32 文字 hex = sessionId) を直接受け取ってペアリングする。
    /// URL 貼り付け方式 (旧 PairFromUrlAsync、Bridge の URL ペアリング撤去に伴い削除) と違って
    /// ブラウザで開かれる事故が起きない。
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
        // rere PR#8 #F2: タスクを保持して role調停フォールバック時に Cancel 後の完了 await を可能にする。
        _listeningTask = ListenForIncomingConnectionAsync(peerId, _listeningCts.Token);
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

        // この iteration で自分が SetState(Connecting) を立てて着信 offer を処理中かどうか。
        // キャンセル catch で State を復旧する条件に使う (ConnectToPeerAsync 側が立てた Connecting を
        // 誤って巻き戻さないよう、自分が立てたときだけ復旧する)
        var processingOffer = false;

        // ポーリング用 Firebase クライアントはループ全体で再利用する
        // （毎反復で new すると接続/TLS ハンドシェイクの churn が発生するため）
        using var pollingSignaling = new FirebaseSignaling(_databaseUrl);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                processingOffer = false;

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
                    // rere #A1-001: probe も通常 offer と同様、送信元がペア相手であることを要求する
                    if (probeOffer == null || probeOffer.From != peerId) continue;
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
                    offerJson = await pollingSignaling.WaitForOfferAsync(pairId, peerId, minCreatedAt, offerCts.Token);
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
                // rere #A1-001: 「自分以外」だけでなく「ペア相手の deviceId と一致」を要求する。
                // pairId を知る第三者が signaling に偽 offer を書き込んだとき、攻撃者の IP へ
                // TCP 接続しに行く誘導 (MITM) を防ぐ (relay 側の hashPairId 防御と対称化)
                if (offer.From != peerId)
                {
                    Util.Logger.Log(
                        offer.From == _deviceId
                            ? $"自己 offer を無視: pairId={pairId}"
                            : $"ペア相手以外からの offer を破棄: pairId={pairId}, from={Util.Logger.MaskDeviceId(offer.From)}",
                        Util.LogLevel.Warning);
                    minCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    continue;
                }

                Util.Logger.Log($"着信接続情報検知！ Answer 側として接続開始: pairId={pairId}, ips=[{string.Join(", ", offer.Ips.Select(Util.Logger.MaskIp))}], port={offer.Port}");
                _connectingByListener = true;  // Connecting の所有権は listener (キャンセル復旧の判定に使う)
                SetState(PeerState.Connecting);
                processingOffer = true;
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
                    From = _deviceId,  // rere #D-001: answer にも送信元 deviceId を載せ offer と対称化 (MITM 検証用)
                };
                var answerJson = SerializeConnectionInfo(answerInfo);
                await sig.SendSdpAnswerAsync(pairId, _deviceId, answerJson, ct);

                // ② TCP 失敗時: UDP ホールパンチを試行
                // offer-v1 には STUN 情報が無い（Offer 側は answer=needRelay を受信した後に STUN し、
                // ExternalIp 付きの offer を同じノードへ上書き再送する遅延 STUN 設計）。ここで offer を
                // 読み直して ExternalIp が載るのを待ってから UDP を試みる。これが無いと最初に読んだ
                // offer-v1 の ExternalIp が常に空 → UDP を一切起動せず（自分の endpoint も publish せず）
                // cross-NAT では必ずリレーへ落ちていた（Answer 側が UDP ホールパンチに到達しない構造バグの修正）。
                if (!connected)
                {
                    var udpOffer = await WaitForOfferExternalIpAsync(sig, pairId, offer, peerId, ct);
                    if (udpOffer != null)
                    {
                        StatusMessageChanged?.Invoke(this, "Status.Phase.UdpHolePunch");
                        connected = await TryUdpHolePunchAnswerAsync(udpOffer, pairId, ct);
                    }
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
                // 監視停止 (ピア切替 / ピア削除 / 接続開始) によるキャンセル。着信 offer 処理中
                // (リレー試行中など) に中断された場合、Connecting のまま残すと次の監視ループが
                // 「接続中」と誤認して着信を永久に処理できなくなるため、自分が立てた Connecting に
                // 限り後始末して Disconnected へ戻す (ConnectToPeerAsync のキャンセル経路と対称)。
                // _connectingByListener は ConnectToPeerAsync が Connecting を立て直した後の
                // 遅延キャンセル処理がオンデマンド側の状態 / transport を踏み潰すのを防ぐ
                if (processingOffer && State == PeerState.Connecting && _connectingByListener)
                {
                    DetachTransportEvents();
                    _transport?.Dispose();
                    _transport = null;
                    SetState(PeerState.Disconnected);
                }
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
        // 進行中の接続 (孤児ポーラー含む) を先に中断してから直列化ゲートを取る。
        // 順序を逆 (Gate→Cancel) にすると、自然完了しない孤児ポーラーを抱えたまま待ち、自己デッドロックする。
        _connectCts?.Cancel();
        await _connectGate.WaitAsync(ct);

        // 所有権がまだ _transport に移っていないローカル transport を例外/キャンセル経路で確実に破棄する。
        // (bound ソケット / LISTEN ポートが GC ファイナライザ回収までリークするのを防ぐ。Dispose は冪等)
        TcpDirectTransport? tcpTransport = null;
        UdpHolePunchTransport? udpTransport = null;
        void DisposeOrphanTransports()
        {
            if (tcpTransport != null && !ReferenceEquals(tcpTransport, _transport))
            {
                try { tcpTransport.Dispose(); } catch { }
            }
            if (udpTransport != null && !ReferenceEquals(udpTransport, _transport))
            {
                try { udpTransport.Dispose(); } catch { }
            }
        }

        try
        {
            _connectCts?.Dispose();
            _connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var linked = _connectCts.Token;

            Util.Logger.Log($"オンデマンド接続開始: peer={peerId}, deviceId={_deviceId}");

            var pairId = GeneratePairId(_deviceId, peerId);

            // rere #D-003: role調停。deviceId 序列で「主 offerer(小) / 譲歩側(大)」を決める。
            // 譲歩側は、ペア相手由来の *新しい* offer が既に出ていれば自分の offer を送らず、着信監視
            // (answerer 経路) に処理を委ねて即 return する。これで双方が同時に接続した際の offer 相互上書き
            // (両者 offerer になり相手の answer を待ち続けてデッドロック → 失敗/不要リレー) を緩和する。
            // schema 不変・既存 answerer 経路の再利用・最悪でも現状維持(回帰なし)。鮮度ゲートで過去の
            // 放置 offer による誤譲歩を防ぐ。完全な同時ウィンドウ解消は per-role offer node 化が必要(設計課題)。
            if (string.CompareOrdinal(_deviceId, peerId) > 0)
            {
                using var peekSig = new FirebaseSignaling(_databaseUrl);
                long? createdAt = null;
                // rere PR#8 #F6: OperationCanceledException は握り潰さず伝播させる (cancel 要求中は offer を
                // 送らず即中断するため)。それ以外の peek 失敗は「相手 offer 不明」として通常 offerer 経路へ。
                try { createdAt = await peekSig.TryReadOfferCreatedAtAsync(pairId, peerId, linked); }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                // rere PR#8 #F7: 未来日付 offer (ageMs<0) を「新鮮」と誤判定して譲歩しないよう、
                // 0 <= ageMs < RoleDeferFreshnessMs の正当な鮮度窓のみ譲歩対象とする (時計ずれ/細工対策)。
                var ageMs = createdAt.HasValue ? nowMs - createdAt.Value : long.MinValue;
                if (createdAt.HasValue && ageMs >= 0 && ageMs < RoleDeferFreshnessMs)
                {
                    string? peekJson = null;
                    try { peekJson = await peekSig.TryReadOfferOnceAsync(pairId, peerId, linked); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }
                    var peekOffer = peekJson == null ? null : DeserializeConnectionInfo(peekJson);
                    if (peekOffer != null && peekOffer.From == peerId)
                    {
                        Util.Logger.Log(
                            $"role調停: ペア相手 ({Util.Logger.MaskDeviceId(peerId)}) が既に offer 済み → " +
                            $"answerer に委譲し offer 送信を見送る (pairId={pairId})");
                        StartListeningForConnection(peerId);  // listener が peer の offer を answer し State=Connected へ
                        // rere PR#8 #F2: 通常経路は SetState(Connected) 後にのみ return するのに対し、旧コードは
                        // listener 起動直後に return していた。listener が確立に失敗すると呼び出し側は「成功」と誤認し、
                        // State が Connected にならないまま例外も出ず *サイレント未接続* になる。listener が実際に
                        // Connected を確立するまで待ち、確立すれば委譲成功で return。時間内に確立できなければ
                        // 下の通常 offerer 経路へフォールバックして自分でも接続を試みる (#D-003 で同時 offer は安全)。
                        if (await WaitForListenerConnectedAsync(RoleDeferListenTimeoutSeconds * 1000, linked))
                            return;  // 委譲成功。finally で gate 解放
                        // rere PR#8 #F2 verify: timeout フォールバック前に listener を *完了まで* 畳む。
                        // StopListeningForConnection は Cancel するだけでタスク完了を待たない (listener はループ型で
                        // 失敗時も再ポーリングを続ける) ため、待たずに下流の _transport?.Dispose() へ進むと、listener が
                        // ちょうど確立した transport をサイレント破壊する競合窓が残る。Cancel → タスク完了 await →
                        // 最終 State 確認 の順で窓を塞ぐ (listener は _connectGate を取らないので await で deadlock しない)。
                        var listenerTask = _listeningTask;
                        StopListeningForConnection();
                        if (listenerTask != null)
                        {
                            try { await listenerTask.WaitAsync(TimeSpan.FromSeconds(6)); }
                            catch { /* listener の faulted/timeout は無視。最終 State で接続成否を判断する */ }
                        }
                        // listener が止まる直前に接続を確立していたら尊重し、確立済み transport を壊さない。
                        if (State == PeerState.Connected)
                            return;
                        Util.Logger.Log(
                            $"role調停: 委譲先 listener が {RoleDeferListenTimeoutSeconds}s 以内に接続確立せず → " +
                            $"通常 offerer 経路へフォールバック (pairId={pairId})", Util.LogLevel.Warning);
                        // listener は上で停止・完了済み。以降の処理が offerer として再試行する。
                    }
                }
            }

            _connectingByListener = false;  // Connecting の所有権をオンデマンド接続側へ移す
            SetState(PeerState.Connecting);

            // 着信監視を一時停止（自分の Offer を自分で拾わないように）
            StopListeningForConnection();

            _signaling?.Dispose();
            _signaling = new FirebaseSignaling(_databaseUrl);

            DetachTransportEvents();
            _transport?.Dispose();
            _transport = null;

            _currentPairId = pairId;
            Util.Logger.Log($"pairId 生成: {pairId}");

            // 古いシグナリングデータを削除
            await _signaling.CleanupSignalingDataAsync(pairId, linked);

            // ① TCP リスナー起動 → offer 送信（STUN なし）
            StatusMessageChanged?.Invoke(this, "Status.Phase.TcpPreparing");
            tcpTransport = new TcpDirectTransport();
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
            await _signaling.SendSdpOfferAsync(pairId, _deviceId, offerJson, linked);

            // ② TCP accept + Answer ポーリングを同時待機
            StatusMessageChanged?.Invoke(this, "Status.Phase.TcpConnecting");
            //    Answer が TCP 結果を通知してくるので、固定タイムアウト不要。
            //    answer ポーリングは専用 CTS で持ち、TCP 成功時に確認応答待ちを打ち切れるようにする
            using var answerCts = CancellationTokenSource.CreateLinkedTokenSource(linked);
            // answer 待ちに全体タイムアウトを設定する。これが無いと相手が無応答 (オフライン / 旧バージョン
            // 非互換 / シグナリング不成立) かつ TCP も来ない場合に下の WhenAny が永久待機し、UI が「待機中」の
            // まま固着する。タイムアウト発火時は answerTask が cancel されて WhenAny を抜け、下の
            // answerJson==null 経路でエラーに落ちる。TCP 成功時は下のブロックで AnswerConfirmWaitSeconds に
            // 再スケジュールするので正常系には影響しない (LAN の TCP accept は通常 1s 未満で先に完了する)。
            // answer 先着 (else 分岐) では answerCts.Token の唯一の consumer (answerTask) が完了済みのため、
            // 後続 STUN/UDP/relay (いずれも linked トークン使用) の最中に 20s が発火しても下流に無影響。
            answerCts.CancelAfter(TimeSpan.FromSeconds(OfferAnswerWaitSeconds));
            var tcpAcceptTask = tcpTransport.AcceptAsync(linked);
            var answerTask = _signaling.WaitForAnswerAsync(pairId, peerId, answerCts.Token);

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

                // Answer ポーリングも完了を待つ（確認応答）。WaitForSdpAsync は内部例外を握りつぶして
                // 無限ポーリングするため、受信側の answer 書き込みが失敗すると TCP 確立済みなのに
                // ここで永久待機する。確認応答は補助情報なので 10 秒で打ち切って接続成立を優先する
                answerCts.CancelAfter(TimeSpan.FromSeconds(AnswerConfirmWaitSeconds));
                try
                {
                    await answerTask;
                    Util.Logger.Log("接続確認応答受信");
                }
                catch (OperationCanceledException) when (!linked.IsCancellationRequested)
                {
                    Util.Logger.Log("接続確認応答タイムアウト (TCP 確立済みのため続行)", Util.LogLevel.Warning);
                }
            }
            else
            {
                // Answer が先に到着 → route を確認
                string? answerJson = null;
                var answerTimedOut = false;
                try
                {
                    answerJson = await answerTask;
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    // ユーザーの転送キャンセル / 新しい接続要求 / 切断によるキャンセル。ここで握りつぶすと
                    // answerJson=null のまま下の Connected 遷移へ素通りし、transport 不在の State=Connected が
                    // 残留して以後の送信が全て「接続されていません」で恒久失敗する (2026-06-10 実機ログで発生)。
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // linked 未発火の OCE = answerCts の全体タイムアウト (OfferAnswerWaitSeconds)。相手から
                    // answer が来ず TCP も来なかった = シグナリング不成立。旧コードはこの分岐が無く (answerCts に
                    // CancelAfter 未設定だったため) WhenAny が永久待機し「待機中」固着していた。answerJson は
                    // null のままにし、下の null 分岐で明示エラーに落とす (相手不在なのでリレーも来ず即エラーが妥当)。
                    answerTimedOut = true;
                    Util.Logger.Log(
                        $"Answer 待機タイムアウト ({OfferAnswerWaitSeconds}s, 相手未応答)", Util.LogLevel.Warning);
                }
                catch (Exception ex)
                {
                    Util.Logger.Log($"Answer 受信エラー: {ex.Message}", Util.LogLevel.Error);
                }
                finally
                {
                    tcpTransport.Dispose(); // TCP は不要 (キャンセル経路でも listener を確実に閉じる)
                    // WhenAny 敗者の AcceptAsync を観測する。Dispose→listener.Stop() で走行中の
                    // AcceptTcpClientAsync が SocketError.Interrupted の SocketException を投げるが、
                    // ct 自体は未キャンセル (OperationCanceledException ではない) ため未観測のまま
                    // finalizer で UnobservedTaskException 化する。ProbeRouteAsync と同様に握りつぶす。
                    _ = ObserveTaskAsync(tcpAcceptTask);
                }

                // Answer 不達 = シグナリング不成立。失敗を確定させずに先へ進むと transport が無いまま
                // Connected になるため、ここで接続失敗として throw する (外側 catch が State=Error にする)。
                // answerTimedOut なら相手不在が原因と分かるメッセージにして UI に出す (無限「待機中」を断つ)。
                if (answerJson == null)
                    throw new InvalidOperationException(
                        answerTimedOut
                            ? "相手から応答がありません（オフライン / 旧バージョン / 接続不可の可能性）"
                            : "Answer を受信できませんでした");

                Util.Logger.Log("Answer が TCP 失敗報告 → STUN/UDP ホールパンチ試行");
                StatusMessageChanged?.Invoke(this, "Status.Phase.StunQuery");

                // ③ STUN + UDP ホールパンチを試行
                udpTransport = new UdpHolePunchTransport();
                var stunResult = await udpTransport.GetExternalEndpointAsync(ct: linked);

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
                    await _signaling.SendSdpOfferAsync(pairId, _deviceId, SerializeConnectionInfo(updatedOffer), linked);

                    StatusMessageChanged?.Invoke(this, "Status.Phase.UdpHolePunch");
                    connected = await TryUdpHolePunchOfferAsync(udpTransport, pairId, peerId, linked);
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
                    var relayConnected = await TryRelayConnectAsync(pairId, "offer", linked);
                    if (!relayConnected)
                        throw new InvalidOperationException("全ての接続方法が失敗しました");
                }
            }

            // キャンセル発火と最終 await の成功完了が重なるレース (UDP PUNCH_ACK / answer 取得が
            // キャンセルと同時に完了するケース) を閉じる: 発火済みなら成功扱いにせず
            // 外側 OCE catch の後始末 (transport 破棄 + Disconnected) に収束させる
            linked.ThrowIfCancellationRequested();

            // 防御: どの経路でも transport が確立していなければ Connected を立てない (偽 Connected 残留の安全網)
            if (_transport == null || !_transport.IsConnected)
                throw new InvalidOperationException("接続経路が確立されていません");

            ConnectedPeer = new PeerInfo
            {
                SessionId = peerId,
                DisplayName = peerId,
                State = PeerState.Connected,
            };
            SetState(PeerState.Connected);
            Util.Logger.Log($"オンデマンド接続完了！ 経路: {_transport?.Route}");
        }
        catch (OperationCanceledException)
        {
            // 新しい接続要求 / DisconnectAsync / ユーザーの転送キャンセルに中断された正常系。
            // Connecting のまま残すと Probe や接続判定が「接続中」と誤認するため、後始末して Disconnected へ戻す。
            // 追い越した側の新しい ConnectToPeerAsync は gate 取得後に自分で Connecting を立て直すので競合しない。
            DisposeOrphanTransports();
            if (State == PeerState.Connecting)
            {
                DetachTransportEvents();
                _transport?.Dispose();
                _transport = null;
                SetState(PeerState.Disconnected);
            }
            Util.Logger.Log("接続試行がキャンセルされました（ユーザー操作または新しい接続要求）");
            throw;
        }
        catch (Exception ex)
        {
            DisposeOrphanTransports();
            // rere #F-003: ex.Message だけだと SocketException 等の汎用文言でどの段の失敗か追えない。
            // LogException で型・stack trace・InnerException・相関 ID(pairId) を残す。
            Util.Logger.LogException("接続エラー", ex);
            SetState(PeerState.Error);
            throw;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>
    /// rere PR#8 #F2: role調停で listener に委譲した後、listener が実際に接続を確立 (State=Connected)
    /// するまで最大 <paramref name="timeoutMs"/> 待つ。Connected になれば true、listener が一度 Connecting
    /// へ進んだ後に失敗 (Disconnected/Error) すれば false を返して即フォールバックさせる。timeout でも false。
    /// listener ループは <see cref="_connectGate"/> を取らず背景で独立に State を進めるため、gate を保持した
    /// まま待っても deadlock しない。ct (新規接続要求/Disconnect) 発火時は OCE を伝播させ上位の後始末に委ねる。
    /// </summary>
    private async Task<bool> WaitForListenerConnectedAsync(int timeoutMs, CancellationToken ct)
    {
        const int PollMs = 200;
        var waited = 0;
        var sawConnecting = false;
        while (waited < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            var s = State;
            if (s == PeerState.Connected) return true;
            if (s == PeerState.Connecting) sawConnecting = true;
            // 一度 Connecting を観測した後で Disconnected/Error に落ちたら listener 失敗 → 即フォールバック。
            // 起動直後の Disconnected (listener が offer 未読の起動窓) は失敗扱いしない。
            else if (sawConnecting && s is PeerState.Disconnected or PeerState.Error) return false;
            await Task.Delay(PollMs, ct);
            waited += PollMs;
        }
        return State == PeerState.Connected;
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
        // in-flight のオンデマンド接続 (孤児ポーラー含む) を中断する。Gate は await しない
        // (Disconnect→Connect の順で呼ばれる経路で自己デッドロックを避けるため、Cancel のみ)。
        _connectCts?.Cancel();
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
    private async Task<bool> TryUdpHolePunchOfferAsync(UdpHolePunchTransport udpTransport, string pairId, string peerId, CancellationToken ct)
    {
        try
        {
            Util.Logger.Log("UDP ホールパンチ（Offer 側）: Answer の外部エンドポイント待機中…");

            // Answer 側の外部エンドポイントをポーリング（最大 10 秒待機）
            using var epCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            epCts.CancelAfter(TimeSpan.FromSeconds(OfferEndpointWaitSeconds));

            string endpointStr;
            try
            {
                // rere #D-001: ペア相手 (peerId) 由来の endpoint だけ採用する (偽 endpoint による UDP 誘導を防ぐ)
                endpointStr = await _signaling!.WaitForEndpointAsync(pairId, peerId, epCts.Token);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // ユーザーの転送キャンセル / 接続置換。false (UDP 失敗) に変換するとキャンセル後も
            // リレー試行へ進んでしまうため、キャンセルとして伝播する (リレー段の rethrow と対称)
            udpTransport.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"UDP ホールパンチ（Offer 側）失敗: {ex.Message}", Util.LogLevel.Warning);
            udpTransport.Dispose();
            return false;
        }
    }

    /// <summary>
    /// Answer 側が UDP ホールパンチへ進む前に、ExternalIp（STUN 後送り）が載った offer を待つ。
    /// 既に ExternalIp を持つ offer ならそのまま返す。持たない場合は Offer 側が answer=needRelay 受信後に
    /// 上書き再送する offer-v2 を最大 8 秒ポーリングして取得する（Offer 側の endpoint 待ち 10 秒に収める）。
    /// MITM 防御（offer.From == ペア相手）は再読み込み分にも適用する。取得できなければ null（→ リレーへ）。
    /// </summary>
    private async Task<ConnectionInfo?> WaitForOfferExternalIpAsync(
        FirebaseSignaling sig, string pairId, ConnectionInfo initialOffer, string peerId, CancellationToken ct)
    {
        // 既に STUN 情報がある offer（probe 等の経路）はそのまま使う。
        if (!string.IsNullOrEmpty(initialOffer.ExternalIp) && initialOffer.ExternalPort > 0)
            return initialOffer;

        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pollCts.CancelAfter(TimeSpan.FromSeconds(OfferV2PollSeconds));
        try
        {
            while (!pollCts.IsCancellationRequested)
            {
                var json = await sig.TryReadOfferOnceAsync(pairId, peerId, pollCts.Token);
                var updated = json == null ? null : DeserializeConnectionInfo(json);
                if (updated != null
                    && updated.From == peerId   // 再読み込みにも MITM 防御を適用（偽 offer すり替え対策）
                    && !string.IsNullOrEmpty(updated.ExternalIp)
                    && updated.ExternalPort > 0)
                {
                    Util.Logger.Log("offer に外部エンドポイント（STUN）を確認 → UDP ホールパンチへ");
                    return updated;
                }
                await Task.Delay(400, pollCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 8 秒以内に ExternalIp 付き offer が来なかった（Offer 側 STUN 失敗等）→ リレーへ委ねる。
            Util.Logger.Log("offer の外部エンドポイント待機タイムアウト → リレーへフォールバック", Util.LogLevel.Warning);
        }
        return null;
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

            // 自身の外部エンドポイントを Firebase に書き込み（Offer 側が読む）。
            // rere #D-001: 送信元 deviceId を埋め込み、Offer 側が MITM 検証できるようにする。
            await _signaling!.SendEndpointAsync(pairId, _deviceId, $"{stunResult.Value.ip}:{stunResult.Value.port}", ct);

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 監視停止によるキャンセル。false (UDP 失敗) に変換するとキャンセル後もリレー試行へ
            // 進んでしまうため、キャンセルとして伝播する (Offer 側 / リレー段の rethrow と対称。
            // listener 側のキャンセル catch が状態復旧を行う)
            udpTransport?.Dispose();
            throw;
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

        WebSocketRelayTransport? relayTransport = null;
        try
        {
            Util.Logger.Log($"WebSocket リレー接続試行: role={role}");
            relayTransport = new WebSocketRelayTransport(RelayUrl, pairId, role);

            using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            relayCts.CancelAfter(TimeSpan.FromSeconds(RelayPeerWaitSeconds));

            await relayTransport.ConnectAsync(relayCts.Token);

            DetachTransportEvents();
            _transport?.Dispose();
            _transport = relayTransport;
            AttachTransportEvents();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // ユーザーの転送キャンセル / 接続置換。false (リレー失敗) に変換すると上位が
            // 「全ての接続方法が失敗」の Error 扱いにするため、キャンセルとして伝播する
            relayTransport?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            relayTransport?.Dispose();
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
        // PR#5 Codex 指摘: transport が ConnectAsync 中（Attach 前）に RouteChanged を発火済みだと
        // Route が Unknown のまま残り、リレー経路のフロー制御ガードが誤って無効化される。
        // Attach 時点の現在値を即時同期して取りこぼしを防ぐ
        if (_transport.Route != ConnectionRoute.Unknown && _transport.Route != Route)
            OnTransportRouteChanged(_transport, _transport.Route);
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
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectGate.Dispose();
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
