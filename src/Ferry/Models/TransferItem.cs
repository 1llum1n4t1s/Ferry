using System;

namespace Ferry.Models;

/// <summary>
/// ファイル転送アイテム。転送履歴や進行中の転送を表す。
/// </summary>
public sealed class TransferItem
{
    /// <summary>転送セッションの一意識別子（レジューム時の照合に使用）。</summary>
    public Guid TransferId { get; set; } = Guid.NewGuid();

    /// <summary>ファイル名。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>ファイルサイズ (バイト)。</summary>
    public long FileSize { get; set; }

    /// <summary>転送済みバイト数。</summary>
    public long TransferredBytes { get; set; }

    /// <summary>最後に確認済みのチャンクインデックス。</summary>
    public int LastConfirmedChunkIndex { get; set; } = -1;

    /// <summary>チャンク総数。</summary>
    public int TotalChunks { get; set; }

    /// <summary>転送方向。</summary>
    public TransferDirection Direction { get; set; }

    /// <summary>転送状態。</summary>
    public TransferState State { get; set; } = TransferState.Pending;

    /// <summary>エラーメッセージ（State が Error の場合）。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>SHA-256 ハッシュ値（転送完了後の検証用）。</summary>
    public string? Sha256Hash { get; set; }

    /// <summary>送信元ファイルパス（送信側で保持、レジューム時に使用）。</summary>
    public string? SourceFilePath { get; set; }

    /// <summary>進捗率 (0.0〜1.0)。</summary>
    public double Progress => FileSize > 0 ? (double)TransferredBytes / FileSize : 0;
}

/// <summary>転送方向。</summary>
public enum TransferDirection
{
    /// <summary>送信。</summary>
    Send,

    /// <summary>受信。</summary>
    Receive,
}

/// <summary>転送状態。</summary>
public enum TransferState
{
    /// <summary>待機中。</summary>
    Pending,

    /// <summary>転送中。</summary>
    InProgress,

    /// <summary>完了。</summary>
    Completed,

    /// <summary>エラー。</summary>
    Error,

    /// <summary>キャンセル。</summary>
    Cancelled,

    /// <summary>一時停止（接続断による中断、レジューム可能）。</summary>
    Suspended,
}
