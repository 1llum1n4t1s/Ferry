using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;
using Ferry.Services;

namespace Ferry.Infrastructure;

/// <summary>
/// CF 単独完結: Cloudflare Workers + Durable Objects + D1 を使うシグナリング実装（<see cref="ISignalingService"/>）。
///
/// - signaling(offer/answer/endpoint/probe): PairDO へ HTTP polling（律速は STUN/ホールパンチで poll 維持が妥当）
/// - presence: DeviceDO へ poll + ETag/304（帯域節約）
/// - pairing 成立通知: DeviceDO への WebSocket(/inbox) で真 push（QR スキャン直後の即時反映）
/// - pairs SSoT: D1（Worker 経由）。404 で remote-unpair 検出
///
/// 認可は <see cref="CfTokenProvider"/> の cfToken を Authorization: Bearer で全リクエストに注入。
/// AOT セーフ: plain HttpClient + System.Text.Json SourceGen（FirebaseDatabase.net 不使用）。
/// </summary>
public sealed class CloudflareSignaling : ISignalingService
{
    private readonly CfTokenProvider _tokens;
    private readonly string _deviceId;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private string _sessionId = string.Empty;
    private string _lastPairingNonce = string.Empty;

    private CancellationTokenSource? _inboxCts;
    private Task? _inboxLoop;
    private long _pairingWatchStartedAtMs;

    /// <summary>presence ETag キャッシュ（peerId → (ETag, LastSeen)）。Firebase 版と同じ帯域節約。</summary>
    private readonly ConcurrentDictionary<string, (string ETag, long LastSeen)> _presenceCache = new();

    public event EventHandler<PairingInfo>? PairingDetected;

    public string LastPairingNonce => _lastPairingNonce;

    public CloudflareSignaling(CfTokenProvider tokens, string deviceId, HttpClient? http = null)
    {
        _tokens = tokens;
        _deviceId = deviceId;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = http == null;
    }

