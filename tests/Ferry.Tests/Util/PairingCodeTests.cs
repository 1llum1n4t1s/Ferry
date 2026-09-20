using Ferry.Util;

namespace Ferry.Tests.Util;

public sealed class PairingCodeTests
{
    private const string DeviceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Nonce = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void CreateとTryParseでdeviceIdとnonceを往復する()
    {
        var code = PairingCode.Create(DeviceId, Nonce);

        Assert.Equal($"{DeviceId}.{Nonce}", code);
        Assert.True(PairingCode.TryParse(code, out var deviceId, out var nonce));
        Assert.Equal(DeviceId, deviceId);
        Assert.Equal(Nonce, nonce);
    }

    [Fact]
    public void 永続deviceId単体はペアリングコードとして受理しない()
    {
        Assert.False(PairingCode.TryParse(DeviceId, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-code")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.short")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.extra")]
    public void 不正なコードは拒否する(string code)
    {
        Assert.False(PairingCode.TryParse(code, out _, out _));
    }
}
