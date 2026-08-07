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

    private static int ProbeReadCount(ISignalingService sig) =>
        sig.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ISignalingService.ReadProbeOffersAsync));

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
    public async Task アイドル中はprobeを読み直さない()
    {
        var (svc, sig) = CreateService();
        try
        {
            svc.StartListeningForConnection(DeviceB);
            // 初回 iteration だけ、listener 起動前に積まれていた probe を 1 度回収する
            await WaitUntilAsync(() => ProbeReadCount(sig) >= 1);
            var before = ProbeReadCount(sig);

            // CF 使用量削減 (2026-07): ノックが来ない間は probe を読み直さない。
            // 旧実装は毎周 probe-offers + offer の 2 リクエストを PairDO へ投げており、
            // それが Durable Objects リクエストの 89%（87.8 万 req/月）を占めていた。
            await Task.Delay(800, TestContext.Current.CancellationToken);
            Assert.Equal(before, ProbeReadCount(sig));
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public async Task ノック受信でprobeも読み直す()
    {
        var (svc, sig) = CreateService();
        try
        {
            svc.StartListeningForConnection(DeviceB);
            await WaitUntilAsync(() => ProbeReadCount(sig) >= 1);
            var before = ProbeReadCount(sig);

            // relay は probe-offer の POST でもノックを push する（signaling-routes.ts）ため、
            // ノックで起きた周は probe も読み直して経路 Probe の取りこぼしを防ぐ
            sig.ConnectKnockReceived += Raise.Event<EventHandler<string>>(sig, PairId);
            await WaitUntilAsync(() => ProbeReadCount(sig) > before);
        }
        finally { svc.Dispose(); }
    }

    // === 安全網ポーリング間隔の決定（inbox WS 切断の継続時間で切り替える） ===
    //
    // 2026-07-31〜08-02 の実測で、inbox WS が張れないまま 3s ポーリングを続けた結果
    // PairDO が 1 日 46,595〜58,393 req（正常値 ~2,600 の 18〜22 倍。2 ペア × 28,800 と一致）に跳ね、
    // 30 日ローリングで Workers Paid 含有枠 100 万の 90% を占めた。瞬断の検知速度は保ったまま、
    // 持続切断だけを 10s に落とすことを固定する。
    //
    // 時間依存を実時間で待つと 2 分かかるため、切断開始時刻のフィールドを直接置いて判定する。

    private static int InvokeResolveIdleListenPollMs(ConnectionService svc) =>
        (int)typeof(ConnectionService)
            .GetMethod("ResolveIdleListenPollMs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(svc, null)!;

    private static void SetField(ConnectionService svc, string name, object? value) =>
        typeof(ConnectionService)
            .GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(svc, value);

    private static long GetInboxDownSinceMs(ConnectionService svc) =>
        (long)typeof(ConnectionService)
            .GetField("_inboxDownSinceMs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(svc)!;

    [Fact]
    public void inbox切断直後は3秒間隔で着信を追う()
    {
        var (svc, _) = CreateService();
        try
        {
            // _knockWatcher 未生成 = inbox 未接続。初回観測なので fast 窓の内側。
            Assert.Equal(3_000, InvokeResolveIdleListenPollMs(svc));
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public void inbox切断が2分を超えたら10秒間隔へ落とす()
    {
        var (svc, _) = CreateService();
        try
        {
            // 5 分前から切断が続いている状態を作る（fast 窓 120s を超過）
            SetField(svc, "_inboxDownSinceMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 300_000);

            // 10s なら送信側の answer 待ち 20s に対し 10 + TCP 試行 5s + 書込 ≒ 16s で収まる。
            // これ以上延ばすと着信を落とすので、上限としてこの値を固定する。
            Assert.Equal(10_000, InvokeResolveIdleListenPollMs(svc));
        }
        finally { svc.Dispose(); }
    }

    [Fact]
    public void inbox接続中は120秒間隔に戻り切断時刻もクリアされる()
    {
        var (svc, _) = CreateService();
        try
        {
            var watcher = Substitute.For<ISignalingService>();
            watcher.InboxConnected.Returns(true);
            SetField(svc, "_knockWatcher", watcher);
            SetField(svc, "_inboxDownSinceMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 300_000);

            // ノックが主経路に戻るので安全網は 120s。再切断時に fast 窓をやり直せるよう 0 に戻す。
            Assert.Equal(120_000, InvokeResolveIdleListenPollMs(svc));
            Assert.Equal(0, GetInboxDownSinceMs(svc));
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
