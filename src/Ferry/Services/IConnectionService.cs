using System;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// 接続管理サービス。
/// ペアリング（QR スキャン → Bridge ページ経由のマッチング）と
/// オンデマンド接続（転送時の一時的な WebRTC 接続）を分離して管理する。
/// </summary>
public interface IConnectionService
{
    // === 状態 ===

    /// <summary>現在の接続状態。</summary>
    PeerState State { get; }

    /// <summary>接続中のピア情報。未接続時は null。</summary>
    PeerInfo? ConnectedPeer { get; }

    /// <summary>現在の接続経路（LAN 直接 / STUN P2P / TURN リレー）。</summary>
    ConnectionRoute Route { get; }

    // === イベント ===

    /// <summary>状態が変化したときに発火するイベント。</summary>
    event EventHandler<PeerState>? StateChanged;

    /// <summary>接続経路が確定したときに発火するイベント。</summary>
    event EventHandler<ConnectionRoute>? RouteChanged;

    /// <summary>ペアリングが完了したときに発火するイベント。</summary>
    event EventHandler<PairedPeer>? PairingCompleted;

    /// <summary>DataChannel でバイナリデータを受信したときに発火するイベント。</summary>
    event EventHandler<byte[]>? DataReceived;

    /// <summary>接続が切断されたときに発火するイベント（転送中の切断検知用）。</summary>
    event EventHandler? ConnectionLost;

    // === ペアリング（QR スキャン → Bridge ページ経由） ===

    /// <summary>
    /// ペアリングセッションを開始し、セッション ID を返す。
    /// QR コード URL 生成に使用する。
    /// Firebase でマッチング完了を監視し、完了時に PairingCompleted を発火する。
    /// </summary>
    Task<string> StartPairingSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// ペアリングセッションをキャンセルする。
    /// </summary>
    Task CancelPairingAsync(CancellationToken ct = default);

    // === オンデマンド接続（転送時） ===

    /// <summary>
    /// ペアリング済みピアに接続する。
    /// Firebase シグナリングで SDP/ICE 交換 → WebRTC 確立。
    /// </summary>
    Task ConnectToPeerAsync(string peerId, CancellationToken ct = default);

    /// <summary>
    /// DataChannel 経由でバイナリデータを送信する。
    /// </summary>
    Task SendAsync(byte[] data, CancellationToken ct = default);

    /// <summary>
    /// 接続を切断し、リソースを解放する。
    /// </summary>
    Task DisconnectAsync(CancellationToken ct = default);
}
