namespace Ferry.Models;

/// <summary>
/// rere #D-001(a) Phase B §6.3: Firebase 側のペア削除（pairs/{pairId} DELETE）が
/// オフライン中に失敗した時に永続化される再試行アイテム。
/// `%APPDATA%\Ferry\pending-pair-deletes.json` に保存され、起動時に処理される。
/// </summary>
public sealed class PendingPairDelete
{
    /// <summary>削除対象の pairId（Ordinal 小さい方 + "_" + 大きい方）。</summary>
    public string PairId { get; set; } = string.Empty;

    /// <summary>最後に retry を試みた時刻（unix ms）。次回 retry 判定に使う。</summary>
    public long LastRetryAtMs { get; set; }

    /// <summary>これまでに retry を試みた回数。exponential backoff の指数に使う（打ち切りは無し。
    /// <see cref="Ferry.Services.PendingPairDeleteQueue"/> 参照）。</summary>
    public int RetryCount { get; set; }
}
