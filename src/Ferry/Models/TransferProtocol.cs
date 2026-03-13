namespace Ferry.Models;

/// <summary>
/// DataChannel メッセージの種別定数。
/// </summary>
public static class TransferProtocol
{
    /// <summary>ファイルメタデータ (JSON)。</summary>
    public const byte FileMeta = 0x01;

    /// <summary>ファイルチャンク [chunkIndex (4byte)] [data]。</summary>
    public const byte FileChunk = 0x02;

    /// <summary>ファイル受信完了確認 [status (1byte)] [sha256 (32byte)]。</summary>
    public const byte FileAck = 0x03;

    /// <summary>ファイル受信拒否 [reason (UTF-8)]。</summary>
    public const byte FileReject = 0x04;

    /// <summary>キープアライブ送信。</summary>
    public const byte Ping = 0x10;

    /// <summary>キープアライブ応答。</summary>
    public const byte Pong = 0x11;

    /// <summary>転送レジュームリクエスト [TransferId (16byte)] [LastChunkIndex (4byte)]。</summary>
    public const byte ResumeRequest = 0x20;

    /// <summary>転送レジューム応答 [TransferId (16byte)] [Status (1byte)] [LastChunkIndex (4byte)]。</summary>
    public const byte ResumeResponse = 0x21;

    /// <summary>チャンクサイズ (16KB)。</summary>
    public const int ChunkSize = 16_384;

    /// <summary>送信バッファ閾値 (64KB)。これを超えたら送信を一時停止する。</summary>
    public const int BufferedAmountThreshold = 65_536;
}
