using System;

namespace Ferry.Models;

/// <summary>
/// ペアリング済みピアの永続化情報。
/// QR ペアリング完了後にローカルに保存し、PC 再起動後も再接続可能にする。
/// </summary>
public sealed class PairedPeer
{
    /// <summary>相手の一意識別子。</summary>
    public required string PeerId { get; init; }

    /// <summary>表示名（デバイス名）。</summary>
    public required string DisplayName { get; set; }

    /// <summary>ペアリング日時 (UTC)。</summary>
    public DateTime PairedAt { get; init; } = DateTime.UtcNow;

    /// <summary>最終転送日時 (UTC)。</summary>
    public DateTime? LastTransferAt { get; set; }
}
