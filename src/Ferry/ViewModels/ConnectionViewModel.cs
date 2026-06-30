using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ferry.Infrastructure;
using Ferry.Models;
using Ferry.Services;

namespace Ferry.ViewModels;

/// <summary>
/// 接続パネルの ViewModel。
/// QR コード表示 → Bridge ページ経由の自動ペアリング → 宛先選択を提供する。
/// </summary>
public sealed partial class ConnectionViewModel : ViewModelBase, IDisposable
{
    private readonly IConnectionService _connectionService;
    private readonly IQrCodeService _qrCodeService;
    private readonly ISettingsService _settingsService;
    private readonly IPeerRegistryService _peerRegistry;
    // #D-001a Phase B §6.3: Firebase pairs/{pairId} DELETE 失敗時の再試行キュー。
    // 注入経由（null 許容）= テストや過渡期で未配線でも動く。
    private readonly PendingPairDeleteQueue? _pendingPairDeletes;
    // rere #B1-001: presence は Infrastructure を直接 new せず、Services 抽象のファクトリ経由で生成する。
    private readonly IPresenceServiceFactory _presenceFactory;

    // プレゼンス監視（オンライン/オフライン検知）
    private IPresenceService? _presenceSignaling;
    private CancellationTokenSource? _presenceCts;
    private const int HeartbeatIntervalMs = 30_000;  // 30秒ごとに heartbeat 送信
    private const int PollIntervalMs = 30_000;       // ③ ピア状態のポーリング間隔（旧 10s → 30s。heartbeat 30s / 閾値 60s に対し十分）
    private const int FullPollEveryNCycles = 4;      // ② 全ピア取得は 4 サイクル(=2分)に1回。選択中ピアは毎サイクル取得
    private const int BackgroundRecheckMs = 5_000;   // ① 非前面時のフラグ再確認 tick（ネットワークアクセスなし）
    private const long OfflineThresholdMs = 60_000;  // 60秒更新なしでオフライン判定

    /// <summary>① presence ポーリングの稼働フラグ。ウィンドウが前面（表示中かつ非最小化）のときだけ true。
    /// バックグラウンド束縛のループ／UI スレッドの両方から触るので volatile。</summary>
    private volatile bool _isForeground = true;

    /// <summary>② 選択ピア優先ポーリングのサイクルカウンタ。FullPollEveryNCycles の剰余で全ピア取得回を決める。</summary>
    private long _pollCycle;

    /// <summary>宛先リスト投影の初期化完了フラグ。ctor 中の PeerSortMode 代入で
    /// OnPeerSortModeChanged が redundant な永続化/再構築を走らせないためのガード。</summary>
    private bool _peerProjectionReady;

    /// <summary>Codex P2 fix (第4弾 verify): 手動 RemovePeerAsync 経路が PeerRegistry の PeerRemoved event を再 trigger
    /// するのを抑制する。TryAdd してから RemovePeerAsync を呼び、handler 側で TryRemove() の戻り値が true なら skip。
    /// これで手動経路と handler 経路で StartSessionAsync が二重発火するのを防ぐ。
    /// ConcurrentDictionary で UI スレッド (Add) と PairSyncService の worker スレッド (PeerRemoved event handler の Remove)
    /// からの同時アクセスを thread-safe にする。HashSet は同時アクセスで内部バケットが破壊される。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _locallyInitiatedRemovals = new();

    [ObservableProperty]
    public partial PeerState ConnectionState { get; set; } = PeerState.Disconnected;

    /// <summary>QR コード関連のステータステキスト（ペアリング中のみ表示）。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Bitmap? QrCodeImage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PairingCode))]
    public partial string SessionId { get; set; } = string.Empty;

    /// <summary>ペアリング用 URL (Bridge ページ + sid/name クエリ)。QR コード生成にのみ使用、UI のテキスト表示は廃止。
    /// v1.0.38: いたずらでブラウザに開かれないよう、UI からの「コピー」対象は <see cref="PairingCode"/> (32 文字 hex) に変更</summary>
    [ObservableProperty]
    public partial string PairingUrl { get; set; } = string.Empty;

    /// <summary>UI 表示・コピー・貼り付け用のペアリングコード (= SessionId, 32 文字 hex)。
    /// v1.0.38 追加: URL を渡すとブラウザでうっかり開かれる事故が起きるため、ただの文字列に変更</summary>
    public string PairingCode => SessionId;

    [ObservableProperty]
    public partial string PeerName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsConnecting { get; set; }

    [ObservableProperty]
    public partial PairedPeer? SelectedPeer { get; set; }

    /// <summary>ペアリング済みピアの一覧。</summary>
    public ObservableCollection<PairedPeer> PairedPeers { get; } = [];

    /// <summary>宛先リストに実表示する投影（📌ピン/🟢オンライン/⚪オフラインの <see cref="PeerListSection"/> 見出しと
    /// <see cref="PairedPeer"/> を混在）。検索フィルタ + セクション分割 + セクション内ソートを適用して
    /// <see cref="RebuildVisiblePeers"/> が再構築する。ListBox はこちらを ItemsSource に bind する。</summary>
    public ObservableCollection<object> VisiblePeers { get; } = [];

    /// <summary>宛先リストの検索テキスト（表示名の部分一致・大文字小文字無視でフィルタ）。空で全件。</summary>
    [ObservableProperty]
    public partial string PeerSearchText { get; set; } = string.Empty;

    /// <summary>各セクション内のソート基準。変更で settings.json に永続化し、リストを再構築する。</summary>
    [ObservableProperty]
    public partial PeerSortMode PeerSortMode { get; set; } = PeerSortMode.Name;

    /// <summary>ペアリング済みピアが存在するか。QR/宛先リストの表示切替に使用。</summary>
    [ObservableProperty]
    public partial bool HasPairedPeers { get; set; }

    /// <summary>接続経路の表示テキスト。</summary>
    [ObservableProperty]
    public partial string ConnectionRouteText { get; set; } = string.Empty;

    /// <summary>リンクコピー済みフラグ（一時的に「コピー済み」表示にする）。</summary>
    [ObservableProperty]
    public partial bool IsLinkCopied { get; set; }

    /// <summary>「相手のペアリングコードを貼り付け」入力欄のテキスト (AddMemberView)。
    /// v1.0.38: PairFromUrlText から rename。コードは 32 文字 hex (sessionId)。</summary>
    [ObservableProperty]
    public partial string PairFromCodeText { get; set; } = string.Empty;

    /// <summary>コードペアリングの結果メッセージ (成功/エラー)。</summary>
    [ObservableProperty]
    public partial string PairFromCodeStatus { get; set; } = string.Empty;

    /// <summary>コードペアリング結果メッセージの色 (success/error で切替)。</summary>
    [ObservableProperty]
    public partial Avalonia.Media.IBrush? PairFromCodeStatusBrush { get; set; }

