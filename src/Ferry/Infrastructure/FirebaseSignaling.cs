using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;
using Ferry.Services;
using Firebase.Database;
using Firebase.Database.Query;
using Firebase.Database.Streaming;

namespace Ferry.Infrastructure;

/// <summary>
/// Firebase Realtime Database を使用したシグナリング実装。
/// セッション登録、ペアリング監視、SDP/ICE 候補の交換を行う。
///
/// Firebase 構造 (rere #D-003 で signaling を per-sender ノード化):
///   sessions/{sessionId} = { DisplayName, CreatedAt }
///   pairings/{pairingId} = { SidA, SidB, NameA, NameB, CreatedAt }
///   signaling/{pairId}/offers/{senderDeviceId}    = TimedSignalingValue { Data(SDP base64), CreatedAt }
///   signaling/{pairId}/answers/{answererDeviceId} = SignalingValue { Data(SDP base64) }
///   signaling/{pairId}/endpoints/{senderDeviceId} = SignalingValue { Data("from|ip:port" base64) }
///   signaling/{pairId}/createdAt                  = タイムスタンプ (firebase-cleanup.yml の stale 掃除用に維持)
///   signaling/{pairId}/probeOffers/{nonce} / probeAnswers/{nonce} = TimedSignalingValue (経路 probe)
/// 書き手は自分の deviceId キー、読み手はペア相手の deviceId キーを読む (SignalingPaths 参照)。
/// </summary>
public sealed class FirebaseSignaling : IDisposable, IPresenceService
{
    private readonly FirebaseClient _client;
    private readonly string _databaseUrl;
    private readonly FirebaseAuthClient? _authClient;
    private string _sessionId = string.Empty;
    private IDisposable? _pairingSubscription;
    /// <summary>Codex P1 fix (第6弾): <see cref="StartWatchingPairing"/> を呼んだ時点の Unix ms。
    /// Firebase の InsertOrUpdate event は subscribe 時に既存子を replay するため、手動 / remote unpair
    /// 後に "Add new peer" を開くと、firebase-cleanup.yml が掃除するまで残っている古い pairings entry が
    /// PairingDetected を再発火し、OnPairingDetected の副作用 (peer 再追加 / WritePairRecordWithFallback /
    /// Revoke 等) で削除済み peer を復活させる critical race があった。SidA/SidB == _sessionId だけの
    /// gate では replay 防御として不十分だったので、subscribe 開始時刻以降の CreatedAt を持つ entry のみ
    /// accept する per-session start time gate を追加する。</summary>
    private long _pairingWatchStartedAtMs;
    private EventHandler? _idTokenRefreshedHandler;
    /// <summary>ペアリング相手が見つかったときに発火するイベント。</summary>
    public event EventHandler<PairingInfo>? PairingDetected;

    /// <summary>presence の LastSeen を ETag 条件付き GET で読むための共有 HttpClient。
    /// HttpClient はインスタンスを使い回す（都度 new はソケット枯渇の原因）。</summary>
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>peerId 単位の presence ETag と直近 LastSeen のキャッシュ。
    /// 次回 GET で If-None-Match を付け、未変更なら 304（本文ゼロ）で返させて帯域を節約する。</summary>
    private readonly ConcurrentDictionary<string, (string ETag, long LastSeen)> _presenceCache = new();

    /// <summary>rere #F-002: presence 取得失敗ログの氾濫防止スロットル（最後に記録した tick(ms)）。
    /// 30s × ピア数で同一エラーが氾濫しないよう、60s に 1 度だけ Warning を出す。</summary>
    private long _presenceErrorLogTick;

    /// <summary>
    /// rere #D-001(a) Phase B: authClient 省略は AUth 未配線テスト・旧経路フォールバック用。
    /// プロダクション経路では authClient を必ず渡す（rules 厳格化後は auth 必須）。
    /// </summary>
    public FirebaseSignaling(string databaseUrl, FirebaseAuthClient? authClient = null)
    {
        _authClient = authClient;
        _client = authClient != null
            ? new FirebaseClient(databaseUrl, new FirebaseOptions
            {
                // FirebaseDatabase.net は AuthTokenAsyncFactory で returns した token を ?auth= に付与する。
                // AsAccessToken=false で `?auth=<idToken>` フォーマット（Realtime DB REST の auth クエリ）。
                AuthTokenAsyncFactory = () => authClient.GetIdTokenAsync(),
                AsAccessToken = false,
            })
            : new FirebaseClient(databaseUrl);
        _databaseUrl = databaseUrl.TrimEnd('/');

        if (authClient != null)
        {
            // IdTokenRefreshed で AsObservable 購読を Dispose → 再 Subscribe する（SSE long-stream が
            // 1h で expire → permission_denied で切断するため。Workflow #1 high 反映）
            _idTokenRefreshedHandler = (_, _) =>
            {
                if (!string.IsNullOrEmpty(_sessionId))
                {
                    Util.Logger.Log("idToken refresh 検知 → pairings 購読を再構築", Util.LogLevel.Debug);
                    StartWatchingPairing();
                }
            };
            authClient.IdTokenRefreshed += _idTokenRefreshedHandler;
        }
    }

