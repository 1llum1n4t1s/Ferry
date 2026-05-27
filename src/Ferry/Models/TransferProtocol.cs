namespace Ferry.Models;

/// <summary>
/// DataChannel メッセージの種別定数。
/// </summary>
public static class TransferProtocol
{
    /// <summary>ファイルメタデータ (JSON)。</summary>
    public const byte FileMeta = 0x01;

    /// <summary>ファイルチャンク [TransferId (16byte)] [chunkIndex (4byte)] [data]。</summary>
    public const byte FileChunk = 0x02;

    /// <summary>ファイル受信完了確認 [status (1byte)] [sha256 (32byte)]。</summary>
    public const byte FileAck = 0x03;

    /// <summary>ファイル受信拒否 [TransferId (16byte)] [reason (UTF-8)]。
    /// v1.0.38 で TransferId プレフィックスを追加 (同時複数転送の承認待ちを区別するため)。</summary>
    public const byte FileReject = 0x04;

    /// <summary>ファイル SHA-256 ハッシュ後送り [sha256 (32byte)]。
    /// P-3: 旧プロトコルは FileMeta に事前計算済みハッシュを入れていたが、
    /// 送信側でファイルを 2 度読み (ハッシュ計算 + 送信) する必要があった。
    /// 新プロトコルではチャンク送信中に IncrementalHash で並行計算し、最後にこのメッセージで送る。</summary>
    public const byte FileHash = 0x05;

    /// <summary>受信側がファイル受信を承認したことを送信側に通知する [TransferId (16byte)]。
    /// v1.0.38 で追加。送信側はこのメッセージを受信するまでチャンクを送らないので、
    /// 承認前の大量チャンク到着 → バッファ上限超過破棄 → ファイル破損というバグを根本解決する。
    /// AutoAcceptFileTransfer=true の場合、受信側は FileMeta 受信直後に自動で送信する。</summary>
    public const byte FileApprove = 0x06;

    /// <summary>キープアライブ送信。</summary>
    public const byte Ping = 0x10;

    /// <summary>キープアライブ応答。</summary>
    public const byte Pong = 0x11;

    /// <summary>転送レジュームリクエスト [TransferId (16byte)] [LastChunkIndex (4byte)]。</summary>
    public const byte ResumeRequest = 0x20;

    /// <summary>転送レジューム応答 [TransferId (16byte)] [Status (1byte)] [LastChunkIndex (4byte)]。</summary>
    public const byte ResumeResponse = 0x21;

    /// <summary>チャンクサイズ (64KB)。
    /// P-15: 旧 16KB から 4 倍化。1GB 転送のチャンク数を 65,536 → 16,384 に削減し、
    /// プロトコルヘッダー overhead を 1.4MB → 344KB へ圧縮。UDP transport は内部で
    /// MTU 1187 bytes にフラグメント化されるため、上位プロトコルから見て影響なし。</summary>
    public const int ChunkSize = 65_536;

    /// <summary>チャンクメッセージのヘッダ長 = 種別(1) + TransferId(16) + chunkIndex(4)。</summary>
    public const int ChunkHeaderSize = 1 + 16 + 4;

    /// <summary>送信バッファ閾値 (256KB)。これを超えたら送信を一時停止する。
    /// ChunkSize 4 倍化に合わせて閾値も 4 倍に拡大。</summary>
    public const int BufferedAmountThreshold = 262_144;
}
