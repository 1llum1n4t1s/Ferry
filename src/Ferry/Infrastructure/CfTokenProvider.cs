using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ferry.Infrastructure;

/// <summary>
/// CF 単独完結: Workers <c>/auth/token</c> から自前 HMAC bearer (cfToken) を取得・refresh するクライアント。
///
/// cfToken は Worker が自前 HMAC で検証するので外部 ID プロバイダへのログインが要らず、idToken のような
/// SSE 再購読も不要。ECDSA P-256 IEEE P1363 署名チャレンジと 401 DEVICE_PUBKEY_MISMATCH 検出で本人性を担保する。
///
/// AOT セーフ: JWT を decode しない（uid は自分の deviceId として自明、exp はレスポンス値）。
/// JSON は <see cref="CfAuthJsonContext"/>（AuthTokenRequest / AuthTokenResponse / AuthErrorResponse）を使う。
/// </summary>
public sealed class CfTokenProvider : IDisposable
{
    private readonly DeviceIdentity _identity;
    private readonly string _deviceId;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _expiryMargin = TimeSpan.FromMinutes(2);
    private readonly TimeSpan _maxBackoff = TimeSpan.FromMinutes(5);
    private readonly Random _jitter = new();
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _signInSemaphore = new(1, 1);

    private sealed record TokenState(string Token, long ExpiresAtMs);
    private volatile TokenState? _token;

    /// <summary>rere レビュー #C-07: /auth/token が CLOCK_SKEW で返した serverTime から算出した補正値 (ms)。
    /// PC の時計が NTP 未同期・CMOS 電池切れ・デュアルブートの RTC 問題等でずれていても、
    /// 署名メッセージの ts をサーバー基準に寄せて認証を通す。</summary>
    private long _serverTimeOffsetMs;
    private CancellationTokenSource? _refreshCts;

    /// <summary>/auth/token が 401 DEVICE_PUBKEY_MISMATCH を返した通知（clean slate UI 用）。</summary>
    public event EventHandler? IdentityLost;

    public CfTokenProvider(DeviceIdentity identity, string deviceId, HttpClient? http = null)
    {
        _identity = identity;
        _deviceId = deviceId;
        _http = http ?? new HttpClient();
        _ownsHttp = http == null;
    }

    /// <summary>fresh な cfToken を返す。未取得または期限切れなら取得完了まで待つ。</summary>
    public async Task<string> GetCfTokenAsync()
    {
        // Relay の WebSocket callback など、呼び出し側が先に EnsureTokenAsync を呼ばない経路でも
        // 期限切れトークンを Bearer に載せない。EnsureTokenAsync は semaphore で直列化されるため、
        // 通常の signaling 呼び出しとの同時実行も既存契約のまま保てる。
        if (!IsFresh())
            await EnsureTokenAsync().ConfigureAwait(false);

        var t = _token;
        if (t == null || string.IsNullOrEmpty(t.Token) || !IsFresh())
            throw new InvalidOperationException("CfTokenProvider has no fresh token");
        return t.Token;
    }

    private bool IsFresh()
    {
        var t = _token;
        if (t == null || string.IsNullOrEmpty(t.Token)) return false;
        if (t.ExpiresAtMs <= 0) return true;
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < t.ExpiresAtMs - (long)_expiryMargin.TotalMilliseconds;
    }

    /// <summary>fresh な cfToken が無ければ取得する（冪等・semaphore 直列化）。完了時に refresh ループを起動。</summary>
    public async Task EnsureTokenAsync(CancellationToken ct = default)
    {
        if (IsFresh()) return;
        await _signInSemaphore.WaitAsync(ct);
        try
        {
            if (IsFresh()) return;
            var (token, expiresAtMs) = await SignInOnceAsync(ct);
            lock (_lock)
            {
                _token = new TokenState(token, expiresAtMs);
                StartRefreshLoop();
            }
        }
        finally { _signInSemaphore.Release(); }
    }

    /// <summary>
    /// rere レビュー #C-08: サーバーが 401 で拒否したトークンを破棄する。
    ///
    /// 旧実装は <see cref="IsFresh"/> が自クロックの有効期限だけを見ていたため、サーバー側が
    /// トークンを拒否している事実がクライアントの状態遷移に一切反映されなかった。
    /// SESSION_HMAC_SECRET をローテーションすると、稼働中クライアントは「fresh」なトークンを
    /// 持ち続けて全ルートが 401 のまま最大 50 分（refresh ループ周期）復旧しなかった。
    /// 401 を観測した呼び出し側がこれを呼ぶことで、次の EnsureTokenAsync が再取得に入る。
    /// </summary>
    public void InvalidateToken()
    {
        lock (_lock)
        {
            if (_token == null) return;
            _token = null;
        }
        Util.Logger.Log("cfToken をサーバー拒否(401)により破棄 → 次回リクエストで再取得", Util.LogLevel.Warning);
    }

