using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;
using Ferry.Services;
using NSubstitute;

namespace Ferry.Tests.Services;

/// <summary>
/// PairSyncService の robustness ロジックを単体検証する（rere #D-001(a) Phase B §6.2）。
///
/// 検証観点（peers.json 全消失の不可逆破壊を防ぐ防御策）:
///   - 404 を <see cref="PairSyncService"/> の閾値 (Consecutive404Threshold=3) 連続で受けて初めて削除
///   - 401/403/5xx は『不明』として未操作（カウンタもリセットしない）
///   - 200 で値ありなら 404 カウンタをリセット
///   - 200 + body="null" は 404 と同じ扱い（Firebase REST の挙動）
///   - applyGracePeriod=true の起動直後 5min は 404 でも削除しない
///
/// FirebaseSignaling は sealed のため、内部 fetchPair デリゲートを直接差し替えるテスト用 ctor を使う。
/// </summary>
public sealed class PairSyncServiceTests
{
    private const string DeviceId = "alice";
    private const string PeerId = "bob";
    // GeneratePairId(alice,bob) = "alice_bob" (Ordinal: a < b)
    private const string ExpectedPairId = "alice_bob";

    // === 404 連続閾値 ===

    [Fact]
    public async Task CheckOnceAsync_404を1回受けてもローカル削除しない()
    {
        var registry = SubstituteRegistry();
        var svc = CreateServiceFromQueue(registry, [(HttpStatusCode.NotFound, "null")]);

        await svc.CheckOnceAsync(applyGracePeriod: false, CancellationToken.None);

        await registry.DidNotReceive().RemovePeerAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task CheckOnceAsync_404を3回連続で受けたらローカル削除する()
    {
        var registry = SubstituteRegistry();
        var svc = CreateServiceFromQueue(registry, [
            (HttpStatusCode.NotFound, "null"),
            (HttpStatusCode.NotFound, "null"),
            (HttpStatusCode.NotFound, "null"),
        ]);

        for (int i = 0; i < 3; i++)
            await svc.CheckOnceAsync(applyGracePeriod: false, CancellationToken.None);

        await registry.Received(1).RemovePeerAsync(PeerId);
    }

    [Fact]
    public async Task CheckOnceAsync_200_null_body_も404と同じ扱い()
    {
        var registry = SubstituteRegistry();
        // Firebase REST は存在しないキーを 200 + "null" で返すケースがある
        var svc = CreateServiceFromQueue(registry, [
            (HttpStatusCode.OK, "null"),
            (HttpStatusCode.OK, "null"),
            (HttpStatusCode.OK, "null"),
        ]);

        for (int i = 0; i < 3; i++)
            await svc.CheckOnceAsync(applyGracePeriod: false, CancellationToken.None);

        await registry.Received(1).RemovePeerAsync(PeerId);
    }

    // === カウンタリセット ===

    [Fact]
    public async Task CheckOnceAsync_途中で200が来たら404カウンタはリセットされる()
    {
        var registry = SubstituteRegistry();
        var svc = CreateServiceFromQueue(registry, [
            (HttpStatusCode.NotFound, "null"),   // 1/3
            (HttpStatusCode.NotFound, "null"),   // 2/3
            (HttpStatusCode.OK, "{\"a\":1}"),    // reset
            (HttpStatusCode.NotFound, "null"),   // 1/3 (再開)
            (HttpStatusCode.NotFound, "null"),   // 2/3
        ]);

        for (int i = 0; i < 5; i++)
            await svc.CheckOnceAsync(applyGracePeriod: false, CancellationToken.None);

        // 5 回中 2 連続 + リセット + 2 連続 = どれも閾値未達 → 削除されない
        await registry.DidNotReceive().RemovePeerAsync(Arg.Any<string>());
    }

    // === 401/403/5xx は不明扱い ===

    [Fact]
    public async Task CheckOnceAsync_401はカウンタを進めずローカル削除しない()
    {
        var registry = SubstituteRegistry();
        var svc = CreateServiceFromQueue(registry, [
            (HttpStatusCode.Unauthorized, ""),
            (HttpStatusCode.Unauthorized, ""),
            (HttpStatusCode.Unauthorized, ""),
            (HttpStatusCode.Unauthorized, ""),
            (HttpStatusCode.Unauthorized, ""),
        ]);

        for (int i = 0; i < 5; i++)
            await svc.CheckOnceAsync(applyGracePeriod: false, CancellationToken.None);

        await registry.DidNotReceive().RemovePeerAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task CheckOnceAsync_500はカウンタを進めずローカル削除しない()
    {
        var registry = SubstituteRegistry();
        var svc = CreateServiceFromQueue(registry, [
            (HttpStatusCode.InternalServerError, ""),
            (HttpStatusCode.InternalServerError, ""),
            (HttpStatusCode.InternalServerError, ""),
            (HttpStatusCode.InternalServerError, ""),
        ]);

        for (int i = 0; i < 4; i++)
            await svc.CheckOnceAsync(applyGracePeriod: false, CancellationToken.None);

        await registry.DidNotReceive().RemovePeerAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task CheckOnceAsync_401と404が混じった場合は404カウンタも温存される()
    {
        var registry = SubstituteRegistry();
        // 401 は『不明』なので 404 カウンタを進めも戻しもしない
        var svc = CreateServiceFromQueue(registry, [
            (HttpStatusCode.NotFound, "null"),         // 404: 1/3
            (HttpStatusCode.Unauthorized, ""),         // 不明: そのまま
            (HttpStatusCode.NotFound, "null"),         // 404: 2/3
            (HttpStatusCode.NotFound, "null"),         // 404: 3/3 → 削除
        ]);

        for (int i = 0; i < 4; i++)
            await svc.CheckOnceAsync(applyGracePeriod: false, CancellationToken.None);

        await registry.Received(1).RemovePeerAsync(PeerId);
    }

    // === Grace period ===

    [Fact]
    public async Task CheckOnceAsync_起動直後はgracePeriodで削除しない()
    {
        var registry = SubstituteRegistry();
        var svc = CreateServiceFromQueue(registry, [
            (HttpStatusCode.NotFound, "null"),
            (HttpStatusCode.NotFound, "null"),
            (HttpStatusCode.NotFound, "null"),
            (HttpStatusCode.NotFound, "null"),
        ]);

        // applyGracePeriod: true で 4 回連続 404 でも削除されない (起動直後 5min 内のため)
        for (int i = 0; i < 4; i++)
            await svc.CheckOnceAsync(applyGracePeriod: true, CancellationToken.None);

        await registry.DidNotReceive().RemovePeerAsync(Arg.Any<string>());
    }

    // === ヘルパ ===

    private static IPeerRegistryService SubstituteRegistry(params PairedPeer[] peers)
    {
        var registry = Substitute.For<IPeerRegistryService>();
        registry.GetPairedPeers().Returns(peers.Length == 0
            ? new List<PairedPeer> { new() { PeerId = PeerId, DisplayName = "Bob" } }
            : new List<PairedPeer>(peers));
        return registry;
    }

    /// <summary>
    /// 戻り値のキューを順に返す fetchPair モックを使って PairSyncService を作る。
    /// CheckOnceAsync を呼ぶたびに pairId 1 件 = キュー 1 件消費される想定。
    /// </summary>
    private static PairSyncService CreateServiceFromQueue(
        IPeerRegistryService registry,
        (HttpStatusCode Status, string Body)[] responses)
    {
        var queue = new Queue<(HttpStatusCode, string)>(responses);
        return new PairSyncService(
            fetchPair: (pairId, _) =>
            {
                Assert.Equal(ExpectedPairId, pairId);
                if (queue.Count == 0) throw new InvalidOperationException("fetchPair 呼出が想定数を超えた");
                return Task.FromResult(queue.Dequeue());
            },
            peerRegistry: registry,
            deviceId: DeviceId);
    }
}
