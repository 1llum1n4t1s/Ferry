using System;
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
    private const int PollIntervalMs = 10_000;       // 10秒ごとにピアの状態をポーリング
    private const long OfflineThresholdMs = 60_000;  // 60秒更新なしでオフライン判定

    [ObservableProperty]
    public partial PeerState ConnectionState { get; set; } = PeerState.Disconnected;

    /// <summary>QR コード関連のステータステキスト（ペアリング中のみ表示）。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Bitmap? QrCodeImage { get; set; }

    [ObservableProperty]
    public partial string SessionId { get; set; } = string.Empty;

    /// <summary>ペアリング用 URL（QR コード下に表示してコピー共有可能にする）。</summary>
    [ObservableProperty]
    public partial string PairingUrl { get; set; } = string.Empty;

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

    /// <summary>「相手の URL を貼り付け」入力欄のテキスト (AddMemberWindow)。</summary>
    [ObservableProperty]
    public partial string PairFromUrlText { get; set; } = string.Empty;

    /// <summary>URL ペアリングの結果メッセージ (成功/エラー)。</summary>
    [ObservableProperty]
    public partial string PairFromUrlStatus { get; set; } = string.Empty;

    /// <summary>URL ペアリング結果メッセージの色 (success/error で切替)。</summary>
    [ObservableProperty]
    public partial Avalonia.Media.IBrush? PairFromUrlStatusBrush { get; set; }

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

        // 保存済みピアを読み込み
        foreach (var peer in _peerRegistry.GetPairedPeers())
        {
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
        if (peer != null) PairedPeers.Remove(peer);
        UpdateHasPairedPeers();

        if (SelectedPeer?.PeerId == peerId)
        {
            SelectedPeer = null;
            await _connectionService.DisconnectAsync();
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

    /// <summary>ペアリングリンクのクリップボード書き込み要求イベント (View 側で TopLevel.Clipboard 経由処理)。</summary>
    public event EventHandler<string>? CopyPairingLinkRequested;

    /// <summary>
    /// ペアリングリンクのコピーを要求する。実際のクリップボード操作は View 側で行う (N-5: MVVM 厳密化)。
    /// View 側はコピー成功後に <see cref="NotifyPairingLinkCopied"/> を呼んで UI を更新する。
    /// </summary>
    [RelayCommand]
    private void CopyPairingLink()
    {
        if (string.IsNullOrEmpty(PairingUrl)) return;
        CopyPairingLinkRequested?.Invoke(this, PairingUrl);
    }

    /// <summary>View 側のクリップボード書き込み成功後に呼び出され、「コピー済み」表示を 2 秒間表示する。</summary>
    public void NotifyPairingLinkCopied()
    {
        IsLinkCopied = true;
        Util.Logger.Log("ペアリングリンクをクリップボードにコピー");
        DispatcherTimer.RunOnce(() => IsLinkCopied = false, TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// 「相手の URL を貼り付け」入力欄から URL を取得し、アプリ内でペアリングを実行する。
    /// Bridge ページを介さない直接ペアリング (カメラ無し PC 同士向け)。
    /// </summary>
    [RelayCommand]
    private async Task PairFromUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(PairFromUrlText)) return;

        PairFromUrlStatus = "処理中…";
        PairFromUrlStatusBrush = Avalonia.Application.Current is { } app
            && app.TryGetResource("TextSecondaryBrush", app.ActualThemeVariant, out var pendingBrush)
            && pendingBrush is Avalonia.Media.IBrush pb ? pb : null;

        try
        {
            var (success, message) = await _connectionService.PairFromUrlAsync(PairFromUrlText.Trim());
            PairFromUrlStatus = message;

            // 結果に応じて文字色を切り替え (success=Green, error=Red)
            var brushKey = success ? "GreenBrush" : "RedBrush";
            if (Avalonia.Application.Current is { } a
                && a.TryGetResource(brushKey, a.ActualThemeVariant, out var b)
                && b is Avalonia.Media.IBrush ib)
            {
                PairFromUrlStatusBrush = ib;
            }

            if (success)
            {
                // 成功時は入力欄をクリア (ペアリング検知は StartWatchingPairing で反映)
                PairFromUrlText = string.Empty;
            }
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"URL ペアリング失敗: {ex.Message}", Util.LogLevel.Warning);
            PairFromUrlStatus = $"エラー: {ex.Message}";
        }
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
        else
        {
            _connectionService.StopListeningForConnection();
        }
    }

    /// <summary>
    /// 選択されたピアにオンデマンド接続する（ファイル転送開始時に呼ばれる）。
    /// </summary>
    public async Task ConnectToSelectedPeerAsync()
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
            await _connectionService.ConnectToPeerAsync(peer.PeerId);
        }
        catch (Exception ex)
        {
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
            // ペアリング情報を永続化
            await _peerRegistry.AddOrUpdatePeerAsync(peer);

            // UI スレッドで ObservableCollection・ObservableProperty を更新
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (PairedPeers.All(p => p.PeerId != peer.PeerId))
                {
                    PairedPeers.Add(peer);
                }
                UpdateHasPairedPeers();

                // QR コード表示をクリアし、宛先選択モードへ
                ClearQrCodeImage();
                SessionId = string.Empty;
                PairingUrl = string.Empty;
                IsLinkCopied = false;
                SelectedPeer = peer;
            });
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
    /// 定期的にペアリング済みピアの lastSeen をチェックし、IsOnline を更新する。
    /// </summary>
    private async Task PresencePollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var peers = PairedPeers.ToArray();

            // Dispose レース対策: ループ内でローカル束縛
            var sig = _presenceSignaling;
            if (sig is null) break;

            // 全ピアのプレゼンスを並列取得（順次 → 並列で N 倍高速化）
            var tasks = peers.Select(async peer =>
            {
                try
                {
                    var presenceData = await sig.GetPresenceAsync(peer.PeerId, ct);
                    var isOnline = presenceData != null && (now - presenceData.LastSeen) < OfflineThresholdMs;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (peer.IsOnline != isOnline)
                            peer.IsOnline = isOnline;

                        // 相手の表示名が変わっていたら同期
                        if (presenceData != null &&
                            !string.IsNullOrEmpty(presenceData.DisplayName) &&
                            presenceData.DisplayName != peer.DisplayName)
                        {
                            peer.DisplayName = presenceData.DisplayName;
                            _ = _peerRegistry.AddOrUpdatePeerAsync(peer);
                        }
                    });
                }
                catch (OperationCanceledException) { /* キャンセルは WhenAll 後に判定 */ }
                catch
                {
                    // 個別ピアのエラーは無視して次へ
                }
            });

            await Task.WhenAll(tasks);
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
}
