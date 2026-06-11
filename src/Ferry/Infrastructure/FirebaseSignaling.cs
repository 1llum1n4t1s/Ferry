using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    private readonly string _databaseUrl;
    private string _sessionId = string.Empty;
    private IDisposable? _pairingSubscription;
    /// <summary>ペアリング相手が見つかったときに発火するイベント。</summary>
    public event EventHandler<PairingInfo>? PairingDetected;

    /// <summary>presence の LastSeen を ETag 条件付き GET で読むための共有 HttpClient。
    /// HttpClient はインスタンスを使い回す（都度 new はソケット枯渇の原因）。</summary>
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>peerId 単位の presence ETag と直近 LastSeen のキャッシュ。
    /// 次回 GET で If-None-Match を付け、未変更なら 304（本文ゼロ）で返させて帯域を節約する。</summary>
    private readonly ConcurrentDictionary<string, (string ETag, long LastSeen)> _presenceCache = new();

    public FirebaseSignaling(string databaseUrl)
    {
        _client = new FirebaseClient(databaseUrl);
        _databaseUrl = databaseUrl.TrimEnd('/');
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
    /// <remarks>
    /// Codex P2 (#3318454466) Phase B blocker: 現実装は `pairings/` の **parent collection** に
    /// AsObservable で購読する。Realtime DB rules は parent collection read を子 ID で filter
    /// できないため、Anonymous Auth 移行後に `pairings/$pid` 個別の `.read` ルールだけを deploy
    /// しても、この購読自体が deny されて pairing が成立しない。
    ///
    /// Phase B 移行案: pairings を per-device path (`pairings/$deviceId/$pid`) に restructure
    /// し、各 client は自分の sessionId 配下のみ購読する。Bridge JS は SidA / SidB の両 path
    /// に mirror write する (Bridge 側の auth はゲートを別途設計)。詳細は
    /// `src/Ferry.Bridge/database.rules.json` の `_comment_phase_b_pairings_blocker` を参照。
    /// </remarks>
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
        // 着信監視ループが約5秒毎に呼ぶため、INFO だとログの大半を埋める。Debug に落とす
        // (Release は Info 以上なので抑制。監視開始自体は呼出元の「着信接続ポーリング開始」INFO で追える)。
        Util.Logger.Log($"SDP ポーリング開始 ({watchField}): pairId={pairId}, minCreatedAt={minCreatedAt}", Util.LogLevel.Debug);
        // v1.0.47: offer/answer の検出遅延を縮めるためポーリング間隔を 1000ms → 400ms に短縮する。
        // 送信開始から受信側が動き出すまでの「待ち」を体感で短くするのが目的（Firebase の read は極小サイズなので
        // Spark 無料枠の帯域にもほぼ影響しない）。エラー時は下の exponential backoff 側で別途間引く。
        const int PollDelayMs = 400;
        var pollCount = 0;
        var lastErrorLog = 0; // エラーログ抑制用カウンタ
        var consecutiveErrors = 0; // rere #F-012: exponential backoff 用カウンタ

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
                    catch (Exception ex) when (!IsQuotaOrServerError(ex))
                    {
                        // ノード未存在 or null レスポンス → createdAt = null として扱う。
                        // PR#5 Codex 指摘: 402/429/503 (枠超過/サービス停止系) はここで握らず
                        // 外側 catch のステータス分類ログ + backoff に到達させる
                    }

                    if (createdAt == null || createdAt.Value < minCreatedAt)
                    {
                        if (pollCount % 30 == 1)
                        {
                            Util.Logger.Log($"SDP 待機中 ({watchField}): createdAt={createdAt?.ToString() ?? "null"}, 待機回数={pollCount}", Util.LogLevel.Debug);
                        }
                        await Task.Delay(PollDelayMs, ct);
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
                catch (Exception ex) when (!IsQuotaOrServerError(ex))
                {
                    // ノード未存在 → value = null として扱う (枠超過系は外側 catch へ、上記と同様)
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
                // rere #F-003: HTTP ステータスを分類してログに併記する。Spark 無料枠超過 (402/429/503 系で
                // 全ペア一斉失敗) と個別ネットワーク不調を事後ログで切り分けられるようにする。
                // 枠超過の可能性が高いステータスは間引き対象外で即 Error 出力する
                var statusInfo = DescribeHttpStatus(ex);
                var likelyQuota = statusInfo is " status=402" or " status=429" or " status=503";
                if (likelyQuota || pollCount - lastErrorLog >= 30)
                {
                    Util.Logger.Log(
                        $"SDP ポーリングエラー ({watchField}): {ex.Message}{statusInfo}" +
                        (likelyQuota ? " (Firebase 枠超過/サービス停止の可能性)" : string.Empty),
                        likelyQuota ? Util.LogLevel.Error : Util.LogLevel.Warning);
                    lastErrorLog = pollCount;
                }
                // rere レビュー #F-012: 例外発生時は exponential backoff + jitter で
                // Firebase rate limit (429) や一時的なネットワーク不調時に hammer しない。
                // 連続成功で backoff はリセットされる
                consecutiveErrors++;
                var backoffMs = Math.Min(1000 * (1 << Math.Min(consecutiveErrors - 1, 5)), 30_000);
                var jitter = Random.Shared.Next(0, 500);
                await Task.Delay(backoffMs + jitter, ct);
                continue;
            }

            // 正常 path 完了 (受信成功 or 待機継続)
            consecutiveErrors = 0;
            await Task.Delay(PollDelayMs, ct);
        }

        throw new OperationCanceledException(ct);
    }

    /// <summary>
    /// rere #F-003: 例外チェーンから HTTP ステータスコードを抽出して " status=NNN" 形式で返す。
    /// 取得できなければ空文字。FirebaseException は ResponseData/InnerException に HTTP 情報を持つ。
    /// </summary>
    /// <summary>
    /// PR#5 Codex 指摘対応: 枠超過/サービス停止系 (402/429/503) かどうかを判定する。
    /// ポーリングの内側 catch (ノード未存在の握り潰し) がこれらを誤って吸収しないためのフィルタ。
    /// </summary>
    private static bool IsQuotaOrServerError(Exception ex)
        => DescribeHttpStatus(ex) is " status=402" or " status=429" or " status=503";

    private static string DescribeHttpStatus(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is System.Net.Http.HttpRequestException { StatusCode: { } code })
                return $" status={(int)code}";
            if (e is FirebaseException fe)
            {
                // PR#5 Codex 指摘: FirebaseException は HTTP ステータスを StatusCode に保持する。
                // default(0) はステータス未設定 (非 HTTP 要因) なのでマーカーのみ返す
                return fe.StatusCode != default
                    ? $" status={(int)fe.StatusCode}"
                    : " status=firebase-error";
            }
        }
        return string.Empty;
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
    /// v14 review fix (Codex): TimedSignalingValue を 1 オブジェクトとして atomic 書き込み
    /// (旧実装は data 子ノードに SignalingValue を書いて nested `data.data` 構造になり
    /// reader が空文字を引いていた)。atomic 書き込みなので v5 の payload→timestamp race も解消。
    /// </summary>
    public async Task SendProbeOfferAsync(string pairId, string nonce, string sdp, CancellationToken ct = default)
    {
        var encoded = EncodeBase64(sdp);
        await _client.Child("signaling").Child(pairId).Child("probeOffers").Child(nonce)
            .PutAsync(new TimedSignalingValue
            {
                Data = encoded,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
    }

    /// <summary>
    /// v1.0.38 review fix v14: probe 専用 answer を per-nonce key
    /// `signaling/{pairId}/probeAnswers/{nonce}` に書き込む。
    /// 該当 offer の nonce をそのまま使うので、probe sender 側は自分の nonce で答えだけを正確に読める。
    /// v14 review fix (Codex): SendProbeOfferAsync と同じく TimedSignalingValue を atomic 書き込み
    /// </summary>
    public async Task SendProbeAnswerAsync(string pairId, string nonce, string sdp, CancellationToken ct = default)
    {
        var encoded = EncodeBase64(sdp);
        await _client.Child("signaling").Child(pairId).Child("probeAnswers").Child(nonce)
            .PutAsync(new TimedSignalingValue
            {
                Data = encoded,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
    }

    /// <summary>
    /// v1.0.38 review fix v14: 自分の probe nonce 専用 answer を待つ。
    /// 他 probe の answer は別 key (別 nonce) に書かれるので絶対に混入しない。
    /// v14 review fix (Codex): TimedSignalingValue 全体を 1 オブジェクトとして読む形に統一
    /// v15 review fix (Codex P2 #3318349010): per-nonce key 化以降、stale answer の隔離は
    /// nonce (Guid.NewGuid hex, sender が毎 probe で発行) で完全に効いている。`CreatedAt`
    /// (answer-side clock) と sender 側 clock を跨いで比較していた旧 `minCreatedAt` フィルタは、
    /// answer 側 PC の時計が遅れているだけで fresh answer が捨てられて `Unknown` タイムアウトする
    /// 回帰を産んでいた。nonce-specific existence + payload check のみに変更。
    /// </summary>
    public async Task<string> WaitForProbeAnswerAsync(string pairId, string nonce, CancellationToken ct = default)
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

                if (entry == null || string.IsNullOrEmpty(entry.Data))
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
    /// (nonce, sdp) のリストを返す。旧 TryReadProbeOfferAsync (単一スロット) の置き換え。
    /// ListenForIncomingConnectionAsync は返ってきた offer を nonce ごとに HandleProbeOfferAsync で処理し、
    /// 既処理 nonce は呼び出し側の HashSet で dedupe する。
    /// v15 review fix (Codex P2 #3318349010): 旧 `minCreatedAt` フィルタは sender-clock /
    /// listener-clock の cross-device 時計差で fresh offer を捨てる回帰を作っていた。
    /// per-nonce key (sender 毎 Guid) と呼び出し側の processedProbeNonces HashSet で
    /// stale dedupe は十分。`CreatedAt` 比較は撤廃。
    /// </summary>
    public async Task<System.Collections.Generic.IReadOnlyList<(string Nonce, string Sdp)>> ReadProbeOffersAsync(
        string pairId, CancellationToken ct = default)
    {
        var results = new System.Collections.Generic.List<(string, string)>();
        try
        {
            var entries = await _client.Child("signaling").Child(pairId).Child("probeOffers")
                .OnceAsync<TimedSignalingValue>();
            foreach (var entry in entries)
            {
                if (entry.Object == null) continue;
                if (string.IsNullOrEmpty(entry.Object.Data)) continue;
                // v15 review fix (Codex P2 #3318454476): per-nonce key 化以降、probeOffers/ は
                // 同一 pair 内の複数 sender の offer が共存する collection になった。1 件でも壊れた
                // base64 (古い不正書き込み / 部分書き込み残骸) があると、旧実装の単一 try/catch では
                // foreach 全体が中断され、全 sender の probe が無視される (= peer 全体の経路 probe
                // が永久 stall する) 経路があった。entry 単位で例外を握りつぶし、不正 1 件は捨てて
                // 残りを処理し続ける。
                string sdp;
                try
                {
                    sdp = DecodeBase64(entry.Object.Data);
                }
                catch (Exception ex)
                {
                    Util.Logger.Log(
                        $"probe offer decode 失敗 (nonce={entry.Key}): {ex.GetType().Name} - {ex.Message} → スキップ",
                        Util.LogLevel.Warning);
                    continue;
                }
                results.Add((entry.Key, sdp));
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
    /// 指定デバイスの presence/{id}/LastSeen のみを ETag 条件付き GET で取得する（オンライン判定専用）。
    ///
    /// 常時ポーリングの帯域節約のための軽量版:
    ///  - ⑤ DisplayName を載せず LastSeen(数値) 単独を取るのでペイロードが最小。
    ///  - ④ X-Firebase-ETag + If-None-Match で、前回から未変更なら 304 Not Modified（本文ゼロ）で返る。
    ///    304 時はキャッシュ済み LastSeen をそのまま返す（オフライン peer や heartbeat 未更新時はほぼ 304 = ほぼ無転送）。
    /// 表示名の同期はこの経路では行わず、<see cref="GetPresenceAsync"/>（手動更新/前面復帰時のフル取得）に委ねる。
    /// </summary>
    /// <returns>LastSeen(ms)。presence 未作成や取得失敗時は null。</returns>
    public async Task<long?> GetPresenceLastSeenAsync(string deviceId, CancellationToken ct = default)
    {
        var url = $"{_databaseUrl}/presence/{Uri.EscapeDataString(deviceId)}/LastSeen.json";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // ETag をレスポンスヘッダに乗せてもらうための Firebase REST 拡張ヘッダ。
        req.Headers.TryAddWithoutValidation("X-Firebase-ETag", "true");
        // 直前のキャッシュ値を 1 度だけ読んで以後 304 ブランチでも使い回す（辞書アクセスを減らす）。
        var cacheHit = _presenceCache.TryGetValue(deviceId, out var cached);
        if (cacheHit && !string.IsNullOrEmpty(cached.ETag))
            req.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);

            // 304: 値は前回から不変 → 既に読んだ cached.LastSeen をそのまま返す（本文転送ゼロ・辞書再検索なし）。
            if (resp.StatusCode == HttpStatusCode.NotModified)
                return cacheHit ? cached.LastSeen : null;

            if (!resp.IsSuccessStatusCode)
                return null;

            var etag = resp.Headers.ETag?.Tag;
            var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();

            // presence ノード未作成（相手が一度も heartbeat していない）は body が "null"。
            if (string.IsNullOrEmpty(body) || body == "null")
                return null;

            if (!long.TryParse(body, out var lastSeen))
                return null;

            if (!string.IsNullOrEmpty(etag))
                _presenceCache[deviceId] = (etag, lastSeen);
            return lastSeen;
        }
        catch (OperationCanceledException)
        {
            throw;
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
        _presenceCache.Clear(); // presence ETag キャッシュを解放（セッションを跨いで古い peerId を残さない）
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
