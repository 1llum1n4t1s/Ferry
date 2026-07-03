using System;
using System.Buffers;
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
    /// メッセージを長さプレフィックス付きで書き込む（byte[] 版、後方互換用）。
    /// 内部で <see cref="WriteMessageAsync(Stream, ReadOnlyMemory{byte}, CancellationToken)"/> に委譲する。
    /// </summary>
    public static Task WriteMessageAsync(Stream stream, byte[] data, CancellationToken ct = default)
        => WriteMessageAsync(stream, data.AsMemory(), ct);

    /// <summary>
    /// メッセージを長さプレフィックス付きで書き込む（ReadOnlyMemory 版）。
    /// M-12: 毎送信で new byte[] していたフレームバッファを ArrayPool で再利用。
    /// P-1: 送信パスのホットループから渡された ArrayPool 借用バッファをそのまま受け取れるよう
    /// ReadOnlyMemory&lt;byte&gt; をプライマリ受け口にした。
    /// 1 GB 転送で約 1 GB の Gen0 alloc が削減される。
    /// </summary>
    public static async Task WriteMessageAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var frameSize = 4 + data.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0, 4), data.Length);
            data.Span.CopyTo(buffer.AsSpan(4));
            // ヘッダー + ペイロードを1バッファに結合して1回で送信
            // NoDelay 有効時、2回の WriteAsync は2つの TCP セグメントになるため統合
            await stream.WriteAsync(buffer.AsMemory(0, frameSize), ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 長さプレフィックス付きメッセージを読み取る。
    /// 接続が閉じられた場合は null を返す。
    /// </summary>
    public static async Task<byte[]?> ReadMessageAsync(Stream stream, CancellationToken ct = default)
    {
        // M-11: 自作 ReadExactAsync を .NET 7+ の Stream.ReadExactlyAsync に置換
        // ヘッダー（4byte 長さ）を読み取る。WriteMessageAsync 同様 ArrayPool で毎メッセージのヒープ確保を回避
        var header = ArrayPool<byte>.Shared.Rent(4);
        int length;
        try
        {
            try { await stream.ReadExactlyAsync(header.AsMemory(0, 4), ct); }
            catch (EndOfStreamException) { return null; } // 接続が閉じられた

            length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0, 4));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }

        if (length < 0 || length > MaxMessageSize)
            throw new InvalidDataException($"不正なメッセージサイズ: {length}");

        if (length == 0)
            return [];

        // ペイロードを読み取る
        var payload = new byte[length];
        try { await stream.ReadExactlyAsync(payload, ct); }
        catch (EndOfStreamException) { return null; } // 途中で切断

        return payload;
    }
}
