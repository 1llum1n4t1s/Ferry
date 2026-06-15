using Ferry.Infrastructure;

namespace Ferry.Tests.Infrastructure;

/// <summary>
/// rere #D-003: signaling の per-sender ノードキー構築 (SignalingPaths) を検証する。
/// 純関数なので Firebase 非依存でテストできる。ここで守りたい不変条件:
///   (1) offers/answers/endpoints が送信元 deviceId で分離され、2 sender が衝突しないこと
///   (2) 同じ (pairId, deviceId) でも node 種別 (offer/answer/endpoint) が別パスになること
///   (3) pairId が必ずパスに含まれ、別 pair が混ざらないこと
///   (4) 自分キー (self=_deviceId) と相手キー (peer=peerId) が別物になること
/// (FirebaseSignaling 自体の読み書きは実 Firebase 依存なので実機検証に委ねる。)
/// </summary>
public class SignalingPathsTests
{
    private const string PairId = "AAAA-BBBB";
    private const string DeviceA = "deadbeefdeadbeefdeadbeefdeadbeef";
    private const string DeviceB = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void ノード名定数が期待値であること()
    {
        Assert.Equal("offers", SignalingPaths.OffersNode);
        Assert.Equal("answers", SignalingPaths.AnswersNode);
        Assert.Equal("endpoints", SignalingPaths.EndpointsNode);
    }

    [Fact]
    public void offerパスが送信元deviceIdで分離され2senderが衝突しないこと()
    {
        var a = SignalingPaths.OfferPath(PairId, DeviceA);
        var b = SignalingPaths.OfferPath(PairId, DeviceB);
        Assert.NotEqual(a, b);
        Assert.Equal($"signaling/{PairId}/offers/{DeviceA}", a);
        Assert.Equal($"signaling/{PairId}/offers/{DeviceB}", b);
    }

    [Fact]
    public void answerパスがanswerer_deviceIdで分離され衝突しないこと()
    {
        var a = SignalingPaths.AnswerPath(PairId, DeviceA);
        var b = SignalingPaths.AnswerPath(PairId, DeviceB);
        Assert.NotEqual(a, b);
        Assert.Equal($"signaling/{PairId}/answers/{DeviceA}", a);
    }

    [Fact]
    public void endpointパスが送信元deviceIdで分離され衝突しないこと()
    {
        var a = SignalingPaths.EndpointPath(PairId, DeviceA);
        var b = SignalingPaths.EndpointPath(PairId, DeviceB);
        Assert.NotEqual(a, b);
        Assert.Equal($"signaling/{PairId}/endpoints/{DeviceA}", a);
    }

    [Fact]
    public void 同一deviceIdでもoffer_answer_endpointが別パスになること()
    {
        var offer = SignalingPaths.OfferPath(PairId, DeviceA);
        var answer = SignalingPaths.AnswerPath(PairId, DeviceA);
        var endpoint = SignalingPaths.EndpointPath(PairId, DeviceA);
        Assert.NotEqual(offer, answer);
        Assert.NotEqual(offer, endpoint);
        Assert.NotEqual(answer, endpoint);
    }

    [Fact]
    public void 別pairIdのパスが混ざらないこと()
    {
        var p1 = SignalingPaths.OfferPath("PAIR-1", DeviceA);
        var p2 = SignalingPaths.OfferPath("PAIR-2", DeviceA);
        Assert.NotEqual(p1, p2);
        Assert.StartsWith("signaling/PAIR-1/", p1);
        Assert.StartsWith("signaling/PAIR-2/", p2);
    }

    [Fact]
    public void 自分キーと相手キーが別パスになること()
    {
        // 書き手は自分の deviceId、読み手はペア相手の deviceId を渡す規約。両者が別パスでなければ
        // 「自分のキーを読んで永久待機」する致命的取り違えが起きる (blueprint risks#1)。
        var myOffer = SignalingPaths.OfferPath(PairId, DeviceA);   // self が書く
        var peerOffer = SignalingPaths.OfferPath(PairId, DeviceB); // peer のを読む
        Assert.NotEqual(myOffer, peerOffer);
    }
}
