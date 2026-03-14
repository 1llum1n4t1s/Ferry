using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
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

    [ObservableProperty]
    private string _statusText = "未接続";

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
            SelectedPeer = null;
    }

    /// <summary>
    /// 接続を切断し、ペアリングセッションもキャンセルする。
    /// </summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _connectionService.DisconnectAsync();
        var oldImage = QrCodeImage;
        QrCodeImage = null;
        oldImage?.Dispose();
        PeerName = string.Empty;
        SessionId = string.Empty;
        ConnectionState = PeerState.Disconnected;
        StatusText = HasPairedPeers ? "宛先を選択してください" : "未接続";
    }

    /// <summary>
    /// 新しいピアを追加するためにペアリング画面に切り替える。
    /// </summary>
    [RelayCommand]
    private async Task AddNewPeerAsync()
    {
        await StartSessionAsync();
    }

    private void OnStateChanged(object? sender, PeerState state)
    {
        ConnectionState = state;
        StatusText = state switch
        {
            PeerState.Disconnected => HasPairedPeers ? "宛先を選択してください" : "未接続",
            PeerState.WaitingForPairing => "QR コードをスマートフォンでスキャンしてください",
            PeerState.WaitingForMatch => "ペアリング先の PC の QR コードをスキャンしてください…",
            PeerState.Connecting => "接続中…",
            PeerState.Connected => $"「{PeerName}」と接続中",
            PeerState.Reconnecting => "再接続中…",
            PeerState.Error => "接続エラー",
            _ => "不明な状態",
        };

        // 未接続時は経路表示をクリア
        if (state != PeerState.Connected)
        {
            ConnectionRouteText = string.Empty;
            if (SelectedPeer != null)
                SelectedPeer.Route = ConnectionRoute.Unknown;
        }
    }

    private void OnRouteChanged(object? sender, ConnectionRoute route)
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
    }

    private async void OnPairingCompleted(object? sender, PairedPeer peer)
    {
        // ペアリング情報を永続化
        await _peerRegistry.AddOrUpdatePeerAsync(peer);

        if (PairedPeers.All(p => p.PeerId != peer.PeerId))
        {
            PairedPeers.Add(peer);
        }
        UpdateHasPairedPeers();

        // QR コード表示をクリアし、宛先選択モードへ（null を先にセットし UI バインディング解除後に Dispose）
        var oldImage = QrCodeImage;
        QrCodeImage = null;
        oldImage?.Dispose();
        SessionId = string.Empty;
        SelectedPeer = peer;
        ConnectionState = PeerState.Disconnected;
        StatusText = $"「{peer.DisplayName}」とペアリング完了";
    }

    private void UpdateHasPairedPeers() => HasPairedPeers = PairedPeers.Count > 0;

    public void Dispose()
    {
        _connectionService.StateChanged -= OnStateChanged;
        _connectionService.RouteChanged -= OnRouteChanged;
        _connectionService.PairingCompleted -= OnPairingCompleted;
        var oldImage = QrCodeImage;
        QrCodeImage = null;
        oldImage?.Dispose();
    }
}