    /// <summary>
    /// セッションを Firebase に登録し、ペアリング監視を開始する。
    /// </summary>
    /// <param name="deviceId">デバイスの安定した一意識別子。</param>
    /// <param name="displayName">表示名。</param>
    /// <param name="publicKey">rere #D-001(b): 長期公開鍵(base64url SPKI)。空可（旧互換・平文経路）。</param>
    /// <returns>セッション ID（= deviceId）。</returns>
    public async Task<string> RegisterSessionAsync(string deviceId, string displayName, string publicKey = "", CancellationToken ct = default)
    {
        _sessionId = deviceId;

        // #D-001a Phase B: Bridge が /pair/token で QR と紐付ける PairingNonce を生成。
        // Codex P1 指摘: sessions/{sid} は auth.uid 関係なく全認証ユーザが read 可なので、ここに Nonce を
        // 載せると任意のログイン済デバイスが Nonce を盗んで /pair/token で sidA として認証され ghost peer
        // 攻撃が復活する。Nonce は別ノード `pairing_nonces/{sid}` に分離し、rules で `.read: false` にして
        // クライアントから一切読めないようにする。Workers は SA 経由で読む。
        var pairingNonce = Guid.NewGuid().ToString("N");
        _lastPairingNonce = pairingNonce;
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _client
            .Child("sessions")
            .Child(_sessionId)
            .PutAsync(new SessionData
            {
                DisplayName = displayName,
                CreatedAt = createdAt,
                PublicKey = publicKey,
            });
        await _client
            .Child("pairing_nonces")
            .Child(_sessionId)
            .PutAsync(new PairingNonceRecord
            {
                Nonce = pairingNonce,
                CreatedAt = createdAt,
            });

        Util.Logger.Log($"セッション登録: {Util.Logger.MaskDeviceId(_sessionId)}");  // CodeRabbit: PII マスク
        return _sessionId;
    }

    private string _lastPairingNonce = string.Empty;

    /// <summary>
    /// 直前の <see cref="RegisterSessionAsync"/> で生成された PairingNonce（QR コードに埋め込む用）。
    /// Bridge が /pair/token を叩くときの紐付け nonce。
    /// </summary>
    public string LastPairingNonce => _lastPairingNonce;

