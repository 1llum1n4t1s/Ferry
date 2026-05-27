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
        using var stream = File.OpenRead(filePath);
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
        using var stream = File.OpenRead(filePath);
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
    /// ファイル SHA-256 後送りメッセージを生成する [種別 1byte] [sha256 32byte]。P-3 プロトコル拡張。
    /// </summary>
    public static byte[] CreateFileHashMessage(byte[] sha256)
    {
        var message = new byte[1 + 32];
        message[0] = TransferProtocol.FileHash;
        sha256.AsSpan(0, 32).CopyTo(message.AsSpan(1));
        return message;
    }

    /// <summary>FileHash メッセージから SHA-256 バイト列を抽出する。</summary>
    public static byte[]? ParseFileHash(ReadOnlySpan<byte> message)
    {
        if (message.Length < 1 + 32) return null;
        return message.Slice(1, 32).ToArray();
    }

    /// <summary>
    /// ファイルの SHA-256 ハッシュを計算する。
    /// </summary>
    public static byte[] ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
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
