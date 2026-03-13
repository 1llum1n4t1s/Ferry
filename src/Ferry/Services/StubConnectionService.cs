using System;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// 接続サービスのスタブ実装。Firebase/WebRTC が未実装の間のプレースホルダー。
/// </summary>
#pragma warning disable CS0067 // スタブ実装のため未使用イベントを許容
public sealed class StubConnectionService : IConnectionService
{
    public PeerState State { get; private set; } = PeerState.Disconnected;
    public PeerInfo? ConnectedPeer { get; private set; }

    public event EventHandler<PeerState>? StateChanged;
    public event EventHandler<PairedPeer>? PairingCompleted;
    public event EventHandler<PeerInfo>? PairingReceived;
    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler? ConnectionLost;

    public Task<string> StartPairingSessionAsync(CancellationToken ct = default)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        State = PeerState.WaitingForPairing;
        StateChanged?.Invoke(this, State);
        return Task.FromResult(sessionId);
    }

    public Task AcceptPairingAsync(CancellationToken ct = default)
    {
        var peer = new PairedPeer
        {
            PeerId = Guid.NewGuid().ToString("N")[..8],
            DisplayName = "スタブデバイス",
        };
        PairingCompleted?.Invoke(this, peer);
        State = PeerState.Disconnected;
        StateChanged?.Invoke(this, State);
        return Task.CompletedTask;
    }

    public Task RejectPairingAsync(CancellationToken ct = default)
    {
        State = PeerState.WaitingForPairing;
        StateChanged?.Invoke(this, State);
        return Task.CompletedTask;
    }

    public Task ConnectToPeerAsync(string peerId, CancellationToken ct = default)
    {
        State = PeerState.Connected;
        ConnectedPeer = new PeerInfo { SessionId = peerId, DisplayName = "スタブデバイス", State = PeerState.Connected };
        StateChanged?.Invoke(this, State);
        return Task.CompletedTask;
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default) => Task.CompletedTask;

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        ConnectedPeer = null;
        State = PeerState.Disconnected;
        StateChanged?.Invoke(this, State);
        return Task.CompletedTask;
    }
}
