using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// ペアリング済みピアの管理サービス。
/// ローカルに永続化し、PC 再起動後もペア情報を保持する。
/// </summary>
public interface IPeerRegistryService
{
    /// <summary>ペアリング済みピアの一覧を取得する。</summary>
    IReadOnlyList<PairedPeer> GetPairedPeers();

    /// <summary>ペアを追加（または既存のペアを更新）する。</summary>
    Task AddOrUpdatePeerAsync(PairedPeer peer);

    /// <summary>
    /// Codex 第15弾 #2 (P2) fix: 既存のときだけ更新し、 存在しなければ何もしない (insert しない)。
    /// 戻り値は更新が行われたか (= 対象 peer が存在したか)。
    /// PairSyncService の SSoT 観測フラグ更新のように「FindPeer != null を確認 → AddOrUpdate」の
    /// 2 段操作の隙間で手動 unpair が走ると、AddOrUpdate の insert 分岐が古い snapshot を
    /// 再追加して削除済み peer を resurrect する race があった。 この API は _peersLock 内で
    /// 「存在確認 → 更新」を 1 アトミックにまとめて insert 分岐自体を持たないことで race を構造的に閉じる。
    /// </summary>
    Task<bool> UpdatePeerIfPresentAsync(PairedPeer peer);

    /// <summary>ペアを削除する。</summary>
    Task RemovePeerAsync(string peerId);

    /// <summary>指定した ID のペアを検索する。</summary>
    PairedPeer? FindPeer(string peerId);

    /// <summary>
    /// Codex P2 fix: ペアが削除されたタイミングを通知する。PairSyncService が
    /// remote unpair 検知で peerRegistry から消したときに発火し、ConnectionViewModel が
    /// PairedPeers を更新する。引数は削除された peerId。
    /// </summary>
    event EventHandler<string>? PeerRemoved;

    /// <summary>
    /// Codex 第12弾 #3 (P2) fix: ユーザーが手動で unpair した直後の race を防ぐための removal-intent marker。
    /// <see cref="RemovePeerAsync"/> 前に立てて、Firebase DELETE / 後段の registry 削除が完了するまでの間、
    /// 他経路 (例: <see cref="Ferry.Services.ConnectionService.WritePairRecordWithFallback"/> の責任者書込み /
    /// 30 秒 fallback) が「<see cref="FindPeer"/> != null」だけを根拠に PUT pairs/{pairId} を再発行して
    /// 削除済みペアを resurrect するのを防ぐ。
    /// 立てた呼び出し側は finally 等で必ず <see cref="ClearPendingRemoval"/> を呼んで掃除する。
    /// </summary>
    void MarkPendingRemoval(string peerId);

    /// <summary><see cref="MarkPendingRemoval"/> を取り消す。 finally から呼ぶ前提。</summary>
    void ClearPendingRemoval(string peerId);

    /// <summary>指定 peer に対するユーザー起点の unpair が in-flight かどうか。 writer 側は true なら abort する。</summary>
    bool IsPendingRemoval(string peerId);
}
