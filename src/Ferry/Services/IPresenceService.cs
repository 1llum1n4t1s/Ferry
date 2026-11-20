using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// プレゼンス（オンライン/オフライン検知）の抽象。
/// rere #B1-001: ConnectionViewModel が Infrastructure 層の実装（旧 FirebaseSignaling）を直接 new していた
/// レイヤー境界違反を解消するため、presence の heartbeat / poll をこのインターフェース越しに行う。
/// 実装は Infrastructure 層（CloudflarePresenceServiceFactory が生成する Cloudflare relay 版）が担い、VM は Services 抽象だけに依存する。
/// </summary>
public interface IPresenceService : IDisposable
{
    /// <summary>自分の presence（LastSeen + DisplayName）を更新する（heartbeat）。</summary>
    Task UpdatePresenceAsync(string deviceId, string displayName, CancellationToken ct = default);

    /// <summary>指定 deviceId の presence（DisplayName + LastSeen）をフル取得する。</summary>
    Task<PresenceData?> GetPresenceAsync(string deviceId, CancellationToken ct = default);

    /// <summary>指定 deviceId の LastSeen のみを ETag 条件付きで取得する（304 で帯域節約）。</summary>
    Task<long?> GetPresenceLastSeenAsync(string deviceId, CancellationToken ct = default);

    /// <summary>自分の presence ノードを削除する（終了時）。</summary>
    Task RemovePresenceAsync(string deviceId);

    /// <summary>presence ETag キャッシュから指定 deviceId を除去する（ペア削除時の stale 解消）。</summary>
    void ForgetPresence(string deviceId);
}