    public ConnectionViewModel(
        IConnectionService connectionService,
        IQrCodeService qrCodeService,
        ISettingsService settingsService,
        IPeerRegistryService peerRegistry,
        IPresenceServiceFactory presenceFactory,
        PendingPairDeleteQueue? pendingPairDeletes = null)
    {
        _connectionService = connectionService;
        _qrCodeService = qrCodeService;
        _settingsService = settingsService;
        _peerRegistry = peerRegistry;
        _presenceFactory = presenceFactory;
        _pendingPairDeletes = pendingPairDeletes;

        _connectionService.StateChanged += OnStateChanged;
        _connectionService.RouteChanged += OnRouteChanged;
        _connectionService.PairingCompleted += OnPairingCompleted;
        _connectionService.StatusMessageChanged += OnStatusMessageChanged;

        // 保存済みピアを読み込み + WentOnline 購読 (Online エッジで経路 Probe 発火用)
        foreach (var peer in _peerRegistry.GetPairedPeers())
        {
            peer.WentOnline += OnPeerWentOnline;
            peer.PropertyChanged += OnPeerPropertyChanged;  // 宛先リストの再ソートトリガー（IsOnline/IsPinned/Route 等）
            PairedPeers.Add(peer);
        }
        UpdateHasPairedPeers();

        // 宛先リストの投影（検索フィルタ + セクション分割 + セクション内ソート）を初期化する。
        // ソート基準は設定から復元。_peerProjectionReady を立ててから RebuildVisiblePeers を呼ぶことで、
        // ctor 中の PeerSortMode 代入で OnPeerSortModeChanged が redundant な永続化を走らせないようにする。
        PairedPeers.CollectionChanged += OnPairedPeersCollectionChanged;
        PeerSortMode = _settingsService.Settings.PeerListSortMode;
        _peerProjectionReady = true;
        RebuildVisiblePeers();
        // 複数ペア同時接続対応 Stage 4: 全 paired peer の listener を即時起動する（旧実装は SelectedPeer の listener のみ）。
        // peer ごとに <see cref="IConnectionService.StartListeningForConnection"/> が加算的に呼ばれるため、
        // 別 peer の listener を巻き込まずに並列稼働する。これで「ペア済みの誰からでも着信を受け付ける」基本動作を担保する。
        StartListeningForAllPairedPeers();
        // Codex P2 fix: PairSyncService が remote unpair を検知して peerRegistry から削除した時、
        // UI 側 PairedPeers を即時に追従させる（旧実装は再起動まで古い peer が UI に残っていた）。
        _peerRegistry.PeerRemoved += OnPeerRemovedFromRegistry;

        // rere PR#8 #F4: プレゼンス監視 (heartbeat + ポーリング) は実 Firebase への書き込み I/O を伴う。
        // ctor で起動すると、テストが VM を直接 new しただけで本番 Firebase に presence を書き込む汚染が
        // 起きる (#D-004 で URL が AppConstants 固定化され空ガードが効かなくなったため顕在化)。
        // 本番は App.axaml.cs が構築直後に StartPresenceMonitoring() を明示呼び出しする。
    }

    /// <summary>
    /// ペアリングセッションを開始し、QR コードを表示する。
    /// </summary>
    [RelayCommand]
    private async Task StartSessionAsync()
    {
        IsConnecting = true;
        StatusText = App.Text("Status.Starting");

        try
        {
            var settings = _settingsService.Settings;
            SessionId = await _connectionService.StartPairingSessionAsync();

            // Bridge ページ URL に sessionId / PC 名 / 公開鍵(rere #D-001(b)) / 認証 nonce(#D-001a Phase B) を付与して QR コード生成。
            // pk は base64url・nonce は 32hex なので URL 安全。空のときは &pk= となり Bridge 側は単に無視する。
            // CF 単独完結: QR の宛先は CF 版 Bridge（relay Worker の Static Assets）固定（Step 6 で Firebase 版を撤去）。
            var displayName = Uri.EscapeDataString(settings.DisplayName);
            var pk = _connectionService.PublicKeyForQr;
            var nonce = _connectionService.LastPairingNonce;
            var bridgeBase = AppConstants.CfBridgePageUrl;
            var bridgeUrl = $"{bridgeBase}?sid={SessionId}&name={displayName}&pk={pk}&nonce={nonce}";
            PairingUrl = bridgeUrl;
            QrCodeImage = _qrCodeService.GenerateQrBitmap(bridgeUrl);

            ConnectionState = PeerState.WaitingForPairing;
            StatusText = App.Text("Status.ScanQR");
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"セッション開始エラー: {ex.Message}", Util.LogLevel.Error);
            ConnectionState = PeerState.Error;
            StatusText = App.Text("Status.Error", ex.Message);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    /// <summary>
    /// ペアリングを解除する。
    /// </summary>
    [RelayCommand]
    private async Task RemovePeerAsync(string peerId)
    {
        // Codex P2 fix (第4弾 verify): 印付けを関数頭に移動する。PairSyncService が remote unpair を
        // 観測する race window (DeletePairFromFirebaseAsync 中に PeerRegistry → PeerRemoved 発火) で
        // handler が先回りして二重クリーンアップする可能性を防ぐ。
        // finally で TryRemove する: peer 不在で PeerRemoved event が発火しなかったケースでも
        // marker leak しないようにする (通常経路では handler が既に TryRemove 済みなので no-op)。
        _locallyInitiatedRemovals.TryAdd(peerId, 0);
        // Codex 第12弾 #3 (P2) fix: PeerRegistry にも「削除 in-flight」marker を立てる。
        // 旧実装は Firebase DELETE → registry.RemovePeerAsync という順序で、 DELETE が走っている間は
        // 依然 FindPeer(peerId) != null。 直前の PairingCompleted で起動した
        // WritePairRecordWithFallback の責任者書込 (即時) や 30s fallback が同 window で発火すると
        // 「peer が居る → PUT pairs/{pairId}」を実行して削除済みペアを resurrect 、 相手側に unpair を
        // 観測させない race があった。 marker を先に立てれば writer 側が IsPendingRemoval=true で abort する。
        // finally で必ず ClearPendingRemoval。
        _peerRegistry.MarkPendingRemoval(peerId);
        try
        {
            var peer = PairedPeers.FirstOrDefault(p => p.PeerId == peerId);

            // #D-001a Phase B §6.3: Firebase pairs/{pairId} を SSoT として削除する。
            // 失敗（オフライン等）したら PendingPairDeleteQueue に積んで起動時 retry に委ねる。
            try
            {
                await _connectionService.DeletePairFromFirebaseAsync(peerId);
            }
            catch (Exception ex)
            {
                var pairId = _connectionService.GeneratePairIdFor(peerId);
                Util.Logger.Log($"pairs/{pairId} 即時 DELETE 失敗 → PendingPairDeleteQueue へ: {ex.Message}", Util.LogLevel.Warning);
                if (_pendingPairDeletes != null)
                    await _pendingPairDeletes.EnqueueAsync(pairId);
            }

            // PeerRemoved event は同期発火し OnPeerRemovedFromRegistry がこの印を TryRemove() で消費する
            // → handler 側は二重実行 (StartSessionAsync race 含む) を skip する。
            await _peerRegistry.RemovePeerAsync(peerId);
            if (peer != null)
            {
                peer.WentOnline -= OnPeerWentOnline;
                PairedPeers.Remove(peer);
            }
            // rere #C2-001: 削除済みピアの presence ETag キャッシュ stale エントリを除去する。
            _presenceSignaling?.ForgetPresence(peerId);
            UpdateHasPairedPeers();

            if (SelectedPeer?.PeerId == peerId)
            {
                SelectedPeer = null;
                await _connectionService.DisconnectAsync();
            }
            else if (_connectionService.CurrentListeningPeerId == peerId)
            {
                // タブ切替 (DeselectKeepingListener) で SelectedPeer 外のピアを着信監視中に、
                // そのピアが削除されたケース。削除済みピアの offer を受け続けないよう監視を停止する。
                _connectionService.StopListeningForConnection();
            }

            // ペアが全て削除されたら QR コードを再表示
            if (PairedPeers.Count == 0)
            {
                StartSessionCommand.Execute(null);
            }
        }
        finally
        {
            // peer 不在で PeerRemoved event が発火しなかった (RemovePeerAsync が false 返却) ケースに備えて
            // marker leak を防ぐ。通常経路では既に handler が TryRemove 済みなので no-op。
            _locallyInitiatedRemovals.TryRemove(peerId, out _);
            // Codex 第12弾 #3 (P2) fix: PeerRegistry の pending-removal marker も必ず掃除する。
            // 30s fallback writer はこの marker が残っている間 abort するが、 marker を残し続けると
            // 同一 peerId で再ペアリングしたケースの writer まで abort されてしまう。
            _peerRegistry.ClearPendingRemoval(peerId);
        }
    }

    /// <summary>
    /// 接続を切断し、ペアリングセッションもキャンセルする。
    /// </summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _connectionService.DisconnectAsync();
        ClearQrCodeImage();
        PeerName = string.Empty;
        SessionId = string.Empty;
        PairingUrl = string.Empty;
        IsLinkCopied = false;
        ConnectionState = PeerState.Disconnected;
        StatusText = string.Empty;

        // 選択中ピアのステータスを更新し、着信監視を再開
        if (SelectedPeer != null)
        {
            SelectedPeer.ConnectionStatusText = string.Empty;
            SelectedPeer.Route = ConnectionRoute.Unknown;
            _connectionService.StartListeningForConnection(SelectedPeer.PeerId);
        }
    }

