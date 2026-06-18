using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ferry.Infrastructure;

/// <summary>
/// rere #D-001(a) Phase B: Workers /auth/token 経由で Firebase Custom Token を取得し、
/// Identity Toolkit signInWithCustomToken で idToken に交換するクライアント。
///
/// 起動シーケンス（App.axaml.cs から）:
///   1. <see cref="SignInAsync"/> 呼出 → /auth/token に署名チャレンジ
///   2. 401 DEVICE_PUBKEY_MISMATCH → <see cref="IdentityLost"/> イベントで MainWindow が clean slate モーダル表示
///   3. 200 → customToken → Firebase Identity Toolkit signInWithCustomToken → idToken
///   4. 50min バックグラウンドタイマーで再 SignIn → 新 idToken → <see cref="IdTokenRefreshed"/>
///
/// AsObservable (SSE long-stream) は接続時 idToken を使い続け 1h で expire → permission_denied で切断
/// するため、購読側は <see cref="IdTokenRefreshed"/> で Dispose → 再 Subscribe する。
///
/// AOT セーフ: System.IdentityModel.Tokens.Jwt は不採用（payload decode 不要、uid は自分の deviceId）。
/// JSON は <see cref="FirebaseAuthJsonContext"/> Source Generator 経由でリフレクション無し。
/// </summary>
public sealed class FirebaseAuthClient : IDisposable
{
    private readonly DeviceIdentity _identity;
    private readonly string _deviceId;
    private readonly HttpClient _http;
    private readonly TimeSpan _refreshAhead = TimeSpan.FromMinutes(10);  // 期限 1h - 10min で refresh
    private readonly TimeSpan _maxBackoff = TimeSpan.FromMinutes(5);
    private readonly Random _jitter = new();

    private volatile string? _idToken;
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshLoop;
    private readonly object _lock = new();

    /// <summary>新 idToken に切り替わった通知（AsObservable 購読の再 Subscribe トリガ）。</summary>
    public event EventHandler? IdTokenRefreshed;

    /// <summary>
    /// /auth/token が 401 DEVICE_PUBKEY_MISMATCH を返した通知。
    /// MainWindow は clean slate モーダル（[やり直す] で DeviceId 再生成 + peers.json reset）を表示する。
    /// </summary>
    public event EventHandler? IdentityLost;

    public FirebaseAuthClient(DeviceIdentity identity, string deviceId, HttpClient? http = null)
    {
        _identity = identity;
        _deviceId = deviceId;
        _http = http ?? new HttpClient();
    }

    /// <summary>現在の idToken（未認証なら null）。FirebaseSignaling の AuthTokenAsyncFactory から呼ばれる。</summary>
    public Task<string> GetIdTokenAsync()
    {
        var t = _idToken;
        if (string.IsNullOrEmpty(t))
            return Task.FromException<string>(new InvalidOperationException("FirebaseAuthClient not signed in yet"));
        return Task.FromResult(t);
    }

    /// <summary>初回サインイン。完了時に refresh ループを起動する。</summary>
    public async Task SignInAsync(CancellationToken ct = default)
    {
        var idToken = await SignInOnceAsync(ct);
        lock (_lock)
        {
            _idToken = idToken;
            StartRefreshLoop();
        }
    }

    /// <summary>
    /// 1 回分のサインイン。リトライ無し（呼出側 = refresh ループ がバックオフで再試行する）。
    /// 401 DEVICE_PUBKEY_MISMATCH 時は <see cref="IdentityLost"/> を発火して例外を投げる。
    /// </summary>
    private async Task<string> SignInOnceAsync(CancellationToken ct)
    {
        // 1. /auth/token に署名チャレンジ
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pubKeySpki = _identity.PublicKeyBase64Url;
        var message = $"ferry-auth-v1|{_deviceId}|{pubKeySpki}|{ts}";
        var sigBytes = _identity.Sign(System.Text.Encoding.UTF8.GetBytes(message));
        var sigB64 = PairCrypto.ToBase64Url(sigBytes);

        var req = new AuthTokenRequest
        {
            DeviceId = _deviceId,
            PubKeySpki = pubKeySpki,
            Ts = ts,
            Sig = sigB64,
        };
        using var resp = await _http.PostAsJsonAsync(
            AppConstants.WorkersAuthTokenUrl, req,
            FirebaseAuthJsonContext.Default.AuthTokenRequest, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await SafeReadErrorAsync(resp, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && err?.Error == "DEVICE_PUBKEY_MISMATCH")
            {
                Util.Logger.Log("Firebase Auth: identity 鍵紛失検出 → clean slate UI へ", Util.LogLevel.Warning);
                IdentityLost?.Invoke(this, EventArgs.Empty);
                throw new IdentityLostException(err.Message ?? "deviceId pubKey mismatch");
            }
            throw new HttpRequestException($"/auth/token failed: {(int)resp.StatusCode} {err?.Error}: {err?.Message}");
        }

        var tokenResp = await resp.Content.ReadFromJsonAsync(
            FirebaseAuthJsonContext.Default.AuthTokenResponse, ct);
        if (tokenResp == null || string.IsNullOrEmpty(tokenResp.CustomToken))
            throw new HttpRequestException("/auth/token returned empty customToken");

        // 2. Firebase Identity Toolkit signInWithCustomToken
        var signInUrl = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithCustomToken?key={AppConstants.FirebaseWebApiKey}";
        var signInReq = new SignInCustomTokenRequest { Token = tokenResp.CustomToken, ReturnSecureToken = true };
        using var signInResp = await _http.PostAsJsonAsync(
            signInUrl, signInReq,
            FirebaseAuthJsonContext.Default.SignInCustomTokenRequest, ct);
        if (!signInResp.IsSuccessStatusCode)
        {
            var body = await signInResp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"signInWithCustomToken failed: {(int)signInResp.StatusCode} {body}");
        }
        var signInBody = await signInResp.Content.ReadFromJsonAsync(
            FirebaseAuthJsonContext.Default.SignInCustomTokenResponse, ct);
        if (signInBody == null || string.IsNullOrEmpty(signInBody.IdToken))
            throw new HttpRequestException("signInWithCustomToken returned empty idToken");

