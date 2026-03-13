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
    public static byte[] CreateFileMetaMessage(string fileName, long fileSize, int totalChunks, string sha256, Guid transferId = default)
    {
        var meta = new FileMeta
        {
            FileName = fileName,
            FileSize = fileSize,
            TotalChunks = totalChunks,
            Sha256 = sha256,
            TransferId = transferId == default ? Guid.NewGuid().ToString() : transferId.ToString(),
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
    public static byte[] CreateChunkMessage(int chunkIndex, ReadOnlySpan<byte> data)
    {
        // [種別 1byte] [chunkIndex 4byte] [data]
        var message = new byte[1 + 4 + data.Length];
        message[0] = TransferProtocol.FileChunk;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(1, 4), chunkIndex);
        data.CopyTo(message.AsSpan(5));
        return message;
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
    /// ファイル拒否メッセージを生成する。
    /// </summary>
    public static byte[] CreateRejectMessage(string reason)
    {
        var reasonBytes = Encoding.UTF8.GetBytes(reason);
        var message = new byte[1 + reasonBytes.Length];
        message[0] = TransferProtocol.FileReject;
        reasonBytes.CopyTo(message.AsSpan(1));
        return message;
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
            var chunk = new byte[bytesRead];
            Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
            yield return (index, chunk);
            index++;
        }
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
        transferId.ToByteArray().CopyTo(message.AsSpan(1, 16));
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
        transferId.ToByteArray().CopyTo(message.AsSpan(1, 16));
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
