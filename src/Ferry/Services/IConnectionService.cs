using System;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// 接続管理サービス。
/// ペアリング（QR スキャン時の 1 回限りのハンドシェイク）と
/// オンデマンド接続（転送時の一時的な WebRTC 接続）を分離して管理する。
/// </summary>
public interface IConnectionService
{
    // === 状態 ===

    /// <summary>現在の接続状態。</summary>
    PeerState State { get; }

    /// <summary>接続中のピア情報。未接続時は null。</summary>
    PeerInfo? ConnectedPeer { get; }

    // === イベント ===

    /// <summary>状態が変化したときに発火するイベント。</summary>
    event EventHandler<PeerState>? StateChanged;

    /// <summary>ペアリングが完了したときに発火するイベント。</summary>
    event EventHandler<PairedPeer>? PairingCompleted;

    /// <summary>ペアリング要求を受信したときに発火するイベント（承認/拒否 UI 表示用）。</summary>
    event EventHandler<PeerInfo>? PairingReceived;

    /// <summary>DataChannel でバイナリデータを受信したときに発火するイベント。</summary>
    event EventHandler<byte[]>? DataReceived;

    /// <summary>接続が切断されたときに発火するイベント（転送中の切断検知用）。</summary>
    event EventHandler? ConnectionLost;

    // === ペアリング（初回 QR スキャン時のみ） ===

    /// <summary>
    /// ペアリングセッションを開始し、セッション ID を返す。
    /// QR コード URL 生成に使用する。
    /// ペアリング完了後、Firebase セッションは即座に切断される。
    /// </summary>
    Task<string> StartPairingSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// ペアリング要求を許可する。
    /// ペア情報は PeerRegistryService に自動保存される。
    /// </summary>
    Task AcceptPairingAsync(CancellationToken ct = default);

    /// <summary>
    /// ペアリング要求を拒否する。
    /// </summary>
    Task RejectPairingAsync(CancellationToken ct = default);

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
