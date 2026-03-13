using System;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;
using Ferry.Services;

namespace Ferry.Infrastructure;

/// <summary>
/// オンデマンド接続マネージャー。
/// 転送開始時に自動接続し、アイドル時に自動切断する。
/// 転送中の切断時は指数バックオフで再接続を試行する。
/// </summary>
public sealed class OnDemandConnectionManager : IDisposable
{
    private readonly IConnectionService _connectionService;
    private readonly IPeerRegistryService _peerRegistry;
    private Timer? _idleTimer;
    private string? _currentPeerId;
    private bool _isTransferring;

    /// <summary>アイドル切断までの秒数。</summary>
    public int IdleTimeoutSeconds { get; set; } = 30;

    /// <summary>最大再接続試行回数。</summary>
    public int MaxReconnectAttempts { get; set; } = 5;

    /// <summary>再接続に成功したときに発火するイベント。</summary>
    public event EventHandler? Reconnected;

    /// <summary>再接続が全て失敗したときに発火するイベント。</summary>
    public event EventHandler? ReconnectFailed;

    public OnDemandConnectionManager(
        IConnectionService connectionService,
        IPeerRegistryService peerRegistry)
    {
        _connectionService = connectionService;
        _peerRegistry = peerRegistry;

        _connectionService.ConnectionLost += OnConnectionLost;
    }

    /// <summary>
    /// 転送のためにピアへ接続する。既に接続済みならスキップ。
    /// </summary>
    public async Task EnsureConnectedAsync(string peerId, CancellationToken ct = default)
    {
        StopIdleTimer();
        _currentPeerId = peerId;

        if (_connectionService.State == PeerState.Connected &&
            _connectionService.ConnectedPeer?.SessionId == peerId)
        {
            return;
        }

        if (_connectionService.State == PeerState.Connected)
        {
            await _connectionService.DisconnectAsync(ct);
        }

        await _connectionService.ConnectToPeerAsync(peerId, ct);
    }

    /// <summary>
    /// 転送開始をマークする（アイドルタイマーを停止）。
    /// </summary>
    public void NotifyTransferStarted()
    {
        _isTransferring = true;
        StopIdleTimer();
    }

    /// <summary>
    /// 転送完了をマークする（アイドルタイマーを開始）。
    /// </summary>
    public void NotifyTransferCompleted()
    {
        _isTransferring = false;
        StartIdleTimer();
    }

    private void StartIdleTimer()
    {
        StopIdleTimer();
        _idleTimer = new Timer(OnIdleTimeout, null,
            TimeSpan.FromSeconds(IdleTimeoutSeconds),
            Timeout.InfiniteTimeSpan);
    }

    private void StopIdleTimer()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
    }

    private async void OnIdleTimeout(object? state)
    {
        if (_isTransferring) return;

        try
        {
            Util.Logger.Log("アイドルタイムアウト: 自動切断");
            await _connectionService.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"アイドル切断エラー: {ex.Message}", Util.LogLevel.Error);
        }
    }

    private async void OnConnectionLost(object? sender, EventArgs e)
    {
        if (!_isTransferring || _currentPeerId == null) return;

        Util.Logger.Log("転送中に接続が切断されました。再接続を試行します。", Util.LogLevel.Warning);

        for (var attempt = 0; attempt < MaxReconnectAttempts; attempt++)
        {
            // 指数バックオフ: 1s, 2s, 4s, 8s, 16s
            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
            Util.Logger.Log($"再接続試行 {attempt + 1}/{MaxReconnectAttempts} ({delay.TotalSeconds}秒後)");

            await Task.Delay(delay);

            try
            {
                await _connectionService.ConnectToPeerAsync(_currentPeerId);
                if (_connectionService.State == PeerState.Connected)
                {
                    Util.Logger.Log("再接続成功");
                    Reconnected?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"再接続試行 {attempt + 1} 失敗: {ex.Message}", Util.LogLevel.Warning);
            }
        }

        Util.Logger.Log("再接続失敗: 最大試行回数に達しました", Util.LogLevel.Error);
        _isTransferring = false;
        ReconnectFailed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _connectionService.ConnectionLost -= OnConnectionLost;
        StopIdleTimer();
    }
}
