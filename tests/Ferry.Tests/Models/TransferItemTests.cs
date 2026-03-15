using Ferry.Models;

namespace Ferry.Tests.Models;

/// <summary>
/// TransferItem の Progress 計算とデフォルト値を検証する。
/// </summary>
public class TransferItemTests
{
    [Fact]
    public void TransferIdがデフォルトでGUIDが生成されること()
    {
        var item = new TransferItem();
        Assert.NotEqual(Guid.Empty, item.TransferId);
    }

    [Fact]
    public void TransferIdがインスタンスごとに異なること()
    {
        var a = new TransferItem();
        var b = new TransferItem();
        Assert.NotEqual(a.TransferId, b.TransferId);
    }

    [Fact]
    public void デフォルト値が正しいこと()
    {
        var item = new TransferItem();
        Assert.Equal(string.Empty, item.FileName);
        Assert.Equal(0L, item.FileSize);
        Assert.Equal(0L, item.TransferredBytes);
        Assert.Equal(-1, item.LastConfirmedChunkIndex);
        Assert.Equal(0, item.TotalChunks);
        Assert.Equal(TransferDirection.Send, item.Direction);
        Assert.Equal(TransferState.Pending, item.State);
        Assert.Null(item.ErrorMessage);
        Assert.Null(item.Sha256Hash);
        Assert.Null(item.SourceFilePath);
    }

    [Fact]
    public void Progressが正常に計算されること()
    {
        var item = new TransferItem { FileSize = 1000, TransferredBytes = 500 };
        Assert.Equal(0.5, item.Progress, precision: 10);
    }

    [Fact]
    public void Progress_転送完了時に1を返すこと()
    {
        var item = new TransferItem { FileSize = 1000, TransferredBytes = 1000 };
        Assert.Equal(1.0, item.Progress, precision: 10);
    }

    [Fact]
    public void Progress_転送開始前に0を返すこと()
    {
        var item = new TransferItem { FileSize = 1000, TransferredBytes = 0 };
        Assert.Equal(0.0, item.Progress, precision: 10);
    }

    [Fact]
    public void Progress_FileSizeが0の場合にゼロ除算せず0を返すこと()
    {
        var item = new TransferItem { FileSize = 0, TransferredBytes = 0 };
        Assert.Equal(0.0, item.Progress);
        // NaN や Infinity にならないこと
        Assert.False(double.IsNaN(item.Progress));
        Assert.False(double.IsInfinity(item.Progress));
    }

    [Fact]
    public void Progress_FileSizeが0でTransferredBytesが正の場合も0を返すこと()
    {
        // 本来ありえない状態だが、ゼロ除算の防御を検証
        var item = new TransferItem { FileSize = 0, TransferredBytes = 100 };
        Assert.Equal(0.0, item.Progress);
    }
}
