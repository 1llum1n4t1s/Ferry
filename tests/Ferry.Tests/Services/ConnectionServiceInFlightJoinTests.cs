using Ferry.Infrastructure;
using Ferry.Services;
using NSubstitute;

namespace Ferry.Tests.Services;

/// <summary>
/// 送信起点の <see cref="ConnectionService.ConnectToPeerAsync"/> が「確立途中の接続」を奪わないことを固定する。
///
/// 2026-07-28 の障害: 着信(answer)側がリレー合流待ちまで進んでいるところへユーザーが送信すると、
/// ConnectToPeerAsync が問答無用で進行中接続を Cancel → offerer 経路でシグナリングを削除して再 offer し、
/// 既にリレーで待機していた相手と噛み合わなくなって 20s 後に PeerUnreachableException
/// （相手から応答がありません）で必ず失敗していた。相手はオンラインで到達可能だった。
/// </summary>
public class ConnectionServiceInFlightJoinTests
{
    // CompareOrdinal(self, peer) <= 0 になる並びにして role調停の譲歩分岐を通さず、
    // 素の offerer 経路（offer 送信 → answer 待ち）だけを見る。
    private static readonly string DeviceSelf = new('a', 32);
    private static readonly string DevicePeer = new('b', 32);

    private static int OfferSendCount(ISignalingService sig) =>
        sig.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ISignalingService.SendSdpOfferAsync));

    private static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(cond());
    }

    [Fact]
    public async Task 確立途中の接続がある間は送信起点の再接続がofferを再送しない()
    {
        var sig = Substitute.For<ISignalingService>();
        sig.TryReadOfferOnceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        sig.ReadProbeOffersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(string Nonce, string Sdp)>>([]));
        // answer を返さず「確立途中(Connecting)」に留め置く。ct 発火で初めて終わる。
        sig.WaitForAnswerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.Delay(Timeout.Infinite, ci.Arg<CancellationToken>()).ContinueWith(_ => string.Empty));

        var svc = new ConnectionService(DeviceSelf, "TestPC", signalingFactory: () => sig);
        using var cts = new CancellationTokenSource();
        try
        {
            // 1 本目: offer を送って answer 待ちに入る（= State: Connecting）
            var first = svc.ConnectToPeerAsync(DevicePeer, cts.Token);
            await WaitUntilAsync(() => OfferSendCount(sig) >= 1);
            Assert.Equal(1, OfferSendCount(sig));

            // 2 本目: ユーザーの「送信」に相当。確立途中なので完走を待つ側に倒れ、
            // 旧実装のように 1 本目を Cancel して offer を再送してはいけない。
            var second = svc.ConnectToPeerAsync(DevicePeer, cts.Token);
            await Task.Delay(1500, TestContext.Current.CancellationToken);

            Assert.False(second.IsCompleted);          // 待機側に倒れている
            Assert.Equal(1, OfferSendCount(sig));      // 進行中接続を奪って再 offer していない

            cts.Cancel();
            foreach (var t in new[] { first, second })
            {
                try { await t.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
                catch { /* Cancel 由来の例外は本テストの関心外 */ }
            }
        }
        finally { svc.Dispose(); }
    }
}
