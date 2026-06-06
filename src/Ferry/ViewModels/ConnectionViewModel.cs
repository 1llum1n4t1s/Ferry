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

    // プレゼンス監視（オンライン/オフライン検知）
    private FirebaseSignaling? _presenceSignaling;
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
        IPeerRegistryService peerRegistry)
    {
        _connectionService = connectionService;
        _qrCodeService = qrCodeService;
        _settingsService = settingsService;
        _peerRegistry = peerRegistry;

        _connectionService.StateChanged += OnStateChanged;
        _connectionService.RouteChanged += OnRouteChanged;
        _connectionService.PairingCompleted += OnPairingCompleted;
        _connectionService.StatusMessageChanged += OnStatusMessageChanged;

        // 保存済みピアを読み込み + WentOnline 購読 (Online エッジで経路 Probe 発火用)
        foreach (var peer in _peerRegistry.GetPairedPeers())
        {
            peer.WentOnline += OnPeerWentOnline;
            PairedPeers.Add(peer);
        }
        UpdateHasPairedPeers();

        // プレゼンス監視を開始（heartbeat 送信 + ピアのオンライン状態ポーリング）
        StartPresenceMonitoring();
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

            // Bridge ページ URL に sessionId と PC 名を付与して QR コード生成
            var displayName = Uri.EscapeDataString(settings.DisplayName);
            var bridgeUrl = $"{settings.BridgePageUrl}?sid={SessionId}&name={displayName}";
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
        var peer = PairedPeers.FirstOrDefault(p => p.PeerId == peerId);
        await _peerRegistry.RemovePeerAsync(peerId);
        if (peer != null)
        {
            peer.WentOnline -= OnPeerWentOnline;
            PairedPeers.Remove(peer);
        }
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

    // === プレゼンス監視 ===

    private void StartPresenceMonitoring()
    {
        var dbUrl = _settingsService.Settings.FirebaseDatabaseUrl;
        if (string.IsNullOrEmpty(dbUrl)) return;

        _presenceCts?.Cancel();
        _presenceCts?.Dispose();
        _presenceCts = new CancellationTokenSource();

        _presenceSignaling?.Dispose();
        _presenceSignaling = new FirebaseSignaling(dbUrl);

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
    /// ④⑤ 取得は LastSeen のみの ETag 条件付き GET（<see cref="FirebaseSignaling.GetPresenceLastSeenAsync"/>）。
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

    public void Dispose()
    {
        _connectionService.StopListeningForConnection();
        _connectionService.StateChanged -= OnStateChanged;
        _connectionService.RouteChanged -= OnRouteChanged;
        _connectionService.PairingCompleted -= OnPairingCompleted;
        _connectionService.StatusMessageChanged -= OnStatusMessageChanged;
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
    private readonly object _probeQueueLock = new();
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
