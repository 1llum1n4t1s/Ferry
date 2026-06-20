using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;
using Ferry.Services;
using NSubstitute;
using Xunit;

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

        await svc.CheckOnceAsync(applyGracePeriod: false, TestContext.Current.CancellationToken);

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
            // Codex 第14弾 #1 fix: 削除確定直前に pair を再 fetch する。SSoT が依然不在 (404) なら削除続行。
            (HttpStatusCode.NotFound, "null"),
        ]);

        for (int i = 0; i < 3; i++)
            await svc.CheckOnceAsync(applyGracePeriod: false, TestContext.Current.CancellationToken);

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
            // Codex 第14弾 #1 fix: 削除確定直前の再 fetch も 200+null (= 不在) なら削除続行。
            (HttpStatusCode.OK, "null"),
        ]);

        for (int i = 0; i < 3; i++)
            await svc.CheckOnceAsync(applyGracePeriod: false, TestContext.Current.CancellationToken);

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
            await svc.CheckOnceAsync(applyGracePeriod: false, TestContext.Current.CancellationToken);

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
            await svc.CheckOnceAsync(applyGracePeriod: false, TestContext.Current.CancellationToken);

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
            await svc.CheckOnceAsync(applyGracePeriod: false, TestContext.Current.CancellationToken);

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
            // Codex 第14弾 #1 fix: 削除確定直前の再 fetch (依然不在) → 削除続行。
            (HttpStatusCode.NotFound, "null"),
        ]);

        for (int i = 0; i < 4; i++)
            await svc.CheckOnceAsync(applyGracePeriod: false, TestContext.Current.CancellationToken);

        await registry.Received(1).RemovePeerAsync(PeerId);
    }

    // === Codex 第14弾 #1: 削除直前の SSoT 復活検出 ===

    [Fact]
    public async Task CheckOnceAsync_削除直前にSSoTが復活していたら削除しない()
    {
        // 3 連続 404 で削除条件は満たすが、削除確定直前の再 fetch で 200 (非 null) を観測したら
        // 「同一 peer 再ペアで pairs/{pairId} が recreate された」とみなして削除を中止する。
        var registry = SubstituteRegistry();
        var svc = CreateServiceFromQueue(registry, [
            (HttpStatusCode.NotFound, "null"),         // 404: 1/3
            (HttpStatusCode.NotFound, "null"),         // 404: 2/3
            (HttpStatusCode.NotFound, "null"),         // 404: 3/3 → 削除判定へ
            (HttpStatusCode.OK, "{\"PairId\":\"alice_bob\"}"),  // 再 fetch: SSoT 復活 → 削除中止
        ]);

        for (int i = 0; i < 3; i++)
            await svc.CheckOnceAsync(applyGracePeriod: false, TestContext.Current.CancellationToken);

        await registry.DidNotReceive().RemovePeerAsync(Arg.Any<string>());
    }

    // === Codex 第15弾 #1: 削除直前の再 fetch が不明 (401/5xx) なら削除しない ===

    [Fact]
    public async Task CheckOnceAsync_削除直前の再fetchが不明なら削除を保留する()
    {
        // 3 連続 404 で削除条件は満たすが、削除確定直前の再 fetch が 401/5xx (= 不在を確証できない) を
        // 返したら削除しない。 一過性のトークン期限切れ・サーバ障害の瞬間に正当ペアを誤削除し、
        // 削除後は次サイクルで列挙されず復旧不能になるのを防ぐ。
        var registry = SubstituteRegistry();
        var svc = CreateServiceFromQueue(registry, [
            (HttpStatusCode.NotFound, "null"),                  // 404: 1/3
            (HttpStatusCode.NotFound, "null"),                  // 404: 2/3
            (HttpStatusCode.NotFound, "null"),                  // 404: 3/3 → 削除判定へ
            (HttpStatusCode.ServiceUnavailable, ""),            // 再 fetch: 不明 → 削除保留
        ]);

        for (int i = 0; i < 3; i++)
            await svc.CheckOnceAsync(applyGracePeriod: false, TestContext.Current.CancellationToken);

        await registry.DidNotReceive().RemovePeerAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task CheckOnceAsync_再fetch不明で保留後の次サイクルで確証404なら削除する()
    {
        // #1 fix はカウンタを温存するので、 再 fetch 不明で保留した次サイクルで改めて 404 → 再 fetch 404
        // (= 不在確証) が得られたら削除に進む。
        var registry = SubstituteRegistry();
        var svc = CreateServiceFromQueue(registry, [
            (HttpStatusCode.NotFound, "null"),                  // 404: 1/3
            (HttpStatusCode.NotFound, "null"),                  // 404: 2/3
            (HttpStatusCode.NotFound, "null"),                  // 404: 3/3 → 削除判定へ
            (HttpStatusCode.ServiceUnavailable, ""),            // 再 fetch: 不明 → 削除保留 (カウンタ温存)
            (HttpStatusCode.NotFound, "null"),                  // 次サイクル 404: 4 件目 → 閾値維持で削除判定へ
            (HttpStatusCode.NotFound, "null"),                  // 再 fetch: 不在確証 → 削除続行
        ]);

        for (int i = 0; i < 4; i++)
            await svc.CheckOnceAsync(applyGracePeriod: false, TestContext.Current.CancellationToken);

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
            await svc.CheckOnceAsync(applyGracePeriod: true, TestContext.Current.CancellationToken);

        await registry.DidNotReceive().RemovePeerAsync(Arg.Any<string>());
    }

    // === Codex P1 fix (第3弾): 未観察 peer の deferral ===

    [Fact]
    public async Task CheckOnceAsync_未観察peerは3回連続404でも削除しない()
    {
        // PairsSsotObserved=false (legacy peer) は責任者側 PC がまだ upgrade してないだけかもしれないので、
        // 非責任者側で 3 連続 404 になっても削除しない。SSoT 観察 or 明示 DELETE まで永続化する。
        var registry = Substitute.For<IPeerRegistryService>();
        registry.GetPairedPeers().Returns(new List<PairedPeer>
        {
            new() { PeerId = PeerId, DisplayName = "Bob", PairsSsotObserved = false },
        });
        // 1 件目を OK 200 で先に観察済みにできない (putPair=null のテスト ctor)。
        // 全て NotFound にして 5 回まで回しても、未観察フラグが立たない限り deferral される。
        var svc = CreateServiceFromQueue(registry, [
            (HttpStatusCode.NotFound, "null"),
            (HttpStatusCode.NotFound, "null"),
            (HttpStatusCode.NotFound, "null"),
            (HttpStatusCode.NotFound, "null"),
            (HttpStatusCode.NotFound, "null"),
        ]);

        for (int i = 0; i < 5; i++)
            await svc.CheckOnceAsync(applyGracePeriod: false, TestContext.Current.CancellationToken);

        await registry.DidNotReceive().RemovePeerAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task CheckOnceAsync_観察済peerは200で観察フラグを永続化する()
    {
        // OK 200 を返したら PairsSsotObserved=true を永続化する (Codex 第15弾 #2: UpdatePeerIfPresentAsync 経由)。
        var peer = new PairedPeer { PeerId = PeerId, DisplayName = "Bob", PairsSsotObserved = false };
        var registry = Substitute.For<IPeerRegistryService>();
        registry.GetPairedPeers().Returns(new List<PairedPeer> { peer });
        // Codex 第15弾 #2 fix: 観察フラグの永続化は update-only API に変更。 peer が存在すれば true を返す。
        registry.UpdatePeerIfPresentAsync(peer).Returns(Task.FromResult(true));
        var svc = CreateServiceFromQueue(registry, [(HttpStatusCode.OK, "{\"a\":1}")]);

        await svc.CheckOnceAsync(applyGracePeriod: false, TestContext.Current.CancellationToken);

        Assert.True(peer.PairsSsotObserved);
        await registry.Received(1).UpdatePeerIfPresentAsync(peer);
        await registry.DidNotReceive().AddOrUpdatePeerAsync(Arg.Any<PairedPeer>());
    }

    // === ヘルパ ===

    /// <summary>
    /// Codex P1 fix (第3弾): 既定 peer は PairsSsotObserved=true（= SSoT を 1 度観察済み）にしておく。
    /// 未観察 peer の挙動は <see cref="CheckOnceAsync_未観察peerは3回連続404でも削除しない"/> で別途検証。
    /// </summary>
    private static IPeerRegistryService SubstituteRegistry(params PairedPeer[] peers)
    {
        var registry = Substitute.For<IPeerRegistryService>();
        var list = peers.Length == 0
            ? new List<PairedPeer> { new() { PeerId = PeerId, DisplayName = "Bob", PairsSsotObserved = true } }
            : new List<PairedPeer>(peers);
        registry.GetPairedPeers().Returns(list);
        // Codex 第9弾 #6 fix: PairSyncService が AddOrUpdatePeerAsync 前に FindPeer で peer 存在を再 check する
        // 仕様になったため、 GetPairedPeers と整合して FindPeer も同じ peer を返すよう mock する。
        // (テスト前提では peers は manual remove されないので、 GetPairedPeers と FindPeer は同期)
        foreach (var p in list)
        {
            registry.FindPeer(p.PeerId).Returns(p);
        }
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
