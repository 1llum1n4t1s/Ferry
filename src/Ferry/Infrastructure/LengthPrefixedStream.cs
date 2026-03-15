using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ferry.Infrastructure;

/// <summary>
/// TCP ストリーム上でメッセージ境界を実現するフレーミングヘルパー。
/// フォーマット: [4byte BigEndian 長さ] [payload]
/// WebRTC DataChannel はメッセージ指向だったが、TCP はストリームなのでフレーミングが必須。
/// </summary>
public static class LengthPrefixedStream
{
    /// <summary>メッセージ最大サイズ (16MB)。不正データによるメモリ枯渇を防ぐ。</summary>
    private const int MaxMessageSize = 16 * 1024 * 1024;

    /// <summary>
    /// メッセージを長さプレフィックス付きで書き込む。
    /// </summary>
    public static async Task WriteMessageAsync(Stream stream, byte[] data, CancellationToken ct = default)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);

        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// 長さプレフィックス付きメッセージを読み取る。
    /// 接続が閉じられた場合は null を返す。
    /// </summary>
    public static async Task<byte[]?> ReadMessageAsync(Stream stream, CancellationToken ct = default)
    {
        // ヘッダー（4byte 長さ）を読み取る
        var header = new byte[4];
        var headerRead = await ReadExactAsync(stream, header, ct);
        if (!headerRead)
            return null; // 接続が閉じられた

        var length = BinaryPrimitives.ReadInt32BigEndian(header);

        if (length < 0 || length > MaxMessageSize)
            throw new InvalidDataException($"不正なメッセージサイズ: {length}");

        if (length == 0)
            return [];

        // ペイロードを読み取る
        var payload = new byte[length];
        var payloadRead = await ReadExactAsync(stream, payload, ct);
        if (!payloadRead)
            return null; // 途中で切断

        return payload;
    }

    /// <summary>
    /// 指定バイト数を確実に読み取る。途中で切断された場合は false を返す。
    /// </summary>
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
                return false; // 接続が閉じられた
            offset += read;
        }
        return true;
    }
}
