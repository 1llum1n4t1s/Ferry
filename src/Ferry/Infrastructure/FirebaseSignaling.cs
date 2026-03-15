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
    private IDisposable? _iceCandidateSubscription;

    /// <summary>ペアリング相手が見つかったときに発火するイベント。</summary>
    public event EventHandler<PairingInfo>? PairingDetected;

    /// <summary>ICE Candidate を受信したときに発火するイベント。</summary>
    public event EventHandler<string>? IceCandidateReceived;

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
    public void StartWatchingIceCandidates(string pairId, string candidateField)
    {
        _iceCandidateSubscription?.Dispose();
        _iceCandidateSubscription = _client
            .Child("signaling")
            .Child(pairId)
            .Child(candidateField)
            .AsObservable<SignalingValue>()
            .Where(e => e.EventType == FirebaseEventType.InsertOrUpdate && e.Object != null && !string.IsNullOrEmpty(e.Object.Data))
            .Subscribe(e =>
            {
                IceCandidateReceived?.Invoke(this, DecodeBase64(e.Object!.Data));
            });
    }

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
    /// ICE Candidate を Firebase に書き込む。
    /// </summary>
    public async Task SendIceCandidateAsync(string pairId, string candidateField, string candidate, CancellationToken ct = default)
    {
        var encoded = EncodeBase64(candidate);
        await _client
            .Child("signaling")
            .Child(pairId)
            .Child(candidateField)
            .PostAsync(new SignalingValue { Data = encoded });
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

    public void StopWatching()
    {
        _pairingSubscription?.Dispose();
        _pairingSubscription = null;
        _iceCandidateSubscription?.Dispose();
        _iceCandidateSubscription = null;
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

/// <summary>ペアリング検知情報。</summary>
public sealed class PairingInfo
{
    public string PairingId { get; set; } = string.Empty;
    public string PeerId { get; set; } = string.Empty;
    public string PeerDisplayName { get; set; } = string.Empty;
    public bool IsInitiator { get; set; }
}
