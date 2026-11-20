using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Ferry.Infrastructure;
using Ferry.Models;

namespace Ferry.Tests.Infrastructure;

/// <summary>
/// FileChunker のうち、送受信の結合テストや型・定数では検出できない wire 互換・安全性の契約を固定する。
/// （チャンク/メタ/ACK の通常系の往復は TransferService の送受信テストが通すので、ここには置かない）
/// </summary>
public class FileChunkerTests
{
    [Fact]
    public void 結果ACKと承認拡張は旧prefixを保ち試行tokenが往復すること()
    {
        var id = Guid.NewGuid();
        var token = Guid.NewGuid();
        var hash = SHA256.HashData([1, 2, 3]);
        var ack = FileChunker.CreateResultAckMessage(true, hash, id, token);
        Assert.Equal(FileChunker.CreateAckMessage(true, hash), ack[..34]);
        var parsed = FileChunker.ParseResultAck(ack)!.Value;
        Assert.Equal(id, parsed.TransferId);
        Assert.Equal(token, parsed.ResultToken);
        Assert.Equal(hash, parsed.Hash);
        var approval = FileChunker.CreateApproveMessage(id, token);
        Assert.Equal(FileChunker.CreateApproveMessage(id), approval[..17]);
        Assert.Equal(id, FileChunker.ParseApprove(approval));
        Assert.Equal(token, FileChunker.ParseApproveResultToken(approval));
        var meta = FileChunker.ParseFileMeta(FileChunker.CreateFileMetaMessage("one", 1, 1, "", id, "folder/one", token, id))!;
        Assert.Equal(token, meta.ResultToken);
        Assert.Equal(id, meta.FolderBatchId);
    }

    [Theory]
    [InlineData(34)]
    [InlineData(65)]
    [InlineData(67)]
    public void 結果ACKは拡張形式の正確な長さだけを受理する(int length)
        => Assert.Null(FileChunker.ParseResultAck(new byte[length]));

    // v1.0.38: CreateRejectMessage は [種別 1] [TransferId 16] [reason UTF-8] の形式（旧版との wire 互換）
    [Fact]
    public void CreateRejectMessage_理由文字列がUTF8でエンコードされること()
    {
        var reason = "ディスク容量不足です";
        var transferId = Guid.NewGuid();
        var msg = FileChunker.CreateRejectMessage(transferId, reason);
        // v1.0.38: 種別(1) + TransferId(16) のあとに reason
        var decoded = Encoding.UTF8.GetString(msg.AsSpan(17));
        Assert.Equal(reason, decoded);
        // TransferId も正しくシリアライズされていること
        var parsed = FileChunker.ParseReject(msg);
        Assert.NotNull(parsed);
        Assert.Equal(transferId, parsed.Value.TransferId);
        Assert.Equal(reason, parsed.Value.Reason);
    }

    // FlowAck の種別・サイズ・TransferId・件数の往復を確かめる唯一のテスト（byte 配置そのものは固定しない）
    [Fact]
    public void CreateFlowAckMessage_先頭バイトがFileFlowAckでTransferIdと件数が復元できること()
    {
        var transferId = Guid.NewGuid();
        var msg = FileChunker.CreateFlowAckMessage(transferId, 12345);
        Assert.Equal(TransferProtocol.FileFlowAck, msg[0]);
        Assert.Equal(TransferProtocol.FlowAckSize, msg.Length);
        var parsed = FileChunker.ParseFlowAck(msg);
        Assert.NotNull(parsed);
        Assert.Equal(transferId, parsed.Value.TransferId);
        Assert.Equal(12345, parsed.Value.ReceivedChunkCount);
    }

    // ==================== CalculateTotalChunks ====================

