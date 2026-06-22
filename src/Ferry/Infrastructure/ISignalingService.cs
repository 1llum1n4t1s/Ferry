using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Services;

namespace Ferry.Infrastructure;

/// <summary>
/// CF 単独完結移行 (dual-path): シグナリング / ペアリング / pairs SSoT の抽象。
/// <see cref="FirebaseSignaling"/>（Firebase RTDB 経路）と <see cref="CloudflareSignaling"/>（CF DO/D1 経路）の
/// 両実装が満たし、<see cref="Services.ConnectionService"/> はファクトリ越しにこの抽象だけに依存する。
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

    /// <summary>ペアリング監視（成立通知の購読）を開始する。</summary>
    void StartWatchingPairing();

    /// <summary>ペアリング監視を停止する。</summary>
    void StopWatching();

    // === SDP / ICE シグナリング（per-sender ノード） ===

    Task<string> WaitForOfferAsync(string pairId, string fromDeviceId, long minCreatedAt = 0, CancellationToken ct = default);
    Task<string> WaitForAnswerAsync(string pairId, string fromDeviceId, CancellationToken ct = default);
    Task SendSdpOfferAsync(string pairId, string senderDeviceId, string sdp, CancellationToken ct = default);
    Task<string?> TryReadOfferOnceAsync(string pairId, string fromDeviceId, CancellationToken ct = default);
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
