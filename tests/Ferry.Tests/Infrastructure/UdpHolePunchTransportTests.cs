using System.Net;
using Ferry.Infrastructure;

namespace Ferry.Tests.Infrastructure;

/// <summary>
/// UDP ホールパンチの双方向開通確認を固定する。
/// 核心: 相手の PUNCH を受信しただけ（=「相手→こちら」片方向の開通確認）では確立せず、
/// PUNCH_ACK 受信（=自分の PUNCH が相手 NAT を通って届き ACK が返った＝「こちら→相手」開通確認）で
/// 初めて確立する。HandlePunch で SetConnected する旧実装は、別NAT間で片肺確立→実データが通らず
/// FileApprove が返らず承認待ち60sタイムアウト→切断する片肺誤確立バグの原因だった。その回帰防止。
/// </summary>
public class UdpHolePunchTransportTests
{
    // TEST-NET-3 (RFC 5737): 実際には到達しないドキュメント用アドレス。
    // PUNCH_ACK の fire-and-forget 送出はここへ飛ぶが失敗は握りつぶされるので状態遷移に影響しない。
    private static IPEndPoint FakeEp() => new(IPAddress.Parse("203.0.113.5"), 50000);

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    public async Task HolePunchAsyncは許可されない接続先を送信前に拒否する(string remoteIp)
    {
        using var transport = new UdpHolePunchTransport();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            transport.HolePunchAsync(remoteIp, 50000, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PUNCH受信だけでは確立しない_双方向確認待ち()
    {
        using var t = new UdpHolePunchTransport();
        var opened = 0;
        t.ChannelOpened += (_, _) => opened++;

        t.HandlePunch(FakeEp());

        Assert.False(t.IsConnected); // 「相手→こちら」片方向では確立しない
        Assert.Equal(0, opened);
    }

    [Fact]
    public void PUNCH_ACK受信で確立する_双方向開通()
    {
        using var t = new UdpHolePunchTransport();
        var opened = 0;
        t.ChannelOpened += (_, _) => opened++;

        t.HandlePunchAck(FakeEp());

        Assert.True(t.IsConnected); // PUNCH_ACK = 自分の PUNCH が相手に届いた確認 → 確立
        Assert.Equal(1, opened);
    }

    [Fact]
    public void PUNCH後にPUNCH_ACKで確立_ChannelOpenedは1回()
    {
        using var t = new UdpHolePunchTransport();
        var opened = 0;
        t.ChannelOpened += (_, _) => opened++;

        t.HandlePunch(FakeEp());      // 確認待ち（未確立）
        Assert.False(t.IsConnected);

        t.HandlePunchAck(FakeEp());   // 双方向確認で確立
        Assert.True(t.IsConnected);
        Assert.Equal(1, opened);      // 確立は1回だけ
    }

    [Fact]
    public void PUNCH_ACKは冪等_既確立なら再発火しない()
    {
        using var t = new UdpHolePunchTransport();
        var opened = 0;
        t.ChannelOpened += (_, _) => opened++;

        t.HandlePunchAck(FakeEp());
        t.HandlePunchAck(FakeEp());   // 2 回目

        Assert.True(t.IsConnected);
        Assert.Equal(1, opened);      // if (!IsConnected) ガードで再発火しない
    }

    [Fact]
    public void 確立後にPUNCHを受けても確立イベントは増えない()
    {
        using var t = new UdpHolePunchTransport();
        var opened = 0;
        t.ChannelOpened += (_, _) => opened++;

        t.HandlePunchAck(FakeEp());   // 確立
        t.HandlePunch(FakeEp());      // 確立後の keepalive PUNCH 相当（PUNCH_ACK は返すが再確立しない）

        Assert.True(t.IsConnected);
        Assert.Equal(1, opened);
    }
}