    // 受信側は TotalChunks == CalculateTotalChunks(FileSize) を検証するので、ChunkSize(64KB) と
    // 計算式は旧版との互換契約。0・1 は空ファイル/最小、65536・65537 は ChunkSize の境界。
    [Theory]
    [InlineData(0, 0)]            // 空ファイルは 0 チャンク
    [InlineData(1, 1)]            // 1 バイトは 1 チャンク
    [InlineData(65_536, 1)]       // ちょうど 1 チャンク
    [InlineData(65_537, 2)]       // 1 バイトはみ出し
    public void CalculateTotalChunks_各種サイズで正しいチャンク数を返すこと(long fileSize, int expected)
    {
        Assert.Equal(expected, FileChunker.CalculateTotalChunks(fileSize));
    }

    // ==================== ResumeRequest 往復テスト ====================

    [Fact]
    public void ResumeRequest_生成と解析が一致すること()
    {
        var transferId = Guid.NewGuid();
        var lastChunk = 42;

        var msg = FileChunker.CreateResumeRequestMessage(transferId, lastChunk);
        Assert.Equal(TransferProtocol.ResumeRequest, msg[0]);
        Assert.Equal(21, msg.Length); // 1 + 16 + 4

        var (parsedId, parsedChunk) = FileChunker.ParseResumeRequest(msg);
        Assert.Equal(transferId, parsedId);
        Assert.Equal(lastChunk, parsedChunk);
    }

