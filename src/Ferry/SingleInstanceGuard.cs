using System;
using System.IO.Pipes;
using System.Linq;
using System.Threading;

namespace Ferry;

/// <summary>
/// 多重起動防止（v1.0.47 で導入、後にクロスプラットフォーム化）。
/// 名前付き Mutex で「同一ユーザーで Ferry は 1 つだけ」を保証し、2 つ目の起動を検知したら
/// 既存インスタンスへ Named Pipe 経由で前面化シグナルを送ってから終了させる。
/// Mutex も Named Pipe も .NET 上で Windows / macOS / Linux すべてに対応するため、全 OS で
/// 「2 個目を起動 → 既存ウィンドウが前面化」が対称に動作する（旧実装は前面化が Windows 限定だった）。
/// </summary>
internal static class SingleInstanceGuard
{
    private static Mutex? _mutex;

    /// <summary>
    /// ユーザーごとに一意な接尾辞。Unix では Mutex / Named Pipe とも /tmp 配下の共有名前空間に
    /// 実体が置かれるため、マシン上の別ユーザー（Fast User Switching / マルチセッション）と
    /// 衝突しないようユーザー名を混ぜる。Windows でも無害（セッションローカル名前空間のまま）。
    /// </summary>
    private static string UserSuffix()
    {
        var user = Environment.UserName ?? "default";
        // 名前/Unix ソケットパスに安全な英数字のみへ正規化
        var safe = new string(user.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrEmpty(safe) ? "default" : safe;
    }

    /// <summary>多重起動検出用の Mutex 名（ユーザーごとに一意）。</summary>
    private static string MutexName() => $"Ferry-SingleInstance-{UserSuffix()}-v1";

    /// <summary>前面化シグナル用の Named Pipe 名（ユーザーごとに一意）。</summary>
    private static string PipeName() => $"Ferry-Activate-{UserSuffix()}-v1";

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
            _mutex = new Mutex(initiallyOwned: false, MutexName());
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

    /// <summary>既存インスタンスへ Named Pipe 経由で「前面化して」シグナルを送る（全 OS 対応）。</summary>
    private static void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(), PipeDirection.Out);
            client.Connect(1000);
            client.WriteByte(1);
            client.Flush();
        }
        catch { /* 通知失敗は無視（2 つ目は終了するだけ） */ }
    }

    /// <summary>
    /// 既存（唯一の）インスタンス側で、2 つ目の起動シグナルを待ち受けるバックグラウンドリスナーを開始する。
    /// シグナルを受けるたびに <paramref name="onActivate"/> を呼ぶ（Windows / macOS / Linux すべて対応）。
    /// </summary>
    public static void StartActivationListener(Action onActivate)
    {
        var thread = new Thread(() =>
        {
            // 接続を 1 件ずつ受理 → 1 byte 読んで前面化 → サーバーを作り直して待機、を繰り返す。
            // 連続して生成に失敗した場合のみ諦める（パイプ未対応環境での暴走ループ防止）。
            var consecutiveFailures = 0;
            while (consecutiveFailures < 5)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName(), PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    var b = server.ReadByte();
                    consecutiveFailures = 0;
                    if (b >= 0)
                        onActivate();
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    Ferry.Util.Logger.Log(
                        $"多重起動アクティベーション監視でエラー（{consecutiveFailures}/5）: {ex.Message}",
                        Ferry.Util.LogLevel.Warning);
                    // 失敗時は軽くスリープして CPU スピンを避ける
                    try { Thread.Sleep(500); } catch { /* 無視 */ }
                }
            }
            Ferry.Util.Logger.Log("多重起動アクティベーション監視を停止しました", Ferry.Util.LogLevel.Warning);
        })
        {
            IsBackground = true,
            Name = "Ferry-ActivationListener",
        };
        thread.Start();
    }
}
