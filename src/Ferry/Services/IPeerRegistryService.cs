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

    /// <summary>ペアを削除する。</summary>
    Task RemovePeerAsync(string peerId);

    /// <summary>指定した ID のペアを検索する。</summary>
    PairedPeer? FindPeer(string peerId);
}
