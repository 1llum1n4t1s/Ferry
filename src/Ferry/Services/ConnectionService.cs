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
    /// <summary>rere #D-001(b): 長期 ECDH 鍵。QR の公開鍵 + ペア相手の公開鍵から PairSecret を導出する。
    /// App が生成・所有しライフサイクル管理するため、本クラスでは Dispose しない（参照保持のみ）。</summary>
    private readonly DeviceIdentity? _identity;

    /// <summary>pairings replay anchor (<see cref="AppSettings.SeenPairingIds"/>) 等の参照元。null 許容（テスト用）。</summary>
    private readonly ISettingsService? _settings;

    /// <summary>rere #D-001(b): 接続相手の PairSecret を引くための registry（null なら暗号は常に無効）。</summary>
    private readonly IPeerRegistryService? _peerRegistry;

    private ISignalingService? _signaling;
    private ITransport? _transport;
    private string? _currentPairId;

    // === 複数ペア同時接続対応 Stage 3a: ConnectionSession 集約の土台（シャドウ運用） ===
    //
    // 接続中ペアごとに ITransport / ISignalingService / pairId / Route / State / SecureChannel /
    // CTS / ロック等をまとめる入れ物。Stage 3b で単数フィールドの参照を Session 経由に置換し、
    // Stage 3c で AttachTransportEvents / OnChannelClosed を per-peer 化、Stage 4 で _connectGate /
    // _connectCts / _listeningCts も per-peer 化する（並行接続解禁）。
    //
    // 現状(Stage 3a)は『単数フィールドの内容を Session に複製してミラーする』シャドウ運用で、
    // ConnectedPeers / RouteOf 等の集合 API だけ _sessions 経由に切り替える（外部公開面の整合性を先行）。
    // 接続フロー本体は単数フィールドを参照し続けるので挙動不変。Stage 3b で参照ごと置換する。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ConnectionSession> _sessions = new(StringComparer.Ordinal);

    // 複数ペア同時接続対応 Stage 4: 旧 _listeningCts / _listeningTask は Session に移管した
    // （Session.ListeningCts / Session.ListeningTask）。StartListeningForConnection は加算的に動き、
    // 別 peer の listener と並列で走れる。_currentListeningPeerId は Stage 4 で「最後に開始した peer」の
    // 単数互換シムとして残るが、ListeningPeerIds は _sessions から本物の集合を返す。

    // Stage 4: オンデマンド接続 (ConnectToPeerAsync) の所有 CTS / 直列化ゲート / Connecting 所有権フラグは
    // 全て <see cref="ConnectionSession"/> 側に移管した。
    //   - <see cref="ConnectionSession.ConnectGate"/>: 同 peer の多重 connect を直列化（別 peer とは独立）
    //   - <see cref="ConnectionSession.ConnectCts"/>: 同 peer の in-flight connect を割り込みキャンセル
    //   - <see cref="ConnectionSession.ConnectingByListener"/>: 当該 peer の Connecting 所有権（listener vs ConnectToPeerAsync）
    // これにより peer Y と peer Z の ConnectToPeerAsync は完全並列に走り、片方の取消は他方を巻き込まない。

    /// <summary>
    /// v1.0.38 review fix: 現在 StartListeningForConnection で監視中のピア ID。
    /// ProbeRouteAsync 終了時にこの値を使って着信監視を再開する。
    /// ConnectedPeer?.SessionId は接続成立後にしか入らないため復元用には使えない。
    /// </summary>
    private string? _currentListeningPeerId;

    /// <summary>現在着信監視中のピア ID（未監視なら null）。タブ切替で SelectedPeer 外の
    /// ピアを監視中に、そのピアが削除された場合の監視停止判定に VM が使う。</summary>
    public string? CurrentListeningPeerId => _currentListeningPeerId;

    /// <summary>複数ペア同時接続対応 Stage 4: 着信監視中ピアの集合。<see cref="_sessions"/> 内で
    /// <see cref="ConnectionSession.ListeningCts"/> が生きている Session の peerId を返す。
    /// 全ペア常時 listen 中は paired peer の数だけ要素が返る。</summary>
    public System.Collections.Generic.IReadOnlyCollection<string> ListeningPeerIds
    {
        get
        {
            var sessions = _sessions;
            if (sessions.IsEmpty) return Array.Empty<string>();
            var list = new System.Collections.Generic.List<string>(sessions.Count);
            foreach (var kvp in sessions)
            {
                if (kvp.Value.ListeningCts != null) list.Add(kvp.Key);
            }
            return list;
        }
    }

    /// <summary>
    /// この pairing watch セッションで既に処理した pairingId。
    /// Firebase 購読時に既存子 (stale な pairings/ エントリ) が replay されても、
    /// 同じ pairing を二重に PairingCompleted へ流さないための重複排除。
    /// StartPairingSessionAsync 開始時にクリアする。
    /// Codex 第6弾 verify minor: IdTokenRefreshed で StartWatchingPairing を再呼出する経路と
    /// 通常の AsObservable 配信が並走しうるため、 ConcurrentDictionary で thread-safe 化する。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _seenPairingIds = new();

    /// <summary>WebSocket リレーサーバーの URL。null の場合はリレーなし（TCP 直接のみ）。</summary>
    public string? RelayUrl { get; set; }

    // rere #B1-007: State は listener タスク / ConnectToPeerAsync / WaitForListenerConnectedAsync の
    // spin-read が複数スレッドから読み書きする。_connectingByListener は volatile なのに State は plain
    // auto-property で可視性保証がなく、弱いメモリモデルの ARM (osx/win/linux-arm64) で更新が遅延しうる。
    // int バッキングを Volatile.Read/Write して可視性を担保する（PeerState は int 既定の enum）。
    private int _state = (int)PeerState.Disconnected;
    public PeerState State
    {
        get => (PeerState)Volatile.Read(ref _state);
        private set => Volatile.Write(ref _state, (int)value);
    }
    public PeerInfo? ConnectedPeer { get; private set; }
    public ConnectionRoute Route { get; private set; } = ConnectionRoute.Unknown;

    /// <summary>複数ペア同時接続対応 Stage 3a: 接続中ペア集合。<see cref="_sessions"/> 経由で射影する。
    /// Connected 状態の Session のみ ConnectedPeer 付きで返す（State!=Connected の Session は
    /// 接続フロー中なので外向きには表に出さない）。</summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, PeerInfo> ConnectedPeers
    {
        get
        {
            var sessions = _sessions;
            if (sessions.IsEmpty) return _emptyConnectedPeers;
            var result = new System.Collections.Generic.Dictionary<string, PeerInfo>(sessions.Count, StringComparer.Ordinal);
            foreach (var kvp in sessions)
            {
                if (kvp.Value.ConnectedPeer is { } info && kvp.Value.State == PeerState.Connected)
                    result[kvp.Key] = info;
            }
            return result;
        }
    }

    private static readonly System.Collections.Generic.IReadOnlyDictionary<string, PeerInfo> _emptyConnectedPeers
        = new System.Collections.Generic.Dictionary<string, PeerInfo>(0);

    /// <summary>複数ペア同時接続対応 Stage 3a: 指定 peer の Route を返す。
    /// <see cref="_sessions"/> から該当 Session を引いて Route を返す。
    /// 未接続/未知 peer は <see cref="ConnectionRoute.Unknown"/>。
    /// Stage 4 で並行接続が解禁されたら、フロー制御等が transferId→peerId→RouteOf で読む。</summary>
    public ConnectionRoute RouteOf(string peerId)
    {
        if (_sessions.TryGetValue(peerId, out var s)) return s.Route;
        return ConnectionRoute.Unknown;
    }

    public event EventHandler<PeerState>? StateChanged;
    public event EventHandler<ConnectionRoute>? RouteChanged;
    public event EventHandler<PairedPeer>? PairingCompleted;
    public event EventHandler<Infrastructure.DataReceivedEventArgs>? DataReceived;
    public event EventHandler<Infrastructure.ConnectionLostEventArgs>? ConnectionLost;
    public event EventHandler<string>? StatusMessageChanged;

    // === rere #D-001(b) / 複数ペア同時接続対応 Stage 3c: per-peer セッション暗号 ===

    /// <summary>暗号ハンドシェイクの待ち時間（秒）。相手非対応/無応答ならこの後フォールバック/切断する。</summary>
    private const int SecureHandshakeTimeoutSeconds = 8;

    // 暗号チャネル/Lock/Ready TCS/Timeout CTS は Stage 3c で <see cref="ConnectionSession"/> 側へ移管した
    // （per-peer 化）。LookupActiveSession() で現在の単数 _transport に対応する Session を引いて読み書きする。
    // Stage 5 で SendAsync/DisconnectAsync が peerId を受けるようになったら lookup を peerId 直引きへ置換する。

    private readonly FirebaseAuthClient? _authClient;

    /// <summary>CF 単独完結移行 (dual-path): signaling 実装の生成ファクトリ。
    /// null なら従来どおり <see cref="FirebaseSignaling"/> を生成する（テスト・旧経路の後方互換）。</summary>
    private readonly Func<ISignalingService>? _signalingFactory;

    /// <summary>CF 単独完結移行: 認証完了を待つデリゲート（Firebase=EnsureSignInAsync / CF=EnsureTokenAsync）。
    /// null なら <see cref="_authClient"/> 経由でフォールバックする。</summary>
    private readonly Func<CancellationToken, Task>? _ensureAuthAsync;

    /// <summary>signaling 実装を生成する。factory があれば CF/Firebase いずれか、無ければ FirebaseSignaling。</summary>
    private ISignalingService NewSignaling() =>
        _signalingFactory?.Invoke() ?? new FirebaseSignaling(_databaseUrl, _authClient);

    /// <summary>
    /// Codex P2 fix (第4弾): pairs/{pairId} の SSoT 書込成功時に、同一 pairId の queued delete を
    /// 取り消すために参照する。null 許容（テストや旧経路は注入なしでも動く）。
    /// </summary>
    private readonly PendingPairDeleteQueue? _pendingPairDeleteQueue;

    /// <summary>
    /// Codex 第12弾 #4 (P2): AppSettings.SeenPairingIds の永続化上限。
    /// 200 件は「直近 200 ペアリング操作」を保持 = 通常運用 (1 ユーザー数十ペア) で 1 年以上 replay 防御が効く想定。
    /// 超過時は List 先頭 (最古) から落とすので最新の history が保たれる。
    /// </summary>
    public const int SeenPairingIdsCap = 200;

    public ConnectionService(string databaseUrl, string deviceId, string displayName,
        DeviceIdentity? identity = null, IPeerRegistryService? peerRegistry = null, ISettingsService? settings = null,
        FirebaseAuthClient? authClient = null, PendingPairDeleteQueue? pendingPairDeleteQueue = null,
        Func<ISignalingService>? signalingFactory = null, Func<CancellationToken, Task>? ensureAuthAsync = null)
    {
        _databaseUrl = databaseUrl;
        _deviceId = deviceId;
        _displayName = displayName;
        _identity = identity;
        _peerRegistry = peerRegistry;
        _settings = settings;
        _authClient = authClient;
        _pendingPairDeleteQueue = pendingPairDeleteQueue;
        _signalingFactory = signalingFactory;
        _ensureAuthAsync = ensureAuthAsync;

        // Codex 第12弾 #4 (P2) fix: 起動時に AppSettings.SeenPairingIds (永続) を _seenPairingIds (in-memory)
        // に展開する。 これにより「アプリ再起動 → 60s 以内に Add peer 開く」で過去 consume 済 entry が
        // 再採用される race を per-ID で塞ぐ (旧 LatestConsumedPairingAtMs global timestamp gate の置換)。
        // List の copy を取って lock-free に展開する (起動時の単一スレッド前提)。
        if (_settings != null)
        {
            foreach (var pid in _settings.Settings.SeenPairingIds)
                _seenPairingIds.TryAdd(pid, 0);
        }
    }

    /// <summary>rere #D-001(b): QR に載せる自分の長期公開鍵(base64url SPKI)。identity 未注入なら空。</summary>
    public string PublicKeyForQr => _identity?.PublicKeyBase64Url ?? string.Empty;

    /// <summary>
    /// rere #D-001(a) Phase B: 直近の <see cref="StartPairingSessionAsync"/> で生成された PairingNonce。
    /// Bridge が <c>/pair/token</c> を叩くときに照合する 32hex 文字列。QR URL に <c>?nonce=...</c> で載せる。
    /// </summary>
    public string LastPairingNonce => _signaling?.LastPairingNonce ?? string.Empty;

    // === ペアリング ===

    public async Task<string> StartPairingSessionAsync(CancellationToken ct = default)
    {
        // Codex P2 fix (第2弾): 初回 SignIn の fire-and-forget が走っている最中に MainWindow Loaded
        // → 自動 QR 表示で本メソッドが呼ばれると GetIdTokenAsync が "not signed in yet" を投げて
        // ペアリング画面がエラー固まりになっていた。EnsureSignInAsync で auth 完了 (or 失敗) を待ってから
        // 進める。失敗時は通常の例外伝播 (UI で「再試行」可)。
        if (_ensureAuthAsync != null)
        {
            try { await _ensureAuthAsync(ct); }
            catch (Exception ex) when (ex is not IdentityLostException)
            {
                Util.Logger.Log($"StartPairingSession: 認証完了待ちで失敗 (rethrow): {ex.Message}", Util.LogLevel.Warning);
                throw;
            }
        }
        else if (_authClient != null)
        {
            try { await _authClient.EnsureSignInAsync(ct); }
            catch (Exception ex) when (ex is not IdentityLostException)
            {
                Util.Logger.Log($"StartPairingSession: 認証完了待ちで失敗 (rethrow): {ex.Message}", Util.LogLevel.Warning);
                throw;
            }
        }
        _signaling?.Dispose();
        _signaling = NewSignaling();
        // Codex P2 fix (第9弾 #2): 旧実装は StartPairingSessionAsync の度に _seenPairingIds.Clear() で
        // 過去 consumed の pairingId を忘れていた。 これだと「直前まで pairing してた peer を remove 直後に
        // Add peer を開く」と、 Firebase に残った old pairings entry (replay filter の -60s tolerance 内)
        // が OnPairingDetected を誤って fire させて peer 再追加 → 新 pairing session が revoke される race
        // になっていた。 _seenPairingIds はアプリ寿命全期間で持続させて、 過去 consumed pairingId は二度と
        // 受け付けないようにする。 _signaling.Dispose() でインスタンスが入れ替わっても、 _seenPairingIds は
        // ConnectionService 側に残るので持続する。
        // _seenPairingIds.Clear();  ← 削除

        // rere #D-001(b): 自分の公開鍵も session に載せる（コード貼付ペアリングで相手が読み取る）。
        var sessionId = await _signaling.RegisterSessionAsync(_deviceId, _displayName, PublicKeyForQr, ct);

        // Codex P2 fix (第12弾 #4): 再起動を跨いだ pairings replay 防御は AppSettings.SeenPairingIds
        // (per-pairingId LRU) に集約済み。 ConnectionService の constructor で _seenPairingIds (in-memory)
        // に展開済みなので、 ここで FirebaseSignaling へ追加注入する必要は無い (旧 LatestConsumedPairingAtMs
        // の global timestamp gate は撤去)。

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

        // 相手セッションの存在確認（rere #D-001(b): 相手の公開鍵も取得して PairSecret 導出に使う）
        var (exists, displayName, peerPublicKey) = await _signaling.CheckSessionAsync(sidB, ct);
        if (!exists)
            return (false, "ペアリング先のセッションが見つかりません。相手の PC でアプリが起動していることを確認してください。");

        var resolvedNameB = displayName ?? "PC-B";
        await _signaling.SubmitPairingAsync(_deviceId, _displayName, sidB, resolvedNameB, PublicKeyForQr, peerPublicKey ?? "", ct);
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

    private async void OnPairingDetected(object? sender, PairingInfo info)
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
            if (!_seenPairingIds.TryAdd(info.PairingId, 0)) return;

            Util.Logger.Log($"ペアリング検知: peer={info.PeerDisplayName}");

            // Codex P2 fix (第12弾 #4): consume したら settings.SeenPairingIds に永続化する。
            // 旧 LatestConsumedPairingAtMs (global timestamp gate) は「2 台目を 30-60s 遅い時計でペアリング」
            // のような正規 pairings entry まで弾く副作用があったため撤去。 per-pairingId LRU (Cap 件) に置換し、
            // 「再起動跨ぎで in-memory が空になる → 過去 consume 済 entry が replay される」race だけを
            // ピンポイントで防ぐ (新規 pairing は弾かれない)。
            try
            {
                var settings = _settings?.Settings;
                // copy-on-write LRU 更新は AppSettings.AddSeenPairingId が所有（不変条件の詳細はそちらの doc）。
                // 新規追加 (true) のときだけ永続化する。SettingsService.SaveAsync は SemaphoreSlim で直列化済
                // (第12弾 verify critical fix) なので他経路の SaveAsync と enumerate/mutation が衝突しない。
                // ここで await して保存完了を確実にしてから revoke / event 発火に進む。
                if (settings != null && settings.AddSeenPairingId(info.PairingId, SeenPairingIdsCap))
                {
                    await _settings!.SaveAsync();
                }
            }
            catch (Exception ex) { Util.Logger.Log($"SeenPairingIds 永続化失敗 (継続): {ex.Message}", Util.LogLevel.Warning); }

            var peer = new PairedPeer
            {
                PeerId = info.PeerId,
                DisplayName = info.PeerDisplayName,
                // Codex P2 fix (第5弾): 新規 PairingDetected で作る peer は PairsSsotObserved=false で
                // 入れ、WritePairRecordWithFallback の PUT 成功時に true に更新 + 永続化する形に変更。
                // 旧実装は最初から true にしていたが、immediate + 30s fallback の両 PUT がともに transient
                // 失敗するケースで、PairSync 後の polling が「観察済みなのに 404 → 削除」ロジックに乗って
                // 勝手に unpair される race があった。false で入れておけば第3弾 #4 fix の「未観察 peer は 3
                // 連続 404 でも削除延期」が効いて保護される。
                // verify 指摘 (第5弾 minor): 両 PUT が transient 失敗かつ相手側責任者 PC も書き損ねた場合、
                // この peer は PairsSsotObserved=false のまま peers.json に goblin peer として残り続ける
                // (削除延期 + SSoT も無い)。次起動時 PairSync 初回 check で SSoT 見えれば true 化される
                // (line 137-142) ので恒久 stuck ではないが、当該書込失敗は line 1784/1813 で Warning
                // ログが出るため運用者は気付ける。将来は UI に "ペア同期失敗" 警告を出す案件 (Phase B-2)。
                PairsSsotObserved = false,
            };

            // rere #D-001(b): 相手の公開鍵 × 自分の秘密鍵の ECDH から PairSecret を導出して永続する。
            // pairId は GeneratePairId（接続時と同じ Ordinal 規則）で salt 分離。相手 pk が空/不正なら
            // null のまま（平文フォールバック）。導出失敗で例外を出さず、ペアリング自体は成立させる。
            if (_identity != null && !string.IsNullOrEmpty(info.PeerPublicKey))
            {
                var pairId = GeneratePairId(_deviceId, info.PeerId);
                var secret = _identity.TryDerivePairSecret(info.PeerPublicKey, pairId);
                if (secret != null)
                {
                    peer.PairSecret = Convert.ToBase64String(secret);
                    Util.Logger.Log($"PairSecret 確立: peer={Util.Logger.MaskDeviceId(info.PeerId)}");
                }
            }

            // Codex P2 fix (第6弾 #4): PairingCompleted event は async void subscriber (ConnectionViewModel) で
            // 受信されるため、subscriber が AddOrUpdatePeerAsync を呼ぶ前に直後の WritePairRecordWithFallback が
            // 走って TryMarkPairsSsotObservedAsync が registry.FindPeer(peerId) で null を返し mark skip
            // → 後で subscriber が PairsSsotObserved=false のまま AddOrUpdatePeerAsync → 相手 unpair 時に
            // 未観察 guard で「3 連続 404 でも削除延期」が永久発火 → 永遠に未同期 (goblin peer) になる race があった。
            // peer を「event 発火前に同期で永続化」しておく。subscriber 側の AddOrUpdatePeerAsync (重複) は
            // idempotent (既存 peer の DisplayName 更新だけ) なので問題なし。
            if (_peerRegistry != null)
            {
                try { await _peerRegistry.AddOrUpdatePeerAsync(peer); }
                catch (Exception ex) { Util.Logger.Log($"OnPairingDetected 内 peer 永続化失敗（継続）: {ex.Message}", Util.LogLevel.Warning); }
            }

            PairingCompleted?.Invoke(this, peer);

            // #D-001a Phase B: pairs/{pairId} SSoT への書込（責任者書込 + 30s fallback）
            // 詳細は docs/design/firebase-auth-pair-ssot.md §6.1 参照。
            WritePairRecordWithFallback(sig, info.PeerId, info.PeerDisplayName);

            // Codex P2 fix (第5弾 #4): pairing 成立 = QR/コードがその役目を終えた瞬間なので、
            // 自分の sessions/{_deviceId} と pairing_nonces/{_deviceId} を即時 revoke する。
            // 残しておくと QR URL を保持した第三者が 1h (Workers /pair/token の nonce TTL) 内に
            // bridge token を mint して PC inbox に書き込めてしまう (ghost pairing 経路)。
            // peer 側の sid は revoke 権限が無いので触らない (相手側 OnPairingDetected が自分自身を
            // revoke する形で対称に処理する想定)。bridge 単独経路 (片方 PC オフライン状態でペアリング
            // 成立) では片側のみ revoke されるが、Workers /pair/token は両 nonce verify を要求するので
            // nonce 漏れがない限り片側 revoke でも実害なし。
            //
            // verify 指摘 (第5弾 minor): 旧実装は `_ = Task.Run(...)` で fire-and-forget だったため、
            // pairing 成立直後に Disconnect が走ると sig が Dispose されて ObjectDisposedException で
            // revoke skip され QR/nonce が即時 revoke されない race があった。tempSig パターン (寿命分離) +
            // 同期 await で race を解消する。最終的な失敗は CleanupAsync / firebase-cleanup.yml が後追い掃除。
            try
            {
                using var revokeSig = NewSignaling();
                await revokeSig.RevokePairingTokensAsync(_deviceId);
            }
            catch (Exception ex) { Util.Logger.Log($"pairing tokens 即時 revoke 失敗 (継続): {ex.Message}", Util.LogLevel.Debug); }

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

    /// <summary>複数ペア同時接続対応 Stage 4: 加算的な per-peer listen。指定 peer の Session に既に listener が
    /// 走っていれば置換（同じ Session 内の二重起動を防ぐ）し、他 peer の listener は維持する。
    /// 「全ペア常時 listen」を支える基盤で、VM 起動時にペア済み peer 全員ぶんを呼ぶ運用が前提。
    /// 既存の単数 _currentListeningPeerId は「最後に開始した peer」の互換シムとして更新する。</summary>
    public void StartListeningForConnection(string peerId)
    {
        if (string.IsNullOrEmpty(peerId)) throw new ArgumentNullException(nameof(peerId));

        var session = _sessions.GetOrAdd(peerId, id => new ConnectionSession(id));

        // 同 peer に既存 listener があれば停止して張り替える（再起動）。他 peer は触らない。
        StopSessionListener(session);

        var cts = new CancellationTokenSource();
        session.ListeningCts = cts;
        _currentListeningPeerId = peerId;  // 互換シム（旧 UI コードが単数値を読むケース向け）
        SyncShadowSession();
        Util.Logger.Log($"着信接続監視開始: peer={Util.Logger.MaskDeviceId(peerId)}");
        // rere PR#8 #F2: タスクを保持して role調停フォールバック時に Cancel 後の完了 await を可能にする。
        session.ListeningTask = ListenForIncomingConnectionAsync(peerId, cts.Token);
    }

    /// <summary>Stage 4: 全 peer の listener を停止する（後方互換 — peerId 不明な経路向け）。
    /// 個別停止は <see cref="StopListeningForConnection(string)"/>。Disconnect 全体・Service.Dispose 等で使う。</summary>
    public void StopListeningForConnection()
    {
        Util.Logger.Log("着信接続監視停止（全ペア）");
        foreach (var kvp in _sessions)
            StopSessionListener(kvp.Value);
        _currentListeningPeerId = null;
        SyncShadowSession();
    }

    /// <summary>Stage 4: 指定 peer の listener だけを停止する。他 peer の listener は維持する。</summary>
    public void StopListeningForConnection(string peerId)
    {
        if (string.IsNullOrEmpty(peerId)) return;
        if (_sessions.TryGetValue(peerId, out var session))
        {
            Util.Logger.Log($"着信接続監視停止: peer={Util.Logger.MaskDeviceId(peerId)}");
            StopSessionListener(session);
        }
        if (string.Equals(_currentListeningPeerId, peerId, StringComparison.Ordinal))
            _currentListeningPeerId = null;
        SyncShadowSession();
    }

    /// <summary>Stage 4: 1 Session の listener を Cancel/Dispose する（共通ヘルパー）。</summary>
    private static void StopSessionListener(ConnectionSession session)
    {
        var cts = session.ListeningCts;
        if (cts == null) return;
        try { cts.Cancel(); } catch { /* 既に Dispose 済み等 */ }
        cts.Dispose();
        session.ListeningCts = null;
        session.ListeningTask = null;
    }

    /// <summary>
    /// バックグラウンドで Offer（接続情報）をポーリングし、
    /// 検知したら TCP 接続 / WebSocket リレー接続を確立する。
    /// </summary>
    private async Task ListenForIncomingConnectionAsync(string peerId, CancellationToken ct)
    {
        var pairId = GeneratePairId(_deviceId, peerId);
        Util.Logger.Log($"着信接続ポーリング開始: pairId={pairId}");

        // Stage 4: per-Session の Connecting 所有権を扱う。Session は StartListeningForConnection が
        // 既に GetOrAdd 済みなので TryGet で十分（呼び出し直後の race も無い）。
        var session = _sessions.GetOrAdd(peerId, id => new ConnectionSession(id));

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
        using var pollingSignaling = NewSignaling();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                processingOffer = false;

                // Stage 4: 別 peer の接続中は当該 peer の listener を止める必要がない。
                // 当該 peer 自身が既に Connecting/Connected ならスキップ（同 peer の重複処理回避）。
                if (session.State is PeerState.Connected or PeerState.Connecting)
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

                // Stage 4: 当該 peer の接続状態だけで判定（別 peer の接続中は影響させない）。
                if (session.State is PeerState.Connected or PeerState.Connecting)
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
                // Stage 4: per-Session の Connecting 所有権を立てる。単数 State は primary alias として追従。
                session.ConnectingByListener = true;
                session.State = PeerState.Connecting;
                SetState(PeerState.Connecting);
                processingOffer = true;
                StatusMessageChanged?.Invoke(this, "Status.Phase.TcpConnecting");

                // Stage 4: per-Session の signaling を生成（pairing 用 _signaling とは独立、別 peer の connect とも独立）。
                session.Signaling?.Dispose();
                var sig = NewSignaling();
                session.Signaling = sig;
                session.PairId = pairId;
                _currentPairId = pairId;  // 単数は primary alias

                // rere #D-001(b): transport を attach する前に暗号チャネルを用意（先着 Hello の取りこぼし防止）。
                CreateSecureChannel(peerId);

                // ① TCP 直接接続を試行
                var connected = await TryTcpConnectAsync(session, offer.Ips, offer.Port, peerId, ct);

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
                        connected = await TryUdpHolePunchAnswerAsync(udpOffer, sig, session, pairId, peerId, ct);
                    }
                }

                // ③ UDP 失敗時: WebSocket リレーにフォールバック
                if (!connected)
                {
                    StatusMessageChanged?.Invoke(this, "Status.Phase.Relay");
                    connected = await TryRelayConnectAsync(session, pairId, "answer", peerId, ct);
                }

                if (!connected)
                {
                    Util.Logger.Log("全接続方法が失敗", Util.LogLevel.Error);
                    session.State = PeerState.Disconnected;
                    SetState(PeerState.Disconnected);
                    minCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    try { await Task.Delay(3000, ct); } catch { break; }
                    continue;
                }

                // Stage 4: per-Session に確立済み peer 情報を反映。単数 ConnectedPeer は primary alias として更新。
                var listenerPeerInfo = new PeerInfo
                {
                    SessionId = peerId,
                    DisplayName = peerId,
                    State = PeerState.Connected,
                };
                session.ConnectedPeer = listenerPeerInfo;
                session.State = PeerState.Connected;
                ConnectedPeer = listenerPeerInfo;
                SetState(PeerState.Connected);
                StartSecureHandshake(peerId); // Stage 3c: per-peer ハンドシェイク開始
                Util.Logger.Log($"着信接続完了！ 経路: {session.Transport?.Route}");

                minCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 監視停止 (ピア切替 / ピア削除 / 接続開始) によるキャンセル。着信 offer 処理中
                // (リレー試行中など) に中断された場合、Connecting のまま残すと次の監視ループが
                // 「接続中」と誤認して着信を永久に処理できなくなるため、自分が立てた Connecting に
                // 限り後始末して Disconnected へ戻す (ConnectToPeerAsync のキャンセル経路と対称)。
                // Stage 4: per-Session の ConnectingByListener / State を見る（別 peer の状態に左右されない）。
                if (processingOffer && session.State == PeerState.Connecting && session.ConnectingByListener)
                {
                    DetachTransportEvents(peerId, session.Transport); // Stage 3b: per-peer detach
                    session.Transport?.Dispose();
                    session.Transport = null;
                    session.State = PeerState.Disconnected;
                    // 単数 alias がこの session の transport を指していたら同期する。
                    if (State == PeerState.Connecting) SetState(PeerState.Disconnected);
                }
                Util.Logger.Log("着信接続監視: 正常キャンセル");
                break;
            }
            catch (OperationCanceledException)
            {
                Util.Logger.Log("着信接続: タイムアウト、リトライ", Util.LogLevel.Warning);
                session.State = PeerState.Disconnected;
                if (State == PeerState.Connecting) SetState(PeerState.Disconnected);
                minCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                try { await Task.Delay(3000, ct); } catch { break; }
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"着信接続処理エラー: {ex.Message}", Util.LogLevel.Error);
                session.State = PeerState.Disconnected;
                if (State == PeerState.Connecting) SetState(PeerState.Disconnected);
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
        var probeSig = NewSignaling();
        var connected = false;
        // 複数ペア同時接続対応 Stage 1: probe transient は受信ルーティングしない (transport を
        // _transport へ昇格させず確立直後に Dispose する) ので PeerId は空のまま。
        // DataReceivedEventArgs は発火しても TransferService 連携には流さない。
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
        if (string.IsNullOrEmpty(peerId)) throw new ArgumentNullException(nameof(peerId));

        // Stage 4: per-Session ゲートに切替。別 peer の Session には触らないため、
        // peer Y と peer Z の同時 connect は完全並列で進む。同 peer の多重 connect は session.ConnectGate
        // で従来どおり直列化する。
        var session = _sessions.GetOrAdd(peerId, id => new ConnectionSession(id));

        // 進行中の接続 (孤児ポーラー含む) を先に中断してから直列化ゲートを取る。
        // 順序を逆 (Gate→Cancel) にすると、自然完了しない孤児ポーラーを抱えたまま待ち、自己デッドロックする。
        session.ConnectCts?.Cancel();
        await session.ConnectGate.WaitAsync(ct);

        // 所有権がまだ _transport に移っていないローカル transport を例外/キャンセル経路で確実に破棄する。
        // (bound ソケット / LISTEN ポートが GC ファイナライザ回収までリークするのを防ぐ。Dispose は冪等)
        TcpDirectTransport? tcpTransport = null;
        UdpHolePunchTransport? udpTransport = null;
        void DisposeOrphanTransports()
        {
            if (tcpTransport != null && !ReferenceEquals(tcpTransport, session.Transport))
            {
                try { tcpTransport.Dispose(); } catch { }
            }
            if (udpTransport != null && !ReferenceEquals(udpTransport, session.Transport))
            {
                try { udpTransport.Dispose(); } catch { }
            }
        }

        try
        {
            session.ConnectCts?.Dispose();
            session.ConnectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var linked = session.ConnectCts.Token;

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
                using var peekSig = NewSignaling();
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
                        // Stage 4: peerId 指定の per-session 版を使う（他 peer の listener 状態と混同しない）。
                        if (await WaitForListenerConnectedAsync(peerId, RoleDeferListenTimeoutSeconds * 1000, linked))
                            return;  // 委譲成功。finally で gate 解放
                        // rere PR#8 #F2 verify: timeout フォールバック前に listener を *完了まで* 畳む。
                        // StopListeningForConnection は Cancel するだけでタスク完了を待たない (listener はループ型で
                        // 失敗時も再ポーリングを続ける) ため、待たずに下流の _transport?.Dispose() へ進むと、listener が
                        // ちょうど確立した transport をサイレント破壊する競合窓が残る。Cancel → タスク完了 await →
                        // 最終 State 確認 の順で窓を塞ぐ (listener は _connectGate を取らないので await で deadlock しない)。
                        // 複数ペア同時接続対応 Stage 4: listener タスクは Session に持つ。当該 peer の listener
                        // だけ取り出して畳む（他 peer の listener は触らない）。
                        Task? listenerTask = null;
                        if (_sessions.TryGetValue(peerId, out var listenerSession))
                            listenerTask = listenerSession.ListeningTask;
                        StopListeningForConnection(peerId);
                        if (listenerTask != null)
                        {
                            try { await listenerTask.WaitAsync(TimeSpan.FromSeconds(6)); }
                            catch { /* listener の faulted/timeout は無視。最終 State で接続成否を判断する */ }
                        }
                        // listener が止まる直前に接続を確立していたら尊重し、確立済み transport を壊さない。
                        // Stage 4: per-Session の State で当該 peer の接続有無だけを判定する（別 peer の状態に依存しない）。
                        if (session.State == PeerState.Connected)
                            return;
                        Util.Logger.Log(
                            $"role調停: 委譲先 listener が {RoleDeferListenTimeoutSeconds}s 以内に接続確立せず → " +
                            $"通常 offerer 経路へフォールバック (pairId={pairId})", Util.LogLevel.Warning);
                        // listener は上で停止・完了済み。以降の処理が offerer として再試行する。
                    }
                }
            }

            // Stage 4: per-Session の所有権を立てる（global ConnectingByListener フラグは撤去）。
            session.ConnectingByListener = false;
            session.State = PeerState.Connecting;
            SetState(PeerState.Connecting);  // 単数 State は primary alias として更新（後方互換）

            // Stage 4: 着信監視を一時停止するのは『この peer の listener のみ』。
            // 別 peer (Y / Z) の listener は維持し、彼らの着信を取り逃さないようにする。
            // 自分の Offer は per-sender ノード offers/{_deviceId} に書く一方、この peer の listener は
            // offers/{peerId} を読むので衝突しないが、role 委譲経路で listener が走っていたら畳む必要がある。
            StopListeningForConnection(peerId);

            // Stage 4: per-Session の signaling を生成（pairing 用 _signaling とは独立）。
            // peer Y の connect と peer Z の connect が並列でも、各々別の ISignalingService インスタンスを使う。
            session.Signaling?.Dispose();
            var sessionSig = NewSignaling();
            session.Signaling = sessionSig;

            // Stage 3b: peerId 指定で detach（古い transport が他 Session のものなら無視されるため安全）。
            DetachTransportEvents(peerId, session.Transport);
            session.Transport?.Dispose();
            session.Transport = null;
            if (ReferenceEquals(_transport, session.Transport)) _transport = null;

            // rere #D-001(b): transport を attach する前に暗号チャネルを用意（先着 Hello の取りこぼし防止）。
            CreateSecureChannel(peerId);

            session.PairId = pairId;
            _currentPairId = pairId;  // 単数 _currentPairId は primary alias として更新
            Util.Logger.Log($"pairId 生成: {pairId}");

            // 古いシグナリングデータを削除
            await sessionSig.CleanupSignalingDataAsync(pairId, linked);

            // ① TCP リスナー起動 → offer 送信（STUN なし）
            StatusMessageChanged?.Invoke(this, "Status.Phase.TcpPreparing");
            // 複数ペア同時接続対応 Stage 1: peerId(SessionId) を transport に注入。
            tcpTransport = new TcpDirectTransport { PeerId = peerId };
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
            await sessionSig.SendSdpOfferAsync(pairId, _deviceId, offerJson, linked);

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
            var answerTask = sessionSig.WaitForAnswerAsync(pairId, peerId, answerCts.Token);

            // どちらか先に完了した方で判断
            var completedTask = await Task.WhenAny(tcpAcceptTask, answerTask);

            var connected = false;

            if (completedTask == tcpAcceptTask && tcpAcceptTask.IsCompletedSuccessfully)
            {
                // TCP 接続成功（LAN 内）
                Util.Logger.Log("TCP 直接接続成功");
                connected = true;
                // Stage 4: session.Transport を権威に、_transport は primary alias として最新の接続に追随。
                session.Transport = tcpTransport;
                _transport = tcpTransport;
                AttachTransportEvents(peerId, tcpTransport); // Stage 3b: per-peer attach

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
                {
                    // answerTimedOut = 相手が answer を一切返さなかった = オフライン/未起動/到達不可。
                    // 専用型で投げて送信側のリトライループに「これは即終了（20s 空打ちを繰り返さない）」と伝える。
                    if (answerTimedOut)
                        throw new PeerUnreachableException(
                            "相手から応答がありません（オフライン / 旧バージョン / 接続不可の可能性）");
                    throw new InvalidOperationException("Answer を受信できませんでした");
                }

                Util.Logger.Log("Answer が TCP 失敗報告 → STUN/UDP ホールパンチ試行");
                StatusMessageChanged?.Invoke(this, "Status.Phase.StunQuery");

                // ③ STUN + UDP ホールパンチを試行
                // 複数ペア同時接続対応 Stage 1: peerId(SessionId) を transport に注入。
                udpTransport = new UdpHolePunchTransport { PeerId = peerId };
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
                    await sessionSig.SendSdpOfferAsync(pairId, _deviceId, SerializeConnectionInfo(updatedOffer), linked);

                    StatusMessageChanged?.Invoke(this, "Status.Phase.UdpHolePunch");
                    // Stage 4: per-session sig + session 引数で UDP ホールパンチを駆動。session.Transport を権威に書く。
                    connected = await TryUdpHolePunchOfferAsync(udpTransport, sessionSig, session, pairId, peerId, linked);
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
                    var relayConnected = await TryRelayConnectAsync(session, pairId, "offer", peerId, linked);
                    if (!relayConnected)
                        throw new InvalidOperationException("全ての接続方法が失敗しました");
                }
            }

            // キャンセル発火と最終 await の成功完了が重なるレース (UDP PUNCH_ACK / answer 取得が
            // キャンセルと同時に完了するケース) を閉じる: 発火済みなら成功扱いにせず
            // 外側 OCE catch の後始末 (transport 破棄 + Disconnected) に収束させる
            linked.ThrowIfCancellationRequested();

            // 防御: どの経路でも transport が確立していなければ Connected を立てない (偽 Connected 残留の安全網)
            var establishedTransport = session.Transport;
            if (establishedTransport == null || !establishedTransport.IsConnected)
                throw new InvalidOperationException("接続経路が確立されていません");

            var peerInfo = new PeerInfo
            {
                SessionId = peerId,
                DisplayName = peerId,
                State = PeerState.Connected,
            };
            session.ConnectedPeer = peerInfo;
            session.State = PeerState.Connected;
            ConnectedPeer = peerInfo;  // 単数 ConnectedPeer は primary alias として更新
            SetState(PeerState.Connected);
            StartSecureHandshake(peerId); // Stage 3c: per-peer ハンドシェイク開始
            Util.Logger.Log($"オンデマンド接続完了！ 経路: {establishedTransport.Route}");
        }
        catch (OperationCanceledException)
        {
            // 新しい接続要求 / DisconnectAsync / ユーザーの転送キャンセルに中断された正常系。
            // Connecting のまま残すと Probe や接続判定が「接続中」と誤認するため、後始末して Disconnected へ戻す。
            // 追い越した側の新しい ConnectToPeerAsync は gate 取得後に自分で Connecting を立て直すので競合しない。
            DisposeOrphanTransports();
            // Stage 4: per-Session の Connecting だけ巻き戻す（別 peer の State は触らない）。
            if (session.State == PeerState.Connecting)
            {
                DetachTransportEvents(peerId, session.Transport);
                session.Transport?.Dispose();
                session.Transport = null;
                session.State = PeerState.Disconnected;
                if (ReferenceEquals(_transport, null) || _transport == null)
                {
                    // 単数 alias がこの session を指していたケース。単数 State も Disconnected へ戻す。
                    if (State == PeerState.Connecting) SetState(PeerState.Disconnected);
                }
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
            // Stage 4: per-Session のエラー状態を立てる。単数 State は primary alias として最後の失敗を表示。
            session.State = PeerState.Error;
            SetState(PeerState.Error);
            throw;
        }
        finally
        {
            session.ConnectGate.Release();
        }
    }

    /// <summary>
    /// rere PR#8 #F2 / Stage 4: role調停で listener に委譲した後、当該 peer の listener が実際に接続を確立
    /// (session.State=Connected) するまで最大 <paramref name="timeoutMs"/> 待つ。Connected になれば true、
    /// listener が一度 Connecting へ進んだ後に失敗 (Disconnected/Error) すれば false を返して即フォールバックさせる。
    /// timeout でも false。
    /// listener ループは ConnectGate を取らず背景で独立に State を進めるため、gate を保持したまま待っても deadlock しない。
    /// ct (新規接続要求/Disconnect) 発火時は OCE を伝播させ上位の後始末に委ねる。
    /// Stage 4: 旧版は単数 <c>State</c> を読んでいたため、別 peer の connect が並走すると挙動が混線していた。
    /// per-Session の <see cref="ConnectionSession.State"/> を読むことで当該 peer の listener 進捗だけを観測する。
    /// </summary>
    private async Task<bool> WaitForListenerConnectedAsync(string peerId, int timeoutMs, CancellationToken ct)
    {
        const int PollMs = 200;
        var waited = 0;
        var sawConnecting = false;
        while (waited < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            var s = _sessions.TryGetValue(peerId, out var session) ? session.State : PeerState.Disconnected;
            if (s == PeerState.Connected) return true;
            if (s == PeerState.Connecting) sawConnecting = true;
            // 一度 Connecting を観測した後で Disconnected/Error に落ちたら listener 失敗 → 即フォールバック。
            // 起動直後の Disconnected (listener が offer 未読の起動窓) は失敗扱いしない。
            else if (sawConnecting && s is PeerState.Disconnected or PeerState.Error) return false;
            await Task.Delay(PollMs, ct);
            waited += PollMs;
        }
        return _sessions.TryGetValue(peerId, out var s2) && s2.State == PeerState.Connected;
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

        ISignalingService? probeSig = null;
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
            probeSig = NewSignaling();
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
            // 複数ペア同時接続対応 Stage 1: probe 経路でも peerId(SessionId) を transport に注入。
            // probe transient は _transport へ昇格しないが、PeerId を埋めておくと将来の意図が明確。
            tcpTransport = new TcpDirectTransport { PeerId = peerId };
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
        // Stage 5: 旧 API は「現在の単数 _transport」を引いて per-peer 経路へ委譲する。
        // 並列接続が解禁された後（Stage 4）も旧 API を使う呼び出し側は単数前提を維持。
        var session = LookupActiveSession();
        if (session == null) throw new InvalidOperationException("接続されていません");
        await SendAsyncCore(session, data, ct);
    }

    /// <summary>
    /// P-1: ArrayPool 借用バッファをコピーなしで transport の Memory 版に流す送信パス。
    /// 1GB 転送で約 1GB の Gen0 alloc 削減（チャンクメッセージごとの new byte[] 解消）。
    /// 暗号有効時のみ封筒化のためコピーが入る（暗号化の本質的コスト）。
    /// </summary>
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var session = LookupActiveSession();
        if (session == null) throw new InvalidOperationException("接続されていません");
        await SendAsyncCore(session, data, ct);
    }

    /// <summary>Stage 5: peerId 指定の送信。指定 peer の Session.Transport へ直送する。</summary>
    public async Task SendAsync(string peerId, byte[] data, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(peerId)) throw new ArgumentNullException(nameof(peerId));
        if (!_sessions.TryGetValue(peerId, out var session) || session.Transport == null)
            throw new InvalidOperationException($"指定 peer は接続されていません: {Util.Logger.MaskDeviceId(peerId)}");
        await SendAsyncCore(session, data, ct);
    }

    /// <summary>Stage 5: peerId 指定の <see cref="ReadOnlyMemory{T}"/> 版送信。</summary>
    public async Task SendAsync(string peerId, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(peerId)) throw new ArgumentNullException(nameof(peerId));
        if (!_sessions.TryGetValue(peerId, out var session) || session.Transport == null)
            throw new InvalidOperationException($"指定 peer は接続されていません: {Util.Logger.MaskDeviceId(peerId)}");
        await SendAsyncCore(session, data, ct);
    }

    /// <summary>Stage 5: 共通化された送信コア。暗号ハンドシェイク完了を待ち、必要なら封筒化して transport に流す。
    /// 各 SendAsync オーバーロードが <see cref="ConnectionSession"/> を解決した後に委譲する。</summary>
    private async Task SendAsyncCore(ConnectionSession session, byte[] data, CancellationToken ct)
    {
        var transport = session.Transport;
        if (transport == null || !transport.IsConnected)
            throw new InvalidOperationException("接続されていません");

        // rere #D-001(b): 暗号有効時はハンドシェイク完了を待ってから封筒化する。
        SecureChannel? channel;
        TaskCompletionSource<bool>? ready;
        lock (session.SecureLock) { channel = session.SecureChannel; ready = session.SecureReadyTcs; }
        if (channel != null)
        {
            if (ready != null) await ready.Task.WaitAsync(ct);
            if (channel.IsSecure)
            {
                await transport.SendAsync(channel.Encrypt(data), ct);
                return;
            }
        }
        await transport.SendAsync(data, ct);
    }

    /// <summary>Stage 5: 共通化された送信コア（<see cref="ReadOnlyMemory{T}"/> 版）。</summary>
    private async Task SendAsyncCore(ConnectionSession session, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var transport = session.Transport;
        if (transport == null || !transport.IsConnected)
            throw new InvalidOperationException("接続されていません");

        SecureChannel? channel;
        TaskCompletionSource<bool>? ready;
        lock (session.SecureLock) { channel = session.SecureChannel; ready = session.SecureReadyTcs; }
        if (channel != null)
        {
            if (ready != null) await ready.Task.WaitAsync(ct);
            if (channel.IsSecure)
            {
                await transport.SendAsync(channel.Encrypt(data.Span), ct);
                return;
            }
        }
        await transport.SendAsync(data, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        Util.Logger.Log("切断処理開始");
        // Stage 4: 全 session の in-flight をキャンセル（Gate は await しない＝Disconnect→Connect 自己デッドロック回避）。
        foreach (var kvp in _sessions)
        {
            try { kvp.Value.ConnectCts?.Cancel(); } catch { /* 既に Dispose 済み等 */ }
        }
        StopListeningForConnection();
        ResetAllSecureChannels(); // Stage 3c: 全 Session の暗号チャネルを破棄

        // Stage 4: 各 Session の transport / signaling を畳む。pairId 別 cleanup は session.PairId を使う。
        var disconnectedPeers = new System.Collections.Generic.List<string>();
        foreach (var kvp in _sessions)
        {
            var s = kvp.Value;
            var wasConnected = s.State == PeerState.Connected;
            var t = s.Transport;
            if (t != null)
            {
                DetachSessionHandlers(s, t);
                try { t.Close(); } catch { }
                try { t.Dispose(); } catch { }
                s.Transport = null;
            }
            if (s.Signaling != null)
            {
                try { await s.Signaling.CleanupAsync(s.PairId, ct); }
                catch (Exception ex) { Util.Logger.Log($"Session signaling cleanup 失敗（無視）: {ex.Message}", Util.LogLevel.Debug); }
                s.Signaling.Dispose();
                s.Signaling = null;
            }
            s.State = PeerState.Disconnected;
            s.ConnectedPeer = null;
            s.Route = ConnectionRoute.Unknown;
            s.PairId = null;
            if (wasConnected) disconnectedPeers.Add(kvp.Key);
        }

        // 単数フィールド (primary alias) も掃除する。
        _transport?.Close();
        _transport?.Dispose();
        _transport = null;
        _currentPairId = null;
        ConnectedPeer = null;
        Route = ConnectionRoute.Unknown;
        SetState(PeerState.Disconnected);

        // Pairing 用 signaling は connect 経路と独立。Disconnect 全体ではここも撤去する（旧挙動と一致）。
        if (_signaling != null)
        {
            try { await _signaling.CleanupAsync(null, ct); }
            catch (Exception ex) { Util.Logger.Log($"Pairing signaling cleanup 失敗（無視）: {ex.Message}", Util.LogLevel.Debug); }
            _signaling.Dispose();
            _signaling = null;
        }

        // 切断された peer 群へまとめて ConnectionLost を通知する（TransferService が当該 peer の transfer を畳む）。
        foreach (var pid in disconnectedPeers)
            ConnectionLost?.Invoke(this, new Infrastructure.ConnectionLostEventArgs(pid));

        Util.Logger.Log("切断処理完了");
    }

    /// <summary>Stage 5: 指定 peer の接続だけを切断する（他 peer の接続と _signaling は維持）。
    /// この peer が「現在の単数 _transport の権威（primary）」だった場合のみ単数フィールドも掃除し、
    /// 単数 State を Disconnected に遷移させる。primary 以外なら ConnectionLost を単発で発火するのみ。
    /// Stage 4 で並列接続が解禁されたあとに、片方の peer だけ閉じるユースケースを支える。</summary>
    public async Task DisconnectAsync(string peerId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(peerId)) throw new ArgumentNullException(nameof(peerId));
        Util.Logger.Log($"切断処理開始: peer={Util.Logger.MaskDeviceId(peerId)}");

        if (!_sessions.TryGetValue(peerId, out var session))
        {
            Util.Logger.Log($"切断処理: 該当 Session 無し（既に切断済）peer={Util.Logger.MaskDeviceId(peerId)}", Util.LogLevel.Debug);
            return;
        }

        // Stage 4: 当該 Session の in-flight connect を先にキャンセルする。これで Connect ループが
        // session.Transport を新しい transport で上書きする race 窓を閉じる（StopSessionListener / ResetSecureChannel
        // → TryRemove の順序に影響しない）。
        try { session.ConnectCts?.Cancel(); } catch { /* 既に Dispose 済み等 */ }

        // listener も先に停止（listener が新しい transport を attach する race 窓も閉じる）。
        StopSessionListener(session);

        var transport = session.Transport;
        var wasPrimary = ReferenceEquals(_transport, transport);
        var wasConnected = session.State == PeerState.Connected;
        var pairIdForCleanup = session.PairId;

        ResetSecureChannel(session);

        if (transport != null)
        {
            DetachSessionHandlers(session, transport);
            session.Transport = null;
            try { transport.Close(); } catch (Exception ex) { Util.Logger.Log($"transport.Close 失敗（無視）: {ex.Message}", Util.LogLevel.Debug); }
            transport.Dispose();
        }

        // Stage 4: per-Session の signaling を撤去する（pairId 別 cleanup も session.PairId を使う）。
        var sessionSig = session.Signaling;
        if (sessionSig != null)
        {
            try { await sessionSig.CleanupAsync(pairIdForCleanup, ct); }
            catch (Exception ex) { Util.Logger.Log($"Session signaling cleanup 失敗（無視）: {ex.Message}", Util.LogLevel.Debug); }
            sessionSig.Dispose();
            session.Signaling = null;
        }

        session.State = PeerState.Disconnected;
        session.ConnectedPeer = null;
        session.Route = ConnectionRoute.Unknown;
        session.PairId = null;

        if (_sessions.TryRemove(peerId, out var removed))
            DisposeSessionSilent(removed);

        // primary だった場合は単数フィールドを掃除して、外向き API（ConnectedPeer/State/Route）を整合させる。
        if (wasPrimary)
        {
            _transport = null;
            ConnectedPeer = null;
            Route = ConnectionRoute.Unknown;
            if (string.Equals(_currentPairId, pairIdForCleanup, StringComparison.Ordinal))
                _currentPairId = null;
            SetState(PeerState.Disconnected);
            if (wasConnected)
                ConnectionLost?.Invoke(this, new Infrastructure.ConnectionLostEventArgs(peerId));
        }
        else if (wasConnected)
        {
            ConnectionLost?.Invoke(this, new Infrastructure.ConnectionLostEventArgs(peerId));
        }

        // pairing 用 _signaling は触らない（他 peer の connect/pairing watch がまだ使っている可能性がある）。
        // 全 peer 切断時は呼び出し側が DisconnectAsync()（peerId 無し）を呼ぶ運用。
    }

    // === 接続ヘルパー ===

    /// <summary>
    /// TCP 直接接続を試行する（Answer 側が使用）。
    /// Stage 4: session を受けて当該 peer の Session.Transport を権威に書く（別 peer の transport を壊さない）。
    /// </summary>
    private async Task<bool> TryTcpConnectAsync(ConnectionSession session, string[] ips, int port, string peerId, CancellationToken ct)
    {
        if (ips.Length == 0 || port <= 0)
        {
            Util.Logger.Log("TCP 接続情報が不正（IP なしまたはポート 0）", Util.LogLevel.Warning);
            return false;
        }

        try
        {
            // 複数ペア同時接続対応 Stage 1: 1 transport = 1 peer の対応関係を peerId で結ぶ。
            // DataReceivedEventArgs.PeerId に常時付帯される。
            var tcpTransport = new TcpDirectTransport { PeerId = peerId };

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(TcpConnectTimeoutSeconds));

            await tcpTransport.ConnectAsync(ips, port, connectCts.Token);

            // Stage 4: 当該 Session 内の旧 transport だけ detach/Dispose する（別 peer の transport を巻き込まない）。
            var prev = session.Transport;
            if (prev != null && !ReferenceEquals(prev, tcpTransport))
            {
                DetachTransportEvents(peerId, prev);
                prev.Dispose();
            }
            session.Transport = tcpTransport;
            _transport = tcpTransport;  // 単数は primary alias
            AttachTransportEvents(peerId, tcpTransport);
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
    /// Stage 4: per-session の signaling / Session を受けて当該 peer 専用の経路として動かす（_signaling 直参照を撤去）。
    /// </summary>
    private async Task<bool> TryUdpHolePunchOfferAsync(UdpHolePunchTransport udpTransport, ISignalingService sig, ConnectionSession session, string pairId, string peerId, CancellationToken ct)
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
                endpointStr = await sig.WaitForEndpointAsync(pairId, peerId, epCts.Token);
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

            // attach を HolePunchAsync の前に行う。確立(SetConnected)の瞬間に DataReceived を購読済みにして、
            // SetConnected〜attach の窓(HolePunchAsync の Task.Delay 最大200ms)に届く最初のアプリデータ
            // (FileMeta/SecureHello)を取りこぼさない。確立前に届く DATA は HandleData の !IsConnected ガードで
            // ドロップされ送信側の再送に委ねられる。UDP 失敗時は attach 済み udpTransport が残るが、次経路
            // (リレー)の DetachTransportEvents+Dispose で確実に掃除される。
            // Stage 4: 当該 Session 内の旧 transport だけ畳む（別 peer の transport を巻き込まない）。
            var prev = session.Transport;
            if (prev != null && !ReferenceEquals(prev, udpTransport))
            {
                DetachTransportEvents(peerId, prev);
                prev.Dispose();
            }
            session.Transport = udpTransport;
            _transport = udpTransport;  // 単数は primary alias
            AttachTransportEvents(peerId, udpTransport);
            await udpTransport.HolePunchAsync(parts[0], remotePort, punchCts.Token);
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
        ISignalingService sig, string pairId, ConnectionInfo initialOffer, string peerId, CancellationToken ct)
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
    /// Stage 4: per-session の signaling / Session を受けて当該 peer 専用の経路として動かす。
    /// </summary>
    private async Task<bool> TryUdpHolePunchAnswerAsync(ConnectionInfo offer, ISignalingService sig, ConnectionSession session, string pairId, string peerId, CancellationToken ct)
    {
        UdpHolePunchTransport? udpTransport = null;
        try
        {
            Util.Logger.Log("UDP ホールパンチ（Answer 側）開始: STUN クエリ実行中…");
            // 複数ペア同時接続対応 Stage 1: peerId を transport へ伝播。
            udpTransport = new UdpHolePunchTransport { PeerId = peerId };
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
            await sig.SendEndpointAsync(pairId, _deviceId, $"{stunResult.Value.ip}:{stunResult.Value.port}", ct);

            // Offer 側の外部エンドポイントに向けてホールパンチ実行
            using var punchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            punchCts.CancelAfter(TimeSpan.FromSeconds(UdpHolePunchTimeoutSeconds));

            // attach を HolePunchAsync の前に行う（理由は Offer 側と同じ。SetConnected〜attach の窓に届く
            // 最初のアプリデータを取りこぼさない。確立前 DATA は HandleData の !IsConnected ガードでドロップ）。
            // Stage 4: 当該 Session 内の旧 transport だけ畳む。
            var prev = session.Transport;
            if (prev != null && !ReferenceEquals(prev, udpTransport))
            {
                DetachTransportEvents(peerId, prev);
                prev.Dispose();
            }
            session.Transport = udpTransport;
            _transport = udpTransport;  // 単数は primary alias
            AttachTransportEvents(peerId, udpTransport);
            await udpTransport.HolePunchAsync(offer.ExternalIp!, offer.ExternalPort, punchCts.Token);
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
    /// Stage 4: session を受けて当該 peer の Session.Transport を権威に書く。
    /// </summary>
    private async Task<bool> TryRelayConnectAsync(ConnectionSession session, string pairId, string role, string peerId, CancellationToken ct)
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
            // 複数ペア同時接続対応 Stage 1: peerId を transport へ伝播。
            relayTransport = new WebSocketRelayTransport(RelayUrl, pairId, role, peerId);

            using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            relayCts.CancelAfter(TimeSpan.FromSeconds(RelayPeerWaitSeconds));

            await relayTransport.ConnectAsync(relayCts.Token);

            // Stage 4: 当該 Session 内の旧 transport だけ畳む。
            var prev = session.Transport;
            if (prev != null && !ReferenceEquals(prev, relayTransport))
            {
                DetachTransportEvents(peerId, prev);
                prev.Dispose();
            }
            session.Transport = relayTransport;
            _transport = relayTransport;  // 単数は primary alias
            AttachTransportEvents(peerId, relayTransport);
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

    // === 複数ペア同時接続対応 Stage 3b: クロージャ束縛 Attach/Detach + per-Session イベントルーティング ===
    //
    // 旧実装はメソッド参照 (OnChannelClosed 等) を _transport の各イベントに attach していたため、
    // 「どの peer の transport から飛んだイベントか」を sender 経由でしか復元できなかった。
    // Stage 3b では Attach 時に peerId と transport を捕捉した lambda を生成し、Session に保存する。
    // 各ハンドラは内部で `ReferenceEquals(transport, session.Transport)` ガードを通り、張り替え済みの
    // 古い transport から漂着したイベントを無視する（Stage 4 で並列接続が解禁されたとき、別 peer の
    // OnChannelClosed が手元の Session を踏み潰さないための防御）。

    private void AttachTransportEvents(string peerId, ITransport transport)
    {
        if (string.IsNullOrEmpty(peerId)) throw new ArgumentNullException(nameof(peerId));
        if (transport == null) throw new ArgumentNullException(nameof(transport));

        var session = _sessions.GetOrAdd(peerId, id => new ConnectionSession(id));

        // 同 Session に Detach 無しで再 Attach されたら、古いハンドラを掃除してから差し替える（防御）。
        var oldTransport = session.Transport;
        if (oldTransport != null && oldTransport != transport)
            DetachSessionHandlers(session, oldTransport);

        session.Transport = transport;
        // 単数フィールド (_transport) は呼び出し側で既に書かれているはず。整合のため SyncShadowSession を呼ぶと
        // 別 Session を巻き込む（単数 State が他 peer 由来）危険があるので、Session.Transport の直接書込で済ます。

        // クロージャ生成: peerId と transport を捕捉。`this` も暗黙キャプチャされ、内部メソッドへ委譲する。
        EventHandler opened = (_, _) => OnSessionChannelOpened(peerId, transport);
        EventHandler closed = (_, _) => OnSessionChannelClosed(peerId, transport);
        EventHandler<Infrastructure.DataReceivedEventArgs> dataReceived = (_, e) => OnSessionDataReceived(peerId, transport, e);
        EventHandler<ConnectionRoute> routeChanged = (_, route) => OnSessionRouteChanged(peerId, transport, route);

        session.OnChannelOpenedHandler = opened;
        session.OnChannelClosedHandler = closed;
        session.OnDataReceivedHandler = dataReceived;
        session.OnRouteChangedHandler = routeChanged;

        transport.ChannelOpened += opened;
        transport.ChannelClosed += closed;
        transport.DataReceived += dataReceived;
        transport.RouteChanged += routeChanged;

        // PR#5 Codex 指摘: transport が ConnectAsync 中（Attach 前）に RouteChanged を発火済みだと
        // Route が Unknown のまま残り、リレー経路のフロー制御ガードが誤って無効化される。
        // Attach 時点の現在値を即時同期して取りこぼしを防ぐ。Stage 3b では Session.Route 経由で判定する。
        if (transport.Route != ConnectionRoute.Unknown && transport.Route != session.Route)
            OnSessionRouteChanged(peerId, transport, transport.Route);
    }

    private void DetachTransportEvents(string peerId, ITransport? transport)
    {
        if (transport == null) return;
        if (string.IsNullOrEmpty(peerId)) return;
        if (!_sessions.TryGetValue(peerId, out var session)) return;

        // 別 transport に張り替え済みなら触らない（古い close が新 transport のハンドラを巻き込まない）。
        if (!ReferenceEquals(session.Transport, transport)) return;

        DetachSessionHandlers(session, transport);
        session.Transport = null;
    }

    /// <summary>Stage 3b: <see cref="ConnectionSession"/> に保存したクロージャ参照で transport から
    /// イベントハンドラを解除し、参照をクリアする。Attach 時の生成と対称な解除経路を 1 ヶ所に集約。</summary>
    private static void DetachSessionHandlers(ConnectionSession session, ITransport transport)
    {
        if (session.OnChannelOpenedHandler != null) transport.ChannelOpened -= session.OnChannelOpenedHandler;
        if (session.OnChannelClosedHandler != null) transport.ChannelClosed -= session.OnChannelClosedHandler;
        if (session.OnDataReceivedHandler != null) transport.DataReceived -= session.OnDataReceivedHandler;
        if (session.OnRouteChangedHandler != null) transport.RouteChanged -= session.OnRouteChangedHandler;
        session.OnChannelOpenedHandler = null;
        session.OnChannelClosedHandler = null;
        session.OnDataReceivedHandler = null;
        session.OnRouteChangedHandler = null;
    }

    /// <summary>Stage 3b: peerId 不明な経路（DisconnectAsync 等）から、指定 transport に attach 済みの
    /// 全 Session ハンドラを掃除する。<see cref="_sessions"/> を走査して該当 Session を見つけて detach する
    /// （Stage 4 で並列接続が解禁されても、転送中の transport を取り違えない）。</summary>
    private void DetachTransportEventsForTransport(ITransport? transport)
    {
        if (transport == null) return;
        foreach (var kvp in _sessions)
        {
            if (ReferenceEquals(kvp.Value.Transport, transport))
            {
                DetachSessionHandlers(kvp.Value, transport);
                kvp.Value.Transport = null;
            }
        }
    }

    private void OnSessionChannelOpened(string peerId, ITransport transport)
    {
        // 張り替え済み transport は無視（古い transport の delayed open イベント等）。
        if (!_sessions.TryGetValue(peerId, out var session) || !ReferenceEquals(session.Transport, transport))
            return;
        Util.Logger.Log($"データチャネル接続完了: peer={Util.Logger.MaskDeviceId(peerId)}");
    }

    private void OnSessionRouteChanged(string peerId, ITransport transport, ConnectionRoute route)
    {
        if (!_sessions.TryGetValue(peerId, out var session) || !ReferenceEquals(session.Transport, transport))
            return;
        session.Route = route;

        // この Session が単数フィールド _transport の権威（primary）なら、単数 Route も同期する。
        // primary 以外（Stage 4 で並列接続が解禁された後の secondary）は単数 Route を触らず、外向きイベントだけ
        // 発火する（VM 側は単数 Route と RouteOf(peerId) を使い分ける設計）。
        if (ReferenceEquals(_transport, transport))
        {
            Route = route;
            Util.Logger.Log($"接続経路確定: {route}");
        }
        else
        {
            Util.Logger.Log($"接続経路確定（secondary）: peer={Util.Logger.MaskDeviceId(peerId)} {route}");
        }
        RouteChanged?.Invoke(this, route);
    }

    private void OnSessionChannelClosed(string peerId, ITransport transport)
    {
        Util.Logger.Log($"データチャネル切断検知: peer={Util.Logger.MaskDeviceId(peerId)} currentState={State}", Util.LogLevel.Warning);
        // 張り替え済み transport（既に Session.Transport != transport）の close は無視 — 新 transport の
        // ハンドラを巻き込まない。Session が見つからない場合（既に Dispose 済み等）も同様に何もしない。
        if (!_sessions.TryGetValue(peerId, out var session) || !ReferenceEquals(session.Transport, transport))
        {
            Util.Logger.Log("（古い transport の close。Session は別 transport へ張り替え済み）", Util.LogLevel.Debug);
            return;
        }

        var wasConnected = session.State == PeerState.Connected;
        session.State = PeerState.Disconnected;

        // Stage 3c: 当該 Session の暗号チャネルを per-peer reset（宙吊りの送信を解放）。
        // 単数 _transport が同じ transport を指していれば（primary）、単数 State も Disconnected へ
        // 遷移し ConnectionLost を発火する。secondary（Stage 4 で並列接続解禁後）は session.State
        // だけ動かして ConnectionLost を単発で出す（VM 側で peer 区別は別経路）。
        ResetSecureChannel(session);
        if (ReferenceEquals(_transport, transport))
        {
            if (State == PeerState.Connected)
            {
                ConnectionLost?.Invoke(this, new Infrastructure.ConnectionLostEventArgs(peerId));
                SetState(PeerState.Disconnected);
            }
        }
        else if (wasConnected)
        {
            ConnectionLost?.Invoke(this, new Infrastructure.ConnectionLostEventArgs(peerId));
        }
    }

    private void OnSessionDataReceived(string peerId, ITransport transport, Infrastructure.DataReceivedEventArgs e)
    {
        // 張り替え済み transport から漂着したデータは無視（幽霊フレームで暗号状態機械を壊さない）。
        if (!_sessions.TryGetValue(peerId, out var session) || !ReferenceEquals(session.Transport, transport))
            return;

        // 複数ペア同時接続対応 Stage 2: transport が運んだ peerId を IConnectionService.DataReceived へ貫通させる。
        // Stage 3c: 暗号チャネルは Session.SecureChannel（Session.SecureLock 配下）を権威で使う。
        var data = e.Data;
        SecureChannelStep? step = null;
        lock (session.SecureLock)
        {
            var channel = session.SecureChannel;
            if (channel != null)
                step = channel.OnFrame(data);
        }
        if (step == null)
        {
            DataReceived?.Invoke(this, e); // 平文（現状パス）。peerId 付きのまま貫通。
            return;
        }
        ApplySecureStep(session, step); // 副作用（送信/配送/遷移）はロック外で。
    }

    // === rere #D-001(b) / 複数ペア同時接続対応 Stage 3c: per-peer セッション暗号ハンドシェイクの駆動 ===

    /// <summary>
    /// 接続確立に先立って（transport を attach する前に）暗号チャネルを用意する。E2E 暗号は常時有効化済み
    /// （旧 EnableSecureChannel トグルは v1.0.48 で撤去）で、相手の PairSecret を保有していれば自動的に
    /// <see cref="SecureChannel"/> を Init 状態で生成する。この時点では Hello を送らず、DataReceived 購読より
    /// 先にチャネルを立てておくことで、確立直後に先着する相手 Hello を Init バッファで取りこぼさない
    /// （attach レース対策）。PairSecret 無し（古い peer / 未交換）の相手は Session.SecureChannel=null のままで
    /// 送受信は平文の現状パスに自然フォールバックする。Stage 3c で per-peer 化済（Session に保存）。
    /// </summary>
    private void CreateSecureChannel(string peerId)
    {
        var session = _sessions.GetOrAdd(peerId, id => new ConnectionSession(id));
        ResetSecureChannel(session);

        if (_peerRegistry == null) return;

        var b64 = _peerRegistry.FindPeer(peerId)?.PairSecret;
        if (string.IsNullOrEmpty(b64)) return;

        byte[] pairSecret;
        try { pairSecret = Convert.FromBase64String(b64); }
        catch (FormatException) { return; }

        lock (session.SecureLock)
        {
            session.SecureReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.SecureChannel = new SecureChannel(_deviceId, peerId, pairSecret, secureEnabled: true);
        }
    }

    /// <summary>
    /// 接続確立直後（ConnectedPeer 設定後）に Hello を送ってハンドシェイクを開始する。
    /// CreateSecureChannel でチャネルが立っていなければ何もしない（平文）。両接続経路から呼ぶ。
    /// Stage 3c: peerId 直引きの per-peer 化済。
    /// </summary>
    private void StartSecureHandshake(string peerId)
    {
        if (!_sessions.TryGetValue(peerId, out var session)) return;

        SecureChannelStep step;
        lock (session.SecureLock)
        {
            var channel = session.SecureChannel;
            if (channel == null) return;

            var timeoutCts = new CancellationTokenSource();
            session.SecureTimeoutCts = timeoutCts;
            _ = SecureTimeoutAsync(session, channel, timeoutCts.Token);

            step = channel.Start(); // Hello 生成 + 先着フレームのドレイン
        }
        ApplySecureStep(session, step); // 送信/配送/遷移はロック外で
    }

    /// <summary>状態機械の 1 ステップ（送るフレーム・配送平文・遷移）を実行する。
    /// Stage 3c: peer ごとの Session に紐づく transport / Deliver peerId / TCS を使う。</summary>
    private void ApplySecureStep(ConnectionSession session, SecureChannelStep step)
    {
        var transport = session.Transport;
        foreach (var f in step.Send)
        {
            // ハンドシェイクフレーム(0x30/0x31)は SendAsync ゲートを通さず transport へ直送する。
            if (transport != null)
                _ = SafeSendRawAsync(transport, f);
        }

        // 復号後の Deliver は Session の peerId を権威で付帯する。
        foreach (var d in step.Deliver)
            DataReceived?.Invoke(this, new Infrastructure.DataReceivedEventArgs(session.PeerId, d));

        switch (step.Outcome)
        {
            case SecureOutcome.Established:
                Util.Logger.Log($"暗号セッション確立（HMAC 相互認証成功）: peer={Util.Logger.MaskDeviceId(session.PeerId)}");
                session.SecureTimeoutCts?.Cancel();
                session.SecureReadyTcs?.TrySetResult(true);
                break;
            case SecureOutcome.FellBackToPlaintext:
                Util.Logger.Log($"暗号ハンドシェイク非成立 → 平文フォールバック: peer={Util.Logger.MaskDeviceId(session.PeerId)}", Util.LogLevel.Warning);
                session.SecureTimeoutCts?.Cancel();
                session.SecureReadyTcs?.TrySetResult(false);
                break;
            case SecureOutcome.Failed:
                Util.Logger.Log($"暗号ハンドシェイク失敗（HMAC 不一致）→ 切断: peer={Util.Logger.MaskDeviceId(session.PeerId)}", Util.LogLevel.Error);
                session.SecureTimeoutCts?.Cancel();
                session.SecureReadyTcs?.TrySetException(new InvalidOperationException("ペア相互認証に失敗しました（HMAC 不一致）"));
                _ = DisconnectAsync();
                break;
        }
    }

    private static async Task SafeSendRawAsync(ITransport transport, byte[] frame)
    {
        try { await transport.SendAsync(frame); }
        catch (Exception ex) { Util.Logger.Log($"ハンドシェイクフレーム送信失敗: {ex.Message}", Util.LogLevel.Warning); }
    }

    /// <summary>複数ペア同時接続対応: 現在の <see cref="_transport"/> から PeerId を引く便宜ヘルパー。
    /// Stage 5 で SendAsync(peerId) になったら本ヘルパーは削除し peerId 直引きに置換する。</summary>
    private string? GetCurrentTransportPeerId() => _transport switch
    {
        TcpDirectTransport t when !string.IsNullOrEmpty(t.PeerId) => t.PeerId,
        UdpHolePunchTransport u when !string.IsNullOrEmpty(u.PeerId) => u.PeerId,
        WebSocketRelayTransport w when !string.IsNullOrEmpty(w.PeerId) => w.PeerId,
        _ => null,
    };

    /// <summary>Stage 3c: 現在の単数 <see cref="_transport"/> に対応する <see cref="ConnectionSession"/> を引く
    /// （SendAsync など peerId を持たない経路向け）。Stage 5 で SendAsync が peerId 直引きになったら本ヘルパーは
    /// 撤去する。</summary>
    private ConnectionSession? LookupActiveSession()
    {
        var transport = _transport;
        if (transport == null) return null;
        foreach (var kvp in _sessions)
        {
            if (ReferenceEquals(kvp.Value.Transport, transport)) return kvp.Value;
        }
        return null;
    }

    private async Task SecureTimeoutAsync(ConnectionSession session, SecureChannel channel, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(SecureHandshakeTimeoutSeconds), ct); }
        catch (OperationCanceledException) { return; }

        SecureChannelStep? step = null;
        lock (session.SecureLock)
        {
            // 別接続に張り替わっていたら無視（古いタイマーの暴発防止）。
            if (ReferenceEquals(session.SecureChannel, channel))
                step = channel.OnTimeout();
        }
        if (step != null) ApplySecureStep(session, step);
    }

    /// <summary>Stage 3c: 指定 Session の暗号チャネルを破棄し、待機中の送信を平文として解放する
    /// （切断 / 再接続時の per-peer reset）。</summary>
    private static void ResetSecureChannel(ConnectionSession session)
    {
        lock (session.SecureLock)
        {
            session.SecureTimeoutCts?.Cancel();
            session.SecureTimeoutCts?.Dispose();
            session.SecureTimeoutCts = null;
            // 宙吊りの SendAsync を解放（切断後は次段の transport 操作で自然に例外化する）。
            session.SecureReadyTcs?.TrySetResult(false);
            session.SecureReadyTcs = null;
            session.SecureChannel = null;
        }
    }

    /// <summary>Stage 3c: 全 Session の暗号チャネルを破棄する（DisconnectAsync 等 peerId 不明な経路向け）。
    /// Stage 5 で DisconnectAsync が peerId を受けるようになったら、per-peer 経路に置換する。</summary>
    private void ResetAllSecureChannels()
    {
        foreach (var kvp in _sessions)
            ResetSecureChannel(kvp.Value);
    }

    private void SetState(PeerState state)
    {
        Util.Logger.Log($"状態遷移: {State} → {state}");
        State = state;
        // 複数ペア同時接続対応 Stage 3a: 単数 State の遷移を Session(現在/便宜セッション)にミラーする。
        // 単数 ConnectedPeer / _currentPairId / _transport 等の context から Session を解決する。
        SyncShadowSession();
        StateChanged?.Invoke(this, state);
    }

    /// <summary>複数ペア同時接続対応 Stage 3a: 単数フィールド (_transport / _signaling / _currentPairId /
    /// Route / State / _secureChannel / ConnectedPeer) を <see cref="_sessions"/> へミラーする。
    /// Stage 3a では単数フィールドが権威で、_sessions は ConnectedPeers / RouteOf 等の集合 API の表に
    /// 整合させる用途。Stage 3b 以降で参照ごと _sessions へ移管し、このミラー処理は除去する。</summary>
    private void SyncShadowSession()
    {
        var peer = ConnectedPeer;
        // Stage 4: 単数 _transport / ConnectedPeer / Route / State の権威を「primary Session」に同期する。
        // Stage 4 から複数 Session が並列に存在するため、ここでは「peer の SessionId が分かる場合だけ
        // その Session を最新化」し、他 Session は触らない（listener / connect が独立に走る前提）。
        var peerId = peer?.SessionId ?? _currentListeningPeerId;
        if (string.IsNullOrEmpty(peerId))
            return; // 単数 ConnectedPeer / Listening もないなら、他 Session の独立状態を尊重する

        var session = _sessions.GetOrAdd(peerId, id => new ConnectionSession(id));
        session.Transport = _transport;
        session.Signaling = _signaling;
        session.PairId = _currentPairId;
        session.Route = Route;
        session.State = State;
        session.ConnectedPeer = peer;
        // SecureChannel / SecureLock は Stage 3c で Session 権威化済み（ここではミラーしない）。
        // Stage 4 の listener-only Session（StartListeningForConnection だけ呼ばれて単数 _transport が
        // 別 peer のもの）は session.Transport を上書きするとマイラ破壊につながるため、上記の peerId が
        // 「接続中 / 単数 listening の peer」のみに対応する Session 限定で同期する。
    }

    /// <summary>
    /// pairs/{pairId} 書込（責任者経路 / 30s fallback 経路）に共通の中止判定。
    /// peer が local registry から外れた（unpair 済）か、ユーザー起点の unpair が in-flight
    /// (<see cref="IPeerRegistryService.IsPendingRemoval"/>) のとき true を返す。これがないと
    /// Firebase DELETE 完了前に PUT が走って削除済みペアを resurrect する race を許す。
    /// テスト用 ctor は _peerRegistry=null で、その場合は本番防御をスキップ（常に false）する。
    /// 新しい writer 経路を足すときも必ずここを通すことで、guard の貼り忘れによる resurrect バグを防ぐ。
    /// </summary>
    private bool ShouldAbortPairWrite(string peerId)
        => _peerRegistry != null
           && (_peerRegistry.FindPeer(peerId) == null || _peerRegistry.IsPendingRemoval(peerId));

    /// <summary>
    /// #D-001a Phase B Q4: pairs/{pairId} の責任者書込 + 30s fallback による冗長化。
    /// - 責任者 (deviceId Ordinal 小さい方): 即書込 + セルフチェック GET
    /// - 非責任者: 30s 後に GET、未存在なら自分が書込（責任者クラッシュ救済）
    /// 設計 §6.1 参照。fire-and-forget で実行（例外は内部で握る）。
    /// </summary>
    private void WritePairRecordWithFallback(ISignalingService sig, string peerId, string peerDisplayName)
    {
        var pairId = GeneratePairId(_deviceId, peerId);
        var isResponsible = string.Compare(_deviceId, peerId, StringComparison.Ordinal) < 0;
        var nameA = isResponsible ? _displayName : peerDisplayName;
        var nameB = isResponsible ? peerDisplayName : _displayName;
        var record = new PairRecord
        {
            PairId = pairId,
            NameA = nameA,
            NameB = nameB,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        // CodeRabbit 指摘: 引数 sig は呼出側 (_signaling) の参照で、責任者側は即時に使う一方
        // 非責任者は 30s Task.Delay 後に使う。その間に _signaling.Dispose() が走ると ObjectDisposedException
        // で fallback 書込が消失する。fallback 用に dedicated FirebaseSignaling を作って sig 寿命と切り離す。
        // 責任者側も async/await の継続後に sig が無効化されるレースを避けるため tempSig 化する (僅か数 ms の追加)。
        // PII 防止: pairId は両 deviceId を含むのでログマスクする。
        var maskedPair = MaskPairId(pairId);
        if (isResponsible)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // 責任者経路は即時 PUT のため race window は狭いが、PairingCompleted → ユーザー速攻 unpair の
                    // 1〜数 ms で local 状態が外れる可能性があるので、非責任者経路 (30s fallback) と同じ
                    // ShouldAbortPairWrite で対称に防御する（Codex 第7弾 #1 / 第12弾 #3 の resurrect 対策）。
                    if (ShouldAbortPairWrite(peerId))
                    {
                        Util.Logger.Log($"pairs/{maskedPair} 責任者書込中止: peer が local から外れた / 削除中 (unpair 検知)", Util.LogLevel.Debug);
                        return;
                    }
                    using var tempSig = NewSignaling();
                    // Codex 第12弾 verify minor (TOCTOU): IsPendingRemoval check と PutPairAsync の間 (ms 単位の HTTP RTT 前)
                    // にユーザーが unpair を開始した場合に備えて、 PUT 直前に再度 check する。
                    if (_peerRegistry?.IsPendingRemoval(peerId) == true)
                    {
                        Util.Logger.Log($"pairs/{maskedPair} 責任者書込直前中止: PUT 寸前に unpair 開始を検知", Util.LogLevel.Debug);
                        return;
                    }
                    await tempSig.PutPairAsync(pairId, record);
                    // Codex P2 fix (第5弾): self-check は GetPairAsync の transient null マッピング
                    // (read/auth エラーも null 返却) で「PUT 成功 + GET 不確定 → queue が残る」race を作り、
                    // 10 分後 retry が新ペアを誤削除する原因になる。サーバが PUT を受理した (例外なしで返った)
                    // 時点で queue clear で十分。PutPair が透過 ok を返す隠れ失敗 (rules 拒否吸収等) は
                    // 別レイヤの信頼性問題として切り離す。
                    Util.Logger.Log($"pairs/{maskedPair} 書込成功（責任者）");
                    // Codex P2 fix (第5弾 #5): PUT 成功直後に peer.PairsSsotObserved=true を立てて永続化する。
                    // OnPairingDetected は false で peer を作っているので、この時点で初めて観察済みに昇格する。
                    // これにより immediate + 30s fallback の両 Task ともに失敗するケースでも PairSync の
                    // 「未観察 peer は 3 連続 404 でも削除延期」が効き、不本意な remote unpair を回避できる。
                    await TryMarkPairsSsotObservedAsync(peerId, maskedPair);
                    // 再ペアリング成立で queued delete が残っていると、後で retry が走ったとき新ペアの
                    // pairs ノードを誤削除して remote unpair になる。PutPair 受理時点で取消す。
                    await TryRemovePendingPairDeleteAsync(pairId, maskedPair);
                }
                catch (Exception ex)
                {
                    Util.Logger.Log($"pairs/{maskedPair} 責任者書込失敗（fallback 待ち）: {ex.Message}", Util.LogLevel.Warning);
                }
            });
        }
        else
        {
            // 非責任者: 30s 後に存在確認 → 未存在なら自分が書く（責任者クラッシュ救済）
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    // 30s wait 中にユーザーが unpair した場合、fallback の PutPair が削除済み pairs/{pairId} を
                    // resurrect して remote unpair が反映されない race を防ぐ（責任者経路と同じ ShouldAbortPairWrite。
                    // Codex 第7弾 #1 / 第12弾 #3 の resurrect 対策）。
                    if (ShouldAbortPairWrite(peerId))
                    {
                        Util.Logger.Log($"pairs/{maskedPair} fallback 中止: peer が local から外れた / 削除中 (unpair 検知)", Util.LogLevel.Debug);
                        return;
                    }
                    using var tempSig = NewSignaling();
                    var existing = await tempSig.GetPairAsync(pairId);
                    if (existing == null)
                    {
                        // Codex 第12弾 verify minor (TOCTOU): GetPairAsync の HTTP RTT 中にユーザーが unpair した
                        // 場合に備えて PUT 直前に再 check する (上の check と PUT の間に HTTP 1 往復が挟まる)。
                        if (_peerRegistry?.IsPendingRemoval(peerId) == true)
                        {
                            Util.Logger.Log($"pairs/{maskedPair} fallback 書込直前中止: PUT 寸前に unpair 開始を検知", Util.LogLevel.Debug);
                            return;
                        }
                        Util.Logger.Log($"pairs/{maskedPair} 未作成検知 → fallback 書込");
                        await tempSig.PutPairAsync(pairId, record);
                        // Codex P2 fix (第5弾): self-check は GetPairAsync の transient null マッピングで
                        // race を作るため廃止。PutPair が例外なしで返れば queue clear で十分（責任者経路と同じ理由）。
                        Util.Logger.Log($"pairs/{maskedPair} fallback 書込成功");
                        // Codex P2 fix (第5弾 #5): fallback 経路も PUT 成功直後に PairsSsotObserved=true へ昇格させる。
                        // 責任者経路と非責任者経路のどちらかが成功すれば一度だけ true になる (両方成功時は idempotent)。
                        await TryMarkPairsSsotObservedAsync(peerId, maskedPair);
                        await TryRemovePendingPairDeleteAsync(pairId, maskedPair);
                    }
                    else
                    {
                        // Codex P2 fix (第9弾 #4): 責任者 (相手) が既に pairs/{pairId} を recreate 済の場合も
                        // queued delete を取消す。残しておくと後の retry が新 pair を誤削除する race。
                        // PUT は不要 (既に存在) だが queue clear は必須。TryMarkPairsSsotObservedAsync は
                        // idempotent (peer.PairsSsotObserved 既に true なら no-op) なので両方呼んで安全。
                        Util.Logger.Log($"pairs/{maskedPair} 既に存在 (相手が recreated) → queue clear");
                        await TryMarkPairsSsotObservedAsync(peerId, maskedPair);
                        await TryRemovePendingPairDeleteAsync(pairId, maskedPair);
                    }
                }
                catch (Exception ex)
                {
                    Util.Logger.Log($"pairs/{maskedPair} fallback 書込失敗: {ex.Message}", Util.LogLevel.Warning);
                }
            });
        }
    }

    /// <summary>
    /// Codex P2 fix (第5弾 #5): pairs/{pairId} の PUT 成功直後に peer.PairsSsotObserved=true を立てて永続化する。
    /// peer 参照は _peerRegistry.FindPeer(peerId) で都度引き直す (Task の lifetime と peer mutation の安全のため)。
    /// 既に true 済み・peer 不在・registry 未注入・例外はすべて best-effort で握りつぶす (書込成功という主目的を阻害しない)。
    /// </summary>
    private async Task TryMarkPairsSsotObservedAsync(string peerId, string maskedPair)
    {
        var registry = _peerRegistry;
        if (registry == null) return;
        try
        {
            var peer = registry.FindPeer(peerId);
            if (peer == null) return;
            if (peer.PairsSsotObserved) return;  // 既に他経路で立っていれば no-op
            peer.PairsSsotObserved = true;
            await registry.AddOrUpdatePeerAsync(peer);
            Util.Logger.Log($"pairs/{maskedPair} 観察済みフラグを永続化");
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"pairs/{maskedPair} PairsSsotObserved 永続化に失敗（無視）: {ex.Message}", Util.LogLevel.Warning);
        }
    }

    /// <summary>
    /// Codex P2 fix (第4弾): pairs/{pairId} の書込成功直後に PendingPairDeleteQueue から同 pairId を取り除く。
    /// queue 注入なしや内部例外は静かに飲み込む（書込成功という主目的を阻害しない）。
    /// </summary>
    private async Task TryRemovePendingPairDeleteAsync(string pairId, string maskedPair)
    {
        var queue = _pendingPairDeleteQueue;
        if (queue == null) return;
        try
        {
            await queue.RemoveAsync(pairId);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"pairs/{maskedPair} 書込成功後の PendingPairDeleteQueue.Remove 失敗（無視）: {ex.Message}", Util.LogLevel.Warning);
        }
    }

    /// <summary>pairId (deviceId_deviceId) を両端だけマスクしてログ用に短くする。 </summary>
    private static string MaskPairId(string pairId)
    {
        var underscoreIdx = pairId.IndexOf('_');
        if (underscoreIdx < 0) return Util.Logger.MaskDeviceId(pairId);
        return Util.Logger.MaskDeviceId(pairId[..underscoreIdx]) + "_" + Util.Logger.MaskDeviceId(pairId[(underscoreIdx + 1)..]);
    }

    /// <summary>外部から pairId を導出するための公開ヘルパー（ConnectionViewModel.RemovePeerAsync で使う）。</summary>
    public string GeneratePairIdFor(string peerId) => GeneratePairId(_deviceId, peerId);

    /// <summary>
    /// #D-001a Phase B §6.3: Firebase pairs/{pairId} を削除する（SSoT 反映）。
    /// _signaling が接続中なら流用、未接続なら一時 FirebaseSignaling を作って DELETE する。
    /// 例外は呼出側にスローして PendingPairDeleteQueue へキューイングさせる。
    /// </summary>
    public async Task DeletePairFromFirebaseAsync(string peerId, CancellationToken ct = default)
    {
        var pairId = GeneratePairId(_deviceId, peerId);
        var sig = _signaling;
        if (sig != null)
        {
            await sig.DeletePairAsync(pairId, ct);
            return;
        }
        using var tempSig = NewSignaling();
        await tempSig.DeletePairAsync(pairId, ct);
    }

    // pairId 導出規約は Util.PairId.Generate に集約済（ConnectionService / PairSyncService 共通）。
    // 既存の多数の呼び出し点を変えないための薄いラッパ。
    private static string GeneratePairId(string a, string b) => Util.PairId.Generate(a, b);

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
        // Stage 4: 全 Session の in-flight connect を先にキャンセル。
        foreach (var kvp in _sessions)
        {
            try { kvp.Value.ConnectCts?.Cancel(); } catch { /* 既に Dispose 済み等 */ }
        }
        StopListeningForConnection();
        _transport?.Dispose();
        _signaling?.Dispose();

        // 複数ペア同時接続対応 Stage 3a: 残存セッションを一括 Dispose する。
        // Stage 4 で ConnectionSession.Dispose が ConnectGate / ConnectCts / Signaling まで含めて掃除する。
        foreach (var kvp in _sessions)
            DisposeSessionSilent(kvp.Value);
        _sessions.Clear();
    }

    /// <summary>複数ペア同時接続対応 Stage 3a: Session を安全に Dispose する単一ヘルパー。
    /// 二重 Dispose は <see cref="ConnectionSession.Dispose"/> 側の Interlocked ガードで無害化される。</summary>
    private static void DisposeSessionSilent(ConnectionSession session)
    {
        try { session.Dispose(); }
        catch (Exception ex) { Util.Logger.Log($"Session Dispose 失敗（無視）: {ex.Message}", Util.LogLevel.Debug); }
    }

    /// <summary>
    /// 複数ペア同時接続対応 Stage 3a: 接続中ペアごとに集約する入れ物。
    /// Stage 3a ではフィールドの入れ物のみ（参照は単数フィールドのまま）でシャドウ運用する。
    /// Stage 3b で単数フィールドの参照を Session 経由に置換、Stage 3c で AttachTransportEvents /
    /// OnChannelClosed / OnTransportRouteChanged を per-peer 化、Stage 4 で gate/CTS を per-peer 化する。
    ///
    /// IDisposable の二重発火は <see cref="_disposed"/> の Interlocked ガードで無害化する
    /// （OnChannelClosed と DisconnectAsync が同時に Dispose する競合への防御）。
    /// </summary>
    private sealed class ConnectionSession : IDisposable
    {
        /// <summary>このセッションが代表する相手 peer の SessionId(32hex)。<see cref="_sessions"/> の辞書キーと一致。</summary>
        public string PeerId { get; }

        /// <summary>このセッションの pairId（<see cref="ConnectionService._currentPairId"/> の Stage 3a シャドウ）。</summary>
        public string? PairId { get; set; }

        /// <summary>このセッションの transport。Stage 3a 時点では <see cref="ConnectionService._transport"/> のミラーで、
        /// 単数フィールド側を Disposable の権威として扱う（二重 Dispose 防止のため Session.Dispose は
        /// 自分が保持する参照のみ触る・単数 _transport は外側で別途破棄される）。</summary>
        public ITransport? Transport { get; set; }

        /// <summary>このセッションの signaling client。Stage 3a 時点では Mirror。</summary>
        public ISignalingService? Signaling { get; set; }

        /// <summary>このセッションの接続経路。Stage 3a 時点では <see cref="ConnectionService.Route"/> のミラー。</summary>
        public ConnectionRoute Route { get; set; } = ConnectionRoute.Unknown;

        /// <summary>このセッションの接続状態。Stage 3a 時点では <see cref="ConnectionService.State"/> のミラー。</summary>
        public PeerState State { get; set; } = PeerState.Disconnected;

        /// <summary>このセッションの相手 PeerInfo（接続成立後に設定）。<see cref="ConnectionService.ConnectedPeer"/> の Stage 3a シャドウ。</summary>
        public PeerInfo? ConnectedPeer { get; set; }

        /// <summary>このセッションの暗号チャネル。Stage 3c から per-peer 暗号セッションの権威ストレージ。
        /// OnSessionDataReceived（受信）と Stage 5 で per-peer 化される送信 API の両側がここを読む。</summary>
        public SecureChannel? SecureChannel { get; set; }

        /// <summary>暗号チャネル状態機械の直列化ロック（Session 単位）。
        /// OnFrame / Start / OnTimeout / ApplySecureStep の各経路が並行しうるためチャネル操作を直列化する。</summary>
        public readonly object SecureLock = new();

        /// <summary>Stage 3c: 暗号ハンドシェイク完了通知。true=確立 / false=平文フォールバック / 例外=HMAC 失敗。
        /// 送信側はこれを待ってから封筒化要否を判断する。</summary>
        public TaskCompletionSource<bool>? SecureReadyTcs { get; set; }

        /// <summary>Stage 3c: ハンドシェイクタイムアウト用 CTS（確立/フォールバックで Cancel する）。</summary>
        public CancellationTokenSource? SecureTimeoutCts { get; set; }

        // === 複数ペア同時接続対応 Stage 4: per-peer 着信監視 ===

        /// <summary>このセッションの着信監視 CTS（StartListeningForConnection で生成、StopListeningForConnection で Cancel/Dispose）。
        /// Stage 4 で per-Session 化し、全ペア常時 listen を支える。</summary>
        public CancellationTokenSource? ListeningCts { get; set; }

        /// <summary>このセッションの着信監視タスク参照。role 調停フォールバック時に Cancel 後の完了 await に使う
        /// （listener と本体のテアダウン競合防御）。Stage 4 で per-Session 化。</summary>
        public Task? ListeningTask { get; set; }

        // === 複数ペア同時接続対応 Stage 4: per-peer 並列接続解禁 ===
        // 旧単数 _connectGate / _connectCts / _connectingByListener / _currentPairId / _signaling は
        // 「同時 ConnectToPeerAsync は直列」前提で、ペアごとの並列接続が組めなかった。Stage 4 で全て per-Session に
        // 移管し、別ペアの connect/listen は互いに干渉しないようにする。同 peer の多重 connect は ConnectGate で
        // 直列化、in-flight 接続は ConnectCts で割り込みキャンセルする。
        //
        // Pairing 用 ISignalingService（StartPairingSessionAsync / OnPairingDetected で使う _signaling）は
        // 「接続フローと寿命が直交する 1 本だけのインスタンス」として ConnectionService 側の単数フィールドのまま
        // 残す（connect 経路は触らない）。Stage 4 ではこの分離で pairing watcher と並列 connect が両立する。

        /// <summary>このセッションの ConnectToPeerAsync を直列化するゲート。同 peer の多重 connect は順次処理する。
        /// 別 peer の Session は別ゲートなので、Y と Z の connect は完全並列に走る。</summary>
        public readonly SemaphoreSlim ConnectGate = new(1, 1);

        /// <summary>このセッションの ConnectToPeerAsync の所有 CTS。Cancel すると孤児ポーラー含め in-flight が中断する。
        /// 別 Session の ConnectCts には影響しない（peer X の connect 取消で peer Y の connect を巻き込まない）。</summary>
        public CancellationTokenSource? ConnectCts { get; set; }

        /// <summary>このセッションの State=Connecting を立てたのが listener かどうか。listener の OCE 復旧
        /// ブロックが ConnectToPeerAsync 側で立て直された Connecting を踏み潰さないための所有権フラグ。
        /// volatile 相当を int バッキングで実現（Volatile.Read/Write で可視性保証）。</summary>
        private int _connectingByListenerFlag;
        public bool ConnectingByListener
        {
            get => Volatile.Read(ref _connectingByListenerFlag) != 0;
            set => Volatile.Write(ref _connectingByListenerFlag, value ? 1 : 0);
        }

        // === 複数ペア同時接続対応 Stage 3b: クロージャ束縛されたトランスポートイベントハンドラ ===
        //
        // AttachTransportEvents(peerId, transport) が peerId と transport を捕捉した
        // クロージャ（lambda）を生成し、ここに保存する。DetachTransportEvents は
        // 保存したクロージャ参照を使って `_=` 解除する（メソッド参照だと自然に解除できるが、
        // クロージャは生成のたびに別インスタンスになるため、Attach 時に保存して同じ参照で
        // Detach する必要がある）。各ハンドラは内部で
        // `ReferenceEquals(transport, session.Transport)` ガードを通し、張り替え済みの
        // 古い transport から漂着したイベントを無視する（Stage 4 で並列接続が解禁されたとき、
        // 古い peer の OnChannelClosed が新 peer の Session を破壊しないための防御）。

        /// <summary>このセッションの transport.ChannelOpened に attach 済みのハンドラ。</summary>
        public EventHandler? OnChannelOpenedHandler { get; set; }

        /// <summary>このセッションの transport.ChannelClosed に attach 済みのハンドラ。</summary>
        public EventHandler? OnChannelClosedHandler { get; set; }

        /// <summary>このセッションの transport.DataReceived に attach 済みのハンドラ。</summary>
        public EventHandler<Infrastructure.DataReceivedEventArgs>? OnDataReceivedHandler { get; set; }

        /// <summary>このセッションの transport.RouteChanged に attach 済みのハンドラ。</summary>
        public EventHandler<ConnectionRoute>? OnRouteChangedHandler { get; set; }

        private int _disposed;

        public ConnectionSession(string peerId)
        {
            PeerId = peerId ?? throw new ArgumentNullException(nameof(peerId));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            // Stage 3b: 古い transport が残っていればハンドラ解除（DetachTransportEvents 通常経路で
            // 既に解除済みのはずだが、二重防御として Session.Dispose 経路でも掃除する）。
            var t = Transport;
            if (t != null)
            {
                if (OnChannelOpenedHandler != null) t.ChannelOpened -= OnChannelOpenedHandler;
                if (OnChannelClosedHandler != null) t.ChannelClosed -= OnChannelClosedHandler;
                if (OnDataReceivedHandler != null) t.DataReceived -= OnDataReceivedHandler;
                if (OnRouteChangedHandler != null) t.RouteChanged -= OnRouteChangedHandler;
            }
            OnChannelOpenedHandler = null;
            OnChannelClosedHandler = null;
            OnDataReceivedHandler = null;
            OnRouteChangedHandler = null;
            Transport = null;

            // Stage 4: per-Session signaling は Session.Dispose 経路でも Dispose する（DisconnectAsync 経路で
            // 既に Dispose 済みなら null。最後の Service.Dispose で残骸を回収する保険）。
            try { Signaling?.Dispose(); } catch { /* ignore */ }
            Signaling = null;

            // Stage 3c: 暗号リソースを解放。Channel は IDisposable ではないが、
            // CTS と TCS は宙吊りの SendAsync を解放するためにここでも掃除する（防御）。
            lock (SecureLock)
            {
                SecureTimeoutCts?.Cancel();
                SecureTimeoutCts?.Dispose();
                SecureTimeoutCts = null;
                SecureReadyTcs?.TrySetResult(false);
                SecureReadyTcs = null;
                SecureChannel = null;
            }

            // Stage 4: per-Session 着信監視 CTS を解放する。
            try { ListeningCts?.Cancel(); } catch { /* 既に Dispose 済み等 */ }
            ListeningCts?.Dispose();
            ListeningCts = null;
            ListeningTask = null;

            // Stage 4: per-Session 接続用 CTS / Gate を解放する。
            try { ConnectCts?.Cancel(); } catch { /* 既に Dispose 済み等 */ }
            ConnectCts?.Dispose();
            ConnectCts = null;
            try { ConnectGate.Dispose(); } catch { /* ignore */ }
        }
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
