using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ferry.Models;

namespace Ferry.Infrastructure;

/// <summary>
/// ファイルのチャンク分割・結合とプロトコルメッセージの生成・解析を行う。
/// </summary>
public static class FileChunker
{
    /// <summary>
    /// ファイルメタデータの JSON メッセージを生成する。
    /// </summary>
    public static byte[] CreateFileMetaMessage(string fileName, long fileSize, int totalChunks, string sha256, Guid transferId = default, string? relativePath = null)
    {
        var meta = new FileMeta
        {
            FileName = fileName,
            FileSize = fileSize,
            TotalChunks = totalChunks,
            Sha256 = sha256,
            TransferId = transferId == default ? Guid.NewGuid().ToString() : transferId.ToString(),
            RelativePath = relativePath,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(meta, FileMetaJsonContext.Default.FileMeta);
        var message = new byte[1 + json.Length];
        message[0] = TransferProtocol.FileMeta;
        json.CopyTo(message.AsSpan(1));
        return message;
    }

    /// <summary>
    /// ファイルチャンクメッセージを生成する。
    /// </summary>
    public static byte[] CreateChunkMessage(Guid transferId, int chunkIndex, ReadOnlySpan<byte> data)
    {
        // [種別 1byte] [TransferId 16byte] [chunkIndex 4byte] [data]
        var message = new byte[TransferProtocol.ChunkHeaderSize + data.Length];
        WriteChunkMessage(message, transferId, chunkIndex, data);
        return message;
    }

    /// <summary>
    /// ファイルチャンクメッセージを呼び出し側が用意したバッファ（ArrayPool 等）に直接書き込む。
    /// P-1: 1GB 転送で約 1GB の Gen0 alloc を削減するため、ホットループでは
    /// CreateChunkMessage の代わりに本メソッド + ArrayPool を使う。
    /// </summary>
    /// <param name="destination">書き込み先バッファ。長さは <c>ChunkHeaderSize + data.Length</c> 以上必要。</param>
    /// <param name="transferId">転送 ID。</param>
    /// <param name="chunkIndex">チャンクインデックス。</param>
    /// <param name="data">チャンクデータ本体。</param>
    public static void WriteChunkMessage(Span<byte> destination, Guid transferId, int chunkIndex, ReadOnlySpan<byte> data)
    {
        var messageSize = TransferProtocol.ChunkHeaderSize + data.Length;
        if (destination.Length < messageSize)
            throw new ArgumentException($"バッファサイズが不足: 必要={messageSize}, 実際={destination.Length}", nameof(destination));

        // [種別 1byte] [TransferId 16byte] [chunkIndex 4byte] [data]
        destination[0] = TransferProtocol.FileChunk;
        transferId.TryWriteBytes(destination.Slice(1, 16));
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(17, 4), chunkIndex);
        data.CopyTo(destination[TransferProtocol.ChunkHeaderSize..]);
    }

    /// <summary>
    /// ファイル ACK メッセージを生成する。
    /// </summary>
    public static byte[] CreateAckMessage(bool success, byte[] sha256Hash)
    {
        // [種別 1byte] [status 1byte] [sha256 32byte]
        var message = new byte[1 + 1 + 32];
        message[0] = TransferProtocol.FileAck;
        message[1] = success ? (byte)1 : (byte)0;
        sha256Hash.AsSpan(0, Math.Min(32, sha256Hash.Length)).CopyTo(message.AsSpan(2));
        return message;
    }

    /// <summary>
    /// ファイル拒否メッセージを生成する [種別 1byte] [TransferId 16byte] [reason UTF-8]。
    /// v1.0.38 で TransferId を追加 (同時複数転送のうちどれの拒否か区別するため)。
    /// </summary>
    public static byte[] CreateRejectMessage(Guid transferId, string reason)
    {
        var reasonBytes = Encoding.UTF8.GetBytes(reason);
        var message = new byte[1 + 16 + reasonBytes.Length];
        message[0] = TransferProtocol.FileReject;
        transferId.TryWriteBytes(message.AsSpan(1, 16));
        reasonBytes.CopyTo(message.AsSpan(17));
        return message;
    }