    /// <summary>
    /// Bridge ページ等が行う `pairings/{pairingId}` の書き込みをアプリ側から直接実行する。
    /// カメラ無し PC 同士のアプリ内 URL 交換ペアリング (Bridge ページを経由しない) で使用。
    /// 両 PC は <see cref="StartWatchingPairing"/> でこの書き込みを検知してペアリング成立を扱う。
    /// </summary>
    /// <param name="sidA">PC-A (招待元) の sessionId。</param>
    /// <param name="nameA">PC-A の表示名。</param>
    /// <param name="sidB">PC-B (招待先) の sessionId。</param>
    /// <param name="nameB">PC-B の表示名。</param>
    /// <param name="pkA">rere #D-001(b): PC-A の長期公開鍵(base64url SPKI)。空可。</param>
    /// <param name="pkB">rere #D-001(b): PC-B の長期公開鍵(base64url SPKI)。空可。</param>
    public async Task SubmitPairingAsync(string sidA, string nameA, string sidB, string nameB, string pkA = "", string pkB = "", CancellationToken ct = default)
    {
        // Bridge ページの ID 形式 (`${Date.now()}_${random(6)}`) に揃えた 20 文字 (13 + 1 + 6) で生成
        var pairingId = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}"[..20];
        var data = new PairingData
        {
            SidA = sidA,
            NameA = string.IsNullOrEmpty(nameA) ? "PC-A" : nameA,
            SidB = sidB,
            NameB = string.IsNullOrEmpty(nameB) ? "PC-B" : nameB,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PkA = pkA,
            PkB = pkB,
        };
        // #D-001a Phase B (PR #10 review fix): pairings/{sidA}/{pid} と pairings/{sidB}/{pid} を
        // **atomic multi-path update** で同時に書く。Firebase REST の root PATCH (`{databaseUrl}/.json`) は
        // body に "path/to/child": value のペアを並べると全パスを 1 トランザクションで適用する。片側 rules で
        // 弾かれたら全体が失敗する＝片側だけ書き残るレースが構造的に起きない。FirebaseDatabase.net には
        // この API が無いので独自 HttpClient で実装。
        var multi = new System.Collections.Generic.Dictionary<string, PairingData>
        {
            [$"pairings/{sidA}/{pairingId}"] = data,
            [$"pairings/{sidB}/{pairingId}"] = data,
        };
        var auth = await GetAuthQueryAsync();
        var url = $"{_databaseUrl}/.json{auth}";
        var json = System.Text.Json.JsonSerializer.Serialize(multi, MultiPathPairingsJsonContext.Default.DictionaryStringPairingData);
        using var req = new HttpRequestMessage(new HttpMethod("PATCH"), url)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8),
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"pairings atomic PATCH 失敗: HTTP {(int)resp.StatusCode} {body}");
        }
        // sidA/sidB は deviceId(32hex)。MaskIp は IPv4(4オクテット)以外を素通しするため deviceId 用の MaskDeviceId を使う
        Util.Logger.Log($"ペアリング書き込み(atomic): {pairingId}, A={Util.Logger.MaskDeviceId(sidA)}, B={Util.Logger.MaskDeviceId(sidB)}");
    }

    /// <summary>
    /// 指定 sessionId が存在するかを確認する (アプリ内 URL ペアリング前の事前チェック用)。
    /// </summary>
    public async Task<(bool Exists, string? DisplayName, string? PublicKey)> CheckSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            var data = await _client.Child("sessions").Child(sessionId).OnceSingleAsync<SessionData>();
            if (data == null) return (false, null, null);
            return (true, data.DisplayName, data.PublicKey);
        }
        catch
        {
            return (false, null, null);
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
        // Codex P1 fix (第6弾): subscribe 開始時刻を確定して replay フィルタの基準にする。
        // 再 Start のシナリオ (同 session で複数回呼ぶケース) でも最新の時刻に更新されるよう defensive に代入。
        _pairingWatchStartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // #D-001a Phase B: 自分の sessionId 配下のみ購読する（per-device path、Codex P2 解消）。
        // rules `pairings/$deviceId/.read: auth.uid == $deviceId` で自分配下しか購読できないが、それで十分。
        _pairingSubscription = _client
            .Child("pairings")
            .Child(_sessionId)
            .AsObservable<PairingData>()
            .Where(e => e.EventType == FirebaseEventType.InsertOrUpdate)
            .Where(e => e.Object != null &&
                        (e.Object.SidA == _sessionId || e.Object.SidB == _sessionId) &&
                        // Codex P1 fix (第6弾): stale replay 防御。subscribe 開始より古い CreatedAt は無視する。
                        // Codex P2 fix (第7弾): rules の ±60s 鮮度ガードと同じ tolerance (60s) を持たせる。
                        // Bridge phone / peer PC の時計が server から最大 -60s 後ろにある場合 (rules では accept される)
                        // 正規 pairing が silent skip されて QR フローが詰まる事象への対策。
                        // PairingData.CreatedAt は Bridge / PC 双方が Unix ms (UTC) で書き込むため startedAt と直接比較可。
                        // rules (database.rules.json:34) で書込時に ±60s 鮮度ガード強制のため、ローカル時計が大きく
                        // ズレた相手は publish 自体不可。stale replay 防御は「-60s tolerance を超えた古い entry を弾く」
                        // ことで維持される (attacker は rules の ±60s ガード内でしか書込できない)。
                        // CreatedAt 欠落 (long の default = 0) の旧 entries は startedAt - 60_000 (≈Unix ms) を
                        // 超えないため silent skip = 意図通り (Phase B 以前の data はもう新規 pairing として扱わない)。
                        // より堅牢な代替 (server timestamp / per-session nonce) は Phase B-2 へ defer。
                        e.Object.CreatedAt >= _pairingWatchStartedAtMs - 60_000)
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
                    // 自分が A なら相手は B（その逆も）。rere #D-001(b): 相手 pk で PairSecret を導出する。
                    PeerPublicKey = isA ? data.PkB : data.PkA,
                });
            });
    }

    /// <summary>
    /// rere #D-003: ペア相手 (fromDeviceId) の offer を per-sender ノード
    /// signaling/{pairId}/offers/{fromDeviceId} からポーリングで待機して取得する。
    /// offer は TimedSignalingValue{Data, CreatedAt} で atomic に書かれるため、鮮度 (CreatedAt) と
    /// ペイロード (Data) を 1 リクエストで取得する (旧 createdAt 別ノード先読みの 2 リクエストを 1 に削減)。
    /// </summary>
    /// <param name="pairId">ペアリング ID。</param>
    /// <param name="fromDeviceId">読み取り対象の送信元 deviceId (=ペア相手)。</param>
    /// <param name="minCreatedAt">この値より新しい CreatedAt を持つ offer のみ受け入れる (0 なら無制限)。</param>
    /// <param name="ct">キャンセルトークン。</param>
    /// <returns>デコード済み SDP 文字列。</returns>
    public async Task<string> WaitForOfferAsync(string pairId, string fromDeviceId, long minCreatedAt = 0, CancellationToken ct = default)
    {
        // 着信監視ループが約5秒毎に呼ぶため、INFO だとログの大半を埋める。Debug に落とす。
        Util.Logger.Log($"SDP ポーリング開始 (offer): pairId={pairId}, from={Util.Logger.MaskDeviceId(fromDeviceId)}, minCreatedAt={minCreatedAt}", Util.LogLevel.Debug);
        const int PollDelayMs = 400;
        var pollCount = 0;
        var lastErrorLog = 0; // エラーログ抑制用カウンタ
        var consecutiveErrors = 0; // rere #F-012: exponential backoff 用カウンタ

        while (!ct.IsCancellationRequested)
        {
            pollCount++;
            try
            {
                // offers/{fromDeviceId} を 1 回読み、Data と CreatedAt を同時に取得する。
                // 未存在 / null レスポンスは entry=null として扱う (枠超過系は外側 catch へ)。
                TimedSignalingValue? entry = null;
                try
                {
                    entry = await _client
                        .Child("signaling")
                        .Child(pairId)
                        .Child(SignalingPaths.OffersNode)
                        .Child(fromDeviceId)
                        .OnceSingleAsync<TimedSignalingValue>();
                }
                catch (Exception ex) when (!ShouldBackoffOnReadError(ex))
                {
                    // ノード未着/非 HTTP 瞬断 → entry = null として高速ポーリング継続。
                    // 具体的な HTTP ステータス (401/403/404/4xx/5xx/枠超過) は外側 catch へ投げて backoff (#F11)。
                }

                if (entry != null && !string.IsNullOrEmpty(entry.Data)
                    && (minCreatedAt <= 0 || entry.CreatedAt >= minCreatedAt))
                {
                    // rere #A2-001: 不正 base64 はスキップして高速ポーリングを継続（backoff ループに落とさない）。
                    if (TryDecodeBase64(entry.Data, out var sdp))
                    {
                        Util.Logger.Log($"SDP 受信 (offer): pairId={pairId}, ポーリング回数={pollCount}");
                        return sdp;
                    }
                    Util.Logger.Log($"不正な base64 offer を無視: pairId={pairId}", Util.LogLevel.Warning);
                }

                if (pollCount % 30 == 1)
                {
                    Util.Logger.Log($"SDP 待機中 (offer): createdAt={entry?.CreatedAt.ToString() ?? "null"}, 待機回数={pollCount}", Util.LogLevel.Debug);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var statusInfo = DescribeHttpStatus(ex);
                var isQuota = statusInfo is " status=402" or " status=429" or " status=503";
                // rere PR#8 #F11: 認証(401/403)・サーバ(5xx)・枠超過 等の具体 HTTP ステータスは即 Error で surface。
                var isSevere = ShouldBackoffOnReadError(ex);
                if (isSevere || pollCount - lastErrorLog >= 30)
                {
                    Util.Logger.Log(
                        $"SDP ポーリングエラー (offer): {ex.Message}{statusInfo}" +
                        (isQuota ? " (Firebase 枠超過/サービス停止の可能性)" : string.Empty),
                        isSevere ? Util.LogLevel.Error : Util.LogLevel.Warning);
                    lastErrorLog = pollCount;
                }
                consecutiveErrors++;
                var backoffMs = Math.Min(1000 * (1 << Math.Min(consecutiveErrors - 1, 5)), 30_000);
                var jitter = Random.Shared.Next(0, 500);
                await Task.Delay(backoffMs + jitter, ct);
                continue;
            }

            consecutiveErrors = 0;
            await Task.Delay(PollDelayMs, ct);
        }

        throw new OperationCanceledException(ct);
    }

    /// <summary>
    /// rere #D-003: ペア相手 (fromDeviceId=answerer) の answer を per-sender ノード
    /// signaling/{pairId}/answers/{fromDeviceId} からポーリングで待機して取得する。
    /// answer は鮮度 (createdAt) を持たない (offerer が自分の offer 直後に待つ 1:1 応答であり、
    /// 旧 WaitForSdpAsync("answer") も minCreatedAt=0 で運用していた踏襲)。
    /// </summary>
    /// <param name="pairId">ペアリング ID。</param>
    /// <param name="fromDeviceId">読み取り対象の answerer deviceId (=ペア相手)。</param>
    /// <param name="ct">キャンセルトークン。</param>
    /// <returns>デコード済み SDP 文字列。</returns>
    public async Task<string> WaitForAnswerAsync(string pairId, string fromDeviceId, CancellationToken ct = default)
    {
        Util.Logger.Log($"SDP ポーリング開始 (answer): pairId={pairId}, from={Util.Logger.MaskDeviceId(fromDeviceId)}", Util.LogLevel.Debug);
        const int PollDelayMs = 400;
        var pollCount = 0;
        var lastErrorLog = 0;
        var consecutiveErrors = 0;

        while (!ct.IsCancellationRequested)
        {
            pollCount++;
            try
            {
                SignalingValue? value = null;
                try
                {
                    value = await _client
                        .Child("signaling")
                        .Child(pairId)
                        .Child(SignalingPaths.AnswersNode)
                        .Child(fromDeviceId)
                        .OnceSingleAsync<SignalingValue>();
                }
                catch (Exception ex) when (!ShouldBackoffOnReadError(ex))
                {
                    // ノード未着/非 HTTP 瞬断 → value = null として高速ポーリング継続。
                    // 具体的な HTTP ステータス (401/403/404/4xx/5xx/枠超過) は外側 catch へ投げて backoff (#F11)。
                }

                if (value != null && !string.IsNullOrEmpty(value.Data))
                {
                    // rere #A2-001: 不正 base64 はスキップして高速ポーリングを継続（backoff ループに落とさない）。
                    if (TryDecodeBase64(value.Data, out var sdp))
                    {
                        Util.Logger.Log($"SDP 受信 (answer): pairId={pairId}, ポーリング回数={pollCount}");
                        return sdp;
                    }
                    Util.Logger.Log($"不正な base64 answer を無視: pairId={pairId}", Util.LogLevel.Warning);
                }

                if (pollCount % 30 == 1)
                {
                    Util.Logger.Log($"SDP 待機中 (answer): データ未着, 待機回数={pollCount}", Util.LogLevel.Debug);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var statusInfo = DescribeHttpStatus(ex);
                var isQuota = statusInfo is " status=402" or " status=429" or " status=503";
                // rere PR#8 #F11: 認証(401/403)・サーバ(5xx)・枠超過 等の具体 HTTP ステータスは即 Error で surface。
                var isSevere = ShouldBackoffOnReadError(ex);
                if (isSevere || pollCount - lastErrorLog >= 30)
                {
                    Util.Logger.Log(
                        $"SDP ポーリングエラー (answer): {ex.Message}{statusInfo}" +
                        (isQuota ? " (Firebase 枠超過/サービス停止の可能性)" : string.Empty),
                        isSevere ? Util.LogLevel.Error : Util.LogLevel.Warning);
                    lastErrorLog = pollCount;
                }
                consecutiveErrors++;
                var backoffMs = Math.Min(1000 * (1 << Math.Min(consecutiveErrors - 1, 5)), 30_000);
                var jitter = Random.Shared.Next(0, 500);
                await Task.Delay(backoffMs + jitter, ct);
                continue;
            }

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
    /// rere PR#8 #F11: ポーリングの内側 catch が握り潰してよいエラーかを判定する。
    /// 具体的な HTTP ステータス (4xx/5xx — 401/403/404/500/502/504、枠超過 402/429/503 を含む) を持つ
    /// エラーは backoff 対象 (true) として外側 catch に投げ、exponential backoff + Error ログにする。
    /// status を持たない瞬断/非 HTTP 要因 ("" / " status=firebase-error") は従来どおり握り潰し、
    /// offer/answer 未着の常態として高速ポーリングを継続する (待機を遅くしないため)。
    /// 旧 IsQuotaOrServerError は 402/429/503 のみ対象で、401/403/500 を内側 catch が吸収し
    /// 認証/サーバ障害中も 400ms 高速ポーリングを続ける暴走があった (本修正で是正)。
    /// </summary>
    private static bool ShouldBackoffOnReadError(Exception ex)
    {
        var status = DescribeHttpStatus(ex);
        return status.Length > 0 && status != " status=firebase-error";
    }

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
    public async Task SendSdpOfferAsync(string pairId, string senderDeviceId, string sdp, CancellationToken ct = default)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // rere #D-003: top-level createdAt は GitHub Actions の firebase-cleanup.yml が
        // signaling/{pairId} サブツリーの stale 掃除に使う (各 pairId の .createdAt を見て削除判定)。
        // per-sender 化で offer 鮮度判定自体は offers/{sender}.CreatedAt に移ったが、cleanup 互換のため
        // top-level createdAt の書き込みは維持する (撤去すると stale signaling が永久に残る)。
        await _client
            .Child("signaling")
            .Child(pairId)
            .Child("createdAt")
            .PutAsync(nowMs);

        // rere #D-003: offer を送信元 deviceId でキー化した per-sender ノード offers/{senderDeviceId} に書く。
        // Data と CreatedAt を TimedSignalingValue で atomic に書くので、読み手は 1 リクエストで鮮度+本体を取得できる
        // (probe の per-nonce key と同じパターン)。同一 sender の offer-v2 再送は同じキーを上書きする (正しい挙動)。
        var encoded = EncodeBase64(sdp);
        await _client
            .Child("signaling")
            .Child(pairId)
            .Child(SignalingPaths.OffersNode)
            .Child(senderDeviceId)
            .PutAsync(new TimedSignalingValue { Data = encoded, CreatedAt = nowMs });
    }

    /// <summary>
    /// rere #D-003: offers/{fromDeviceId} を 1 回だけ読み取り、デコード済み offer 文字列を返す（未存在・一時エラーは null）。
    /// Offer 側は TCP 失敗報告（answer=needRelay）を受けてから STUN し、ExternalIp 付きの offer-v2 を
    /// 同じ per-sender キーに上書き再送する。Answer 側がその offer-v2 を読み直して UDP ホールパンチへ進むための単発読み取り。
    /// （ポーリングは呼出側が担当するため、ここでは 1 回読んで即返す）
    /// </summary>
    public async Task<string?> TryReadOfferOnceAsync(string pairId, string fromDeviceId, CancellationToken ct = default)
    {
        try
        {
            var entry = await _client
                .Child("signaling")
                .Child(pairId)
                .Child(SignalingPaths.OffersNode)
                .Child(fromDeviceId)
                .OnceSingleAsync<TimedSignalingValue>();
            if (entry != null && !string.IsNullOrEmpty(entry.Data))
                return DecodeBase64(entry.Data);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* ノード未存在 / 一時的な読み取りエラーは null（呼出側が次のポーリングでリトライ） */ }
        return null;
    }

    /// <summary>
    /// rere #D-003: offers/{fromDeviceId} の CreatedAt を 1 回だけ読む（role 調停 deferral の鮮度判定用）。
    /// per-sender 化により「ペア相手が *新しい* offer を出しているか」を相手キー直読みで正確に判定できる
    /// (旧 top-level createdAt は自分の過去 offer でも更新され得て曖昧だった)。未存在 / 一時エラーは null。
    /// </summary>
    public async Task<long?> TryReadOfferCreatedAtAsync(string pairId, string fromDeviceId, CancellationToken ct = default)
    {
        try
        {
            var entry = await _client
                .Child("signaling")
                .Child(pairId)
                .Child(SignalingPaths.OffersNode)
                .Child(fromDeviceId)
                .OnceSingleAsync<TimedSignalingValue>();
            return entry?.CreatedAt;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// SDP Answer を Firebase に書き込む。
    /// </summary>
    public async Task SendSdpAnswerAsync(string pairId, string answererDeviceId, string sdp, CancellationToken ct = default)
    {
        // rere #D-003: answer を answerer の deviceId でキー化した per-sender ノード answers/{answererDeviceId} に書く。
        var encoded = EncodeBase64(sdp);
        await _client
            .Child("signaling")
            .Child(pairId)
            .Child(SignalingPaths.AnswersNode)
            .Child(answererDeviceId)
            .PutAsync(new SignalingValue { Data = encoded });
    }

    /// <summary>
    /// UDP ホールパンチ用の外部エンドポイントを per-sender ノード endpoints/{senderDeviceId} に書き込む。
    /// </summary>
    /// <param name="pairId">ペアリング ID。</param>
    /// <param name="senderDeviceId">送信元 deviceId。endpoints/{senderDeviceId} のキー兼 payload の From 値。</param>
    /// <param name="endpoint">"ip:port" 形式の文字列。</param>
    public async Task SendEndpointAsync(string pairId, string senderDeviceId, string endpoint, CancellationToken ct = default)
    {
        // rere #D-003: endpoint を送信元 deviceId でキー化 (endpoints/{senderDeviceId})。読み手はペア相手の
        // キーをピンポイントで読むので、ペア相手以外の endpoint は構造的に届かない (MITM 一次防御)。
        // rere #D-001: payload にも "from|ip:port" 形式で送信元 deviceId を維持し、読み手の From 一致検証を
        // 二重防護として残す (from は senderDeviceId と同値だが、検証ロジック互換のため payload は据置)。
        var encoded = EncodeBase64($"{senderDeviceId}|{endpoint}");
        await _client
            .Child("signaling")
            .Child(pairId)
            .Child(SignalingPaths.EndpointsNode)
            .Child(senderDeviceId)
            .PutAsync(new SignalingValue { Data = encoded });
        Util.Logger.Log($"外部エンドポイント送信: {Util.Logger.MaskIp(endpoint)}");
    }

    /// <summary>
    /// rere #D-003: UDP ホールパンチ用の外部エンドポイントを per-sender ノード endpoints/{fromDeviceId} から
    /// ポーリングで待機して取得する。キー自体がペア相手の deviceId なので、ペア相手以外の endpoint は構造的に届かない。
    /// rere #D-001: payload に埋め込まれた送信元 deviceId が <paramref name="fromDeviceId"/> と一致するものだけ採用する
    /// 二重防護も残す。区切り無し (形式不正) や From 不一致は偽 endpoint とみなしてスキップし、正規の相手の
    /// endpoint が来るまでポーリングを継続する。
    /// </summary>
    /// <param name="pairId">ペアリング ID。</param>
    /// <param name="fromDeviceId">読み取り対象の送信元 deviceId (=ペア相手)。</param>
    /// <param name="ct">キャンセルトークン。</param>
    public async Task<string> WaitForEndpointAsync(string pairId, string fromDeviceId, CancellationToken ct = default)
    {
        Util.Logger.Log($"外部エンドポイント待機開始: pairId={pairId}, from={Util.Logger.MaskDeviceId(fromDeviceId)}");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var value = await _client
                    .Child("signaling")
                    .Child(pairId)
                    .Child(SignalingPaths.EndpointsNode)
                    .Child(fromDeviceId)
                    .OnceSingleAsync<SignalingValue>();

                if (value?.Data != null)
                {
                    var decoded = DecodeBase64(value.Data);
                    var sep = decoded.IndexOf('|');
                    if (sep > 0)
                    {
                        var from = decoded.Substring(0, sep);
                        var endpoint = decoded.Substring(sep + 1);
                        if (from == fromDeviceId)
                        {
                            Util.Logger.Log($"外部エンドポイント受信: {Util.Logger.MaskIp(endpoint)}");
                            return endpoint;
                        }
                        Util.Logger.Log(
                            $"ペア相手以外の endpoint を破棄: from={Util.Logger.MaskDeviceId(from)}",
                            Util.LogLevel.Warning);
                    }
                    else
                    {
                        Util.Logger.Log("endpoint 形式不正を破棄", Util.LogLevel.Warning);
                    }
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
    ///
    /// Codex P2 fix (第4弾): leaf path DELETE に書き換え。bucket parent DELETE は rules で deny される。
    /// database.rules.json は `signaling/$pairId/offers/$senderDeviceId` 等の leaf にしか .write を許可
    /// していないため、bucket parent (`signaling/{pairId}/offers` 等) を DELETE すると permission_denied
    /// になり stale データ (特に `answers/{peerId}` が次回 `WaitForAnswerAsync` で即消費される) が残る。
    /// pairId は `{deviceA}_{deviceB}` 形式なので Split('_') で両 deviceId を抽出し、
    /// keyed children (offers / answers / endpoints) は `{child}/{deviceId}` の leaf を狙って DELETE する。
    /// createdAt は単独 leaf なのでそのまま DELETE。probeOffers / probeAnswers は per-nonce で sender が
    /// finally で即時 cleanup する (CleanupProbeAsync) ので本メソッドでは扱わない。
    /// </summary>
    public async Task CleanupSignalingDataAsync(string pairId, CancellationToken ct = default)
    {
        try
        {
            var ids = pairId.Split('_');
            if (ids.Length != 2)
            {
                Util.Logger.Log($"signaling cleanup: pairId 形式不正 (skip): {pairId}", Util.LogLevel.Warning);
                return;
            }
            // keyed children は leaf (`{child}/{deviceId}`) を直接 DELETE する。
            // 1 つ失敗しても他の cleanup は続行する best-effort。
            //
            // 注: rules (`auth.uid == $senderDeviceId` / `$answererDeviceId`) により、自分の deviceId で
            // 相手 leaf を DELETE する操作は permission_denied になり no-op になる。実害は無い
            // (次回接続時に相手が同 leaf を PUT で上書きする last-write-wins で stale 残留は起きない) が、
            // 両 deviceId を defensive に試行しておくのは将来 rules が緩和されたとき自動で全消去動作になる
            // ようにするため。
            var keyedChildren = new[] { "offers", "answers", "endpoints" };
            foreach (var child in keyedChildren)
            {
                foreach (var devId in ids)
                {
                    try { await _client.Child("signaling").Child(pairId).Child(child).Child(devId).DeleteAsync(); }
                    catch (Exception ex) { Util.Logger.Log($"  signaling/{pairId}/{child}/{devId[0..Math.Min(8, devId.Length)]}.. 削除失敗 (継続): {ex.Message}", Util.LogLevel.Debug); }
                }
            }
            try { await _client.Child("signaling").Child(pairId).Child("createdAt").DeleteAsync(); }
            catch (Exception ex) { Util.Logger.Log($"  signaling/{pairId}/createdAt 削除失敗 (継続): {ex.Message}", Util.LogLevel.Debug); }
            Util.Logger.Log($"シグナリングデータ削除: {pairId}");
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"シグナリングデータ削除エラー: {ex.Message}", Util.LogLevel.Warning);
        }
    }

    /// <summary>
    /// Codex P2 fix (第5弾 #4): pairing 成立後に <c>sessions/{sid}</c> と <c>pairing_nonces/{sid}</c> を即時 revoke する。
    /// QR URL が外部に漏れていても 1h (Workers /pair/token の nonce 受理 TTL) を待たず bridge token mint を不可にする。
    /// success 経路 (<see cref="ConnectionService.OnPairingDetected"/>) と cancel 経路 (<see cref="CleanupAsync"/>) の
    /// 両方から呼ばれる想定で best-effort 化 (個別 DELETE 失敗は warn ログのみで握りつぶす)。
    /// </summary>
    public async Task RevokePairingTokensAsync(string sid, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sid)) return;
        try { await _client.Child("sessions").Child(sid).DeleteAsync(); }
        catch (Exception ex) { Util.Logger.Log($"sessions/{Util.Logger.MaskDeviceId(sid)} 即時 revoke 失敗 (継続): {ex.Message}", Util.LogLevel.Debug); }
        try { await _client.Child("pairing_nonces").Child(sid).DeleteAsync(); }
        catch (Exception ex) { Util.Logger.Log($"pairing_nonces/{Util.Logger.MaskDeviceId(sid)} 即時 revoke 失敗 (継続): {ex.Message}", Util.LogLevel.Debug); }
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
                // Codex P2 fix (第2弾→第5弾 #4): session + pairing_nonces を即破棄する。
                // 残しておくと cancel 後も Workers /pair/token が同 nonce で 1h 内 Custom Token を発行できてしまう。
                // success 経路 (OnPairingDetected) と共通化するため RevokePairingTokensAsync に集約。
                await RevokePairingTokensAsync(_sessionId, ct);
            }
            if (!string.IsNullOrEmpty(pairingId))
            {
                // CodeRabbit 第4弾 fix: 旧 `pairings/{pairingId}` の 2 セグメント DELETE は Phase B per-device 再構成
                // (pairings/{sidA}/{pid} + pairings/{sidB}/{pid}) と path がミスマッチで no-op (rule 上も deny で例外
                // catch されていた) になっていたため削除。本来の per-device DELETE は ConnectionService 側に `pid`
                // (SubmitPairingAsync が払い出す 20 char) を持たせる設計変更が必要なので Phase B-2 へ defer。
                // 当面の pairings 掃除は firebase-cleanup.yml (admin SDK、1h 以上 stale を削除) に委譲する。
                // signaling/{pairId} の parent DELETE も rules で deny されるので CleanupSignalingDataAsync
                // (child 個別 DELETE) を使う。
                await CleanupSignalingDataAsync(pairingId);
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
        // rere PR#8 #F4: FirebaseDatabase.net の PutAsync は CancellationToken を受け取れないため、
        // 入口で cancel を弾き、await を WaitAsync(ct) で監視して dispose/停止時に待機を即解く
        // (進行中の PUT 自体は best-effort で継続しうるが、呼び出し側 HeartbeatLoop は即座に解放される)。
        ct.ThrowIfCancellationRequested();
        await _client
            .Child("presence")
            .Child(deviceId)
            .PutAsync(new PresenceData
            {
                LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DisplayName = displayName,
                Version = AppVersion.Value,  // #D-001a Phase B / Q5: 両 PC v1.0.62 機械検証用
            })
            .WaitAsync(ct);
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
        // #D-001a Phase B: 独自 HttpClient 経路には FirebaseClient の AuthTokenAsyncFactory が効かないので、
        // ?auth=<idToken> を URL に直接付与する（Realtime DB REST の標準 auth クエリ）。
        var auth = await GetAuthQueryAsync();
        var url = $"{_databaseUrl}/presence/{Uri.EscapeDataString(deviceId)}/LastSeen.json{auth}";
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
            {
                // rere #F-002: 401/403(rules 誤更新)・5xx(枠超過/障害) を黙って null に倒すと、
                // 全ピア offline 表示の原因がログに残らず切り分け不能になる。スロットル付きで surface する。
                var code = (int)resp.StatusCode;
                if (code is 401 or 403 || code >= 500)
                    LogPresenceErrorThrottled($"HTTP {code}");
                return null;
            }

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
        catch (Exception ex)
        {
            // rere #F-002: ネットワーク断等の継続失敗も痕跡を残す（スロットル付き）。
            LogPresenceErrorThrottled(ex.Message);
            return null;
        }
    }

    /// <summary>rere #F-002: presence 取得失敗を 60s に 1 度だけ Warning ログする（氾濫防止）。
    /// rules 誤更新（read deny）やネットワーク断が「全員オフライン表示」として現れたとき、
    /// 原因がログに残るようにするための診断用。</summary>
    private void LogPresenceErrorThrottled(string detail)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _presenceErrorLogTick);
        if (now - last < 60_000) return;
        if (Interlocked.CompareExchange(ref _presenceErrorLogTick, now, last) != last) return;
        Util.Logger.Log($"presence 取得失敗（rules 誤更新/ネットワーク断の疑い）: {detail}", Util.LogLevel.Warning);
    }

    /// <summary>rere #C2-001: ペア削除時に presence ETag キャッシュから該当 deviceId を除去し、
    /// 削除済みピアの stale エントリが Dispose まで残るのを防ぐ。</summary>
    public void ForgetPresence(string deviceId) => _presenceCache.TryRemove(deviceId, out _);

    // === #D-001a Phase B: pairs/{pairId} SSoT ノードの CRUD ===

    /// <summary>
    /// pairs/{pairId} を書き込む（ペア成立時の責任者書込・fallback 書込・両方の入口）。
    /// rules で `auth.uid` が pairId 当事者のときだけ書ける。
    /// </summary>
    public async Task PutPairAsync(string pairId, PairRecord record, CancellationToken ct = default)
    {
        await _client
            .Child("pairs")
            .Child(pairId)
            .PutAsync(record)
            .WaitAsync(ct);
    }

    /// <summary>
    /// pairs/{pairId} を取得する（存在しなければ null）。書込セルフチェック + fallback 用。
    /// </summary>
    public async Task<PairRecord?> GetPairAsync(string pairId, CancellationToken ct = default)
    {
        try
        {
            return await _client
                .Child("pairs")
                .Child(pairId)
                .OnceSingleAsync<PairRecord>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// pairs/{pairId} を独自 HttpClient 経由で取得し、HTTP ステータスと body を返す。
    /// PairSyncService の robustness 強化用（404/null/401/5xx を区別して削除判定する）。
    /// </summary>
    public async Task<(HttpStatusCode Status, string Body)> GetPairWithStatusAsync(string pairId, CancellationToken ct = default)
    {
        var auth = await GetAuthQueryAsync();
        var url = $"{_databaseUrl}/pairs/{Uri.EscapeDataString(pairId)}.json{auth}";
        using var resp = await _http.GetAsync(url, ct);
        var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();
        return (resp.StatusCode, body);
    }

    /// <summary>
    /// pairs/{pairId} を削除する（ペア解除の Firebase SSoT 反映）。
    /// </summary>
    public async Task DeletePairAsync(string pairId, CancellationToken ct = default)
    {
        await _client
            .Child("pairs")
            .Child(pairId)
            .DeleteAsync()
            .WaitAsync(ct);
    }

    /// <summary>独自 HttpClient 経路向けに `?auth=<idToken>` クエリ文字列を組み立てる。</summary>
    private async Task<string> GetAuthQueryAsync()
    {
        if (_authClient == null) return string.Empty;
        try
        {
            var token = await _authClient.GetIdTokenAsync();
            if (string.IsNullOrEmpty(token)) return string.Empty;
            return "?auth=" + Uri.EscapeDataString(token);
        }
        catch
        {
            return string.Empty;
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
        if (_authClient != null && _idTokenRefreshedHandler != null)
        {
            _authClient.IdTokenRefreshed -= _idTokenRefreshedHandler;
            _idTokenRefreshedHandler = null;
        }
        _client.Dispose();
    }

    private static string EncodeBase64(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string DecodeBase64(string encoded) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(encoded));

    /// <summary>rere #A2-001: 不正 base64 を例外でなく false で返す版。匿名書き込みで細工された
    /// offer/answer ノードの不正 base64 が FormatException → 外側 catch の backoff ループに落ちて
    /// 接続確立を ct タイムアウトまでスタックさせる DoS を防ぐ（probe 経路は元から try/catch 済み）。</summary>
    private static bool TryDecodeBase64(string encoded, out string decoded)
    {
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return true;
        }
        catch (FormatException)
        {
            decoded = string.Empty;
            return false;
        }
    }
}

/// <summary>Firebase に書き込むセッションデータ。</summary>
public sealed class SessionData
{
    public string DisplayName { get; set; } = string.Empty;
    public long CreatedAt { get; set; }

    /// <summary>rere #D-001(b): このデバイスの長期公開鍵(base64url SPKI)。
    /// コード貼付ペアリング経路で相手が CheckSession 時に読み取り、PairSecret 導出に使う。
    /// 旧クライアントは未設定(空) → 相手は PairSecret を導出できず平文フォールバック。</summary>
    public string PublicKey { get; set; } = string.Empty;
}

/// <summary>
/// rere #D-001(a) Phase B (Codex P1 fix): PairingNonce はかつて <see cref="SessionData"/> に同梱されていたが
/// `sessions/{sid}` が任意の認証済みデバイスから read 可なため ghost peer 攻撃面となっていた。
/// 別ノード `pairing_nonces/{sid}` に分離し、rules で `.read: false` (server only) として
/// クライアントから一切読めないようにする。Workers /pair/token のみが SA 経由で読む。
/// </summary>
public sealed class PairingNonceRecord
{
    public string Nonce { get; set; } = string.Empty;
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

    /// <summary>rere #D-001(b): PC-A / PC-B の長期公開鍵(base64url SPKI)。
    /// Bridge 経路は両 QR の pk を、コード貼付経路は自分の pk と session から読んだ相手 pk を載せる。
    /// 各 PC は相手側の pk を読んで PairSecret を導出する。旧データは空 → 平文フォールバック。</summary>
    public string PkA { get; set; } = string.Empty;
    public string PkB { get; set; } = string.Empty;
}

// AOT: pairings の root PATCH atomic update body をシリアライズするための SourceGen context。
[System.Text.Json.Serialization.JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, PairingData>))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class MultiPathPairingsJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }

/// <summary>Firebase に書き込むシグナリングデータのラッパー。
/// PutAsync/PostAsync に string を直接渡すと JSON としてシリアライズされず
/// Firebase REST API に拒否されるため、オブジェクトに包んで送る。</summary>
public sealed class SignalingValue
{
    public string Data { get; set; } = string.Empty;
}

/// <summary>
/// rere #D-001(a) Phase B: pairs/{pairId} ノードの SSoT データ（永続・cleanup 対象外）。
/// ペア成立時に責任者 PC が書き込み、両 PC が <see cref="FirebaseSignaling.GetPairAsync"/> で存在チェック、
/// 削除時に <see cref="FirebaseSignaling.DeletePairAsync"/> で消す。1ヶ月オフラインから戻った PC も
/// この存在/不在で「相手が削除したか」を判定する（PairSyncService）。
/// </summary>
public sealed class PairRecord
{
    public string PairId { get; set; } = string.Empty;
    public string NameA { get; set; } = string.Empty;
    public string NameB { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
}

/// <summary>v1.0.38 review fix v14: probe 専用、子要素 data + createdAt を 1 オブジェクトで持つ。
/// per-nonce key (signaling/{pairId}/probeOffers/{nonce}) 配下の全フィールドを一括 OnceAsync で
/// 取得するため、Data と CreatedAt を同一型に packed する。</summary>
public sealed class TimedSignalingValue
{
    public string Data { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
}

/// <summary>ペアリング検知情報。</summary>
public sealed class PairingInfo
{
    public string PairingId { get; set; } = string.Empty;
    public string PeerId { get; set; } = string.Empty;
    public string PeerDisplayName { get; set; } = string.Empty;
    public bool IsInitiator { get; set; }

    /// <summary>rere #D-001(b): ペア相手の長期公開鍵(base64url SPKI)。空なら PairSecret 未確立(平文)。</summary>
    public string PeerPublicKey { get; set; } = string.Empty;
}