    [Fact]
    public void ResumeRequest_BigEndianでチャンクインデックスが書き込まれること()
    {
        var transferId = Guid.NewGuid();
        var msg = FileChunker.CreateResumeRequestMessage(transferId, 0x01020304);
        var readBack = BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(17, 4));
        Assert.Equal(0x01020304, readBack);
    }

    // ==================== ResumeResponse 往復テスト ====================

    [Fact]
    public void ResumeResponse_生成と解析が一致すること_受諾()
    {
        var transferId = Guid.NewGuid();
        var msg = FileChunker.CreateResumeResponseMessage(transferId, true, 99);
        Assert.Equal(TransferProtocol.ResumeResponse, msg[0]);
        Assert.Equal(22, msg.Length); // 1 + 16 + 1 + 4

        var (parsedId, accepted, parsedChunk) = FileChunker.ParseResumeResponse(msg);
        Assert.Equal(transferId, parsedId);
        Assert.True(accepted);
        Assert.Equal(99, parsedChunk);
    }

    [Fact]
    public void ResumeResponse_生成と解析が一致すること_拒否()
    {
        var transferId = Guid.NewGuid();
        var msg = FileChunker.CreateResumeResponseMessage(transferId, false, 0);

        var (parsedId, accepted, parsedChunk) = FileChunker.ParseResumeResponse(msg);
        Assert.Equal(transferId, parsedId);
        Assert.False(accepted);
        Assert.Equal(0, parsedChunk);
    }

    [Fact]
    public void ResumeResponse_BigEndianでチャンクインデックスが書き込まれること()
    {
        var transferId = Guid.NewGuid();
        var msg = FileChunker.CreateResumeResponseMessage(transferId, true, 0x7F000001);
        var readBack = BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(18, 4));
        Assert.Equal(0x7F000001, readBack);
    }

    // ==================== ResumeResponse V2（rere #D-005 差分レジューム）====================

    private static bool[] Bits(int total, params int[] receivedIndices)
    {
        var b = new bool[total];
        foreach (var i in receivedIndices) b[i] = true;
        return b;
    }

    [Fact]
    public void ResumeResponseV2_穴あきビットマップが往復すること()
    {
        var transferId = Guid.NewGuid();
        var received = Bits(10, 0, 2, 5, 9); // 順不同・穴あき
        var msg = FileChunker.CreateResumeResponseMessageV2(transferId, accepted: true, totalChunks: 10, received);
        Assert.Equal(TransferProtocol.ResumeResponse, msg[0]);

        var parsed = FileChunker.ParseResumeResponseV2(msg);
        Assert.NotNull(parsed);
        Assert.Equal(transferId, parsed!.Value.TransferId);
        Assert.True(parsed.Value.Accepted);
        Assert.Equal(10, parsed.Value.TotalChunks);
        Assert.Equal(received, parsed.Value.ReceivedSet);
    }

    [Fact]
    public void ResumeResponseV2_空集合と全集合が往復すること()
    {
        var id = Guid.NewGuid();
        var empty = FileChunker.ParseResumeResponseV2(
            FileChunker.CreateResumeResponseMessageV2(id, true, 17, new bool[17]));
        Assert.NotNull(empty);
        Assert.All(empty!.Value.ReceivedSet, b => Assert.False(b));

        var allSet = new bool[17];
        Array.Fill(allSet, true);
        var full = FileChunker.ParseResumeResponseV2(
            FileChunker.CreateResumeResponseMessageV2(id, true, 17, allSet));
        Assert.NotNull(full);
        Assert.All(full!.Value.ReceivedSet, b => Assert.True(b));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(100)]
    public void ResumeResponseV2_totalChunks端数でもbitが正確に往復すること(int total)
    {
        var id = Guid.NewGuid();
        // 偶数 index のみ受信
        var received = new bool[total];
        for (var i = 0; i < total; i += 2) received[i] = true;
        var parsed = FileChunker.ParseResumeResponseV2(
            FileChunker.CreateResumeResponseMessageV2(id, true, total, received));
        Assert.NotNull(parsed);
        Assert.Equal(received, parsed!.Value.ReceivedSet);
        // bitmap 長は (total+7)/8
        var msg = FileChunker.CreateResumeResponseMessageV2(id, true, total, received);
        Assert.Equal(22 + (total + 7) / 8, msg.Length);
    }

    [Fact]
    public void ResumeResponseV2_totalChunksゼロを扱えること()
    {
        var id = Guid.NewGuid();
        var msg = FileChunker.CreateResumeResponseMessageV2(id, false, 0, System.ReadOnlySpan<bool>.Empty);
        Assert.Equal(22, msg.Length);
        var parsed = FileChunker.ParseResumeResponseV2(msg);
        Assert.NotNull(parsed);
        Assert.False(parsed!.Value.Accepted);
        Assert.Equal(0, parsed.Value.TotalChunks);
        Assert.Empty(parsed.Value.ReceivedSet);
    }

    [Fact]
    public void ResumeResponseV2_短すぎるメッセージはnullを返すこと()
    {
        Assert.Null(FileChunker.ParseResumeResponseV2(new byte[21])); // ヘッダ未満
    }

    [Fact]
    public void ResumeResponseV2_bitmap長不足のメッセージはnullを返すこと()
    {
        // totalChunks=64（bitmap 8byte 必要）と宣言しつつ bitmap を欠く 22byte のみ
        var msg = new byte[22];
        msg[0] = TransferProtocol.ResumeResponse;
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(18, 4), 64);
        Assert.Null(FileChunker.ParseResumeResponseV2(msg));
    }

    [Fact]
    public void ResumeResponseV2_巨大totalChunksの細工メッセージで例外でなくnullを返すこと()
    {
        // レビュー #D-005: totalChunks≈int.MaxValue で (totalChunks+7)/8 が int オーバーフローし
        // 負の bitmapLen → 長さガードすり抜け → new bool[巨大]/負 Slice で例外死する DoS を防ぐ回帰。
        var msg = new byte[22];
        msg[0] = TransferProtocol.ResumeResponse;
        BinaryPrimitives.WriteInt32BigEndian(msg.AsSpan(18, 4), int.MaxValue);
        Assert.Null(FileChunker.ParseResumeResponseV2(msg)); // throw せず null
    }

    // ==================== BuildResumeSkipSet（受信bitmap→送信skip集合）====================

    [Fact]
    public void BuildResumeSkipSet_受信済みをそのままskip対象にすること()
    {
        var received = Bits(6, 1, 3);
        var skip = FileChunker.BuildResumeSkipSet(received, 6);
        Assert.Equal(received, skip);
    }

    [Fact]
    public void BuildResumeSkipSet_受信bitmapが短い場合は不足分を未受信扱いにすること()
    {
        // received は 3 要素しかないが totalChunks=6 → 残り 3 個は false（=送る）
        var skip = FileChunker.BuildResumeSkipSet(Bits(3, 0, 2), 6);
        Assert.Equal(6, skip.Length);
        Assert.True(skip[0]);
        Assert.False(skip[1]);
        Assert.True(skip[2]);
        Assert.False(skip[3]);
        Assert.False(skip[4]);
        Assert.False(skip[5]);
    }

    [Fact]
    public void BuildResumeSkipSet_受信bitmapが長い場合はtotalChunksで切り詰めること()
    {
        var skip = FileChunker.BuildResumeSkipSet(Bits(10, 0, 1, 2, 9), 4);
        Assert.Equal(4, skip.Length);
        Assert.True(skip[0]);
        Assert.True(skip[1]);
        Assert.True(skip[2]);
        Assert.False(skip[3]);
    }

    // ==================== FileHash (rere #B1-001: TransferId プレフィクス付き) ====================

    [Fact]
    public void ParseFileHash_旧形式の短いメッセージはnullを返しParseLegacyFileHashだけが抽出できること()
    {
        // 旧形式 [種別][sha256 32byte] = 33byte は TransferId なしのため ParseFileHash では拒否される
        var sha256 = new byte[32];
        Random.Shared.NextBytes(sha256);
        var legacy = new byte[1 + 32];
        legacy[0] = TransferProtocol.FileHash;
        sha256.CopyTo(legacy, 1);
        Assert.Null(FileChunker.ParseFileHash(legacy));

        // v1.0.50 以前との混在期間フォールバック (受信側のみ)
        var parsed = FileChunker.ParseLegacyFileHash(legacy);
        Assert.NotNull(parsed);
        Assert.Equal(sha256, parsed);
    }

    // === rere レビュー #C-10: FileReject の reason サニタイズ ===
    // reason は相手プロセス由来の自由文で、そのまま UI (ErrorMessage) とログへ流れる。
    // FileMeta.FileName は制御文字を弾いているのに reason は素通しだった非対称を埋める。

    private static byte[] BuildRejectMessage(Guid transferId, string reason)
    {
        var reasonBytes = System.Text.Encoding.UTF8.GetBytes(reason);
        var msg = new byte[1 + 16 + reasonBytes.Length];
        msg[0] = TransferProtocol.FileReject;
        transferId.TryWriteBytes(msg.AsSpan(1, 16));
        reasonBytes.CopyTo(msg, 17);
        return msg;
    }

    [Fact]
    public void ParseReject_改行やNUL等の制御文字を空白へ畳むこと()
    {
        var tid = Guid.NewGuid();
        var msg = BuildRejectMessage(tid, "拒否\r\nFAKE LOG LINE\0tail");

        var parsed = FileChunker.ParseReject(msg);

        Assert.NotNull(parsed);
        Assert.Equal(tid, parsed!.Value.TransferId);
        // 制御文字が残っていない = ログの行構造を偽装できない
        Assert.DoesNotContain(parsed.Value.Reason, c => char.IsControl(c));
        Assert.Contains("FAKE LOG LINE", parsed.Value.Reason);
    }

    [Fact]
    public void ParseReject_長すぎるreasonを上限で切り詰めること()
    {
        var tid = Guid.NewGuid();
        var msg = BuildRejectMessage(tid, new string('あ', 5000));

        var parsed = FileChunker.ParseReject(msg);

        Assert.NotNull(parsed);
        // 上限 + 省略記号 "…" の 1 文字ぶんまで
        Assert.True(
            parsed!.Value.Reason.Length <= FileChunker.MaxRejectReasonLength + 1,
            $"reason が切り詰められていない: {parsed.Value.Reason.Length} 文字");
        Assert.EndsWith("…", parsed.Value.Reason);
    }
}
