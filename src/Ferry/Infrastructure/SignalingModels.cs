namespace Ferry.Infrastructure;

// CF 単独完結移行 Step 6: signaling 経路で共有する DTO を FirebaseSignaling.cs から分離。
// PairingInfo / PairRecord は CloudflareSignaling・ISignalingService・ConnectionService・PairSyncService が
// 参照する経路非依存の型。Firebase 撤去後も CF（CloudflareSignaling）が同じ型を使う。

/// <summary>ペアリング検知情報（signaling 実装が PairingDetected で通知する）。</summary>
public sealed class PairingInfo
{
    public string PairingId { get; set; } = string.Empty;
    public string PeerId { get; set; } = string.Empty;
    public string PeerDisplayName { get; set; } = string.Empty;
    public bool IsInitiator { get; set; }

    /// <summary>rere #D-001(b): ペア相手の長期公開鍵(base64url SPKI)。空なら PairSecret 未確立(平文)。</summary>
    public string PeerPublicKey { get; set; } = string.Empty;

    // Codex 第11弾 #3 で持っていた CreatedAt は global timestamp gate (Codex 第12弾 #4) の撤去に伴い不要化。
    // 再起動跨ぎ replay 防御は per-pairingId 永続化 (AppSettings.SeenPairingIds) に集約。
}

/// <summary>
/// rere #D-001(a) Phase B: pairs/{pairId} ノードの SSoT データ（永続・cleanup 対象外）。
/// ペア成立時に責任者 PC が書き込み、両 PC が <see cref="ISignalingService.GetPairAsync"/> で存在チェック、
/// 削除時に <see cref="ISignalingService.DeletePairAsync"/> で消す。1ヶ月オフラインから戻った PC も
/// この存在/不在で「相手が削除したか」を判定する（PairSyncService）。CF 経路では D1 が SSoT を担う。
/// </summary>
public sealed class PairRecord
{
    public string PairId { get; set; } = string.Empty;
    public string NameA { get; set; } = string.Empty;
    public string NameB { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
}
