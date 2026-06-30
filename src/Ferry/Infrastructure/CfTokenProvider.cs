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
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signInSemaphore = new(1, 1);

    private sealed record TokenState(string Token, long ExpiresAtMs);
    private volatile TokenState? _token;
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

    /// <summary>現在の cfToken（未取得なら例外）。CloudflareSignaling の Bearer 注入から呼ばれる。</summary>
    public Task<string> GetCfTokenAsync()
    {
        var t = _token;
        if (t == null || string.IsNullOrEmpty(t.Token))
            return Task.FromException<string>(new InvalidOperationException("CfTokenProvider not signed in yet"));
        return Task.FromResult(t.Token);
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

    private async Task<(string Token, long ExpiresAtMs)> SignInOnceAsync(CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
            throw new HttpRequestException($"/auth/token (cf) failed: {(int)resp.StatusCode} {err?.Error}: {err?.Message}");
        }

        var body = await resp.Content.ReadFromJsonAsync(CfAuthJsonContext.Default.AuthTokenResponse, ct);
        if (body == null || string.IsNullOrEmpty(body.CfToken))
            throw new HttpRequestException("/auth/token が cfToken を返しません（Worker の SESSION_HMAC_SECRET 未設定の可能性）");

        var expiresInSec = body.ExpiresIn > 0 ? body.ExpiresIn : 3600;
        var expiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresInSec * 1000L;
        Util.Logger.Log($"CF Auth 認証成功 uid={_deviceId} (expiresIn={expiresInSec}s)");
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
