namespace Ferry.Models;

/// <summary>
/// 宛先リストの各セクション内ソート基準。settings.json に永続化する（<see cref="AppSettings.PeerListSortMode"/>）。
/// オンライン/オフラインの区切りはセクション分割側が担うため、ここはセクション内の並び順のみを表す。
/// </summary>
public enum PeerSortMode
{
    /// <summary>表示名の昇順（既定）。</summary>
    Name = 0,

    /// <summary>最終転送日時の新しい順。</summary>
    LastTransfer = 1,

    /// <summary>接続経路順（LAN → P2P → リレー → 未確定）。</summary>
    Route = 2,

    /// <summary>進行中転送がある相手を優先。</summary>
    Transferring = 3,
}