    /// <summary>FileReject メッセージから TransferId と理由を抽出する。</summary>
    public static (Guid TransferId, string Reason)? ParseReject(ReadOnlySpan<byte> message)
    {
        if (message.Length < 1 + 16) return null;
        var transferId = new Guid(message.Slice(1, 16));
        var reason = message.Length > 17
            ? Encoding.UTF8.GetString(message.Slice(17))
            : string.Empty;
        return (transferId, reason);
    }

    /// <summary>
    /// ファイル承認メッセージを生成する [種別 1byte] [TransferId 16byte]。v1.0.38 追加。
    /// 受信側が ApproveTransfer 時に送信側へ送る。これを受け取った送信側がチャンク送信を開始する。
    /// </summary>
    public static byte[] CreateApproveMessage(Guid transferId)
    {
        var message = new byte[1 + 16];
        message[0] = TransferProtocol.FileApprove;
        transferId.TryWriteBytes(message.AsSpan(1, 16));
        return message;
    }

    /// <summary>FileApprove メッセージから TransferId を抽出する。</summary>
    public static Guid? ParseApprove(ReadOnlySpan<byte> message)
    {
        if (message.Length < 1 + 16) return null;
        return new Guid(message.Slice(1, 16));
    }

    /// <summary>
    /// フロー制御 ACK メッセージを生成する [種別 1byte] [TransferId 16byte] [receivedChunkCount 4byte]。v1.0.46 追加。
    /// 受信側が一定チャンクごとに「書き込み済みチャンク数」を送信側へ返し、送信側のウィンドウ制御に使う。
    /// </summary>
    public static byte[] CreateFlowAckMessage(Guid transferId, int receivedChunkCount)
    {
        var message = new byte[TransferProtocol.FlowAckSize];
        message[0] = TransferProtocol.FileFlowAck;
        transferId.TryWriteBytes(message.AsSpan(1, 16));
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(17, 4), receivedChunkCount);
        return message;
    }

    /// <summary>FlowAck メッセージから TransferId と受信済みチャンク数を抽出する。</summary>
    public static (Guid TransferId, int ReceivedChunkCount)? ParseFlowAck(ReadOnlySpan<byte> message)
    {
        if (message.Length < TransferProtocol.FlowAckSize) return null;
        var transferId = new Guid(message.Slice(1, 16));
        var count = BinaryPrimitives.ReadInt32BigEndian(message.Slice(17, 4));
        return (transferId, count);
    }

    /// <summary>
    /// Ping メッセージを生成する。
    /// </summary>
    public static byte[] CreatePingMessage() => [TransferProtocol.Ping];

    /// <summary>
    /// Pong メッセージを生成する。
    /// </summary>
    public static byte[] CreatePongMessage() => [TransferProtocol.Pong];

    /// <summary>
    /// ファイルを読み込み、チャンクの列挙を返す。
    /// </summary>
    public static IEnumerable<(int Index, byte[] Data)> ReadChunks(string filePath)
    {
        // FileShare.ReadWrite: 他プロセスが書込み中のファイル（自分の現行ログ等）も読み取って送信できるようにする。
        // File.OpenRead (FileShare.Read) だと書込みハンドル保持中のファイルで IOException になる。
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[TransferProtocol.ChunkSize];
        var index = 0;
        int bytesRead;

        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            // M-4: Buffer.BlockCopy → Span.CopyTo（コードベース一貫性）
            var chunk = new byte[bytesRead];
            buffer.AsSpan(0, bytesRead).CopyTo(chunk);
            yield return (index, chunk);
            index++;
        }
    }

    /// <summary>
    /// ファイルを読み込みつつ SHA-256 を並行計算するチャンク列挙。
    /// P-3: 送信パスで「ハッシュ計算 + チャンク送信」を 1 度のディスク読みで完結させる。
    /// 列挙完了後に <paramref name="hashSink"/>.GetHashAndReset() で最終ハッシュが取れる。
    /// </summary>
    public static IEnumerable<(int Index, byte[] Data)> ReadChunksWithHash(string filePath, IncrementalHash hashSink)
    {
        // FileShare.ReadWrite: 他プロセスが書込み中のファイル（自分の現行ログ等）も読み取って送信できるようにする。
        // File.OpenRead (FileShare.Read) だと書込みハンドル保持中のファイルで IOException になる。
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[TransferProtocol.ChunkSize];
        var index = 0;
        int bytesRead;

        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            // ハッシュは元バッファ上で計算（コピー前のサイズ分だけ）
            hashSink.AppendData(buffer, 0, bytesRead);

            var chunk = new byte[bytesRead];
            buffer.AsSpan(0, bytesRead).CopyTo(chunk);
            yield return (index, chunk);
            index++;
        }
    }

    /// <summary>
    /// ファイル SHA-256 後送りメッセージを生成する [種別 1byte] [TransferId 16byte] [sha256 32byte]。P-3 プロトコル拡張。
    /// rere #B1-001: 並列転送 (ParallelTransferCount>1) で別ファイルへハッシュが誤紐付けされないよう
    /// FileChunk/FileReject と同様に TransferId プレフィクスを持つ (旧形式 [種別][sha256] とは非互換)。
    /// </summary>
    public static byte[] CreateFileHashMessage(Guid transferId, byte[] sha256)
    {
        // 呼び出し元の誤用 (SHA-256 以外のハッシュ長) を明確な例外で検出する
        if (sha256.Length != 32)
            throw new ArgumentException($"SHA-256 は 32 バイト必要: 実際={sha256.Length}", nameof(sha256));
        var message = new byte[1 + 16 + 32];
        message[0] = TransferProtocol.FileHash;
        transferId.TryWriteBytes(message.AsSpan(1, 16));
        sha256.AsSpan(0, 32).CopyTo(message.AsSpan(17));
        return message;
    }

    /// <summary>FileHash メッセージから TransferId と SHA-256 バイト列を抽出する。</summary>
    public static (Guid TransferId, byte[] Sha256)? ParseFileHash(ReadOnlySpan<byte> message)
    {
        if (message.Length < 1 + 16 + 32) return null;
        var transferId = new Guid(message.Slice(1, 16));
        return (transferId, message.Slice(17, 32).ToArray());
    }

    /// <summary>旧形式 (v1.0.50 以前、TransferId なし [種別][sha256 32byte]) の FileHash から
    /// SHA-256 を抽出する。新旧混在期間 (自動更新は両端同時でない) の受信側フォールバック用。</summary>
    public static byte[]? ParseLegacyFileHash(ReadOnlySpan<byte> message)
    {
        if (message.Length < 1 + 32) return null;
        return message.Slice(1, 32).ToArray();
    }

    /// <summary>
    /// ファイルの SHA-256 ハッシュを計算する。
    /// </summary>
    public static byte[] ComputeSha256(string filePath)
    {
        // FileShare.ReadWrite: 他プロセスが書込み中のファイル（自分の現行ログ等）も読み取って送信できるようにする。
        // File.OpenRead (FileShare.Read) だと書込みハンドル保持中のファイルで IOException になる。
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return SHA256.HashData(stream);
    }

    /// <summary>
    /// ファイルの SHA-256 ハッシュを 16 進文字列で返す。
    /// </summary>
    public static string ComputeSha256Hex(string filePath)
    {
        var hash = ComputeSha256(filePath);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// チャンク総数を計算する。
    /// </summary>
    public static int CalculateTotalChunks(long fileSize)
    {
        return (int)((fileSize + TransferProtocol.ChunkSize - 1) / TransferProtocol.ChunkSize);
    }

    /// <summary>
    /// レジュームリクエストメッセージを生成する。
    /// </summary>
    public static byte[] CreateResumeRequestMessage(Guid transferId, int lastChunkIndex)
    {
        // [種別 1byte] [TransferId 16byte] [LastChunkIndex 4byte]
        var message = new byte[1 + 16 + 4];
        message[0] = TransferProtocol.ResumeRequest;
        // M-2: ToByteArray() のヒープ確保を回避（CreateChunkMessage と同パターン）
        transferId.TryWriteBytes(message.AsSpan(1, 16));
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(17, 4), lastChunkIndex);
        return message;
    }

    /// <summary>
    /// レジューム応答メッセージを生成する。
    /// </summary>
    /// <param name="transferId">転送 ID。</param>
    /// <param name="accepted">レジューム受諾。</param>
    /// <param name="lastChunkIndex">相手側で確認済みの最終チャンクインデックス。</param>
    public static byte[] CreateResumeResponseMessage(Guid transferId, bool accepted, int lastChunkIndex)
    {
        // [種別 1byte] [TransferId 16byte] [Status 1byte] [LastChunkIndex 4byte]
        var message = new byte[1 + 16 + 1 + 4];
        message[0] = TransferProtocol.ResumeResponse;
        // M-2: ToByteArray() のヒープ確保を回避（CreateChunkMessage と同パターン）
        transferId.TryWriteBytes(message.AsSpan(1, 16));
        message[17] = accepted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(18, 4), lastChunkIndex);
        return message;
    }

    /// <summary>
    /// レジュームリクエストメッセージを解析する。
    /// </summary>
    public static (Guid TransferId, int LastChunkIndex) ParseResumeRequest(ReadOnlySpan<byte> message)
    {
        // message[0] = 種別（呼び出し元で検証済み）
        var transferId = new Guid(message.Slice(1, 16));
        var lastChunkIndex = BinaryPrimitives.ReadInt32BigEndian(message.Slice(17, 4));
        return (transferId, lastChunkIndex);
    }

    /// <summary>
    /// レジューム応答メッセージを解析する。
    /// </summary>
    public static (Guid TransferId, bool Accepted, int LastChunkIndex) ParseResumeResponse(ReadOnlySpan<byte> message)
    {
        var transferId = new Guid(message.Slice(1, 16));
        var accepted = message[17] == 1;
        var lastChunkIndex = BinaryPrimitives.ReadInt32BigEndian(message.Slice(18, 4));
        return (transferId, accepted, lastChunkIndex);
    }

    /// <summary>
    /// rere #D-005: レジューム応答 V2。受信済みチャンクを packed bitmap で返す（順不同・穴あき受信に対応）。
    /// 旧 <see cref="CreateResumeResponseMessage"/> は「連続最終 index」しか返せず、UDP 順不同で穴あき受信する
    /// Ferry の実態（HandleFileChunk が chunkIndex×ChunkSize へ Seek 書き込み）に合わなかった。
    /// 形式: [種別(1)][TransferId(16)][accepted(1)][totalChunks(4 BigEndian)][packed bitmap((totalChunks+7)/8)]。
    /// bit i（byte i/8 の i%8 ビット, LSB 先頭）= チャンク i 受信済み。種別バイトは旧 V1 と同じ 0x21 だが、
    /// V2 は呼び出し側が明示的に使い分ける（混在しない）。
    /// </summary>
    public static byte[] CreateResumeResponseMessageV2(Guid transferId, bool accepted, int totalChunks, ReadOnlySpan<bool> receivedSet)
    {
        if (totalChunks < 0) totalChunks = 0;
        var bitmapLen = (totalChunks + 7) / 8;
        var message = new byte[1 + 16 + 1 + 4 + bitmapLen];
        message[0] = TransferProtocol.ResumeResponse;
        transferId.TryWriteBytes(message.AsSpan(1, 16));
        message[17] = accepted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(18, 4), totalChunks);

        var bitmap = message.AsSpan(22, bitmapLen);
        var n = Math.Min(totalChunks, receivedSet.Length);
        for (var i = 0; i < n; i++)
        {
            if (receivedSet[i])
                bitmap[i >> 3] |= (byte)(1 << (i & 7));
        }
        return message;
    }

    /// <summary>
    /// rere #D-005: レジューム応答 V2 を解析する。形式不正（22 byte 未満・totalChunks 負・bitmap 長不足）は null を返す。
    /// </summary>
    public static (Guid TransferId, bool Accepted, int TotalChunks, bool[] ReceivedSet)? ParseResumeResponseV2(ReadOnlySpan<byte> message)
    {
        if (message.Length < 22) return null;
        var transferId = new Guid(message.Slice(1, 16));
        var accepted = message[17] == 1;
        var totalChunks = BinaryPrimitives.ReadInt32BigEndian(message.Slice(18, 4));
        if (totalChunks < 0) return null;

        // 整数オーバーフロー / 過大メモリ確保の防止 (crypto/protocol レビュー #D-005, PR#8 #F10):
        // 細工された totalChunks≈int.MaxValue は (totalChunks+7)/8 を int 加算オーバーフローさせて
        // 負の bitmapLen を作り、下の長さガードをすり抜けて new bool[巨大] / 負 Slice で例外死させ得る
        // (「形式不正は null」契約違反 → 配線後はペア済み peer の 1 通で受信ループを落とす DoS)。
        // bitmap 必要バイト数を long で算出し payload と比較する。(totalChunks + 7) を int 域で評価しない
        // ことでオーバーフローを構造的に排除し、int キャストは payload 内に収まると検証できた後に限定する。
        var payloadBytes = message.Length - 22;
        var requiredBitmapBytes = ((long)totalChunks + 7L) / 8L;
        if (requiredBitmapBytes > payloadBytes) return null;

        var bitmapLen = (int)requiredBitmapBytes;

        var bitmap = message.Slice(22, bitmapLen);
        var received = new bool[totalChunks];
        for (var i = 0; i < totalChunks; i++)
            received[i] = (bitmap[i >> 3] & (1 << (i & 7))) != 0;
        return (transferId, accepted, totalChunks, received);
    }

    /// <summary>
    /// rere #D-005: 受信側の received ビットマップから、送信側 SendChunksAsync 用の skipSet を作る純関数。
    /// skipSet[i]==true は「チャンク i は受信済みなので送らない（スキップ）」を表す。
    /// 防御的に <paramref name="totalChunks"/> 長へ正規化する（received が短ければ不足分は未受信=送る、
    /// 長ければ切り詰める）。これで bitmap 長と totalChunks の不整合があっても index 範囲外を起こさない。
    /// </summary>
    public static bool[] BuildResumeSkipSet(ReadOnlySpan<bool> received, int totalChunks)
    {
        var skip = new bool[Math.Max(totalChunks, 0)];
        var n = Math.Min(received.Length, skip.Length);
        for (var i = 0; i < n; i++)
            skip[i] = received[i];
        return skip;
    }

    /// <summary>
    /// ファイルメタデータメッセージを解析する。
    /// </summary>
    public static FileMeta? ParseFileMeta(ReadOnlySpan<byte> message)
    {
        if (message.Length < 2) return null;
        return JsonSerializer.Deserialize(message[1..], FileMetaJsonContext.Default.FileMeta);
    }

    /// <summary>
    /// 受信メッセージの種別を取得する。
    /// </summary>
    public static byte GetMessageType(ReadOnlySpan<byte> message)
    {
        return message.Length > 0 ? message[0] : (byte)0;
    }
}
