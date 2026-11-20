using System.Net;
using Ferry.Infrastructure;

namespace Ferry.Tests.Infrastructure;

/// <summary>
/// IPv6 TCP 直結対応（デュアルスタック広告アドレス）の純関数部分のテスト。
/// <see cref="TcpDirectTransport.IsAdvertisableIpv6"/> / <see cref="TcpDirectTransport.InterleaveAddresses"/>
/// が offer に載せる IP の選別と試行順序を決める。
/// </summary>
public class TcpDirectTransportAddressTests
{
    // === IsAdvertisableIpv6: 許可するもの ===

    [Theory]
    [InlineData("2001:db8::1")]                        // GUA (2000::/3)
    [InlineData("3fff:ffff::1")]                       // GUA 上端側
    [InlineData("fd12:3456:789a::1")]                  // ULA (fd00::/8)
    [InlineData("fc00::1")]                            // ULA (fc00::/8)
    public void IsAdvertisableIpv6_GUAとULAを許可する(string ip)
    {
        Assert.True(TcpDirectTransport.IsAdvertisableIpv6(IPAddress.Parse(ip)));
    }

    // === IsAdvertisableIpv6: 弾くもの ===

    [Theory]
    [InlineData("fe80::1")]                            // リンクローカル (scope id が必要)
    [InlineData("::1")]                                // ループバック
    [InlineData("ff02::1")]                            // マルチキャスト
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")] // Teredo (2001:0::/32)
    [InlineData("::ffff:192.168.0.1")]                 // IPv4-mapped
    [InlineData("fec0::1")]                            // 旧サイトローカル (deprecated)
    public void IsAdvertisableIpv6_広告に不適切なアドレスを弾く(string ip)
    {
        Assert.False(TcpDirectTransport.IsAdvertisableIpv6(IPAddress.Parse(ip)));
    }

    [Fact]
    public void IsAdvertisableIpv6_IPv4アドレスはfalse()
    {
        Assert.False(TcpDirectTransport.IsAdvertisableIpv6(IPAddress.Parse("192.168.0.1")));
    }

    // === InterleaveAddresses: 試行順序 ===

    [Fact]
    public void InterleaveAddresses_v4先行で交互に編み余りを末尾に連結する()
    {
        var result = TcpDirectTransport.InterleaveAddresses(
            ["10.0.0.1", "192.168.0.1"],
            ["2001:db8::1", "2001:db8::2", "2001:db8::3"]);

        // 先頭は必ず v4（LAN 直結の最速パスを維持）、次に v6（v4 不達時に予算内で試行順が回る）。
        // 長さ不一致の余り（v6 の 3 つ目）は末尾に連結される。
        Assert.Equal(["10.0.0.1", "2001:db8::1", "192.168.0.1", "2001:db8::2", "2001:db8::3"], result);
    }

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.10.20")]
    [InlineData("2001:db8::20")]
    [InlineData("fd12:3456:789a::20")]
    public void PeerEndpointPolicy_LANとIPv6直接接続に必要なアドレスを許可する(string ip)
    {
        Assert.True(PeerEndpointPolicy.IsAllowedAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.170.2")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fd00:ec2::254")]
    public void PeerEndpointPolicy_ローカルサービスとメタデータ向けアドレスを拒否する(string ip)
    {
        Assert.False(PeerEndpointPolicy.IsAllowedAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    public void PeerEndpointPolicy_ポート範囲を検証する(int port, bool expected)
    {
        Assert.Equal(expected, PeerEndpointPolicy.IsAllowedPort(port));
    }
}
