using Ferry.Models;

namespace Ferry.Tests.Models;

/// <summary>
/// TransferProtocol の定数値が仕様通りであることを検証する。
/// </summary>
public class TransferProtocolTests
{
    [Fact]
    public void メッセージ種別の定数値が仕様通りであること()
    {
        Assert.Equal(0x01, TransferProtocol.FileMeta);
        Assert.Equal(0x02, TransferProtocol.FileChunk);
        Assert.Equal(0x03, TransferProtocol.FileAck);
        Assert.Equal(0x04, TransferProtocol.FileReject);
        Assert.Equal(0x10, TransferProtocol.Ping);
        Assert.Equal(0x11, TransferProtocol.Pong);
        Assert.Equal(0x20, TransferProtocol.ResumeRequest);
        Assert.Equal(0x21, TransferProtocol.ResumeResponse);
    }
}
