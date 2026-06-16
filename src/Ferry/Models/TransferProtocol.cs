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

    /// <summary>受信側 → 送信側のフロー制御 ACK [TransferId (16byte)] [receivedChunkCount (4byte, BigEndian)]。
    /// v1.0.46 で追加。リレー経路 (WebSocket) には TCP のような end-to-end バックプレッシャーが無く、
    /// 送信側が受信側のドレイン速度を超えてチャンクを流し込むと Cloudflare 中継バッファが膨張して
    /// 接続が切断される (大容量ファイルが転送開始 ~55秒で死ぬ現象)。受信側が一定チャンクごとに
    /// 書き込み済みチャンク数をこの ACK で返し、送信側は <see cref="FlowControlWindowChunks"/> を超えて
    /// 先行しないよう待機することで中継バッファを一定量に抑える。</summary>
    public const byte FileFlowAck = 0x07;

    /// <summary>キープアライブ送信。</summary>
    public const byte Ping = 0x10;

    /// <summary>キープアライブ応答。</summary>
    public const byte Pong = 0x11;

    /// <summary>転送レジュームリクエスト [TransferId (16byte)] [LastChunkIndex (4byte)]。</summary>
    public const byte ResumeRequest = 0x20;

    /// <summary>転送レジューム応答。V1: [TransferId(16)][Status(1)][LastChunkIndex(4)]。
    /// rere #D-005 V2: [TransferId(16)][Status(1)][totalChunks(4 BigEndian)][packed bitmap((totalChunks+7)/8)]
    /// で受信済みチャンクを bitmap 返却（順不同・穴あき対応、FileChunker.CreateResumeResponseMessageV2）。種別は共通 0x21。
    /// ⚠️ 現状 TransferService.HandleResumeResponse は **V1 ParseResumeResponse のまま**。V2 は未配線（段階2 で結線予定）。</summary>
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

    /// <summary>FlowAck メッセージ長 = 種別(1) + TransferId(16) + receivedChunkCount(4)。</summary>
    public const int FlowAckSize = 1 + 16 + 4;

    /// <summary>フロー制御ウィンドウ (チャンク数)。送信側は受信側が書き込み済みのチャンクから
    /// 最大この数だけ先行できる。512 chunks × 64KB = 32MB。
    /// Cloudflare Durable Object は 1 インスタンス 128MB メモリ割当で、その送信 WebSocket は
    /// backpressure 未実装 (workerd#988) のため受信側ドレイン未了分を DO メモリ内にキューする。
    /// リレー DO に積まれる未ドレイン分 ≒ 送信先行 32MB + wire/受信 OS バッファ数MB ≈ 最悪 ~40MB で、
    /// 128MB 上限に対し約 3〜4 倍マージン。これが v1.0.46 以前の「~55秒で DO がメモリ超過リセット → 切断」を解消した実証値。
    /// 縮める (例 64=4MB) と高 RTT 経路で帯域遅延積を満たせずスループットが落ちるため非推奨。</summary>
    public const int FlowControlWindowChunks = 512;

    /// <summary>受信側が FlowAck を送る間隔 (チャンク数)。64 chunks = 4MB ごと。
    /// ウィンドウ (512) の 1/8 刻みで送ることで送信側のウィンドウ待機を滑らかに解消する。</summary>
    public const int FlowAckIntervalChunks = 64;

    /// <summary>受信を受け付ける FileSize の絶対上限 (1TB)。
    /// rere #A2-001: <see cref="Infrastructure.FileChunker.CalculateTotalChunks"/> の int キャストは
    /// FileSize ≈ 137TB 超で桁溢れし、悪意ある FileMeta の負/折り返し TotalChunks が
    /// 「再計算値との一致検証」を素通りして new bool[負値] の未処理例外を起こす。
    /// 一致検証より前にこの上限で弾くことで桁溢れ域に到達させない (1TB = TotalChunks 約 1,600 万)。</summary>
    public const long MaxFileSizeBytes = 1L << 40;
}
