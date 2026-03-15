using System.Text.Json;
using Ferry.Infrastructure;

namespace Ferry.Tests.Infrastructure;

/// <summary>
/// FileMetaJsonContext の AOT シリアライゼーション往復テスト。
/// Source Generator によるシリアライズ/デシリアライズが正しく動作することを検証する。
/// </summary>
public class FileMetaJsonContextTests
{
    [Fact]
    public void シリアライズとデシリアライズが往復で一致すること()
    {
        var original = new FileMeta
        {
            FileName = "テストファイル.txt",
            FileSize = 1_234_567,
            TotalChunks = 76,
            Sha256 = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            TransferId = Guid.NewGuid().ToString(),
        };

        // AOT 対応コンテキストでシリアライズ
        var json = JsonSerializer.Serialize(original, FileMetaJsonContext.Default.FileMeta);
        Assert.False(string.IsNullOrEmpty(json));

        // AOT 対応コンテキストでデシリアライズ
        var deserialized = JsonSerializer.Deserialize(json, FileMetaJsonContext.Default.FileMeta);
        Assert.NotNull(deserialized);
        Assert.Equal(original.FileName, deserialized!.FileName);
        Assert.Equal(original.FileSize, deserialized.FileSize);
        Assert.Equal(original.TotalChunks, deserialized.TotalChunks);
        Assert.Equal(original.Sha256, deserialized.Sha256);
        Assert.Equal(original.TransferId, deserialized.TransferId);
    }

    [Fact]
    public void UTF8バイト列でのシリアライズとデシリアライズが往復で一致すること()
    {
        var original = new FileMeta
        {
            FileName = "日本語ファイル名.pdf",
            FileSize = 999_999_999,
            TotalChunks = 61036,
            Sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
            TransferId = "custom-transfer-id",
        };

        // UTF-8 バイト列でシリアライズ（実際のプロトコルで使用する形式）
        var utf8Json = JsonSerializer.SerializeToUtf8Bytes(original, FileMetaJsonContext.Default.FileMeta);
        Assert.NotNull(utf8Json);
        Assert.True(utf8Json.Length > 0);

        // UTF-8 バイト列からデシリアライズ
        var deserialized = JsonSerializer.Deserialize(utf8Json, FileMetaJsonContext.Default.FileMeta);
        Assert.NotNull(deserialized);
        Assert.Equal(original.FileName, deserialized!.FileName);
        Assert.Equal(original.FileSize, deserialized.FileSize);
        Assert.Equal(original.TotalChunks, deserialized.TotalChunks);
        Assert.Equal(original.Sha256, deserialized.Sha256);
        Assert.Equal(original.TransferId, deserialized.TransferId);
    }

    [Fact]
    public void デフォルト値のFileMetaがシリアライズ可能であること()
    {
        var meta = new FileMeta();
        var json = JsonSerializer.Serialize(meta, FileMetaJsonContext.Default.FileMeta);

        var deserialized = JsonSerializer.Deserialize(json, FileMetaJsonContext.Default.FileMeta);
        Assert.NotNull(deserialized);
        Assert.Equal(string.Empty, deserialized!.FileName);
        Assert.Equal(0L, deserialized.FileSize);
        Assert.Equal(0, deserialized.TotalChunks);
        Assert.Equal(string.Empty, deserialized.Sha256);
        Assert.Equal(string.Empty, deserialized.TransferId);
    }

    [Fact]
    public void 大きなファイルサイズ値が正しくシリアライズされること()
    {
        var meta = new FileMeta
        {
            FileName = "large.bin",
            FileSize = long.MaxValue,
            TotalChunks = int.MaxValue,
            Sha256 = "hash",
            TransferId = "id",
        };

        var json = JsonSerializer.Serialize(meta, FileMetaJsonContext.Default.FileMeta);
        var deserialized = JsonSerializer.Deserialize(json, FileMetaJsonContext.Default.FileMeta);

        Assert.NotNull(deserialized);
        Assert.Equal(long.MaxValue, deserialized!.FileSize);
        Assert.Equal(int.MaxValue, deserialized.TotalChunks);
    }

    [Fact]
    public void マルチバイト文字を含むファイル名が正しく往復すること()
    {
        var names = new[]
        {
            "テスト.txt",
            "文件传输.zip",
            "파일전송.dat",
            "Ünïcödé.bin",
            "emoji_🚢.tar.gz",
        };

        foreach (var name in names)
        {
            var meta = new FileMeta { FileName = name };
            var json = JsonSerializer.Serialize(meta, FileMetaJsonContext.Default.FileMeta);
            var deserialized = JsonSerializer.Deserialize(json, FileMetaJsonContext.Default.FileMeta);
            Assert.Equal(name, deserialized!.FileName);
        }
    }
}
