using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ferry.Models;
using Ferry.Services;

namespace Ferry.ViewModels;

/// <summary>
/// 接続パネルの ViewModel。
/// QR コード表示、ペアリング済みピア一覧、接続状態管理を提供する。
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

    /// <summary>ペアリング済みピアの一覧。</summary>
    public ObservableCollection<PairedPeer> PairedPeers { get; } = [];

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
        _connectionService.PairingReceived += OnPairingReceived;
        _connectionService.PairingCompleted += OnPairingCompleted;

        // 保存済みピアを読み込み
        foreach (var peer in _peerRegistry.GetPairedPeers())
        {
            PairedPeers.Add(peer);
        }
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

            // Bridge ページ URL に sessionId を付与して QR コード生成
            var bridgeUrl = $"{settings.BridgePageUrl}?sid={SessionId}";
            QrCodeImage = _qrCodeService.GenerateQrBitmap(bridgeUrl);

            ConnectionState = PeerState.WaitingForPairing;
            StatusText = "QR コードをスマートフォンでスキャンしてください";
        }
        catch (Exception ex)
        {
            ConnectionState = PeerState.Error;
            StatusText = $"エラー: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    /// <summary>
    /// ペアリング要求を承認する。
    /// </summary>
    [RelayCommand]
    private async Task AcceptPairingAsync()
    {
        try
        {
            await _connectionService.AcceptPairingAsync();
            StatusText = "ペアリング完了";
        }
        catch (Exception ex)
        {
            ConnectionState = PeerState.Error;
            StatusText = $"エラー: {ex.Message}";
        }
    }

    /// <summary>
    /// ペアリング要求を拒否する。
    /// </summary>
    [RelayCommand]
    private async Task RejectPairingAsync()
    {
        try
        {
            await _connectionService.RejectPairingAsync();
            ConnectionState = PeerState.WaitingForPairing;
            StatusText = "QR コードをスマートフォンでスキャンしてください";
        }
        catch (Exception ex)
        {
            ConnectionState = PeerState.Error;
            StatusText = $"エラー: {ex.Message}";
        }
    }

    /// <summary>
    /// ペアリングを解除する。
    /// </summary>
    [RelayCommand]
    private async Task RemovePeerAsync(string peerId)
    {
        await _peerRegistry.RemovePeerAsync(peerId);
        var peer = FindPairedPeer(peerId);
        if (peer != null) PairedPeers.Remove(peer);
    }

    /// <summary>
    /// 接続を切断する。
    /// </summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _connectionService.DisconnectAsync();
        QrCodeImage?.Dispose();
        QrCodeImage = null;
        PeerName = string.Empty;
        SessionId = string.Empty;
        ConnectionState = PeerState.Disconnected;
        StatusText = "未接続";
    }

    private void OnStateChanged(object? sender, PeerState state)
    {
        ConnectionState = state;
        StatusText = state switch
        {
            PeerState.Disconnected => "未接続",
            PeerState.WaitingForPairing => "QR コードをスマートフォンでスキャンしてください",
            PeerState.PairingRequested => $"「{PeerName}」からの接続要求",
            PeerState.Connecting => "接続中…",
            PeerState.Connected => $"「{PeerName}」と接続中",
            PeerState.Reconnecting => "再接続中…",
            PeerState.Error => "接続エラー",
            _ => "不明な状態",
        };
    }

    private void OnPairingReceived(object? sender, PeerInfo peer)
    {
        PeerName = peer.DisplayName;
        ConnectionState = PeerState.PairingRequested;
        StatusText = $"「{peer.DisplayName}」からの接続要求";
    }

    private async void OnPairingCompleted(object? sender, PairedPeer peer)
    {
        // ペアリング情報を永続化
        await _peerRegistry.AddOrUpdatePeerAsync(peer);

        if (FindPairedPeer(peer.PeerId) == null)
        {
            PairedPeers.Add(peer);
        }

        // QR コード表示をクリア
        QrCodeImage?.Dispose();
        QrCodeImage = null;
        SessionId = string.Empty;
        ConnectionState = PeerState.Disconnected;
        StatusText = $"「{peer.DisplayName}」とペアリング完了";
    }

    private PairedPeer? FindPairedPeer(string peerId)
    {
        foreach (var p in PairedPeers)
        {
            if (p.PeerId == peerId) return p;
        }
        return null;
    }

    public void Dispose()
    {
        _connectionService.StateChanged -= OnStateChanged;
        _connectionService.PairingReceived -= OnPairingReceived;
        _connectionService.PairingCompleted -= OnPairingCompleted;
        QrCodeImage?.Dispose();
    }
}
