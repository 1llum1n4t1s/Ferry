using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Services;

namespace Ferry.Infrastructure;

/// <summary>
/// シグナリング / ペアリング / pairs SSoT の抽象。
/// <see cref="CloudflareSignaling"/>（CF DO/D1 経路）が実装し、
/// <see cref="Services.ConnectionService"/> はファクトリ越しにこの抽象だけに依存する。
/// presence は <see cref="IPresenceService"/> を継承（同じインスタンスが presence も担う）。
///
/// レイヤ逆転回避のため Ferry.Infrastructure に置く（PairingInfo / PairRecord を参照するため）。
/// </summary>
public interface ISignalingService : IPresenceService
{
    // === ペアリング ===

    /// <summary>直前の <see cref="RegisterSessionAsync"/> で生成した PairingNonce（QR 埋め込み用）。</summary>
    string LastPairingNonce { get; }

    /// <summary>ペア相手が見つかったときに発火するイベント。</summary>
    event EventHandler<PairingInfo>? PairingDetected;

    /// <summary>セッションを登録し、ペアリング監視を開始できる状態にする。戻り値は sessionId(=deviceId)。</summary>
    Task<string> RegisterSessionAsync(string deviceId, string displayName, string publicKey = "", CancellationToken ct = default);

    /// <summary>アプリ内 URL 交換ペアリング（Bridge 非経由）。両 PC が <see cref="StartWatchingPairing"/> で検知する。</summary>
    Task SubmitPairingAsync(string sidA, string nameA, string sidB, string nameB, string pkA = "", string pkB = "", CancellationToken ct = default);

    /// <summary>指定 sessionId の存在と表示名 / 公開鍵を取得する（URL ペアリング前の事前チェック）。</summary>
    Task<(bool Exists, string? DisplayName, string? PublicKey)> CheckSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>ペアリング監視（成立通知の購読）を開始する。inbox WebSocket の購読開始でもあり、
    /// <see cref="ConnectKnockReceived"/> の受信にも同じ購読を使う。</summary>
    void StartWatchingPairing();

    /// <summary>ペアリング監視を停止する。</summary>
    void StopWatching();

    /// <summary>接続ノック（ペア相手が offer / probe-offer を書いた合図。relay Worker が inbox WS へ push する）。
    /// 引数は pairId。listener はこれを主検知経路にして安全網ポーリングを低頻度化する（CF 使用量削減）。</summary>
    event EventHandler<string>? ConnectKnockReceived;

    /// <summary>inbox WebSocket が現在接続中か（ノック即時性の目安）。切断中は listener 側が
    /// ポーリング間隔を詰めてノック欠落を補う。</summary>
    bool InboxConnected { get; }

    // === SDP / ICE シグナリング（per-sender ノード） ===

    Task<string> WaitForAnswerAsync(string pairId, string fromDeviceId, CancellationToken ct = default);
    Task SendSdpOfferAsync(string pairId, string senderDeviceId, string sdp, CancellationToken ct = default);
    Task<string?> TryReadOfferOnceAsync(string pairId, string fromDeviceId, long minCreatedAt = 0, CancellationToken ct = default);
    Task<long?> TryReadOfferCreatedAtAsync(string pairId, string fromDeviceId, CancellationToken ct = default);
    Task SendSdpAnswerAsync(string pairId, string answererDeviceId, string sdp, CancellationToken ct = default);
    Task SendEndpointAsync(string pairId, string senderDeviceId, string endpoint, CancellationToken ct = default);
    Task<string> WaitForEndpointAsync(string pairId, string fromDeviceId, CancellationToken ct = default);

    // === 経路 Probe（per-nonce ノード） ===

    Task SendProbeOfferAsync(string pairId, string nonce, string sdp, CancellationToken ct = default);
    Task SendProbeAnswerAsync(string pairId, string nonce, string sdp, CancellationToken ct = default);
    Task<string> WaitForProbeAnswerAsync(string pairId, string nonce, CancellationToken ct = default);
    Task<IReadOnlyList<(string Nonce, string Sdp)>> ReadProbeOffersAsync(string pairId, CancellationToken ct = default);
    Task CleanupProbeAsync(string pairId, string nonce, CancellationToken ct = default);

    // === クリーンアップ ===

    Task CleanupSignalingDataAsync(string pairId, CancellationToken ct = default);
    Task RevokePairingTokensAsync(string sid, CancellationToken ct = default);
    Task CleanupAsync(string? pairingId = null, CancellationToken ct = default);

    // === pairs/{pairId} SSoT ===

    Task PutPairAsync(string pairId, PairRecord record, CancellationToken ct = default);
    Task<PairRecord?> GetPairAsync(string pairId, CancellationToken ct = default);
    Task<(HttpStatusCode Status, string Body)> GetPairWithStatusAsync(string pairId, CancellationToken ct = default);
    Task DeletePairAsync(string pairId, CancellationToken ct = default);
}