    /// <summary>
    /// 新しいピアを追加するためにペアリング画面に切り替える。
    /// </summary>
    [RelayCommand]
    private async Task AddNewPeerAsync()
    {
        await StartSessionAsync();
    }

    /// <summary>ペアリングコードのクリップボード書き込み要求イベント (View 側で TopLevel.Clipboard 経由処理)。
    /// v1.0.38: 旧 CopyPairingLinkRequested から rename。値は PairingCode (= SessionId)。</summary>
    public event EventHandler<string>? CopyPairingCodeRequested;

    /// <summary>
    /// ペアリングコードのコピーを要求する。実際のクリップボード操作は View 側で行う (N-5: MVVM 厳密化)。
    /// View 側はコピー成功後に <see cref="NotifyPairingLinkCopied"/> を呼んで UI を更新する。
    /// </summary>
    [RelayCommand]
    private void CopyPairingCode()
    {
        if (string.IsNullOrEmpty(PairingCode)) return;
        CopyPairingCodeRequested?.Invoke(this, PairingCode);
    }

    /// <summary>View 側のクリップボード書き込み成功後に呼び出され、「コピー済み」表示を 2 秒間表示する。</summary>
    public void NotifyPairingLinkCopied()
    {
        IsLinkCopied = true;
        Util.Logger.Log("ペアリングリンクをクリップボードにコピー");
        DispatcherTimer.RunOnce(() => IsLinkCopied = false, TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// 「相手のペアリングコードを貼り付け」入力欄からコードを取得し、アプリ内でペアリングを実行する。
    /// v1.0.38: 旧 PairFromUrlAsync から rename。Bridge ページ URL ではなく 32 文字 hex (sessionId) を受け取る形に変更。
    /// </summary>
    [RelayCommand]
    private async Task PairFromCodeAsync()
    {
        if (string.IsNullOrWhiteSpace(PairFromCodeText)) return;

        PairFromCodeStatus = "処理中…";
        PairFromCodeStatusBrush = Avalonia.Application.Current is { } app
            && app.TryGetResource("TextSecondaryBrush", app.ActualThemeVariant, out var pendingBrush)
            && pendingBrush is Avalonia.Media.IBrush pb ? pb : null;

        try
        {
            var (success, message) = await _connectionService.PairFromCodeAsync(PairFromCodeText.Trim());
            PairFromCodeStatus = message;

            var brushKey = success ? "GreenBrush" : "RedBrush";
            if (Avalonia.Application.Current is { } a
                && a.TryGetResource(brushKey, a.ActualThemeVariant, out var b)
                && b is Avalonia.Media.IBrush ib)
            {
                PairFromCodeStatusBrush = ib;
            }

            if (success)
            {
                // 成功時は入力欄をクリア (ペアリング検知は StartWatchingPairing で反映)
                PairFromCodeText = string.Empty;
            }
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"コードペアリング失敗: {ex.Message}", Util.LogLevel.Warning);
            PairFromCodeStatus = $"エラー: {ex.Message}";
        }
    }

    /// <summary>
    /// v1.0.38: ピア一覧を手動更新する。全ピアの presence を即取得して IsOnline を反映、
    /// Online ピアの経路 Probe クールダウンをリセットして経路バッジを再判定する。
    /// </summary>
    [RelayCommand]
    private async Task RefreshPeersAsync()
    {
        var sig = _presenceSignaling;
        if (sig is null)
        {
            Util.Logger.Log("RefreshPeers スキップ: presence signaling 未初期化");
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var peers = PairedPeers.ToArray();
        Util.Logger.Log($"ピア一覧を手動更新: {peers.Length} 件");

        // Probe クールダウン全リセット (手動更新時は最新化を優先)
        lock (_lastProbeAt) { _lastProbeAt.Clear(); }

        var tasks = peers.Select(async peer =>
        {
            try
            {
                var presenceData = await sig.GetPresenceAsync(peer.PeerId);
                var isOnline = presenceData != null && (now - presenceData.LastSeen) < OfflineThresholdMs;

                var nameChanged = false;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // v1.0.47 修正: PresencePollLoop は帯域節約のため LastSeen のみ取得していて
                    // 表示名同期をこの RefreshPeersAsync（手動更新 / 前面復帰）に委譲している。
                    // ここで反映しないと相手が改名しても古い名前が残り続ける。
                    if (presenceData != null
                        && !string.IsNullOrEmpty(presenceData.DisplayName)
                        && peer.DisplayName != presenceData.DisplayName)
                    {
                        peer.DisplayName = presenceData.DisplayName;
                        nameChanged = true;
                    }

                    // IsOnline edge トリガーで Probe 走る (false → true の場合) ので、
                    // 既に true の場合は明示的に Probe 呼び出し
                    var wasOnline = peer.IsOnline;
                    peer.IsOnline = isOnline;
                    if (isOnline
                        && _connectionService.State is PeerState.Connected or PeerState.Connecting
                        && _connectionService.ConnectedPeer?.SessionId == peer.PeerId)
                    {
                        // v1.0.47: 転送中(=このピアに接続中)はライブ経路を即反映し probe しない。
                        // probe はメイン接続と競合してタイムアウト→Unknown 退行しやすいため。
                        peer.Route = _connectionService.Route;
                    }
                    else if (isOnline && wasOnline)
                    {
                        // edge ではないので手動で発火
                        _ = Task.Run(() => ProbePeerRouteAsync(peer));
                    }
                    else if (!isOnline)
                    {
                        // v1.0.38 review fix v13: offline になったら古い Route バッジを消す。
                        // バッジ可視性は PairedPeer.IsConnected (Route != Unknown) で駆動されているため、
                        // 旧 LAN/P2P/relay バッジが offline 後も残り続けて誤誘導するのを防ぐ
                        peer.Route = ConnectionRoute.Unknown;
                    }
                });

                // 表示名が変わったら peers.json へ永続化する（再起動後もスタールしないように）。
                // UI スレッド外の await は OK（PeerRegistryService 内で SaveAsync が走る）。
                if (nameChanged)
                    await _peerRegistry.AddOrUpdatePeerAsync(peer);
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"RefreshPeers ピア更新エラー: {peer.PeerId}, {ex.Message}", Util.LogLevel.Warning);
            }
        });

