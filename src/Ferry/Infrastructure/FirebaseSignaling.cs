using System;
using System.Linq;
using System.Reactive.Linq;
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
    private IDisposable? _sdpSubscription;
    private IDisposable? _iceCandidateSubscription;

    /// <summary>ペアリング相手が見つかったときに発火するイベント。</summary>
    public event EventHandler<PairingInfo>? PairingDetected;

    /// <summary>SDP Offer/Answer を受信したときに発火するイベント。</summary>
    public event EventHandler<string>? SdpReceived;

    /// <summary>ICE Candidate を受信したときに発火するイベント。</summary>
    public event EventHandler<string>? IceCandidateReceived;

    public FirebaseSignaling(string databaseUrl)
    {
        _client = new FirebaseClient(databaseUrl);
    }

    /// <summary>
    /// セッションを Firebase に登録し、ペアリング監視を開始する。
    /// </summary>
    /// <returns>セッション ID。</returns>
    public async Task<string> RegisterSessionAsync(string displayName, CancellationToken ct = default)
    {
        _sessionId = Guid.NewGuid().ToString("N");

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
    /// SDP Offer/Answer ノードの変更を監視する。
    /// </summary>
    /// <param name="pairId">ペアリング ID。</param>
    /// <param name="watchField">"offer" または "answer"。</param>
    public void StartWatchingSdp(string pairId, string watchField)
    {
        _sdpSubscription?.Dispose();
        _sdpSubscription = _client
            .Child("signaling")
            .Child(pairId)
            .Child(watchField)
            .AsObservable<string>()
            .Where(e => e.EventType == FirebaseEventType.InsertOrUpdate && !string.IsNullOrEmpty(e.Object))
            .Subscribe(e =>
            {
                Util.Logger.Log($"SDP 受信 ({watchField}): {pairId}");
                SdpReceived?.Invoke(this, e.Object!);
            });
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
            .AsObservable<string>()
            .Where(e => e.EventType == FirebaseEventType.InsertOrUpdate && !string.IsNullOrEmpty(e.Object))
            .Subscribe(e =>
            {
                IceCandidateReceived?.Invoke(this, e.Object!);
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

        await _client
            .Child("signaling")
            .Child(pairId)
            .Child("offer")
            .PutAsync(sdp);
    }

    /// <summary>
    /// SDP Answer を Firebase に書き込む。
    /// </summary>
    public async Task SendSdpAnswerAsync(string pairId, string sdp, CancellationToken ct = default)
    {
        await _client
            .Child("signaling")
            .Child(pairId)
            .Child("answer")
            .PutAsync(sdp);
    }

    /// <summary>
    /// ICE Candidate を Firebase に書き込む。
    /// </summary>
    public async Task SendIceCandidateAsync(string pairId, string candidateField, string candidate, CancellationToken ct = default)
    {
        await _client
            .Child("signaling")
            .Child(pairId)
            .Child(candidateField)
            .PostAsync(candidate);
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
        _sdpSubscription?.Dispose();
        _sdpSubscription = null;
        _iceCandidateSubscription?.Dispose();
        _iceCandidateSubscription = null;
    }

    public void Dispose()
    {
        StopWatching();
        _client.Dispose();
    }
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

/// <summary>ペアリング検知情報。</summary>
public sealed class PairingInfo
{
    public string PairingId { get; set; } = string.Empty;
    public string PeerId { get; set; } = string.Empty;
    public string PeerDisplayName { get; set; } = string.Empty;
    public bool IsInitiator { get; set; }
}
