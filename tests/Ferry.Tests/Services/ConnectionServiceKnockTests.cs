using Ferry.Infrastructure;
using Ferry.Services;
using NSubstitute;

namespace Ferry.Tests.Services;

/// <summary>
/// CF 使用量削減: 着信 listener の「接続ノック + 低頻度安全網ポーリング」への移行を検証する。
/// 旧実装（WaitForOfferAsync の 400ms 常時ポーリング）はアイドルでも 1 ペアあたり ~20 万 req/日を
/// relay Worker に流していた（2026-07 実測）。ノック（inbox WS push）が主検知経路になったことと、
/// ノック無しではポーリングが安全網間隔まで発生しないことを固定する。
/// </summary>
public class ConnectionServiceKnockTests
{
    private static readonly string DeviceA = new('a', 32);
    private static readonly string DeviceB = new('b', 32);
    private static readonly string PairId = $"{DeviceA}_{DeviceB}";

    private static (ConnectionService Service, ISignalingService Sig) CreateService()
    {
        var sig = Substitute.For<ISignalingService>();
        sig.TryReadOfferOnceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        sig.ReadProbeOffersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(string Nonce, string Sdp)>>([]));
        var svc = new ConnectionService(DeviceA, "TestPC", signalingFactory: () => sig);
        return (svc, sig);
    }

    private static int OfferReadCount(ISignalingService sig) =>
        sig.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ISignalingService.TryReadOfferOnceAsync));

    private static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(cond());
    }

    [Fact]
    public async Task ノック受信で着信listenerが即時に再読みする()
    {
        var (svc, sig) = CreateService();
        try
        {
            svc.StartListeningForConnection(DeviceB);
            await WaitUntilAsync(() => OfferReadCount(sig) >= 1); // 初回 iteration の単発読み
            var before = OfferReadCount(sig);

            // relay Worker からの接続ノック（inbox WS push）を模擬 → 安全網間隔を待たず即時に再読みする
            sig.ConnectKnockReceived += Raise.Event<EventHandler<string>>(sig, PairId);
            await WaitUntilAsync(() => OfferReadCount(sig) > before);
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task ノックが無ければ安全網間隔まで再読みしない()
    {
        var (svc, sig) = CreateService();
        try
        {
            svc.StartListeningForConnection(DeviceB);
            await WaitUntilAsync(() => OfferReadCount(sig) >= 1);
            var before = OfferReadCount(sig);

            // 安全網間隔（WS 切断時 3s）より十分短い時間ではポーリングが発生しない（旧 400ms 常時ポーリングの根絶）
            await Task.Delay(800, TestContext.Current.CancellationToken);
            Assert.Equal(before, OfferReadCount(sig));
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task 無関係なpairIdのノックでは再読みしない()
    {
        var (svc, sig) = CreateService();
        try
        {
            svc.StartListeningForConnection(DeviceB);
            await WaitUntilAsync(() => OfferReadCount(sig) >= 1);
            var before = OfferReadCount(sig);

            // 監視していないペアのノックは無視される（別ペア宛の合図で起きない）
            var unrelated = $"{new string('c', 32)}_{new string('d', 32)}";
            sig.ConnectKnockReceived += Raise.Event<EventHandler<string>>(sig, unrelated);
            await Task.Delay(500, TestContext.Current.CancellationToken);
            Assert.Equal(before, OfferReadCount(sig));
        }
        finally { svc.Dispose(); }
    }
}
