using Ferry.Models;

namespace Ferry.Tests.Models;

/// <summary>
/// PeerInfo のデフォルト値を検証する。
/// </summary>
public class PeerInfoTests
{
    [Fact]
    public void デフォルト値が正しいこと()
    {
        var info = new PeerInfo();
        Assert.Equal(string.Empty, info.SessionId);
        Assert.Equal(string.Empty, info.DisplayName);
        Assert.Equal(PeerState.Disconnected, info.State);
    }

    [Fact]
    public void プロパティが設定可能であること()
    {
        var info = new PeerInfo
        {
            SessionId = "abc-123",
            DisplayName = "テストPC",
            State = PeerState.Connected,
        };

        Assert.Equal("abc-123", info.SessionId);
        Assert.Equal("テストPC", info.DisplayName);
        Assert.Equal(PeerState.Connected, info.State);
    }

    [Theory]
    [InlineData(PeerState.Disconnected)]
    [InlineData(PeerState.WaitingForPairing)]
    [InlineData(PeerState.WaitingForMatch)]
    [InlineData(PeerState.Connecting)]
    [InlineData(PeerState.Connected)]
    [InlineData(PeerState.Error)]
    [InlineData(PeerState.Reconnecting)]
    public void 全てのPeerState値が設定可能であること(PeerState state)
    {
        var info = new PeerInfo { State = state };
        Assert.Equal(state, info.State);
    }
}
