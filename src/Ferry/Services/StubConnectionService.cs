using System;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// 接続サービスのスタブ実装。Firebase/WebRTC が未実装の間のプレースホルダー。
/// StartPairingSession 後、3 秒後に自動でペアリング完了をシミュレートする。
/// </summary>
#pragma warning disable CS0067 // スタブ実装のため未使用イベントを許容
public sealed class StubConnectionService : IConnectionService
{
    private CancellationTokenSource? _pairingCts;

    public PeerState State { get; private set; } = PeerState.Disconnected;
    public PeerInfo? ConnectedPeer { get; private set; }
    public ConnectionRoute Route { get; private set; } = ConnectionRoute.Unknown;

    public event EventHandler<PeerState>? StateChanged;
    public event EventHandler<ConnectionRoute>? RouteChanged;
    public event EventHandler<PairedPeer>? PairingCompleted;
    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler? ConnectionLost;

    public Task<string> StartPairingSessionAsync(CancellationToken ct = default)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        State = PeerState.WaitingForPairing;
        StateChanged?.Invoke(this, State);

        // Bridge ページ経由のマッチングをシミュレート
        _pairingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = SimulateMatchAsync(sessionId, _pairingCts.Token);

        return Task.FromResult(sessionId);
    }

    public Task CancelPairingAsync(CancellationToken ct = default)
    {
        _pairingCts?.Cancel();
        _pairingCts?.Dispose();
        _pairingCts = null;
        State = PeerState.Disconnected;
        StateChanged?.Invoke(this, State);
        return Task.CompletedTask;
    }

    public Task ConnectToPeerAsync(string peerId, CancellationToken ct = default)
    {
        State = PeerState.Connected;
        ConnectedPeer = new PeerInfo { SessionId = peerId, DisplayName = "スタブデバイス", State = PeerState.Connected };
        StateChanged?.Invoke(this, State);

        // LAN 直接接続をシミュレート
        Route = ConnectionRoute.Direct;
        RouteChanged?.Invoke(this, Route);
        return Task.CompletedTask;
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default) => Task.CompletedTask;

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _pairingCts?.Cancel();
        _pairingCts?.Dispose();
        _pairingCts = null;
        ConnectedPeer = null;
        State = PeerState.Disconnected;
        Route = ConnectionRoute.Unknown;
        StateChanged?.Invoke(this, State);
        return Task.CompletedTask;
    }

    /// <summary>
    /// マッチングをシミュレート。3 秒後に自動ペアリング完了。
    /// </summary>
    private async Task SimulateMatchAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            // 1台目スキャン完了をシミュレート
            await Task.Delay(1500, ct);
            State = PeerState.WaitingForMatch;
            StateChanged?.Invoke(this, State);

            // 2台目スキャン完了 → ペアリング完了をシミュレート
            await Task.Delay(1500, ct);
            var peer = new PairedPeer
            {
                PeerId = Guid.NewGuid().ToString("N")[..8],
                DisplayName = "スタブPC",
            };
            PairingCompleted?.Invoke(this, peer);
            State = PeerState.Disconnected;
            StateChanged?.Invoke(this, State);
        }
        catch (OperationCanceledException)
        {
            // キャンセルされた場合は何もしない
        }
    }
}
