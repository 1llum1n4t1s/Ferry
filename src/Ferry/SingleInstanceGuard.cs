using System;
using System.Threading;

namespace Ferry;

/// <summary>
/// 多重起動防止（v1.0.47）。名前付き Mutex で「同一ユーザーセッションで Ferry は 1 つだけ」を保証する。
/// 2 つ目の起動を検知したら、既存インスタンスを前面化するよう通知（Windows のみ）してから終了させる。
/// </summary>
internal static class SingleInstanceGuard
{
    // 名前は接頭辞なし = セッションローカル名前空間（同一ユーザー・同一セッションで一意）。
    private const string MutexName = "Ferry-SingleInstance-Mutex-v1";
    private const string ActivateEventName = "Ferry-Activate-Event-v1";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activateEvent;

    /// <summary>
    /// 唯一のインスタンスとして起動できるかを判定する。
    /// 戻り値 true なら起動継続、false なら既に起動済み（呼び出し側で即終了する）。
    /// </summary>
    /// <remarks>
    /// Velopack の更新適用後の再起動では「旧プロセス終了 → 新プロセス起動」の間にわずかな重なりが
    /// 起こりうるため、初回取得に失敗しても最大 2 秒は取得を待つ（旧インスタンスの終了を待ってから諦める）。
    /// </remarks>
    public static bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName);
            bool acquired;
            try
            {
                acquired = _mutex.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (AbandonedMutexException)
            {
                // 前オーナーが解放せずに落ちた → このプロセスが所有権を取得した扱い
                acquired = true;
            }

            if (acquired)
                return true;

            // 既に別インスタンスが稼働中。前面化を通知して終了する。
            SignalExistingInstance();
            return false;
        }
        catch (Exception ex)
        {
            // Mutex 生成自体に失敗（権限・プラットフォーム差異など）したら、多重起動防止を諦めて起動を継続する
            // （アプリの基本機能を壊さない方を優先）。
            Ferry.Util.Logger.Log($"多重起動ガードの初期化に失敗（起動は継続）: {ex.Message}", Ferry.Util.LogLevel.Warning);
            return true;
        }
    }

    /// <summary>既存インスタンスへ「前面化して」シグナルを送る（Windows のみ。非対応プラットフォームでは何もしない）。</summary>
    private static void SignalExistingInstance()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
            {
                handle.Set();
                handle.Dispose();
            }
        }
        catch { /* 通知失敗は無視（2 つ目は終了するだけ） */ }
    }

    /// <summary>
    /// 既存（唯一の）インスタンス側で、2 つ目の起動シグナルを待ち受けるバックグラウンドリスナーを開始する。
    /// シグナルを受けるたびに <paramref name="onActivate"/> を呼ぶ（Windows のみ。他 OS では何もしない）。
    /// </summary>
    public static void StartActivationListener(Action onActivate)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }
        catch (Exception ex)
        {
            Ferry.Util.Logger.Log($"多重起動アクティベーション監視を開始できませんでした: {ex.Message}", Ferry.Util.LogLevel.Warning);
            return;
        }

        var thread = new Thread(() =>
        {
            var ev = _activateEvent;
            if (ev is null) return;
            while (true)
            {
                try
                {
                    ev.WaitOne();
                    onActivate();
                }
                catch { break; }
            }
        })
        {
            IsBackground = true,
            Name = "Ferry-ActivationListener",
        };
        thread.Start();
    }
}
