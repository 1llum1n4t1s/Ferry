using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Ferry.Infrastructure;
using Ferry.Models;

namespace Ferry.Tests.Infrastructure;

/// <summary>
/// FileChunker の全メソッドを網羅的にテストする。
/// ファイルI/O系テストは一時ファイルを使用し、テスト後に必ず削除する。
/// </summary>
public class FileChunkerTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    /// <summary>テスト用の一時ファイルを作成し、パスを返す。</summary>
    private string CreateTempFile(byte[] content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* テスト後片付け */ }
        }
    }

    // ==================== CreateFileMetaMessage ====================

    [Fact]
    public void CreateFileMetaMessage_先頭バイトがFileMetaであること()
    {
        var msg = FileChunker.CreateFileMetaMessage("test.txt", 100, 1, "abc123");
        Assert.Equal(TransferProtocol.FileMeta, msg[0]);
    }

    [Fact]
    public void CreateFileMetaMessage_JSONが正しくパースできること()
    {
        var transferId = Guid.NewGuid();
        var msg = FileChunker.CreateFileMetaMessage("test.txt", 12345, 3, "sha256hex", transferId);

        var meta = FileChunker.ParseFileMeta(msg);
        Assert.NotNull(meta);
        Assert.Equal("test.txt", meta!.FileName);
        Assert.Equal(12345L, meta.FileSize);
        Assert.Equal(3, meta.TotalChunks);
        Assert.Equal("sha256hex", meta.Sha256);
        Assert.Equal(transferId.ToString(), meta.TransferId);
    }

    [Fact]
    public void CreateFileMetaMessage_transferIdがdefaultの場合に自動生成されること()
    {
        var msg = FileChunker.CreateFileMetaMessage("a.bin", 1, 1, "hash");
        var meta = FileChunker.ParseFileMeta(msg);
        Assert.NotNull(meta);
        Assert.True(Guid.TryParse(meta!.TransferId, out var parsed));
        Assert.NotEqual(Guid.Empty, parsed);
    }

    // ==================== CreateChunkMessage ====================

    [Fact]
    public void CreateChunkMessage_先頭バイトがFileChunkであること()
    {
        var data = new byte[] { 0xAA, 0xBB, 0xCC };
        var msg = FileChunker.CreateChunkMessage(Guid.NewGuid(), 0, data);
        Assert.Equal(TransferProtocol.FileChunk, msg[0]);
    }

    [Fact]
    public void CreateChunkMessage_チャンクインデックスがBigEndianで書き込まれること()
    {
        var msg = FileChunker.CreateChunkMessage(Guid.NewGuid(), 0x01020304, new byte[] { 0xFF });
        // TransferId(16) の後ろ msg[17..21] に BigEndian の int が入る
        var index = BinaryPrimitives.ReadInt32BigEndian(msg.AsSpan(17, 4));
        Assert.Equal(0x01020304, index);
    }

    [Fact]
    public void CreateChunkMessage_データ部分が正しくコピーされること()
    {
        var data = new byte[] { 10, 20, 30, 40, 50 };
        var msg = FileChunker.CreateChunkMessage(Guid.NewGuid(), 0, data);
        Assert.Equal(TransferProtocol.ChunkHeaderSize + 5, msg.Length);
        Assert.Equal(data, msg[TransferProtocol.ChunkHeaderSize..]);
    }

    [Fact]
    public void CreateChunkMessage_空データでも動作すること()
    {
        var msg = FileChunker.CreateChunkMessage(Guid.NewGuid(), 0, ReadOnlySpan<byte>.Empty);
        Assert.Equal(TransferProtocol.ChunkHeaderSize, msg.Length); // 種別1 + TransferId16 + インデックス4
    }

    // ==================== CreateAckMessage ====================

    [Fact]
    public void CreateAckMessage_成功フラグが正しく設定されること()
    {
        var hash = new byte[32];
        var msgSuccess = FileChunker.CreateAckMessage(true, hash);
        var msgFail = FileChunker.CreateAckMessage(false, hash);

        Assert.Equal(TransferProtocol.FileAck, msgSuccess[0]);
        Assert.Equal(1, msgSuccess[1]); // success = true
        Assert.Equal(0, msgFail[1]);    // success = false
    }

    [Fact]
    public void CreateAckMessage_32バイトのハッシュが正しくコピーされること()
    {
        var hash = new byte[32];
        for (int i = 0; i < 32; i++) hash[i] = (byte)i;

        var msg = FileChunker.CreateAckMessage(true, hash);
        Assert.Equal(34, msg.Length); // 1 + 1 + 32
        Assert.Equal(hash, msg[2..34]);
    }

    [Fact]
    public void CreateAckMessage_32バイト未満のハッシュでも例外にならないこと()
    {
        // 16バイトのハッシュ（短い場合）
        var shortHash = new byte[16];
        for (int i = 0; i < 16; i++) shortHash[i] = (byte)(0xA0 + i);

        var msg = FileChunker.CreateAckMessage(true, shortHash);
        Assert.Equal(34, msg.Length); // メッセージサイズは常に34

        // 先頭16バイトがコピーされ、残り16バイトは0で埋まること
        for (int i = 0; i < 16; i++)
            Assert.Equal((byte)(0xA0 + i), msg[2 + i]);
        for (int i = 16; i < 32; i++)
            Assert.Equal(0, msg[2 + i]);
    }

    [Fact]
    public void CreateAckMessage_空のハッシュ配列でも例外にならないこと()
    {
        var emptyHash = Array.Empty<byte>();
        var msg = FileChunker.CreateAckMessage(true, emptyHash);
        Assert.Equal(34, msg.Length);
    }

    // ==================== CreateRejectMessage ====================

    // v1.0.38: CreateRejectMessage は [種別 1] [TransferId 16] [reason UTF-8] の形式に変更
    [Fact]
    public void CreateRejectMessage_先頭バイトがFileRejectであること()
    {
        var msg = FileChunker.CreateRejectMessage(Guid.NewGuid(), "容量不足");
        Assert.Equal(TransferProtocol.FileReject, msg[0]);
    }

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

    [Fact]
    public void CreateRejectMessage_空文字列でも動作すること()
    {
        var msg = FileChunker.CreateRejectMessage(Guid.NewGuid(), "");
        // 種別(1) + TransferId(16) = 17 バイト
        Assert.Equal(17, msg.Length);
    }

    [Fact]
    public void CreateApproveMessage_先頭バイトがFileApproveでTransferIdが復元できること()
    {
        var transferId = Guid.NewGuid();
        var msg = FileChunker.CreateApproveMessage(transferId);
        Assert.Equal(TransferProtocol.FileApprove, msg[0]);
        Assert.Equal(17, msg.Length);
        var parsed = FileChunker.ParseApprove(msg);
        Assert.Equal(transferId, parsed);
    }

    [Fact]
    public void ParseApprove_短すぎるメッセージはnullを返すこと()
    {
        Assert.Null(FileChunker.ParseApprove(new byte[] { 0x06 }));
        Assert.Null(FileChunker.ParseApprove(new byte[] { 0x06, 0x01, 0x02 }));
    }

    // ==================== CreateFlowAckMessage / ParseFlowAck (v1.0.46 フロー制御) ====================

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

    [Fact]
    public void CreateFlowAckMessage_件数ゼロや大きい値でも往復すること()
    {
        var transferId = Guid.NewGuid();
        foreach (var count in new[] { 0, 1, 126716, int.MaxValue })
        {
            var parsed = FileChunker.ParseFlowAck(FileChunker.CreateFlowAckMessage(transferId, count));
            Assert.NotNull(parsed);
            Assert.Equal(count, parsed.Value.ReceivedChunkCount);
        }
    }

    [Fact]
    public void ParseFlowAck_短すぎるメッセージはnullを返すこと()
    {
        Assert.Null(FileChunker.ParseFlowAck(new byte[] { TransferProtocol.FileFlowAck }));
        Assert.Null(FileChunker.ParseFlowAck(new byte[] { TransferProtocol.FileFlowAck, 0x01, 0x02 }));
    }

    [Fact]
    public void ParseReject_短すぎるメッセージはnullを返すこと()
    {
        // v1.0.38 review nitpick: ParseApprove 異常系と対称な負系テスト追加
        Assert.Null(FileChunker.ParseReject(new byte[] { TransferProtocol.FileReject }));
        Assert.Null(FileChunker.ParseReject(new byte[] { TransferProtocol.FileReject, 0x01, 0x02 }));
    }

    [Fact]
    public void ParseReject_TransferIdだけのメッセージはreason空文字を返す()
    {
        // 17 バイトちょうど (種別 1 + TransferId 16) で reason 空
        var transferId = Guid.NewGuid();
        var msg = new byte[17];
        msg[0] = TransferProtocol.FileReject;
        transferId.TryWriteBytes(msg.AsSpan(1, 16));
        var parsed = FileChunker.ParseReject(msg);
        Assert.NotNull(parsed);
        Assert.Equal(transferId, parsed.Value.TransferId);
        Assert.Equal(string.Empty, parsed.Value.Reason);
    }

    // ==================== Ping / Pong ====================

    [Fact]
    public void CreatePingMessage_1バイトでPing種別であること()
    {
        var msg = FileChunker.CreatePingMessage();
        Assert.Single(msg);
        Assert.Equal(TransferProtocol.Ping, msg[0]);
    }

    [Fact]
    public void CreatePongMessage_1バイトでPong種別であること()
    {
        var msg = FileChunker.CreatePongMessage();
        Assert.Single(msg);
        Assert.Equal(TransferProtocol.Pong, msg[0]);
    }

    // ==================== ReadChunks ====================

    [Fact]
    public void ReadChunks_空ファイルは0チャンクを返すこと()
    {
        var path = CreateTempFile([]);
        var chunks = FileChunker.ReadChunks(path).ToList();
        Assert.Empty(chunks);
    }

    [Fact]
    public void ReadChunks_ChunkSize未満のファイルは1チャンクを返すこと()
    {
        var data = new byte[100];
        Random.Shared.NextBytes(data);
        var path = CreateTempFile(data);

        var chunks = FileChunker.ReadChunks(path).ToList();
        Assert.Single(chunks);
        Assert.Equal(0, chunks[0].Index);
        Assert.Equal(data, chunks[0].Data);
    }

    [Fact]
    public void ReadChunks_ちょうどChunkSizeのファイルは1チャンクを返すこと()
    {
        var data = new byte[TransferProtocol.ChunkSize];
        Random.Shared.NextBytes(data);
        var path = CreateTempFile(data);

        var chunks = FileChunker.ReadChunks(path).ToList();
        Assert.Single(chunks);
        Assert.Equal(data, chunks[0].Data);
    }

    [Fact]
    public void ReadChunks_ChunkSize超のファイルは複数チャンクに分割されること()
    {
        var size = TransferProtocol.ChunkSize * 2 + 100;
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        var path = CreateTempFile(data);

        var chunks = FileChunker.ReadChunks(path).ToList();
        Assert.Equal(3, chunks.Count);
        Assert.Equal(0, chunks[0].Index);
        Assert.Equal(1, chunks[1].Index);
        Assert.Equal(2, chunks[2].Index);

        // 各チャンクサイズの検証
        Assert.Equal(TransferProtocol.ChunkSize, chunks[0].Data.Length);
        Assert.Equal(TransferProtocol.ChunkSize, chunks[1].Data.Length);
        Assert.Equal(100, chunks[2].Data.Length);

        // 全チャンクを結合すると元データと一致すること
        var reassembled = chunks.SelectMany(c => c.Data).ToArray();
        Assert.Equal(data, reassembled);
    }

    [Fact]
    public void ReadChunks_存在しないファイルで例外が発生すること()
    {
        // Windows では DirectoryNotFoundException、Unix では FileNotFoundException になりうる
        Assert.ThrowsAny<IOException>(() =>
            FileChunker.ReadChunks(Path.Combine(Path.GetTempPath(), "nonexistent_dir_ferry_test", "file.bin")).ToList());
    }

    // ==================== ComputeSha256 / ComputeSha256Hex ====================

    [Fact]
    public void ComputeSha256_正しいハッシュを返すこと()
    {
        var data = Encoding.UTF8.GetBytes("Hello, Ferry!");
        var path = CreateTempFile(data);

        var hash = FileChunker.ComputeSha256(path);
        var expected = SHA256.HashData(data);
        Assert.Equal(expected, hash);
    }

    [Fact]
    public void ComputeSha256Hex_小文字16進数文字列を返すこと()
    {
        var data = Encoding.UTF8.GetBytes("test data");
        var path = CreateTempFile(data);

        var hex = FileChunker.ComputeSha256Hex(path);
        // 64文字の16進数（SHA-256 = 32バイト = 64文字）
        Assert.Equal(64, hex.Length);
        // 全て小文字の16進数であること
        Assert.Matches("^[0-9a-f]{64}$", hex);

        // 期待値と一致すること
        var expected = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        Assert.Equal(expected, hex);
    }

    [Fact]
    public void ComputeSha256_空ファイルのハッシュが正しいこと()
    {
        var path = CreateTempFile([]);
        var hash = FileChunker.ComputeSha256Hex(path);
        // SHA-256 of empty = e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
    }

    // ==================== CalculateTotalChunks ====================

    [Fact]
    public void CalculateTotalChunks_ゼロサイズは0チャンクを返すこと()
    {
        // fileSize=0 の場合: (0 + 65535) / 65536 = 0 (P-15 で 64KB に更新)
        Assert.Equal(0, FileChunker.CalculateTotalChunks(0));
    }

    [Fact]
    public void CalculateTotalChunks_1バイトは1チャンクを返すこと()
    {
        Assert.Equal(1, FileChunker.CalculateTotalChunks(1));
    }

    [Fact]
    public void CalculateTotalChunks_ちょうどChunkSizeは1チャンクを返すこと()
    {
        Assert.Equal(1, FileChunker.CalculateTotalChunks(TransferProtocol.ChunkSize));
    }

    [Fact]
    public void CalculateTotalChunks_ChunkSize_plus_1は2チャンクを返すこと()
    {
        Assert.Equal(2, FileChunker.CalculateTotalChunks(TransferProtocol.ChunkSize + 1));
    }

    [Theory]
    // P-15: ChunkSize 64KB (65,536 bytes) 前提に更新
    [InlineData(65_536, 1)]        // ちょうど1チャンク
    [InlineData(65_537, 2)]        // 1バイトはみ出し
    [InlineData(131_072, 2)]       // ちょうど2チャンク
    [InlineData(100_000, 2)]       // 100KB → 2チャンク
    [InlineData(1_000_000, 16)]    // 1MB ≈ 15.26チャンク → 16
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

    // ==================== ParseFileMeta ====================

    [Fact]
    public void ParseFileMeta_正常なメッセージを解析できること()
    {
        var msg = FileChunker.CreateFileMetaMessage("document.pdf", 999999, 62, "abcdef1234567890");
        var meta = FileChunker.ParseFileMeta(msg);

        Assert.NotNull(meta);
        Assert.Equal("document.pdf", meta!.FileName);
        Assert.Equal(999999L, meta.FileSize);
        Assert.Equal(62, meta.TotalChunks);
        Assert.Equal("abcdef1234567890", meta.Sha256);
    }

    [Fact]
    public void ParseFileMeta_空メッセージはnullを返すこと()
    {
        var result = FileChunker.ParseFileMeta(ReadOnlySpan<byte>.Empty);
        Assert.Null(result);
    }

    [Fact]
    public void ParseFileMeta_1バイトメッセージはnullを返すこと()
    {
        var result = FileChunker.ParseFileMeta(new byte[] { 0x01 });
        Assert.Null(result);
    }

    [Fact]
    public void ParseFileMeta_不正なJSONで例外が発生すること()
    {
        // 種別バイト + 不正な JSON
        var invalidJson = new byte[] { 0x01, (byte)'{', (byte)'!' };
        Assert.ThrowsAny<Exception>(() => FileChunker.ParseFileMeta(invalidJson));
    }

    // ==================== GetMessageType ====================

    [Fact]
    public void GetMessageType_正しい種別を返すこと()
    {
        Assert.Equal(TransferProtocol.FileMeta, FileChunker.GetMessageType(new byte[] { 0x01, 0x00 }));
        Assert.Equal(TransferProtocol.FileChunk, FileChunker.GetMessageType(new byte[] { 0x02 }));
        Assert.Equal(TransferProtocol.Ping, FileChunker.GetMessageType(new byte[] { 0x10 }));
    }

    [Fact]
    public void GetMessageType_空メッセージで0を返すこと()
    {
        Assert.Equal(0, FileChunker.GetMessageType(ReadOnlySpan<byte>.Empty));
    }

    // ==================== 統合テスト: ファイル全体の送受信シミュレーション ====================

    [Fact]
    public void 統合テスト_ファイルのチャンク分割とSHA256検証が一致すること()
    {
        // テストデータ（ChunkSize * 2.5 = 163840バイト = 64KB × 2.5、P-15 で 64KB に更新）
        var data = new byte[TransferProtocol.ChunkSize * 2 + TransferProtocol.ChunkSize / 2];
        Random.Shared.NextBytes(data);
        var path = CreateTempFile(data);

        // チャンク数計算
        var totalChunks = FileChunker.CalculateTotalChunks(data.Length);
        Assert.Equal(3, totalChunks);

        // SHA-256 計算
        var sha256Hex = FileChunker.ComputeSha256Hex(path);
        var sha256Bytes = FileChunker.ComputeSha256(path);

        // メタデータメッセージ生成・解析
        var metaMsg = FileChunker.CreateFileMetaMessage("test.bin", data.Length, totalChunks, sha256Hex);
        var meta = FileChunker.ParseFileMeta(metaMsg);
        Assert.NotNull(meta);
        Assert.Equal(data.Length, meta!.FileSize);

        // チャンク読み込み
        var chunks = FileChunker.ReadChunks(path).ToList();
        Assert.Equal(totalChunks, chunks.Count);

        // 各チャンクをメッセージ化して再構成
        var reassembled = new MemoryStream();
        var roundtripId = Guid.NewGuid();
        foreach (var (index, chunkData) in chunks)
        {
            var chunkMsg = FileChunker.CreateChunkMessage(roundtripId, index, chunkData);
            // メッセージからデータ部分を抽出
            reassembled.Write(chunkMsg, TransferProtocol.ChunkHeaderSize, chunkMsg.Length - TransferProtocol.ChunkHeaderSize);
        }
        Assert.Equal(data, reassembled.ToArray());

        // ACK メッセージ
        var ackMsg = FileChunker.CreateAckMessage(true, sha256Bytes);
        Assert.Equal(TransferProtocol.FileAck, ackMsg[0]);
        Assert.Equal(1, ackMsg[1]); // success
    }

    // ==================== FileHash (rere #B1-001: TransferId プレフィクス付き) ====================

    [Fact]
    public void CreateFileHashMessage_TransferIdとSha256がラウンドトリップすること()
    {
        var transferId = Guid.NewGuid();
        var sha256 = new byte[32];
        Random.Shared.NextBytes(sha256);

        var msg = FileChunker.CreateFileHashMessage(transferId, sha256);
        Assert.Equal(TransferProtocol.FileHash, msg[0]);
        Assert.Equal(1 + 16 + 32, msg.Length);

        var parsed = FileChunker.ParseFileHash(msg);
        Assert.NotNull(parsed);
        Assert.Equal(transferId, parsed!.Value.TransferId);
        Assert.Equal(sha256, parsed.Value.Sha256);
    }

    [Fact]
    public void ParseFileHash_旧形式の短いメッセージはnullを返すこと()
    {
        // 旧形式 [種別][sha256 32byte] = 33byte は TransferId なしのため拒否される
        var legacy = new byte[1 + 32];
        legacy[0] = TransferProtocol.FileHash;
        Assert.Null(FileChunker.ParseFileHash(legacy));
    }

    [Fact]
    public void ParseLegacyFileHash_旧形式33byteからSha256を抽出できること()
    {
        // v1.0.50 以前との混在期間フォールバック (受信側のみ)
        var sha256 = new byte[32];
        Random.Shared.NextBytes(sha256);
        var legacy = new byte[1 + 32];
        legacy[0] = TransferProtocol.FileHash;
        sha256.CopyTo(legacy, 1);

        var parsed = FileChunker.ParseLegacyFileHash(legacy);
        Assert.NotNull(parsed);
        Assert.Equal(sha256, parsed);
    }
}
