using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Firebase.Database;
using Firebase.Database.Query;
using Firebase.Database.Streaming;

namespace Ferry.Infrastructure;

/// <summary>
/// Firebase Realtime Database を使用したシグナリング実装。
/// セッション登録、ペアリング監視、SDP/ICE 候補の交換を行う。
///
/// Firebase 構造:
///   sessions/{sessionId} = { displayName, createdAt }
///   pairings/{pairingId} = { sidA, sidB, nameA, nameB }
///   signaling/{pairId}/offer  = SDP 文字列
///   signaling/{pairId}/answer = SDP 文字列
///   signaling/{pairId}/candidatesA/{key} = ICE candidate 文字列
///   signaling/{pairId}/candidatesB/{key} = ICE candidate 文字列
/// </summary>
public sealed class FirebaseSignaling : IDisposable
{
    private readonly FirebaseClient _client;
    private string _sessionId = string.Empty;
    private IDisposable? _pairingSubscription;
    /// <summary>ペアリング相手が見つかったときに発火するイベント。</summary>
    public event EventHandler<PairingInfo>? PairingDetected;

    public FirebaseSignaling(string databaseUrl)
    {
        _client = new FirebaseClient(databaseUrl);
    }

    /// <summary>
    /// セッションを Firebase に登録し、ペアリング監視を開始する。
    /// </summary>
    /// <param name="deviceId">デバイスの安定した一意識別子。</param>
    /// <param name="displayName">表示名。</param>
    /// <returns>セッション ID（= deviceId）。</returns>
    public async Task<string> RegisterSessionAsync(string deviceId, string displayName, CancellationToken ct = default)
    {
        _sessionId = deviceId;

        await _client
            .Child("sessions")
            .Child(_sessionId)
            .PutAsync(new SessionData
            {
                DisplayName = displayName,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

        Util.Logger.Log($"セッション登録: {_sessionId}");
        return _sessionId;
    }

    /// <summary>
    /// Bridge ページ等が行う `pairings/{pairingId}` の書き込みをアプリ側から直接実行する。
    /// カメラ無し PC 同士のアプリ内 URL 交換ペアリング (Bridge ページを経由しない) で使用。
    /// 両 PC は <see cref="StartWatchingPairing"/> でこの書き込みを検知してペアリング成立を扱う。
    /// </summary>
    /// <param name="sidA">PC-A (招待元) の sessionId。</param>
    /// <param name="nameA">PC-A の表示名。</param>
    /// <param name="sidB">PC-B (招待先) の sessionId。</param>
    /// <param name="nameB">PC-B の表示名。</param>
    public async Task SubmitPairingAsync(string sidA, string nameA, string sidB, string nameB, CancellationToken ct = default)
    {
        // Bridge ページの ID 形式 (`${Date.now()}_${random(6)}`) に揃えた 20 文字 (13 + 1 + 6) で生成
        var pairingId = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}"[..20];
        await _client
            .Child("pairings")
            .Child(pairingId)
            .PutAsync(new PairingData
            {
                SidA = sidA,
                NameA = string.IsNullOrEmpty(nameA) ? "PC-A" : nameA,
                SidB = sidB,
                NameB = string.IsNullOrEmpty(nameB) ? "PC-B" : nameB,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        Util.Logger.Log($"ペアリング書き込み: {pairingId}, A={Util.Logger.MaskIp(sidA)}, B={Util.Logger.MaskIp(sidB)}");
    }

    /// <summary>
    /// 指定 sessionId が存在するかを確認する (アプリ内 URL ペアリング前の事前チェック用)。
    /// </summary>
    public async Task<(bool Exists, string? DisplayName)> CheckSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            var data = await _client.Child("sessions").Child(sessionId).OnceSingleAsync<SessionData>();
            if (data == null) return (false, null);
            return (true, data.DisplayName);
        }
        catch
        {
            return (false, null);
        }
    }

    /// <summary>
    /// pairings ノードの変更を監視し、自分の sessionId を含むペアリングを検知する。
    /// </summary>
    public void StartWatchingPairing()
    {
        _pairingSubscription?.Dispose();
        _pairingSubscription = _client
            .Child("pairings")
            .AsObservable<PairingData>()
            .Where(e => e.EventType == FirebaseEventType.InsertOrUpdate)
            .Where(e => e.Object != null &&
                        (e.Object.SidA == _sessionId || e.Object.SidB == _sessionId))
            .Subscribe(e =>
            {
                Util.Logger.Log($"ペアリング検知: {e.Key}");
                var data = e.Object!;
                var isA = data.SidA == _sessionId;
                PairingDetected?.Invoke(this, new PairingInfo
                {
                    PairingId = e.Key,
                    PeerId = isA ? data.SidB : data.SidA,
                    PeerDisplayName = isA ? data.NameB : data.NameA,
                    IsInitiator = isA,
                });
            });
    }

    /// <summary>
    /// SDP Offer/Answer をポーリングで待機して取得する。
    /// AsObservable は子ノードを監視するため単一値の SDP には不向き。
    /// OnceSingleAsync で定期的にチェックする。
    /// </summary>
    /// <param name="pairId">ペアリング ID。</param>
    /// <param name="watchField">"offer" または "answer"。</param>
    /// <param name="minCreatedAt">この値より新しい createdAt を持つデータのみ受け入れる（0 なら無制限）。</param>
    /// <param name="ct">キャンセルトークン。</param>
    /// <returns>デコード済み SDP 文字列。</returns>
    public async Task<string> WaitForSdpAsync(string pairId, string watchField, long minCreatedAt = 0, CancellationToken ct = default)
    {
        Util.Logger.Log($"SDP ポーリング開始 ({watchField}): pairId={pairId}, minCreatedAt={minCreatedAt}");
        var pollCount = 0;
        var lastErrorLog = 0; // エラーログ抑制用カウンタ

        while (!ct.IsCancellationRequested)
        {
            pollCount++;
            try
            {
                // minCreatedAt が設定されている場合、createdAt タイムスタンプで鮮度を検証する
                if (minCreatedAt > 0)
                {
                    // Firebase ライブラリはノード未存在時に例外を投げることがあるため個別に捕捉
                    long? createdAt = null;
                    try
                    {
                        createdAt = await _client
                            .Child("signaling")
                            .Child(pairId)
                            .Child("createdAt")
                            .OnceSingleAsync<long?>();
                    }
                    catch
                    {
                        // ノード未存在 or null レスポンス → createdAt = null として扱う
                    }

                    if (createdAt == null || createdAt.Value < minCreatedAt)
                    {
                        if (pollCount % 30 == 1)
                        {
                            Util.Logger.Log($"SDP 待機中 ({watchField}): createdAt={createdAt?.ToString() ?? "null"}, 待機回数={pollCount}", Util.LogLevel.Debug);
                        }
                        await Task.Delay(1000, ct);
                        continue;
                    }

                    Util.Logger.Log($"SDP 鮮度チェック通過 ({watchField}): createdAt={createdAt.Value}");
                }

                // SDP データの取得（未存在時は null を返す場合と例外を投げる場合がある）
                SignalingValue? value = null;
                try
                {
                    value = await _client
                        .Child("signaling")
                        .Child(pairId)
                        .Child(watchField)
                        .OnceSingleAsync<SignalingValue>();
                }
                catch
                {
                    // ノード未存在 → value = null として扱う
                }

                if (value != null && !string.IsNullOrEmpty(value.Data))
                {
                    Util.Logger.Log($"SDP 受信 ({watchField}): pairId={pairId}, ポーリング回数={pollCount}");
                    return DecodeBase64(value.Data);
                }

                if (pollCount % 30 == 1)
                {
                    Util.Logger.Log($"SDP 待機中 ({watchField}): データ未着, 待機回数={pollCount}", Util.LogLevel.Debug);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // エラーは30回に1回だけログ出力（WARN スパム防止）
                if (pollCount - lastErrorLog >= 30)
                {
                    Util.Logger.Log($"SDP ポーリングエラー ({watchField}): {ex.Message}", Util.LogLevel.Warning);
                    lastErrorLog = pollCount;
                }
            }

            await Task.Delay(1000, ct);
        }

        throw new OperationCanceledException(ct);
    }

    /// <summary>
    /// ICE Candidate ノードの変更を監視する。
    /// </summary>
    /// <param name="pairId">ペアリング ID。</param>
    /// <param name="candidateField">"candidatesA" または "candidatesB"。</param>
    /// <summary>
    /// SDP Offer を Firebase に書き込む。
    /// </summary>
    public async Task SendSdpOfferAsync(string pairId, string sdp, CancellationToken ct = default)
    {
        // シグナリング開始時にタイムスタンプを記録（クリーンアップ用）
        await _client
            .Child("signaling")
            .Child(pairId)
            .Child("createdAt")
            .PutAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // Firebase REST API は JSON 値しか受け付けないため、
        // Base64 エンコードした文字列をオブジェクトに包んで送る
        var encoded = EncodeBase64(sdp);
        await _client
            .Child("signaling")
            .Child(pairId)
            .Child("offer")
            .PutAsync(new SignalingValue { Data = encoded });
    }

    /// <summary>
    /// SDP Answer を Firebase に書き込む。
    /// </summary>
    public async Task SendSdpAnswerAsync(string pairId, string sdp, CancellationToken ct = default)
    {
        var encoded = EncodeBase64(sdp);
        await _client
            .Child("signaling")
            .Child(pairId)
            .Child("answer")
            .PutAsync(new SignalingValue { Data = encoded });
    }

    /// <summary>
    /// UDP ホールパンチ用の外部エンドポイントを Firebase に書き込む。
    /// </summary>
    /// <param name="pairId">ペアリング ID。</param>
    /// <param name="role">"offer" または "answer"。</param>
    /// <param name="endpoint">"ip:port" 形式の文字列。</param>
    public async Task SendEndpointAsync(string pairId, string role, string endpoint, CancellationToken ct = default)
    {
        var encoded = EncodeBase64(endpoint);
        await _client
            .Child("signaling")
            .Child(pairId)
            .Child($"{role}Endpoint")
            .PutAsync(new SignalingValue { Data = encoded });
        Util.Logger.Log($"外部エンドポイント送信 ({role}): {Util.Logger.MaskIp(endpoint)}");
    }

    /// <summary>
    /// UDP ホールパンチ用の外部エンドポイントをポーリングで待機して取得する。
    /// </summary>
    public async Task<string> WaitForEndpointAsync(string pairId, string role, CancellationToken ct = default)
    {
        Util.Logger.Log($"外部エンドポイント待機開始 ({role}): pairId={pairId}");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var value = await _client
                    .Child("signaling")
                    .Child(pairId)
                    .Child($"{role}Endpoint")
                    .OnceSingleAsync<SignalingValue>();

                if (value?.Data != null)
                {
                    var decoded = DecodeBase64(value.Data);
                    Util.Logger.Log($"外部エンドポイント受信 ({role}): {Util.Logger.MaskIp(decoded)}");
                    return decoded;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            await Task.Delay(500, ct);
        }

        throw new OperationCanceledException(ct);
    }

    /// <summary>
    /// v1.0.38 review fix v14: probe 専用 offer を per-nonce key
    /// `signaling/{pairId}/probeOffers/{nonce}` に書き込む。
    /// 旧 v4-v12 の単一スロット (probeOffer) では bidirectional 同時 probe で
    /// 後勝ち上書きが起き、自分の offer が消されて相手の answer を誤採用する race が
    /// あった。per-nonce にすることで複数 probe が共存可能になり、その race を根絶する。
    /// 書き込み順序は v5 と同じく data 先 → createdAt 後 (waiting 側 freshness race 回避)。
    /// </summary>
    public async Task SendProbeOfferAsync(string pairId, string nonce, string sdp, CancellationToken ct = default)
    {
        var encoded = EncodeBase64(sdp);
        await _client.Child("signaling").Child(pairId).Child("probeOffers").Child(nonce).Child("data")
            .PutAsync(new SignalingValue { Data = encoded });
        await _client.Child("signaling").Child(pairId).Child("probeOffers").Child(nonce).Child("createdAt")
            .PutAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// v1.0.38 review fix v14: probe 専用 answer を per-nonce key
    /// `signaling/{pairId}/probeAnswers/{nonce}` に書き込む。
    /// 該当 offer の nonce をそのまま使うので、probe sender 側は自分の nonce で答えだけを正確に読める。
    /// </summary>
    public async Task SendProbeAnswerAsync(string pairId, string nonce, string sdp, CancellationToken ct = default)
    {
        var encoded = EncodeBase64(sdp);
        await _client.Child("signaling").Child(pairId).Child("probeAnswers").Child(nonce).Child("data")
            .PutAsync(new SignalingValue { Data = encoded });
        await _client.Child("signaling").Child(pairId).Child("probeAnswers").Child(nonce).Child("createdAt")
            .PutAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// v1.0.38 review fix v14: 自分の probe nonce 専用 answer を待つ。
    /// 他 probe の answer は別 key (別 nonce) に書かれるので絶対に混入しない。
    /// </summary>
    public async Task<string> WaitForProbeAnswerAsync(string pairId, string nonce, long minCreatedAt, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                TimedSignalingValue? entry = null;
                try
                {
                    entry = await _client.Child("signaling").Child(pairId)
                        .Child("probeAnswers").Child(nonce).OnceSingleAsync<TimedSignalingValue>();
                }
                catch { }

                if (entry == null || entry.CreatedAt < minCreatedAt || string.IsNullOrEmpty(entry.Data))
                {
                    await Task.Delay(1000, ct);
                    continue;
                }

                return DecodeBase64(entry.Data);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* ignore transient */ }

            await Task.Delay(1000, ct);
        }
        throw new OperationCanceledException(ct);
    }

    /// <summary>
    /// v1.0.38 review fix v14: pair 配下の全 probe offer を 1 回読む (non-blocking)。
    /// minCreatedAt より新しい offer 全てを (nonce, sdp) のリストで返す。
    /// 旧 TryReadProbeOfferAsync (単一スロット) の置き換え。
    /// ListenForIncomingConnectionAsync は返ってきた offer を nonce ごとに HandleProbeOfferAsync で処理する。
    /// </summary>
    public async Task<System.Collections.Generic.IReadOnlyList<(string Nonce, string Sdp)>> ReadProbeOffersAsync(
        string pairId, long minCreatedAt, CancellationToken ct = default)
    {
        var results = new System.Collections.Generic.List<(string, string)>();
        try
        {
            var entries = await _client.Child("signaling").Child(pairId).Child("probeOffers")
                .OnceAsync<TimedSignalingValue>();
            foreach (var entry in entries)
            {
                if (entry.Object == null) continue;
                if (entry.Object.CreatedAt < minCreatedAt) continue;
                if (string.IsNullOrEmpty(entry.Object.Data)) continue;
                results.Add((entry.Key, DecodeBase64(entry.Object.Data)));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* ノード未生成 / transient → 空リスト */ }
        return results;
    }

    /// <summary>
    /// v1.0.38 review fix v14: probe sender が成功 / タイムアウト後に自分の probe ノードを削除する。
    /// 残骸が大量に Firebase に溜まるのを防ぐ (GitHub Actions cleanup は時間がかかるため即時 cleanup する)。
    /// </summary>
    public async Task CleanupProbeAsync(string pairId, string nonce, CancellationToken ct = default)
    {
        try { await _client.Child("signaling").Child(pairId).Child("probeOffers").Child(nonce).DeleteAsync(); } catch { }
        try { await _client.Child("signaling").Child(pairId).Child("probeAnswers").Child(nonce).DeleteAsync(); } catch { }
    }

    /// <summary>
    /// 指定した pairId のシグナリングデータのみを Firebase から削除する。
    /// 再接続時に古い offer/answer/candidates が残っていると接続失敗するため。
    /// </summary>
    public async Task CleanupSignalingDataAsync(string pairId, CancellationToken ct = default)
    {
        try
        {
            await _client.Child("signaling").Child(pairId).DeleteAsync();
            Util.Logger.Log($"シグナリングデータ削除: {pairId}");
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"シグナリングデータ削除エラー: {ex.Message}", Util.LogLevel.Warning);
        }
    }

    /// <summary>
    /// セッションとシグナリングデータを Firebase から削除する。
    /// </summary>
    public async Task CleanupAsync(string? pairingId = null, CancellationToken ct = default)
    {
        try
        {
            if (!string.IsNullOrEmpty(_sessionId))
            {
                await _client.Child("sessions").Child(_sessionId).DeleteAsync();
            }
            if (!string.IsNullOrEmpty(pairingId))
            {
                await _client.Child("pairings").Child(pairingId).DeleteAsync();
                await _client.Child("signaling").Child(pairingId).DeleteAsync();
            }
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"Firebase クリーンアップエラー: {ex.Message}", Util.LogLevel.Warning);
        }
    }

    // === プレゼンス（オンライン/オフライン検知） ===

    /// <summary>
    /// 自分のプレゼンス（lastSeen タイムスタンプ）を Firebase に書き込む。
    /// </summary>
    public async Task UpdatePresenceAsync(string deviceId, string displayName, CancellationToken ct = default)
    {
        await _client
            .Child("presence")
            .Child(deviceId)
            .PutAsync(new PresenceData
            {
                LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DisplayName = displayName,
            });
    }

    /// <summary>
    /// 指定デバイスのプレゼンスデータを取得する。
    /// </summary>
    public async Task<PresenceData?> GetPresenceAsync(string deviceId, CancellationToken ct = default)
    {
        try
        {
            return await _client
                .Child("presence")
                .Child(deviceId)
                .OnceSingleAsync<PresenceData>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 自分のプレゼンスを Firebase から削除する（アプリ終了時）。
    /// </summary>
    public async Task RemovePresenceAsync(string deviceId)
    {
        try
        {
            await _client.Child("presence").Child(deviceId).DeleteAsync();
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"プレゼンス削除エラー: {ex.Message}", Util.LogLevel.Warning);
        }
    }

    public void StopWatching()
    {
        _pairingSubscription?.Dispose();
        _pairingSubscription = null;
    }

    public void Dispose()
    {
        StopWatching();
        _client.Dispose();
    }

    private static string EncodeBase64(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string DecodeBase64(string encoded) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
}

/// <summary>Firebase に書き込むセッションデータ。</summary>
public sealed class SessionData
{
    public string DisplayName { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
}

/// <summary>Firebase に書き込むペアリングデータ。</summary>
public sealed class PairingData
{
    public string SidA { get; set; } = string.Empty;
    public string SidB { get; set; } = string.Empty;
    public string NameA { get; set; } = string.Empty;
    public string NameB { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
}

/// <summary>Firebase に書き込むシグナリングデータのラッパー。
/// PutAsync/PostAsync に string を直接渡すと JSON としてシリアライズされず
/// Firebase REST API に拒否されるため、オブジェクトに包んで送る。</summary>
public sealed class SignalingValue
{
    public string Data { get; set; } = string.Empty;
}

/// <summary>v1.0.38 review fix v14: probe 専用、子要素 data + createdAt を 1 オブジェクトで持つ。
/// per-nonce key (signaling/{pairId}/probeOffers/{nonce}) 配下の全フィールドを一括 OnceAsync で
/// 取得するため、Data と CreatedAt を同一型に packed する。</summary>
public sealed class TimedSignalingValue
{
    public string Data { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
}

/// <summary>Firebase に書き込むプレゼンスデータ。</summary>
public sealed class PresenceData
{
    public long LastSeen { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>ペアリング検知情報。</summary>
public sealed class PairingInfo
{
    public string PairingId { get; set; } = string.Empty;
    public string PeerId { get; set; } = string.Empty;
    public string PeerDisplayName { get; set; } = string.Empty;
    public bool IsInitiator { get; set; }
}