        await Task.WhenAll(tasks);
        Util.Logger.Log("ピア一覧の手動更新完了");
    }

    /// <summary>
    /// true の間は SelectedPeer=null でも着信監視を止めない。
    /// 「設定」「ペアリング先追加」タブへ切り替えるとき、宛先リストの選択ハイライトは外しつつ
    /// 直前ピアの着信監視を維持し、タブ表示中も相手からのファイル転送を受けられるようにする。
    /// </summary>
    private bool _keepListeningOnDeselect;

    /// <summary>
    /// タブ切替用の選択解除。宛先リストのハイライトは外すが、直前ピアの着信監視は維持する。
    /// （設定 / ペアリング先追加タブへ切り替える際に MainWindowViewModel から呼ぶ。
    ///  単純に SelectedPeer=null すると着信監視が止まり、タブ表示中に相手が転送を
    ///  開始できなくなる回帰を避けるため。）
    /// </summary>
    public void DeselectKeepingListener()
    {
        if (SelectedPeer == null) return;
        _keepListeningOnDeselect = true;
        try { SelectedPeer = null; }
        finally { _keepListeningOnDeselect = false; }
    }

    /// <summary>
    /// ピア選択時は宛先を記憶し、着信接続監視を開始する。
    /// 相手側がファイルを送ろうとした時に自動的に Answer を返せるようにする。
    /// </summary>
    partial void OnSelectedPeerChanged(PairedPeer? oldValue, PairedPeer? newValue)
    {
        // 前の選択ピアのステータスをクリア。
        // Route は過去の接続実績として残し、メンバーリストにバッジを表示し続ける。
        if (oldValue != null)
        {
            oldValue.ConnectionStatusText = string.Empty;
        }

        if (newValue != null)
        {
            PeerName = newValue.DisplayName;
            // 着信接続監視を開始（相手からの Offer に自動応答できるようにする）
            _connectionService.StartListeningForConnection(newValue.PeerId);
            Util.Logger.Log($"ピア選択・着信監視開始: {newValue.DisplayName} ({newValue.PeerId})");
        }
        else if (!_keepListeningOnDeselect)
        {
            // 通常の選択解除（ピア削除など）。タブ切替による一時解除のときは
            // _keepListeningOnDeselect=true なので止めず、直前ピアの着信監視を維持する。
            _connectionService.StopListeningForConnection();
        }
    }

    /// <summary>
    /// 選択されたピアにオンデマンド接続する（ファイル転送開始時に呼ばれる）。
    /// v1.0.47: ct を渡せるようにして、転送キャンセルで「接続待ち」状態にも割り込めるようにする
    /// （旧実装は CT 無しで、相手 offline / NAT 越え試行中の長い待ちをユーザーが中断できなかった）。
    /// </summary>
    public async Task ConnectToSelectedPeerAsync(CancellationToken ct = default)
    {
        var peer = SelectedPeer;
        if (peer == null)
        {
            Util.Logger.Log("ConnectToSelectedPeerAsync: SelectedPeer が null のため接続スキップ", Util.LogLevel.Warning);
            throw new InvalidOperationException("接続先のピアが選択されていません");
        }

        if (ConnectionState == PeerState.Connected && _connectionService.ConnectedPeer?.SessionId == peer.PeerId)
            return; // 既に接続済み

        // 前の接続を切断
        if (ConnectionState is PeerState.Connected or PeerState.Connecting)
            await _connectionService.DisconnectAsync();

        IsConnecting = true;
        peer.ConnectionStatusText = App.Text("Status.Connecting");

        try
        {
            await _connectionService.ConnectToPeerAsync(peer.PeerId, ct);
        }
        catch (Exception ex)
        {
            // ユーザー操作によるキャンセル (CancelTransfer 経由の CT 発火) は Info に落とす。
            // 接続待ち中のキャンセルは仕様通りの動作なので Warning にすると正常操作がエラーログ汚染になる。
            if (ex is OperationCanceledException)
                Util.Logger.Log($"接続キャンセル ({peer.DisplayName})");
            else
                Util.Logger.Log($"接続エラー ({peer.DisplayName}): {ex.Message}", Util.LogLevel.Warning);
            peer.ConnectionStatusText = string.Empty;
            ConnectionState = PeerState.Disconnected;
            // 接続失敗後に着信監視を再開
            _connectionService.StartListeningForConnection(peer.PeerId);
            throw;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private void OnStateChanged(object? sender, PeerState state)
    {
        // 非 UI スレッドから呼ばれる可能性があるため UI スレッドにディスパッチ
        Dispatcher.UIThread.Post(() =>
        {
            ConnectionState = state;

            // QR ペアリング中のステータスのみ StatusText に表示
            if (state is PeerState.WaitingForPairing or PeerState.WaitingForMatch)
            {
                StatusText = state switch
                {
                    PeerState.WaitingForPairing => App.Text("Status.ScanQR"),
                    PeerState.WaitingForMatch => App.Text("Status.ScanPeerQR"),
                    _ => string.Empty,
                };
            }
            else
            {
                StatusText = string.Empty;
            }

            // 接続状態をピアのリスト項目に反映（転送時のみ表示、待機中は非表示）
            if (SelectedPeer != null)
            {
                SelectedPeer.ConnectionStatusText = state switch
                {
                    PeerState.Connecting => "🔄 " + App.Text("Status.Connecting"),
                    PeerState.Reconnecting => "🔄 " + App.Text("Status.Reconnecting"),
                    _ => string.Empty,
                };
            }

            // 未接続時は TransferView の経路テキストだけクリア。
            // ピアの Route バッジ (メンバーリスト) は過去の接続実績として残す方が、
            // 次回どの経路で繋がりそうか予測できて UX が良い。次回接続時に新しい Route で上書きされる。
            if (state != PeerState.Connected)
            {
                ConnectionRouteText = string.Empty;
            }
        });
    }

    private void OnStatusMessageChanged(object? sender, string messageKey)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (SelectedPeer != null)
            {
                SelectedPeer.ConnectionStatusText = "🔄 " + App.Text(messageKey);
            }
        });
    }

    private void OnRouteChanged(object? sender, ConnectionRoute route)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 選択中のピアの経路を更新（宛先リストの各行に表示される）
            if (SelectedPeer != null)
                SelectedPeer.Route = route;

            ConnectionRouteText = route switch
            {
                ConnectionRoute.Direct => "🟢 " + App.Text("Route.Direct"),
                ConnectionRoute.StunAssisted => "🟡 " + App.Text("Route.Stun"),
                ConnectionRoute.Relay => "🔴 " + App.Text("Route.Relay"),
                _ => string.Empty,
            };
        });
    }

    private async void OnPairingCompleted(object? sender, PairedPeer peer)
    {
        try
        {
            // UI スレッドで判定・更新する (PairedPeers / ObservableProperty は UI スレッド専用)。
            // 既知ピアの再検知は新規ペアリングではないので UI を切り替えない:
            // pairings/{pairingId} は即削除されず最大 1 時間 Firebase に残るため、
            // 「ペアリング先追加」で StartWatchingPairing を始めた直後に、過去に成立済みの
            // 自分宛ペアリングを拾って PairingCompleted が再発火する。これを通すと既存ピアが
            // SelectedPeer に再選択され、ペアリング追加画面がすぐ閉じて宛先画面へ戻ってしまう。
            var isNewPeer = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (PairedPeers.Any(p => p.PeerId == peer.PeerId))
                {
                    Util.Logger.Log($"既知ピアのペアリング再検知を無視: {peer.DisplayName} ({peer.PeerId})");
                    return false;
                }

                peer.WentOnline += OnPeerWentOnline;
                PairedPeers.Add(peer);
                UpdateHasPairedPeers();

                // QR コード表示をクリアし、宛先選択モードへ
                ClearQrCodeImage();
                SessionId = string.Empty;
                PairingUrl = string.Empty;
                IsLinkCopied = false;
                SelectedPeer = peer;
                return true;
            });

            // 新規ピア成立時のみ: pairing watch を止めて永続化する。
            // 既知ピアの再検知 (stale な pairings/ エントリ由来) では watch を止めず、
            // 新規デバイスとのペアリングを引き続き検知できるようにする。
            if (isNewPeer)
            {
                _connectionService.StopPairingWatch();
                await _peerRegistry.AddOrUpdatePeerAsync(peer); // 既存ピアを上書きして PairedAt / LastTransferAt を潰さない
            }
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"ペアリング完了処理エラー: {ex.Message}", Util.LogLevel.Error);
        }
    }

    private void UpdateHasPairedPeers() => HasPairedPeers = PairedPeers.Count > 0;

    // === 宛先リストの検索 / ソート / セクション分割（VisiblePeers 投影）===

    /// <summary>再ソートを誘発する PairedPeer のプロパティ名集合（IsOnline 切替・ピン・経路変化など）。</summary>
    private static readonly HashSet<string> PeerResortTriggers =
    [
        nameof(PairedPeer.DisplayName), nameof(PairedPeer.IsOnline), nameof(PairedPeer.IsPinned),
        nameof(PairedPeer.Route), nameof(PairedPeer.IsTransferring), nameof(PairedPeer.ActiveTransferCount),
    ];

    partial void OnPeerSearchTextChanged(string value)
    {
        if (_peerProjectionReady) RebuildVisiblePeers();
    }

    partial void OnPeerSortModeChanged(PeerSortMode value)
    {
        if (!_peerProjectionReady) return;
        _settingsService.Settings.PeerListSortMode = value;
        _ = _settingsService.SaveAsync();  // 選択を永続化（次回起動で復元）
        RebuildVisiblePeers();
    }

    /// <summary>PairedPeers への add/remove に応じて PropertyChanged 購読を張り替え、投影を再構築する。</summary>
    private void OnPairedPeersCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (PairedPeer p in e.OldItems) p.PropertyChanged -= OnPeerPropertyChanged;
        if (e.NewItems != null)
            foreach (PairedPeer p in e.NewItems) p.PropertyChanged += OnPeerPropertyChanged;
        RebuildVisiblePeers();
    }

    private void OnPeerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != null && PeerResortTriggers.Contains(e.PropertyName))
            RebuildVisiblePeers();
    }

    /// <summary>検索フィルタ → 📌ピン/🟢オンライン/⚪オフラインのセクション分割 → セクション内ソートで VisiblePeers を再構築する。
    /// ObservableCollection は UI スレッド専用なので、別スレッドからの呼び出しは UI スレッドへ marshal する。</summary>
    private void RebuildVisiblePeers()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RebuildVisiblePeers);
            return;
        }

        // VisiblePeers を全消し（Clear）すると ListBox.SelectedItem→null→SelectedPeer→null が TwoWay で伝播し、
        // OnSelectedPeerChanged の副作用だけでなく外部購読者（TransferViewModel/MainWindow）にも SelectedPeer 変更が
        // 飛んで、転送パネルが空になる等の退行を招く。そのため全消しせず in-place の差分更新で寄せる。
        // さらに「選択中ピアは検索フィルタを無視して常に表示」することで、検索入力中も選択がコレクションから
        // 外れず（= SelectedPeer が null 化されず）、進行中転送 UI を維持する。
        var target = BuildPeerProjection(PairedPeers, PeerSearchText, PeerSortMode, static k => App.Text(k), SelectedPeer);
        ReconcileVisiblePeers(target);
    }

    /// <summary>VisiblePeers を <paramref name="target"/> に最小差分で寄せる（全 Clear しない）。
    /// 選択中の <see cref="PairedPeer"/> は target に残る限りコレクションから外れないため、
    /// ListBox.SelectedItem→SelectedPeer の null churn が起きない。<see cref="PeerListSection"/> は record の
    /// 値等価なので、再構築をまたいで同じ見出しは同一視され不要な削除・再追加が避けられる。</summary>
    private void ReconcileVisiblePeers(List<object> target)
    {
        // 1) target に無い項目を後ろから除去（PairedPeer は参照等価、PeerListSection は値等価）。
        for (int i = VisiblePeers.Count - 1; i >= 0; i--)
            if (!target.Contains(VisiblePeers[i]))
                VisiblePeers.RemoveAt(i);
        // 2) target の順序に合わせて挿入 / 移動。
        for (int i = 0; i < target.Count; i++)
        {
            var item = target[i];
            int cur = VisiblePeers.IndexOf(item);
            if (cur < 0) VisiblePeers.Insert(i, item);
            else if (cur != i) VisiblePeers.Move(cur, i);
        }
        // 3) 末尾の余剰を除去（通常は発生しないが安全側）。
        while (VisiblePeers.Count > target.Count)
            VisiblePeers.RemoveAt(VisiblePeers.Count - 1);
    }

    /// <summary>純関数: 検索フィルタ → 📌ピン/🟢オンライン/⚪オフラインのセクション分割 → セクション内ソートで
    /// 表示用の混在リスト（<see cref="PeerListSection"/> 見出し + <see cref="PairedPeer"/> 行）を組み立てる。
    /// Dispatcher 非依存なのでユニットテスト可能。<paramref name="label"/> はセクション見出しの
    /// ローカライズ解決（本番は App.Text、テストは恒等関数）。<paramref name="keep"/> は検索フィルタに
    /// 一致しなくても必ず残すピア（= 選択中ピア。検索入力で選択が消えて転送 UI が飛ぶ退行を防ぐ）。</summary>
    internal static List<object> BuildPeerProjection(
        IEnumerable<PairedPeer> peers, string? search, PeerSortMode mode, Func<string, string> label, PairedPeer? keep = null)
    {
        var result = new List<object>();
        var s = search?.Trim();
        bool hasSearch = !string.IsNullOrEmpty(s);
        var filtered = peers
            .Where(p => !hasSearch
                        || p.DisplayName.Contains(s!, StringComparison.OrdinalIgnoreCase)
                        || ReferenceEquals(p, keep))
            .ToList();

        // ピンは online/offline を問わず最上位セクションへ。残りを online/offline に分割。
        AddPeerSection(result, "📌", label("Peer.Section.Pinned"), filtered.Where(p => p.IsPinned), mode);
        AddPeerSection(result, "🟢", label("Peer.Section.Online"), filtered.Where(p => !p.IsPinned && p.IsOnline), mode);
        AddPeerSection(result, "⚪", label("Peer.Section.Offline"), filtered.Where(p => !p.IsPinned && !p.IsOnline), mode);
        return result;
    }

    /// <summary>セクション内のソートを適用し、メンバーが 1 件以上あるときだけ見出し + 行を積む。</summary>
    private static void AddPeerSection(List<object> dst, string icon, string label, IEnumerable<PairedPeer> members, PeerSortMode mode)
    {
        var sorted = SortPeersWithin(members, mode);
        if (sorted.Count == 0) return;
        dst.Add(new PeerListSection { Icon = icon, Label = label });
        dst.AddRange(sorted);
    }

    private static List<PairedPeer> SortPeersWithin(IEnumerable<PairedPeer> peers, PeerSortMode mode) => mode switch
    {
        PeerSortMode.LastTransfer => [.. peers.OrderByDescending(p => p.LastTransferAt ?? DateTime.MinValue)
                                               .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)],
        PeerSortMode.Route => [.. peers.OrderBy(p => RouteSortRank(p.Route))
                                       .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)],
        PeerSortMode.Transferring => [.. peers.OrderByDescending(p => p.IsTransferring)
                                              .ThenByDescending(p => p.ActiveTransferCount)
                                              .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)],
        _ => [.. peers.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)],
    };

    private static int RouteSortRank(ConnectionRoute route) => route switch
    {
        ConnectionRoute.Direct => 0,
        ConnectionRoute.StunAssisted => 1,
        ConnectionRoute.Relay => 2,
        _ => 3,
    };

    /// <summary>ピン留めの切替。peers.json に永続化し、IsPinned 変更通知で宛先リストを再構築する。</summary>
    [RelayCommand]
    private async Task TogglePinAsync(PairedPeer? peer)
    {
        if (peer is null) return;
        peer.IsPinned = !peer.IsPinned;  // PropertyChanged → RebuildVisiblePeers
        await _peerRegistry.UpdatePeerIfPresentAsync(peer);
    }

    /// <summary>ソート基準を切り替える（ソートメニューから呼ぶ）。OnPeerSortModeChanged が永続化 + 再構築する。</summary>
    [RelayCommand]
    private void SetSortMode(PeerSortMode mode) => PeerSortMode = mode;

    /// <summary>
    /// Codex P2 fix: <see cref="IPeerRegistryService.PeerRemoved"/> ハンドラ。PairSyncService が remote unpair を
    /// 検知して peerRegistry から peer を消したとき、UI 側 PairedPeers と presence 監視も同期的に外す。
    /// </summary>
    private void OnPeerRemovedFromRegistry(object? sender, string peerId)
    {
        // Codex P2 fix (第4弾): 手動 RemovePeerAsync 経路が既に全クリーンアップを実行済みの場合は skip。
        // event は同期発火するので Dispatcher.Post する前の outer scope で判定する必要がある
        // (Post 後だと UI スレッドに切り替わるまでに別の event が来て印を消費し race する)。
        if (_locallyInitiatedRemovals.TryRemove(peerId, out _)) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var peer = PairedPeers.FirstOrDefault(p => p.PeerId == peerId);
                if (peer != null)
                {
                    peer.WentOnline -= OnPeerWentOnline;
                    PairedPeers.Remove(peer);
                }
                _presenceSignaling?.ForgetPresence(peerId);
                UpdateHasPairedPeers();

                // Codex P2 fix (第2弾): 手動 RemovePeerAsync と同等のフルクリーンアップを適用。
                // 旧実装は PairedPeers 表示の更新だけで、SelectedPeer / 接続中状態 / CurrentListeningPeerId
                // が残ったままアプリが「もう存在しない peer」を listen / 送信し続ける状態だった。
                if (SelectedPeer?.PeerId == peerId)
                {
                    SelectedPeer = null;
                    try { await _connectionService.DisconnectAsync(); }
                    catch (Exception ex) { Util.Logger.Log($"PairSync 削除後の DisconnectAsync エラー (継続): {ex.Message}", Util.LogLevel.Debug); }
                }
                else if (_connectionService.CurrentListeningPeerId == peerId)
                {
                    _connectionService.StopListeningForConnection();
                }

                if (PairedPeers.Count == 0)
                {
                    // 手動削除パスでは「最後の 1 件削除で QR 再表示」していた。PairSync 由来の削除でも対称に揃える。
                    try { await StartSessionAsync(); }
                    catch (Exception ex) { Util.Logger.Log($"PairSync 全削除後の StartSession エラー (継続): {ex.Message}", Util.LogLevel.Debug); }
                }
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"OnPeerRemovedFromRegistry エラー: {ex.Message}", Util.LogLevel.Warning);
            }
        });
    }

    // === プレゼンス監視 ===

    /// <summary>
    /// プレゼンス監視 (heartbeat 送信 + ピアのオンライン状態ポーリング) を開始する。
    /// rere PR#8 #F4: 実 Firebase I/O を伴うため ctor では呼ばず、本番は App.axaml.cs が
    /// VM 構築直後に明示呼び出しする (テストは呼ばないので本番 Firebase を汚染しない)。冪等。
    /// </summary>
    public void StartPresenceMonitoring()
    {
        // rere #D-004: Firebase DB URL は AppConstants 固定（常に非空なので空ガードは不要）。
        _presenceCts?.Cancel();
        _presenceCts?.Dispose();
        _presenceCts = new CancellationTokenSource();

        _presenceSignaling?.Dispose();
        _presenceSignaling = _presenceFactory.Create();

        var deviceId = _settingsService.Settings.DeviceId;
        var displayName = _settingsService.Settings.DisplayName;
        var ct = _presenceCts.Token;

        _ = HeartbeatLoopAsync(deviceId, displayName, ct);
        _ = PresencePollLoopAsync(ct);
    }

    /// <summary>
    /// 定期的に自分の lastSeen を Firebase に書き込む。
    /// </summary>
    private async Task HeartbeatLoopAsync(string deviceId, string displayName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Dispose レースで _presenceSignaling が null 化されても NRE しないよう
            // ループ反復ごとにローカル束縛する（ConnectionService.OnPairingDetected と同パターン）
            var sig = _presenceSignaling;
            if (sig is null) break;

            try
            {
                // 設定変更に対応するため毎回最新の表示名を取得
                var currentName = _settingsService.Settings.DisplayName;
                await sig.UpdatePresenceAsync(deviceId, currentName, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Util.Logger.Log($"Heartbeat 送信エラー: {ex.Message}", Util.LogLevel.Warning);
            }

            try { await Task.Delay(HeartbeatIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// ① presence ポーリングの稼働状態を切り替える（View 側のウィンドウ可視性から呼ぶ）。
    /// トレイ格納／最小化中はポーリングを止めて Firebase ダウンロード帯域を節約する
    /// （Heartbeat 送信は <see cref="HeartbeatLoopAsync"/> 側で継続するので相手からは online のまま見える）。
    /// 前面復帰時は <see cref="RefreshPeersAsync"/> で全ピアを即フル取得し、表示名同期と経路再判定も済ませる。
    /// </summary>
    public void SetPresencePollingActive(bool active)
    {
        if (_isForeground == active) return;
        _isForeground = active;
        if (active)
            _ = RefreshPeersAsync(); // 前面復帰: 全ピアを即更新（DisplayName 同期込み）。ループは次 tick で通常 cadence に戻る
    }

    /// <summary>
    /// 定期的にペアリング済みピアの lastSeen をチェックし、IsOnline を更新する。
    /// 帯域節約のため、① 非前面時は停止、② 選択中ピアは毎サイクル・他は数サイクルに1回、
    /// ④⑤ 取得は LastSeen のみの ETag 条件付き GET（<see cref="Services.IPresenceService.GetPresenceLastSeenAsync"/>）。
    /// </summary>
    private async Task PresencePollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // ① 可視性ゲート: トレイ格納／最小化中はネットワークポーリングを止める。
            //    （フラグ再確認用の軽い tick だけ回す。ネットワークアクセスは発生しない）
            if (!_isForeground)
            {
                try { await Task.Delay(BackgroundRecheckMs, ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // Dispose レース対策: ループ内でローカル束縛
            var sig = _presenceSignaling;
            if (sig is null) break;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // ② 全ピア取得は FullPollEveryNCycles 回に1回だけ。それ以外のサイクルは選択中ピアのみ高速更新。
            //    ピア未選択（一覧を眺めている状態）は一覧の online 表示を最新に保つため毎サイクル全ピア取得する。
            //    いずれも前面時のみ（① のゲート通過済み）なので帯域影響は実使用中ウィンドウに限定される。
            var isFullCycle = (_pollCycle % FullPollEveryNCycles) == 0;
            _pollCycle++;
            var selected = SelectedPeer;
            var targets = (isFullCycle || selected is null)
                ? PairedPeers.ToArray()
                : new[] { selected };

            if (targets.Length > 0)
            {
                // 対象ピアの LastSeen を並列取得（順次 → 並列で N 倍高速化）
                var tasks = targets.Select(async peer =>
                {
                    try
                    {
                        // ④⑤ DisplayName を載せない LastSeen 単独取得 + ETag 条件付き（未変更なら 304 で本文ゼロ）
                        var lastSeen = await sig.GetPresenceLastSeenAsync(peer.PeerId, ct);
                        var isOnline = lastSeen.HasValue && (now - lastSeen.Value) < OfflineThresholdMs;

                        Dispatcher.UIThread.Post(() =>
                        {
                            if (peer.IsOnline != isOnline)
                            {
                                // false → true で WentOnline が発火し経路 Probe が走る（OnPeerWentOnline）
                                peer.IsOnline = isOnline;
                                // v1.0.38 review fix v13: offline 遷移時に古い Route バッジを消す
                                // (バッジ可視性は IsConnected = Route != Unknown で駆動されるため、
                                // 古い LAN/P2P/relay バッジが offline 後も残り続けて誤誘導するのを防ぐ)
                                if (!isOnline)
                                    peer.Route = ConnectionRoute.Unknown;
                            }
                            // 表示名同期は本ループでは行わない（⑤）。RefreshPeersAsync（手動/前面復帰）に委譲。
                        });
                    }
                    catch (OperationCanceledException) { /* キャンセルは WhenAll 後に判定 */ }
                    catch
                    {
                        // 個別ピアのエラーは無視して次へ
                    }
                });

                await Task.WhenAll(tasks);
            }

            if (ct.IsCancellationRequested) break;

            // ポーリング間隔（末尾に配置して初回は即座にチェック）
            try { await Task.Delay(PollIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// QrCodeImage を安全にクリアする。
    /// null 代入 → UI レイアウト完了後に Dispose することで
    /// レイアウトパス中の NullReferenceException を防ぐ。
    /// </summary>
    private void ClearQrCodeImage()
    {
        var oldImage = QrCodeImage;
        QrCodeImage = null;
        if (oldImage is not null)
        {
            // Background 優先度で Dispose → レイアウトパス完了後に実行される
            Dispatcher.UIThread.Post(() => oldImage.Dispose(), DispatcherPriority.Background);
        }
    }

    /// <summary>複数ペア同時接続対応 Stage 4: 全 paired peer の <see cref="IConnectionService.StartListeningForConnection"/>
    /// を呼ぶヘルパー。コンストラクタの保存ピア読み込み直後と、必要時の再起動経路で利用する。
    /// listener は per-peer Session に保存され、相互独立に走るため複数 peer 並列で着信を受けられる。</summary>
    private void StartListeningForAllPairedPeers()
    {
        foreach (var peer in PairedPeers)
        {
            if (string.IsNullOrEmpty(peer.PeerId)) continue;
            try { _connectionService.StartListeningForConnection(peer.PeerId); }
            catch (Exception ex)
            {
                Util.Logger.Log($"全ペア listener 起動失敗 (peer={peer.DisplayName}): {ex.Message}", Util.LogLevel.Warning);
            }
        }
    }

    public void Dispose()
    {
        _connectionService.StopListeningForConnection();
        _connectionService.StateChanged -= OnStateChanged;
        _connectionService.RouteChanged -= OnRouteChanged;
        _connectionService.PairingCompleted -= OnPairingCompleted;
        _connectionService.StatusMessageChanged -= OnStatusMessageChanged;
        _peerRegistry.PeerRemoved -= OnPeerRemovedFromRegistry;
        ClearQrCodeImage();

        // 全ピアの WentOnline 購読を解除
        foreach (var peer in PairedPeers)
            peer.WentOnline -= OnPeerWentOnline;
        _probeSemaphore.Dispose();

        // プレゼンス監視を停止し、自分のプレゼンスを削除
        _presenceCts?.Cancel();
        _presenceCts?.Dispose();
        if (_presenceSignaling != null)
        {
            var deviceId = _settingsService.Settings.DeviceId;
            _ = _presenceSignaling.RemovePresenceAsync(deviceId);
            _presenceSignaling.Dispose();
        }
    }

    // === 経路 Probe (オンラインエッジで 1 回、ファイル送信前から経路バッジを表示するため) ===

    /// <summary>peer.PeerId → 最終 Probe 時刻。cooldown 判定で使う。</summary>
    private readonly Dictionary<string, DateTimeOffset> _lastProbeAt = new();
    /// <summary>同時 Probe を 1 件に絞るセマフォ (シグナリングノード競合防止)。</summary>
    private readonly SemaphoreSlim _probeSemaphore = new(1, 1);
    /// <summary>v1.0.38 review fix v10: セマフォ取得失敗で skip された peer の待ちキュー。
    /// Release 時に dequeue して順次処理する (複数 peer 同時 Online 時に最初の 1 つしか
    /// probe されないバグの修正)。lock(_probeQueueLock) で保護。</summary>
    private readonly Queue<PairedPeer> _probeQueue = new();
    private readonly Lock _probeQueueLock = new();
    /// <summary>同じピアへの Probe 連発を防ぐクールダウン (Online flap 対策)。</summary>
    private static readonly TimeSpan ProbeCooldown = TimeSpan.FromMinutes(5);

    /// <summary>
    /// PairedPeer.IsOnline が false → true に切り替わった瞬間に呼ばれる。
    /// 5 分クールダウン + 単一実行ガードを通過したら ProbeRouteAsync を 1 回だけ走らせる。
    /// </summary>
    private void OnPeerWentOnline(object? sender, EventArgs e)
    {
        if (sender is not PairedPeer peer) return;
        _ = Task.Run(() => ProbePeerRouteAsync(peer));
    }

    private async Task ProbePeerRouteAsync(PairedPeer peer)
    {
        var now = DateTimeOffset.UtcNow;

        // クールダウン**チェックのみ** (Online flap で連発するのを抑制)
        // v1.0.38 review fix v5: cooldown の記録はセマフォ取得後に移動。
        // 旧実装は WaitAsync(0) 失敗で skip された peer も cooldown に乗ってしまい、
        // 複数 peer 同時 Online 時に最初の 1 つを除いて永久 refresh されないバグがあった
        lock (_lastProbeAt)
        {
            if (_lastProbeAt.TryGetValue(peer.PeerId, out var last) && now - last < ProbeCooldown)
            {
                Util.Logger.Log($"経路 Probe スキップ (cooldown): peer={peer.PeerId}, last={last:O}");
                return;
            }
        }

        // 同時 Probe は 1 件まで (シグナリング pairId ノード競合防止)。
        // v1.0.38 review fix v10: 既に他で実行中の場合は cooldown を記録せずに待ちキューへ。
        // Release 時に dequeue して順次処理されるので、複数 peer 同時 Online でも全 peer が probe される
        // (旧実装は skip した peer を放置 → IsOnline edge が再 fire されないので永久 unknown だった)
        if (!await _probeSemaphore.WaitAsync(0))
        {
            lock (_probeQueueLock)
            {
                // 同一 peer の重複 enqueue を避ける
                if (!_probeQueue.Contains(peer))
                {
                    _probeQueue.Enqueue(peer);
                    Util.Logger.Log($"経路 Probe 待ちキューに追加 (他で実行中): peer={peer.PeerId}, queue={_probeQueue.Count}");
                }
            }
            return;
        }

        // セマフォ取得成功 → ここで初めて cooldown を記録 (実際に probe を走らせる peer のみ)
        lock (_lastProbeAt)
        {
            _lastProbeAt[peer.PeerId] = DateTimeOffset.UtcNow;
        }

        try
        {
            var route = await _connectionService.ProbeRouteAsync(peer.PeerId);
            // v1.0.47 修正版: probe Unknown の保護対象を「いま実際に接続中のピア」だけに絞る。
            // 転送中のメイン接続と probe が競合して Unknown を返すケースを守る必要があるが、それ以外で
            // probe が Unknown を返したのは経路が本当に取れていない状態（LAN 切替・相手 NIC ダウン等）
            // なので、stale な LAN/P2P/relay バッジを残すと誤誘導になる。online edge / 手動 refresh 由来の
            // 通常 probe では Unknown を素直に書き戻す。offline 化は !isOnline 分岐で別途処理される。
            Dispatcher.UIThread.Post(() =>
            {
                var isLiveConnectedPeer = _connectionService.State is PeerState.Connected or PeerState.Connecting
                                          && _connectionService.ConnectedPeer?.SessionId == peer.PeerId;
                if (route != ConnectionRoute.Unknown || !isLiveConnectedPeer)
                    peer.Route = route;
            });
            Util.Logger.Log($"経路 Probe 完了: peer={peer.PeerId}, route={route}");
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"経路 Probe エラー: peer={peer.PeerId}, {ex.Message}", Util.LogLevel.Warning);
        }
        finally
        {
            _probeSemaphore.Release();

            // v1.0.38 review fix v10: 待ちキューに peer があれば順次処理
            // (Release 直後に別の Online edge が走り込んでも、その peer は WaitAsync(0) で成功するか
            // queue に入るので最終的に必ず処理される)
            PairedPeer? next = null;
            lock (_probeQueueLock)
            {
                if (_probeQueue.Count > 0)
                    next = _probeQueue.Dequeue();
            }
            if (next != null)
            {
                Util.Logger.Log($"経路 Probe 待ちキューから取り出し: peer={next.PeerId}");
                _ = Task.Run(() => ProbePeerRouteAsync(next));
            }
        }
    }
}
