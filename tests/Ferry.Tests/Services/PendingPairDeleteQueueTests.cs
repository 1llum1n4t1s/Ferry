using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Ferry.Models;
using Ferry.Services;

namespace Ferry.Tests.Services;

/// <summary>
/// PendingPairDeleteQueue のユニットテスト（rere #D-001(a) Phase B §6.3）。
///
/// 検証観点:
///   - Enqueue / Process の挙動と永続化
///   - exponential backoff（即時 → 1min → 5min → ...）と打ち切り（RetryCount >= 5）
///   - delete callback が例外を投げても安全に retry 失敗として扱う
///   - ファイル破損時の自動退避（`.corrupt-*`）
/// </summary>
public sealed class PendingPairDeleteQueueTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public PendingPairDeleteQueueTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FerryTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "pending-pair-deletes.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private PendingPairDeleteQueue CreateQueue() => new(_filePath);

    // === Enqueue ===

    [Fact]
    public async Task EnqueueAsync_新規追加されCountが増える()
    {
        var q = CreateQueue();
        await q.EnqueueAsync("a_b");
        Assert.Equal(1, q.Count);
    }

    [Fact]
    public async Task EnqueueAsync_同じpairIdは重複追加せず既存をリセットする()
    {
        var q = CreateQueue();
        await q.EnqueueAsync("a_b");
        // 1 回 retry 失敗させて RetryCount を進める
        await q.ProcessAsync(_ => Task.FromResult(false));
        Assert.Equal(1, q.Count);

        // 再度 Enqueue → 既存項目を即時 retry 対象に戻す（重複追加しない）
        await q.EnqueueAsync("a_b");
        Assert.Equal(1, q.Count);

        // backoff 待たずに retry 動くはず（LastRetryAtMs=0 リセット効果）
        var attempted = false;
        await q.ProcessAsync(_ => { attempted = true; return Task.FromResult(true); });
        Assert.True(attempted);
        Assert.Equal(0, q.Count);
    }

    // === Process: 成功・失敗 ===

    [Fact]
    public async Task ProcessAsync_成功でキューから除去される()
    {
        var q = CreateQueue();
        await q.EnqueueAsync("a_b");
        await q.ProcessAsync(_ => Task.FromResult(true));
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public async Task ProcessAsync_失敗するとRetryCountが増える()
    {
        var q = CreateQueue();
        await q.EnqueueAsync("a_b");
        await q.ProcessAsync(_ => Task.FromResult(false));
        Assert.Equal(1, q.Count);

        // 保存ファイルから RetryCount=1 を確認
        var items = LoadItems();
        Assert.Single(items);
        Assert.Equal(1, items[0].RetryCount);
        Assert.True(items[0].LastRetryAtMs > 0);
    }

    [Fact]
    public async Task ProcessAsync_callback例外は失敗として扱う()
    {
        var q = CreateQueue();
        await q.EnqueueAsync("a_b");
        await q.ProcessAsync(_ => throw new InvalidOperationException("boom"));
        var items = LoadItems();
        Assert.Single(items);
        Assert.Equal(1, items[0].RetryCount);
    }

    // === Backoff ===

    [Fact]
    public async Task ProcessAsync_backoff未到達なら再試行をスキップする()
    {
        var q = CreateQueue();
        await q.EnqueueAsync("a_b");
        // 1 回失敗させて RetryCount=1 / LastRetryAtMs=now にする
        await q.ProcessAsync(_ => Task.FromResult(false));

        // backoff=60s 経過していない → 次の ProcessAsync では callback 呼ばれない
        var called = false;
        await q.ProcessAsync(_ => { called = true; return Task.FromResult(false); });
        Assert.False(called);

        // RetryCount は据置
        var items = LoadItems();
        Assert.Equal(1, items[0].RetryCount);
    }

    [Fact]
    public async Task ProcessAsync_5回以上失敗してもキューに残り続ける()
    {
        // Codex P2 fix (第2弾): 旧仕様は 5 回失敗で諦めてキューから消していたが、オフライン期間が
        // 2h 超だと「相手のペアを消す」というユーザー意図が永久に反映されなかった。打ち切り廃止後は
        // 諦めず 24h cap の backoff で永続 retry する。
        var q = CreateQueue();
        await q.EnqueueAsync("a_b");

        PendingPairDeleteQueue current = q;
        for (int i = 0; i < 6; i++)
        {
            await current.ProcessAsync(_ => Task.FromResult(false));
            ResetBackoff();
            current = CreateQueue();
        }

        Assert.Equal(1, current.Count);
        var items = LoadItems();
        Assert.Equal("a_b", items[0].PairId);
        Assert.True(items[0].RetryCount >= 6);
    }

    // === 永続化 ===

    [Fact]
    public async Task EnqueueAsync_次回起動でアイテムが復元される()
    {
        var q1 = CreateQueue();
        await q1.EnqueueAsync("a_b");
        await q1.EnqueueAsync("c_d");

        var q2 = CreateQueue();
        Assert.Equal(2, q2.Count);
    }

    [Fact]
    public void コンストラクタ_破損ファイル時は退避される()
    {
        File.WriteAllText(_filePath, "{ broken json");
        var q = CreateQueue();
        Assert.Equal(0, q.Count);
        // .corrupt-* 退避ファイルが作られていること
        var corrupted = Directory.GetFiles(_tempDir, "*.corrupt-*");
        Assert.Single(corrupted);
    }

    // === ヘルパ ===

    private System.Collections.Generic.List<PendingPairDelete> LoadItems()
    {
        var bytes = File.ReadAllBytes(_filePath);
        return JsonSerializer.Deserialize<System.Collections.Generic.List<PendingPairDelete>>(bytes)!;
    }

    /// <summary>
    /// 保存ファイル上の LastRetryAtMs をリセットして backoff を回避する（次回 Process が即実行されるように）。
    /// テスト用ヘルパ: 時間をモックする代わりにファイルを書き換える。
    /// </summary>
    private void ResetBackoff()
    {
        var items = LoadItems();
        foreach (var item in items) item.LastRetryAtMs = 0;
        File.WriteAllBytes(_filePath, JsonSerializer.SerializeToUtf8Bytes(items));
    }
}
