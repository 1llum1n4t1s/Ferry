using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    [ObservableProperty]
    private PeerState _connectionState = PeerState.Disconnected;

    /// <summary>QR コード関連のステータステキスト（ペアリング中のみ表示）。</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private Bitmap? _qrCodeImage;

    [ObservableProperty]
    private string _sessionId = string.Empty;

    [ObservableProperty]
    private string _peerName = string.Empty;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private PairedPeer? _selectedPeer;

    /// <summary>ペアリング済みピアの一覧。</summary>
    public ObservableCollection<PairedPeer> PairedPeers { get; } = [];

    /// <summary>ペアリング済みピアが存在するか。QR/宛先リストの表示切替に使用。</summary>
    [ObservableProperty]
    private bool _hasPairedPeers;

    /// <summary>接続経路の表示テキスト。</summary>
    [ObservableProperty]
    private string _connectionRouteText = string.Empty;

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

        // 保存済みピアを読み込み
        foreach (var peer in _peerRegistry.GetPairedPeers())
        {
            PairedPeers.Add(peer);
        }
        UpdateHasPairedPeers();
    }

    /// <summary>
    /// ペアリングセッションを開始し、QR コードを表示する。
    /// </summary>
    [RelayCommand]
    private async Task StartSessionAsync()
    {
        IsConnecting = true;
        StatusText = "セッション開始中…";

        try
        {
            var settings = _settingsService.Settings;
            SessionId = await _connectionService.StartPairingSessionAsync();

            // Bridge ページ URL に sessionId と PC 名を付与して QR コード生成
            var displayName = Uri.EscapeDataString(settings.DisplayName);
            var bridgeUrl = $"{settings.BridgePageUrl}?sid={SessionId}&name={displayName}";
            QrCodeImage = _qrCodeService.GenerateQrBitmap(bridgeUrl);

            ConnectionState = PeerState.WaitingForPairing;
            StatusText = "QR コードをスマートフォンでスキャンしてください";
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"セッション開始エラー: {ex.Message}", Util.LogLevel.Error);
            ConnectionState = PeerState.Error;
            StatusText = $"エラー: {ex.Message}";
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
        ConnectionState = PeerState.Disconnected;
        StatusText = string.Empty;

        // 選択中ピアのステータスを更新し、着信監視を再開
        if (SelectedPeer != null)
        {
            SelectedPeer.ConnectionStatusText = "待機中";
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

    /// <summary>
    /// ピア選択時は宛先を記憶し、着信接続監視を開始する。
    /// 相手側がファイルを送ろうとした時に自動的に Answer を返せるようにする。
    /// </summary>
    partial void OnSelectedPeerChanged(PairedPeer? oldValue, PairedPeer? newValue)
    {
        // 前の選択ピアのステータスをクリア
        if (oldValue != null)
        {
            oldValue.ConnectionStatusText = string.Empty;
            oldValue.Route = ConnectionRoute.Unknown;
        }

        if (newValue != null)
        {
            PeerName = newValue.DisplayName;
            newValue.ConnectionStatusText = "待機中";
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
        if (peer == null) return;

        if (ConnectionState == PeerState.Connected && _connectionService.ConnectedPeer?.SessionId == peer.PeerId)
            return; // 既に接続済み

        // 前の接続を切断
        if (ConnectionState is PeerState.Connected or PeerState.Connecting)
            await _connectionService.DisconnectAsync();

        IsConnecting = true;
        peer.ConnectionStatusText = "接続中…";

        try
        {
            await _connectionService.ConnectToPeerAsync(peer.PeerId);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"接続エラー ({peer.DisplayName}): {ex.Message}", Util.LogLevel.Warning);
            peer.ConnectionStatusText = "オフライン";
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
                    PeerState.WaitingForPairing => "QR コードをスマートフォンでスキャンしてください",
                    PeerState.WaitingForMatch => "ペアリング先の PC の QR コードをスキャンしてください…",
                    _ => string.Empty,
                };
            }
            else
            {
                StatusText = string.Empty;
            }

            // 接続状態をピアのリスト項目に反映
            if (SelectedPeer != null)
            {
                SelectedPeer.ConnectionStatusText = state switch
                {
                    PeerState.Connected => "✅ 接続中",
                    PeerState.Connecting => "🔄 接続中…",
                    PeerState.Reconnecting => "🔄 再接続中…",
                    PeerState.Error => "❌ オフライン",
                    PeerState.Disconnected => "待機中",
                    _ => string.Empty,
                };
            }

            // 未接続時は経路表示をクリア
            if (state != PeerState.Connected)
            {
                ConnectionRouteText = string.Empty;
                if (SelectedPeer != null)
                    SelectedPeer.Route = ConnectionRoute.Unknown;
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
                ConnectionRoute.Direct => "🟢 LAN 直接接続",
                ConnectionRoute.StunAssisted => "🟡 インターネット P2P（STUN）",
                ConnectionRoute.Relay => "🔴 サーバー経由（TURN リレー）",
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
                SelectedPeer = peer;
            });
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"ペアリング完了処理エラー: {ex.Message}", Util.LogLevel.Error);
        }
    }

    private void UpdateHasPairedPeers() => HasPairedPeers = PairedPeers.Count > 0;

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
        ClearQrCodeImage();
    }
}