    // ===== HTTP コア（Bearer 注入） =====

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? jsonBody, CancellationToken ct, string? ifNoneMatch = null)
    {
        await _tokens.EnsureTokenAsync(ct);
        var token = await _tokens.GetCfTokenAsync();
        using var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        if (ifNoneMatch != null) req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        if (jsonBody != null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
    }

    private static string Url(string path) => AppConstants.CfApiBaseUrl + path;
    private static string Esc(string s) => Uri.EscapeDataString(s);

    /// <summary>severe（認証/サーバ/枠超過）なら backoff 対象。404/未着は高速ポーリング継続。</summary>
    private static bool IsSevere(HttpStatusCode code)
    {
        var n = (int)code;
        return n == 401 || n == 403 || n == 429 || n >= 500;
    }

    // ===== ペアリング =====

    public async Task<string> RegisterSessionAsync(string deviceId, string displayName, string publicKey = "", CancellationToken ct = default)
    {
        _sessionId = deviceId;
        var nonce = Guid.NewGuid().ToString("N");
        _lastPairingNonce = nonce;
        var body = JsonSerializer.Serialize(
            new SessionRegisterDto { DisplayName = displayName, PublicKey = publicKey, PairingNonce = nonce },
            CfJsonContext.Default.SessionRegisterDto);
        using var resp = await SendAsync(HttpMethod.Post, Url($"/pair/session"), body, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"/pair/session 登録失敗: HTTP {(int)resp.StatusCode}");
        Util.Logger.Log($"セッション登録(CF): {Util.Logger.MaskDeviceId(_sessionId)}");
        return _sessionId;
    }

    /// <summary>
    /// PC コード貼付ペアリング（QR/Bridge を介さずアプリ内でコードを貼り付けて直接ペア成立させる経路）。
    /// CF 単独完結 Step 7: relay Worker の POST /pair/link を叩く。認可は「自分の bearer (cfToken) で
    /// sidA=本人をサーバーが保証 + 相手 (sidB) はセッションが現在アクティブであることだけを要求」
    /// （相手の nonce 値の所有は不要）。旧 Firebase rules の `pairing_nonces/{sidB}.exists()` 条件と
    /// 同じセキュリティレベルで、QR/Bridge 専用の /pair/create（両 nonce 値所有）とは別の認可経路。
    /// sidA 引数はサーバーに送らない（claims.deviceId のみを信頼の源にするため。呼び出し元
    /// ConnectionService は常に自分の _deviceId を渡す契約）。
    /// </summary>
    public async Task SubmitPairingAsync(string sidA, string nameA, string sidB, string nameB, string pkA = "", string pkB = "", CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(
            new PairLinkPostDto { SidB = sidB, NameA = nameA, NameB = nameB, PkA = pkA, PkB = pkB },
            CfJsonContext.Default.PairLinkPostDto);
        using var resp = await SendAsync(HttpMethod.Post, Url("/pair/link"), body, ct);
        if (resp.IsSuccessStatusCode) return;

        var errorCode = await TryReadErrorCodeAsync(resp, ct);
        throw new HttpRequestException(errorCode switch
        {
            "SESSION_NOT_FOUND" or "EXPIRED_SESSION" =>
                "ペアリング先のセッションが見つかりません。相手の PC でアプリが起動していることを確認してください。",
            "SAME_SID" => "これは自分の PC のコードです。もう片方の PC のコードを貼り付けてください。",
            "BAD_TOKEN" => "認証に失敗しました。アプリを再起動して再試行してください。",
            _ => $"ペアリングに失敗しました (HTTP {(int)resp.StatusCode})。",
        });
    }

    /// <summary>relay Worker のエラーレスポンス（<c>{ error, message }</c>）からエラーコードだけ取り出す。
    /// DTO は <see cref="AuthErrorResponse"/>（CfTokenProvider と共有、二重定義しない）。</summary>
    private static async Task<string?> TryReadErrorCodeAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            var dto = JsonSerializer.Deserialize(json, CfAuthJsonContext.Default.AuthErrorResponse);
            return dto?.Error;
        }
        catch { return null; }
    }

    public async Task<(bool Exists, string? DisplayName, string? PublicKey)> CheckSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, Url($"/pair/session/{Esc(sessionId)}"), null, ct);
            if (resp.StatusCode != HttpStatusCode.OK) return (false, null, null);
            var json = await resp.Content.ReadAsStringAsync(ct);
            var dto = JsonSerializer.Deserialize(json, CfJsonContext.Default.SessionGetDto);
            if (dto == null) return (false, null, null);
            return (true, dto.DisplayName, dto.PublicKey);
        }
        catch { return (false, null, null); }
    }

    public void StartWatchingPairing()
    {
        StopWatching();
        _pairingWatchStartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _inboxCts = new CancellationTokenSource();
        _inboxLoop = Task.Run(() => InboxLoopAsync(_inboxCts.Token));
    }

    public void StopWatching()
    {
        try { _inboxCts?.Cancel(); } catch { }
        _inboxCts?.Dispose();
        _inboxCts = null;
        _inboxLoop = null;
    }

    /// <summary>/inbox WebSocket を張り、ペア成立イベントを受けて <see cref="PairingDetected"/> を発火する。
    /// 指数バックオフで再接続（idToken のような expire 再購読は不要・cfToken 失効は次接続で新 token を使う）。</summary>
    private async Task InboxLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _tokens.EnsureTokenAsync(ct);
                var token = await _tokens.GetCfTokenAsync();
                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
                await ws.ConnectAsync(new Uri(AppConstants.CfInboxWsUrl), ct);
                attempt = 0;
                Util.Logger.Log("CF inbox WebSocket 接続成立", Util.LogLevel.Debug);

                var buf = new byte[16 * 1024];
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                            break;
                        }
                        sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    HandleInboxMessage(sb.ToString());
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                attempt++;
                var backoff = Math.Min(Math.Pow(2, attempt - 1), 30);
                Util.Logger.Log($"CF inbox WS 切断（{backoff:F0}s 後に再接続）: {ex.Message}", Util.LogLevel.Debug);
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private void HandleInboxMessage(string json)
    {
        InboxEventDto? e;
        try { e = JsonSerializer.Deserialize(json, CfJsonContext.Default.InboxEventDto); }
        catch { return; }
        if (e == null) return;

        // unpair 通知は将来クライアント側 peer 削除に配線する（現状は PairSyncService の GET ポーリングが拾う）。
        if (string.Equals(e.Type, "unpair", StringComparison.Ordinal)) return;

        // 自分が当事者で、subscribe 開始 -60s tolerance より新しいものだけ採用（Firebase の replay gate と同じ）。
        if (e.SidA != _sessionId && e.SidB != _sessionId) return;
        if (e.CreatedAt < _pairingWatchStartedAtMs - 60_000) return;

        var isA = e.SidA == _sessionId;
        Util.Logger.Log($"ペアリング検知(CF): {e.PairingId}");
        PairingDetected?.Invoke(this, new PairingInfo
        {
            PairingId = e.PairingId,
            PeerId = isA ? e.SidB : e.SidA,
            PeerDisplayName = isA ? e.NameB : e.NameA,
            IsInitiator = isA,
            PeerPublicKey = isA ? e.PkB : e.PkA,
        });
    }

    // ===== SDP / ICE シグナリング（PairDO へ poll） =====

    public async Task SendSdpOfferAsync(string pairId, string senderDeviceId, string sdp, CancellationToken ct = default)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var body = JsonSerializer.Serialize(new OfferPostDto { Sdp = sdp, CreatedAt = nowMs }, CfJsonContext.Default.OfferPostDto);
        using var resp = await SendAsync(HttpMethod.Post, Url($"/sig/{Esc(pairId)}/offer"), body, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"offer 送信失敗: HTTP {(int)resp.StatusCode}");
    }

    public async Task<string> WaitForOfferAsync(string pairId, string fromDeviceId, long minCreatedAt = 0, CancellationToken ct = default)
    {
        var url = Url($"/sig/{Esc(pairId)}/offer?from={Esc(fromDeviceId)}&minCreatedAt={minCreatedAt}");
        return await PollForDataAsync(url, "offer", pairId, 400, ct);
    }

    public async Task<string?> TryReadOfferOnceAsync(string pairId, string fromDeviceId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, Url($"/sig/{Esc(pairId)}/offer?from={Esc(fromDeviceId)}"), null, ct);
            if (resp.StatusCode != HttpStatusCode.OK) return null;
            var dto = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(ct), CfJsonContext.Default.TimedDataDto);
            return string.IsNullOrEmpty(dto?.Data) ? null : dto!.Data;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public async Task<long?> TryReadOfferCreatedAtAsync(string pairId, string fromDeviceId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, Url($"/sig/{Esc(pairId)}/offer?from={Esc(fromDeviceId)}"), null, ct);
            if (resp.StatusCode != HttpStatusCode.OK) return null;
            var dto = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(ct), CfJsonContext.Default.TimedDataDto);
            return dto?.CreatedAt;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public async Task SendSdpAnswerAsync(string pairId, string answererDeviceId, string sdp, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new SdpPostDto { Sdp = sdp }, CfJsonContext.Default.SdpPostDto);
        using var resp = await SendAsync(HttpMethod.Post, Url($"/sig/{Esc(pairId)}/answer"), body, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"answer 送信失敗: HTTP {(int)resp.StatusCode}");
    }

    public async Task<string> WaitForAnswerAsync(string pairId, string fromDeviceId, CancellationToken ct = default)
    {
        var url = Url($"/sig/{Esc(pairId)}/answer?from={Esc(fromDeviceId)}");
        // answer は DataDto（createdAt 無し）。data フィールドは TimedDataDto と互換なので共通ポーラを使う。
        return await PollForDataAsync(url, "answer", pairId, 400, ct);
    }

    public async Task SendEndpointAsync(string pairId, string senderDeviceId, string endpoint, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new EndpointPostDto { Endpoint = endpoint }, CfJsonContext.Default.EndpointPostDto);
        using var resp = await SendAsync(HttpMethod.Post, Url($"/sig/{Esc(pairId)}/endpoint"), body, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"endpoint 送信失敗: HTTP {(int)resp.StatusCode}");
        Util.Logger.Log($"外部エンドポイント送信(CF): {Util.Logger.MaskIp(endpoint)}");
    }

    public async Task<string> WaitForEndpointAsync(string pairId, string fromDeviceId, CancellationToken ct = default)
    {
        Util.Logger.Log($"外部エンドポイント待機開始(CF): pairId={pairId}, from={Util.Logger.MaskDeviceId(fromDeviceId)}");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var resp = await SendAsync(HttpMethod.Get, Url($"/sig/{Esc(pairId)}/endpoint?from={Esc(fromDeviceId)}"), null, ct);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    var dto = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(ct), CfJsonContext.Default.EndpointGetDto);
                    if (dto != null && !string.IsNullOrEmpty(dto.Endpoint))
                    {
                        // From 二重防護（server キーで保証済だが互換のため残す）。
                        if (dto.From == fromDeviceId)
                        {
                            Util.Logger.Log($"外部エンドポイント受信(CF): {Util.Logger.MaskIp(dto.Endpoint)}");
                            return dto.Endpoint;
                        }
                        Util.Logger.Log($"ペア相手以外の endpoint を破棄(CF): from={Util.Logger.MaskDeviceId(dto.From)}", Util.LogLevel.Warning);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            await Task.Delay(500, ct);
        }
        throw new OperationCanceledException(ct);
    }

    // ===== 経路 Probe =====

    public async Task SendProbeOfferAsync(string pairId, string nonce, string sdp, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new SdpPostDto { Sdp = sdp }, CfJsonContext.Default.SdpPostDto);
        using var resp = await SendAsync(HttpMethod.Post, Url($"/sig/{Esc(pairId)}/probe-offer/{Esc(nonce)}"), body, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"probe-offer 送信失敗: HTTP {(int)resp.StatusCode}");
    }

    public async Task SendProbeAnswerAsync(string pairId, string nonce, string sdp, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new SdpPostDto { Sdp = sdp }, CfJsonContext.Default.SdpPostDto);
        using var resp = await SendAsync(HttpMethod.Post, Url($"/sig/{Esc(pairId)}/probe-answer/{Esc(nonce)}"), body, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"probe-answer 送信失敗: HTTP {(int)resp.StatusCode}");
    }

    public async Task<string> WaitForProbeAnswerAsync(string pairId, string nonce, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var resp = await SendAsync(HttpMethod.Get, Url($"/sig/{Esc(pairId)}/probe-answer/{Esc(nonce)}"), null, ct);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    var dto = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(ct), CfJsonContext.Default.ProbeAnswerDto);
                    if (!string.IsNullOrEmpty(dto?.Sdp)) return dto!.Sdp;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            await Task.Delay(1000, ct);
        }
        throw new OperationCanceledException(ct);
    }

    public async Task<IReadOnlyList<(string Nonce, string Sdp)>> ReadProbeOffersAsync(string pairId, CancellationToken ct = default)
    {
        var results = new List<(string, string)>();
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, Url($"/sig/{Esc(pairId)}/probe-offers"), null, ct);
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                var dto = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(ct), CfJsonContext.Default.ProbeOffersDto);
                if (dto?.Offers != null)
                    foreach (var o in dto.Offers)
                        if (!string.IsNullOrEmpty(o.Nonce) && !string.IsNullOrEmpty(o.Sdp))
                            results.Add((o.Nonce, o.Sdp));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return results;
    }

    public async Task CleanupProbeAsync(string pairId, string nonce, CancellationToken ct = default)
    {
        try { using var resp = await SendAsync(HttpMethod.Delete, Url($"/sig/{Esc(pairId)}/probe/{Esc(nonce)}"), null, ct); }
        catch { }
    }

    // ===== クリーンアップ =====

    public async Task CleanupSignalingDataAsync(string pairId, CancellationToken ct = default)
    {
        try { using var resp = await SendAsync(HttpMethod.Delete, Url($"/sig/{Esc(pairId)}"), null, ct); Util.Logger.Log($"シグナリングデータ削除(CF): {pairId}"); }
        catch (Exception ex) { Util.Logger.Log($"シグナリングデータ削除エラー(CF): {ex.Message}", Util.LogLevel.Warning); }
    }

    public async Task RevokePairingTokensAsync(string sid, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sid)) return;
        try { using var resp = await SendAsync(HttpMethod.Delete, Url($"/pair/session/{Esc(sid)}"), null, ct); }
        catch (Exception ex) { Util.Logger.Log($"pair/session revoke 失敗(CF): {ex.Message}", Util.LogLevel.Debug); }
    }

    public async Task CleanupAsync(string? pairingId = null, CancellationToken ct = default)
    {
        try
        {
            if (!string.IsNullOrEmpty(_sessionId)) await RevokePairingTokensAsync(_sessionId, ct);
            if (!string.IsNullOrEmpty(pairingId)) await CleanupSignalingDataAsync(pairingId, ct);
        }
        catch (Exception ex) { Util.Logger.Log($"CF クリーンアップエラー: {ex.Message}", Util.LogLevel.Warning); }
    }

    // ===== pairs SSoT (D1) =====

    public async Task PutPairAsync(string pairId, PairRecord record, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(
            new PairPutDto { NameA = record.NameA, NameB = record.NameB, CreatedAt = record.CreatedAt },
            CfJsonContext.Default.PairPutDto);
        using var resp = await SendAsync(HttpMethod.Put, Url($"/pairs/{Esc(pairId)}"), body, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"pairs PUT 失敗: HTTP {(int)resp.StatusCode}");
    }

    public async Task<PairRecord?> GetPairAsync(string pairId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, Url($"/pairs/{Esc(pairId)}"), null, ct);
            if (resp.StatusCode != HttpStatusCode.OK) return null;
            var dto = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(ct), CfJsonContext.Default.PairGetDto);
            if (dto == null) return null;
            return new PairRecord { PairId = dto.PairId, NameA = dto.NameA, NameB = dto.NameB, CreatedAt = dto.CreatedAt };
        }
        catch { return null; }
    }

    public async Task<(HttpStatusCode Status, string Body)> GetPairWithStatusAsync(string pairId, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Get, Url($"/pairs/{Esc(pairId)}"), null, ct);
        var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();
        return (resp.StatusCode, body);
    }

    public async Task DeletePairAsync(string pairId, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Delete, Url($"/pairs/{Esc(pairId)}"), null, ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NotFound)
            throw new HttpRequestException($"pairs DELETE 失敗: HTTP {(int)resp.StatusCode}");
    }

    // ===== presence (DeviceDO へ poll) =====

    public async Task UpdatePresenceAsync(string deviceId, string displayName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var body = JsonSerializer.Serialize(
            new PresencePostDto { DisplayName = displayName, Version = AppVersion.Value },
            CfJsonContext.Default.PresencePostDto);
        using var resp = await SendAsync(HttpMethod.Post, Url($"/presence/{Esc(deviceId)}"), body, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"presence 更新失敗: HTTP {(int)resp.StatusCode}");
    }

    public async Task<PresenceData?> GetPresenceAsync(string deviceId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, Url($"/presence/{Esc(deviceId)}"), null, ct);
            if (resp.StatusCode != HttpStatusCode.OK) return null;
            var dto = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(ct), CfJsonContext.Default.PresenceFullDto);
            if (dto == null) return null;
            return new PresenceData { LastSeen = dto.LastSeen, DisplayName = dto.DisplayName, Version = dto.Version };
        }
        catch { return null; }
    }

    public async Task<long?> GetPresenceLastSeenAsync(string deviceId, CancellationToken ct = default)
    {
        var cacheHit = _presenceCache.TryGetValue(deviceId, out var cached);
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, Url($"/presence/{Esc(deviceId)}/last-seen"), null, ct,
                ifNoneMatch: cacheHit && !string.IsNullOrEmpty(cached.ETag) ? cached.ETag : null);

            if (resp.StatusCode == HttpStatusCode.NotModified)
                return cacheHit ? cached.LastSeen : (long?)null;
            if (resp.StatusCode != HttpStatusCode.OK) return null;

            var etag = resp.Headers.ETag?.Tag;
            var dto = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(ct), CfJsonContext.Default.LastSeenDto);
            if (dto == null) return null;
            if (!string.IsNullOrEmpty(etag)) _presenceCache[deviceId] = (etag, dto.LastSeen);
            return dto.LastSeen;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public async Task RemovePresenceAsync(string deviceId)
    {
        try { using var resp = await SendAsync(HttpMethod.Delete, Url($"/presence/{Esc(deviceId)}"), null, CancellationToken.None); }
        catch (Exception ex) { Util.Logger.Log($"プレゼンス削除エラー(CF): {ex.Message}", Util.LogLevel.Warning); }
    }

    public void ForgetPresence(string deviceId) => _presenceCache.TryRemove(deviceId, out _);

    // ===== 共通ポーラ =====

    /// <summary>data フィールド（TimedDataDto と互換）を 200 で得るまでポーリングする。
    /// severe HTTP は指数バックオフ、404/未着は <paramref name="pollMs"/> で高速継続。</summary>
    private async Task<string> PollForDataAsync(string url, string label, string pairId, int pollMs, CancellationToken ct)
    {
        var consecutiveErrors = 0;
        var pollCount = 0;
        while (!ct.IsCancellationRequested)
        {
            pollCount++;
            try
            {
                using var resp = await SendAsync(HttpMethod.Get, url, null, ct);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    var dto = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(ct), CfJsonContext.Default.TimedDataDto);
                    if (!string.IsNullOrEmpty(dto?.Data))
                    {
                        Util.Logger.Log($"SDP 受信(CF, {label}): pairId={pairId}, ポーリング回数={pollCount}");
                        return dto!.Data;
                    }
                }
                else if (IsSevere(resp.StatusCode))
                {
                    throw new HttpRequestException($"{label} GET HTTP {(int)resp.StatusCode}");
                }
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                consecutiveErrors++;
                var backoffMs = Math.Min(1000 * (1 << Math.Min(consecutiveErrors - 1, 5)), 30_000);
                if (consecutiveErrors == 1 || pollCount % 30 == 0)
                    Util.Logger.Log($"SDP ポーリングエラー(CF, {label}): {ex.Message}", Util.LogLevel.Warning);
                await Task.Delay(backoffMs + Random.Shared.Next(0, 500), ct);
                continue;
            }
            await Task.Delay(pollMs, ct);
        }
        throw new OperationCanceledException(ct);
    }

    public void Dispose()
    {
        StopWatching();
        _presenceCache.Clear();
        if (_ownsHttp) _http.Dispose();
    }
}