        Util.Logger.Log($"Firebase Auth 認証成功 uid={_deviceId} (expiresIn={signInBody.ExpiresIn}s)");
        return signInBody.IdToken;
    }

    private async Task<AuthErrorResponse?> SafeReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadFromJsonAsync(FirebaseAuthJsonContext.Default.AuthErrorResponse, ct); }
        catch { return null; }
    }

    /// <summary>refresh ループ: 50min ごとに SignInOnceAsync を呼んで idToken を入れ替える。</summary>
    private void StartRefreshLoop()
    {
        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        _refreshLoop = Task.Run(() => RefreshLoopAsync(_refreshCts.Token));
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            // 通常は 1h - 10min = 50min 待ってから refresh
            var nextWait = TimeSpan.FromHours(1) - _refreshAhead;
            try { await Task.Delay(nextWait, ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                var newToken = await SignInOnceAsync(ct);
                _idToken = newToken;
                attempt = 0;
                IdTokenRefreshed?.Invoke(this, EventArgs.Empty);
            }
            catch (IdentityLostException) { return; }  // clean slate UI 発火済み・ループ終了
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                attempt++;
                var backoff = ComputeBackoff(attempt);
                Util.Logger.Log($"Firebase Auth refresh 失敗 (attempt {attempt}, backoff {backoff.TotalSeconds:F0}s): {ex.Message}", Util.LogLevel.Warning);
                try { await Task.Delay(backoff, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        // 指数バックオフ (1s, 2s, 4s, ..., 300s 上限) + jitter ±25%
        var baseSec = Math.Min(Math.Pow(2, attempt - 1), _maxBackoff.TotalSeconds);
        var jitter = 1.0 + (_jitter.NextDouble() * 0.5 - 0.25);
        return TimeSpan.FromSeconds(baseSec * jitter);
    }

    public void Dispose()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
    }
}

/// <summary>/auth/token 401 DEVICE_PUBKEY_MISMATCH をクライアント側で識別するための例外。</summary>
public sealed class IdentityLostException : Exception
{
    public IdentityLostException(string message) : base(message) { }
}

// === JSON DTO + Source Generator (AOT セーフ) ===

public sealed class AuthTokenRequest
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("pubKeySpki")] public string PubKeySpki { get; set; } = string.Empty;
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("sig")] public string Sig { get; set; } = string.Empty;
}

public sealed class AuthTokenResponse
{
    [JsonPropertyName("customToken")] public string CustomToken { get; set; } = string.Empty;
    [JsonPropertyName("expiresIn")] public int ExpiresIn { get; set; }
}

public sealed class AuthErrorResponse
{
    [JsonPropertyName("error")] public string Error { get; set; } = string.Empty;
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("serverTime")] public long? ServerTime { get; set; }
}

public sealed class SignInCustomTokenRequest
{
    [JsonPropertyName("token")] public string Token { get; set; } = string.Empty;
    [JsonPropertyName("returnSecureToken")] public bool ReturnSecureToken { get; set; }
}

public sealed class SignInCustomTokenResponse
{
    [JsonPropertyName("idToken")] public string IdToken { get; set; } = string.Empty;
    [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = string.Empty;
    [JsonPropertyName("expiresIn")] public string ExpiresIn { get; set; } = string.Empty;  // Identity Toolkit は string
}

[JsonSerializable(typeof(AuthTokenRequest))]
[JsonSerializable(typeof(AuthTokenResponse))]
[JsonSerializable(typeof(AuthErrorResponse))]
[JsonSerializable(typeof(SignInCustomTokenRequest))]
[JsonSerializable(typeof(SignInCustomTokenResponse))]
internal partial class FirebaseAuthJsonContext : JsonSerializerContext { }