    private async Task<(string Token, long ExpiresAtMs)> SignInOnceAsync(CancellationToken ct)
    {
        // rere レビュー #C-07: サーバーが CLOCK_SKEW とともに返す serverTime を反映する。
        // PC の時計がずれていると /auth/token が 400 を返し続け、cfToken が取れず
        // シグナリング・プレゼンス・ペアリング・リレーが全滅する（UI には専用の説明も出ない）。
        // サーバーは正しい時刻を渡してくれているので、それを offset として保持して再署名する。
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Volatile.Read(ref _serverTimeOffsetMs);
        var pubKeySpki = _identity.PublicKeyBase64Url;
        var message = $"ferry-auth-v1|{_deviceId}|{pubKeySpki}|{ts}";
        var sig = PairCrypto.ToBase64Url(_identity.Sign(System.Text.Encoding.UTF8.GetBytes(message)));

        var req = new AuthTokenRequest { DeviceId = _deviceId, PubKeySpki = pubKeySpki, Ts = ts, Sig = sig };
        using var resp = await _http.PostAsJsonAsync(
            AppConstants.WorkersAuthTokenUrl, req, CfAuthJsonContext.Default.AuthTokenRequest, ct);

        if (!resp.IsSuccessStatusCode)
        {
            AuthErrorResponse? err = null;
            try { err = await resp.Content.ReadFromJsonAsync(CfAuthJsonContext.Default.AuthErrorResponse, ct); }
            catch { /* body 無し */ }
            if (resp.StatusCode == HttpStatusCode.Unauthorized && err?.Error == "DEVICE_PUBKEY_MISMATCH")
            {
                Util.Logger.Log("CF Auth: identity 鍵紛失検出 → clean slate UI へ", Util.LogLevel.Warning);
                IdentityLost?.Invoke(this, EventArgs.Empty);
                throw new IdentityLostException(err.Message ?? "deviceId pubKey mismatch");
            }
            // #C-07: CLOCK_SKEW ならサーバー時刻との差を記録して次回以降の署名を補正する。
            // 補正しないと同じローカル時計で同じ ts を作り直すだけで、backoff 付きとはいえ永久に失敗する。
            if (err?.Error == "CLOCK_SKEW" && err.ServerTime is { } serverTime && serverTime > 0)
            {
                var offset = serverTime - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Volatile.Write(ref _serverTimeOffsetMs, offset);
                Util.Logger.Log(
                    $"CF Auth: PC の時計がサーバーと {offset} ms ずれています → 補正して再試行します",
                    Util.LogLevel.Warning);
            }
            throw new HttpRequestException($"/auth/token (cf) failed: {(int)resp.StatusCode} {err?.Error}: {err?.Message}");
        }

        var body = await resp.Content.ReadFromJsonAsync(CfAuthJsonContext.Default.AuthTokenResponse, ct);
        if (body == null || string.IsNullOrEmpty(body.CfToken))
            throw new HttpRequestException("/auth/token が cfToken を返しません（Worker の SESSION_HMAC_SECRET 未設定の可能性）");

        var expiresInSec = body.ExpiresIn > 0 ? body.ExpiresIn : 3600;
        var expiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresInSec * 1000L;
        // #C-17: 他 26 箇所は MaskDeviceId 済みなのにここだけ生の deviceId を Info で出していた
        Util.Logger.Log($"CF Auth 認証成功 uid={Util.Logger.MaskDeviceId(_deviceId)} (expiresIn={expiresInSec}s)");
        return (body.CfToken!, expiresAtMs);
    }

    private void StartRefreshLoop()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        _ = Task.Run(() => RefreshLoopAsync(_refreshCts.Token));
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            // 1h - 10min = 50min で refresh。失敗後は backoff。
            var wait = attempt == 0 ? TimeSpan.FromMinutes(50) : ComputeBackoff(attempt);
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { return; }
            try
            {
                var (token, expiresAtMs) = await SignInOnceAsync(ct);
                _token = new TokenState(token, expiresAtMs);
                attempt = 0;
            }
            catch (IdentityLostException) { return; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                attempt++;
                Util.Logger.Log($"CF Auth refresh 失敗 (attempt {attempt}): {ex.Message}", Util.LogLevel.Warning);
            }
        }
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        var baseSec = Math.Min(Math.Pow(2, attempt - 1), _maxBackoff.TotalSeconds);
        var jitter = 1.0 + (_jitter.NextDouble() * 0.5 - 0.25);
        return TimeSpan.FromSeconds(baseSec * jitter);
    }

    public void Dispose()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
        _signInSemaphore.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