// ===== CF API DTO（System.Text.Json SourceGen / AOT セーフ） =====

internal sealed class OfferPostDto
{
    [JsonPropertyName("sdp")] public string Sdp { get; set; } = string.Empty;
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
}
internal sealed class SdpPostDto
{
    [JsonPropertyName("sdp")] public string Sdp { get; set; } = string.Empty;
}
internal sealed class EndpointPostDto
{
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = string.Empty;
}
internal sealed class TimedDataDto
{
    [JsonPropertyName("data")] public string Data { get; set; } = string.Empty;
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
}
internal sealed class EndpointGetDto
{
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = string.Empty;
    [JsonPropertyName("from")] public string From { get; set; } = string.Empty;
}
internal sealed class ProbeOffersDto
{
    [JsonPropertyName("offers")] public List<ProbeOfferItemDto>? Offers { get; set; }
}
internal sealed class ProbeOfferItemDto
{
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = string.Empty;
    [JsonPropertyName("sdp")] public string Sdp { get; set; } = string.Empty;
}
internal sealed class ProbeAnswerDto
{
    [JsonPropertyName("sdp")] public string Sdp { get; set; } = string.Empty;
}
internal sealed class PresencePostDto
{
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
}
internal sealed class PresenceFullDto
{
    [JsonPropertyName("lastSeen")] public long LastSeen { get; set; }
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
}
internal sealed class LastSeenDto
{
    [JsonPropertyName("lastSeen")] public long LastSeen { get; set; }
}
internal sealed class PairPutDto
{
    [JsonPropertyName("nameA")] public string NameA { get; set; } = string.Empty;
    [JsonPropertyName("nameB")] public string NameB { get; set; } = string.Empty;
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
}
internal sealed class PairGetDto
{
    [JsonPropertyName("pairId")] public string PairId { get; set; } = string.Empty;
    [JsonPropertyName("nameA")] public string NameA { get; set; } = string.Empty;
    [JsonPropertyName("nameB")] public string NameB { get; set; } = string.Empty;
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
}
internal sealed class SessionRegisterDto
{
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = string.Empty;
    [JsonPropertyName("pairingNonce")] public string PairingNonce { get; set; } = string.Empty;
}
internal sealed class SessionGetDto
{
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = string.Empty;
}
internal sealed class PairLinkPostDto
{
    [JsonPropertyName("sidB")] public string SidB { get; set; } = string.Empty;
    [JsonPropertyName("nameA")] public string NameA { get; set; } = string.Empty;
    [JsonPropertyName("nameB")] public string NameB { get; set; } = string.Empty;
    [JsonPropertyName("pkA")] public string PkA { get; set; } = string.Empty;
    [JsonPropertyName("pkB")] public string PkB { get; set; } = string.Empty;
}
internal sealed class InboxEventDto
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("pairingId")] public string PairingId { get; set; } = string.Empty;
    [JsonPropertyName("sidA")] public string SidA { get; set; } = string.Empty;
    [JsonPropertyName("nameA")] public string NameA { get; set; } = string.Empty;
    [JsonPropertyName("sidB")] public string SidB { get; set; } = string.Empty;
    [JsonPropertyName("nameB")] public string NameB { get; set; } = string.Empty;
    [JsonPropertyName("pkA")] public string PkA { get; set; } = string.Empty;
    [JsonPropertyName("pkB")] public string PkB { get; set; } = string.Empty;
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
}

[JsonSerializable(typeof(OfferPostDto))]
[JsonSerializable(typeof(SdpPostDto))]
[JsonSerializable(typeof(EndpointPostDto))]
[JsonSerializable(typeof(TimedDataDto))]
[JsonSerializable(typeof(EndpointGetDto))]
[JsonSerializable(typeof(ProbeOffersDto))]
[JsonSerializable(typeof(ProbeOfferItemDto))]
[JsonSerializable(typeof(ProbeAnswerDto))]
[JsonSerializable(typeof(PresencePostDto))]
[JsonSerializable(typeof(PresenceFullDto))]
[JsonSerializable(typeof(LastSeenDto))]
[JsonSerializable(typeof(PairPutDto))]
[JsonSerializable(typeof(PairGetDto))]
[JsonSerializable(typeof(SessionRegisterDto))]
[JsonSerializable(typeof(SessionGetDto))]
[JsonSerializable(typeof(PairLinkPostDto))]
[JsonSerializable(typeof(InboxEventDto))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class CfJsonContext : JsonSerializerContext { }
