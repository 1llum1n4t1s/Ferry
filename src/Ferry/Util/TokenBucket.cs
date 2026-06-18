using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ferry.Util;

/// <summary>
/// シンプルなトークンバケットによるレート制限器。
/// 送信側 (SendChunksAsync) と受信側 (HandleFileChunk) の両方で使う。
///
/// バースト容量は 1 秒分 (rate と同じ) で固定。これにより:
///   - 完全に静止した直後でも 1 秒分は即座に投入できる (微小ファイルを律儀に遅らせない)
///   - 持続レートは厳密に rate で頭打ち (1 秒以上のスループットは超えられない)
///
/// レート 0 は「無制限」とみなし、Wait は即座に return する。
/// SetRate でいつでもレートを更新でき、進行中の転送にも次回 Wait から反映される。
/// </summary>
public sealed class TokenBucket
{
    /// <summary>1 秒あたりの補充バイト数。0 で無制限。Volatile で読み書きする。</summary>
    private long _ratePerSec;

    /// <summary>現在保有しているトークン (バイト数)。Reserve 呼び出しで _gate 排他下にあるとき以外は触らない。</summary>
    private double _tokens;

    /// <summary>最後に _tokens を補充した tick (ms)。0 は未初期化。</summary>
    private long _lastRefillTick;

    /// <summary>Reserve の同時呼び出し直列化用。複数転送並列でもトークン会計が壊れないようにする。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>現在のレートが「無制限」 (= 0) か。Wait 呼び出し側の早期 return 判定に使う。</summary>
    public bool IsUnlimited => Volatile.Read(ref _ratePerSec) == 0;

    /// <summary>レートを更新する。0 以下は無制限扱い。</summary>
    /// <param name="kbps">KB/s (1024 B/s 単位)。0 で無制限。</param>
    public void SetRate(int kbps)
    {
        var newRate = kbps <= 0 ? 0L : (long)kbps * 1024L;
        var oldRate = Interlocked.Exchange(ref _ratePerSec, newRate);
        if (oldRate == newRate) return; // 変化なしなら予約を巻き戻さない（無駄なバースト付与を防ぐ）

        // rere #B1-008: レート変更を「次回 Wait から反映」(docstring) させる。ReserveAndComputeDelay が
        // 並列累積のため _lastRefillTick を未来へ進めて予約するので、レートを下げた直後は旧レートの予約が
        // 残って新レートが長時間効かない。バケットを再初期化して予約を巻き戻し、新レートで律速し直す。
        _gate.Wait();
        try
        {
            _lastRefillTick = 0; // 次回 Refill で再初期化（新レートの 1 秒バースト容量から開始）
            _tokens = 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 指定バイト数の送受信許可を待つ。無制限なら即 return。
    /// 同期処理 (HandleFileChunk) からも呼べるよう非同期版と同期版の両方を提供する。
    /// </summary>
    public async Task WaitAsync(int bytes, CancellationToken ct = default)
    {
        if (bytes <= 0) return;
        var rate = Volatile.Read(ref _ratePerSec);
        if (rate == 0) return; // 無制限

        var delayMs = ReserveAndComputeDelay(bytes, rate);
        if (delayMs > 0)
            await Task.Delay(delayMs, ct);
    }

    /// <summary>
    /// 同期版。受信ハンドラのような void コールバックから使う。
    /// 内部で Thread.Sleep を使うため UI スレッドからは呼ばないこと (Ferry の受信ループは
    /// ConnectionService 側のバックグラウンドタスクなので問題ない)。
    /// </summary>
    public void Wait(int bytes, CancellationToken ct = default)
    {
        if (bytes <= 0) return;
        var rate = Volatile.Read(ref _ratePerSec);
        if (rate == 0) return;

        var delayMs = ReserveAndComputeDelay(bytes, rate);
        if (delayMs > 0)
        {
            // Thread.Sleep は ct を見ないので分割スリープで応答性を保つ
            var remaining = delayMs;
            while (remaining > 0 && !ct.IsCancellationRequested)
            {
                var step = Math.Min(remaining, 100);
                Thread.Sleep(step);
                remaining -= step;
            }
        }
    }

    /// <summary>トークンを補充して bytes を消費し、不足分の待ち時間 (ms) を返す。</summary>
    private int ReserveAndComputeDelay(int bytes, long rate)
    {
        // SemaphoreSlim.Wait は ct 無し版でデッドロックを避けるため短いタイムアウト付き
        // (ここで blocking しても上流に影響は少ない: チャンク 1 個 < 1ms)
        _gate.Wait();
        try
        {
            Refill(rate);
            if (_tokens >= bytes)
            {
                _tokens -= bytes;
                return 0;
            }

            // 不足分を時間に変換。tokens は 0 にして借金分は次回 Refill から控除される
            var deficit = bytes - _tokens;
            _tokens = 0;
            var waitMs = (int)Math.Ceiling(deficit / rate * 1000.0);
            // ローカル時計を「将来の到達時刻」に進めて、並列呼び出し間で待ち時間が累積するようにする
            // (同じ瞬間に 5 個並列が来ても、それぞれが順に "1 秒先" "2 秒先" の待ち時間を割り当てられる)
            _lastRefillTick += waitMs;
            return waitMs;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>経過時間に応じてトークンを補充する。容量は 1 秒分 (rate と同じ)。</summary>
    private void Refill(long rate)
    {
        var now = Environment.TickCount64;
        if (_lastRefillTick == 0)
        {
            _lastRefillTick = now;
            _tokens = rate; // 起動直後は 1 秒分のバースト容量を即与える
            return;
        }
        var dtMs = now - _lastRefillTick;
        if (dtMs <= 0) return;
        _lastRefillTick = now;
        var refilled = dtMs / 1000.0 * rate;
        _tokens = Math.Min(rate, _tokens + refilled);
    }
}
