using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Infrastructure;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// rere #D-001(a) Phase B §6.2: pairs/{pairId} SSoT のローカル同期サービス。
///
/// 起動時即 + 5min + 1h のハイブリッドポーリングで Firebase pairs/{pairId} を GET し、
/// 404 を検出したら相手が削除したと判定してローカル peers.json から該当ペアを削除する。
///
/// Robustness（peers.json 全消失の不可逆破壊を防ぐ）:
///   - 404/null のときだけ削除候補（401/403/5xx/timeout/network error は『不明』として未操作）
///   - N=3 回連続 404 で初めて削除（一時的な伝播遅延の誤検出を防ぐ）
///   - 起動直後 5min の grace period（初回 fetch のみ例外で許可。bootstrap race を避ける）
///   - Visibility gate: <see cref="SetActive"/> false の間はループ停止（presence と同方針・帯域節約）
/// </summary>
public sealed class PairSyncService : IDisposable
{
    private readonly Func<string, CancellationToken, Task<(HttpStatusCode Status, string Body)>> _fetchPair;
    private readonly Func<string, PairRecord, CancellationToken, Task>? _putPair;
    private readonly IPeerRegistryService _peerRegistry;
    private readonly string _deviceId;
    private readonly ConcurrentDictionary<string, int> _consecutive404 = new();
    /// <summary>Codex P1 fix: 旧 peers.json 由来の既存ペアは pairs/{pairId} が未作成なので、責任者側が初回 backfill を試みる。Codex P2 fix (第4弾): 成功時のみ marker を set する (失敗時はマーカーを残さず次サイクルで再試行)。</summary>
    private readonly ConcurrentDictionary<string, byte> _backfillAttempted = new();
    private const int Consecutive404Threshold = 3;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private readonly TimeSpan _gracePeriod = TimeSpan.FromMinutes(5);
    private CancellationTokenSource? _cts;
    private volatile bool _isActive = true;

    public PairSyncService(FirebaseSignaling signaling, IPeerRegistryService peerRegistry, string deviceId)
        : this(
            (pairId, ct) => signaling.GetPairWithStatusAsync(pairId, ct),
            (pairId, record, ct) => signaling.PutPairAsync(pairId, record),
            peerRegistry,
            deviceId)
    {
    }

    /// <summary>
    /// テスト用コンストラクタ。FirebaseSignaling は sealed のため、HTTP 取得デリゲートを直接差し替えて
    /// 404 閾値 / grace period / 401 未操作などのロジックを単体検証する。
    /// 本番経路は <see cref="PairSyncService(FirebaseSignaling, IPeerRegistryService, string)"/> を使う。
    /// </summary>
    internal PairSyncService(
        Func<string, CancellationToken, Task<(HttpStatusCode Status, string Body)>> fetchPair,
        IPeerRegistryService peerRegistry,
        string deviceId)
        : this(fetchPair, null, peerRegistry, deviceId)
    {
    }

    private PairSyncService(
        Func<string, CancellationToken, Task<(HttpStatusCode Status, string Body)>> fetchPair,
        Func<string, PairRecord, CancellationToken, Task>? putPair,
        IPeerRegistryService peerRegistry,
        string deviceId)
    {
        _fetchPair = fetchPair;
        _putPair = putPair;
        _peerRegistry = peerRegistry;
        _deviceId = deviceId;
    }

    /// <summary>同期ループを開始する。起動時に 1 回呼ぶ。</summary>
    public void Start()
    {
        // 再 Start 時は古い CTS をキャンセルしてから Dispose（リソースリーク防止 / CodeRabbit 指摘）
        var old = _cts;
        old?.Cancel();
        old?.Dispose();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Visibility gate。MainWindow の前面 / 最小化に応じてオン/オフする。</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            // 起動時即: gracePeriod を例外的に通過させる（最初のチェックは「相手が長期オフライン中に
            // 削除した」を即拾うため。連続 404 カウンタ閾値=3 で誤検出は防げる）。
            await CheckOnceAsync(applyGracePeriod: false, ct);

            // 5min 後（gracePeriod 経過後の確認）
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); }
            catch (OperationCanceledException) { return; }
            if (_isActive) await CheckOnceAsync(applyGracePeriod: true, ct);

            // 以降 15min ごと (Codex P2 fix: 旧 1h ポーリングだと 3 回連続必要 = 最悪 3h、PR の 1h 反映契約に違反した。
            // 15min なら最悪 45min で 1h 以内に収まる。presence ETag は別経路で、これは pairs/{id} を読むだけなので
            // 帯域は 1 ペアあたり ~100B/15min ≪ 無料枠)。
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(15), ct); }
                catch (OperationCanceledException) { return; }
                if (_isActive) await CheckOnceAsync(applyGracePeriod: true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Util.Logger.Log($"PairSyncService ループ予期せぬエラー: {ex.Message}", Util.LogLevel.Warning);
        }
    }

    internal async Task CheckOnceAsync(bool applyGracePeriod, CancellationToken ct)
    {
        var inGrace = applyGracePeriod && DateTime.UtcNow - _startedAtUtc < _gracePeriod;
        // 404 連続閾値到達時に RemovePeerAsync で peerRegistry を変更するため、列挙中の collection 改変を
        // 避けて snapshot を取る（InvalidOperationException 防止 / CodeRabbit 指摘）。
        // Codex 第6弾 #5 (P2): snapshot 取得自体が UI/pairing 側 mutation と競合して例外を投げる可能性が
        // あるため、try/catch で囲ってループを永久終了させない（次サイクルで再試行）。
        // PeerRegistryService 側でも lock を入れているのでこの catch はフォールバック。
        List<PairedPeer> peers;
        try
        {
            peers = _peerRegistry.GetPairedPeers().ToList();
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"PairSyncService snapshot 取得失敗 (次サイクルで再試行): {ex.Message}", Util.LogLevel.Warning);
            return;
        }
        foreach (var peer in peers)
        {
            if (ct.IsCancellationRequested) return;
            var pairId = GeneratePairId(_deviceId, peer.PeerId);
            try
            {
                var (status, body) = await _fetchPair(pairId, ct);
                if (status == HttpStatusCode.OK && body != "null")
                {
                    _consecutive404[peer.PeerId] = 0;  // 存在確認できたらカウンタリセット
                    // Codex P1 fix (第2弾): 一度でも SSoT を観察したら以後 backfill 不可にして remote unpair を resurrect
                    // しないようにする。peers.json に永続するので再起動後も観察済みフラグが保たれる。
                    if (!peer.PairsSsotObserved)
                    {
                        // Codex P2 fix (第9弾 #6): snapshot 取得 → AddOrUpdatePeerAsync 呼出までに manual remove が
                        // 走ると、registry に居ない peer を AddOrUpdate の「不在なら Add」分岐で resurrect してしまう。
                        // FindPeer で再 check し、不在ならスキップする (= ユーザーの remove 意図を尊重)。
                        if (_peerRegistry.FindPeer(peer.PeerId) == null)
                        {
                            Util.Logger.Log($"PairSync 200 観察したが peer はローカルから削除済 → 永続化 skip", Util.LogLevel.Debug);
                            continue;
                        }
                        peer.PairsSsotObserved = true;
                        try { await _peerRegistry.AddOrUpdatePeerAsync(peer); }
                        catch (Exception ex) { Util.Logger.Log($"PairsSsotObserved 永続化に失敗 (継続): {ex.Message}", Util.LogLevel.Debug); }
                    }
                }
                else if (status == HttpStatusCode.NotFound || (status == HttpStatusCode.OK && body == "null"))
                {
                    // Codex P1 fix (第2弾): backfill は **未観察 (= 旧 peers.json upgrade 由来)** の peer に限定する。
                    // 新規 PairingDetected で AddOrUpdatePeerAsync された peer は最初から PairsSsotObserved=true で
                    // 入るので、相手が削除した時の 404 を backfill で resurrect する誤りを防げる。
                    // backfill 成功なら以降の 404 は発生しない。失敗・非責任者・観察済みは従来通り 3 連続 404 で削除。
                    var isResponsible = string.Compare(_deviceId, peer.PeerId, StringComparison.Ordinal) < 0;
                    if (isResponsible && !peer.PairsSsotObserved && _putPair != null && !_backfillAttempted.ContainsKey(peer.PeerId))
                    {
                        try
                        {
                            // Codex P2 fix (第11弾 #5): PUT 前にも FindPeer で再 check する。
                            // snapshot 取得後 → PUT 発射までに manual remove が走ると、 stale peer 参照で PUT が走り
                            // SSoT を recreate して remote unpair 拒否される race を防ぐ。
                            if (_peerRegistry.FindPeer(peer.PeerId) == null)
                            {
                                Util.Logger.Log($"PairSync backfill PUT 前に peer がローカル削除済 → PUT skip", Util.LogLevel.Debug);
                                continue;
                            }
                            await _putPair(pairId, new PairRecord
                            {
                                PairId = pairId,
                                NameA = string.Empty,  // 既存 peers.json から名前を引き戻すのは別 issue
                                NameB = peer.DisplayName ?? string.Empty,
                                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            }, ct);
                            // Codex P2 fix (第4弾): 成功時のみ marker を set する。PUT 前に立てると transient 失敗で
                            // セッション中 backfill が永久 skip され、削除 guard と相まって peer も SSoT もない宙ぶらりん
                            // 状態になる。
                            _backfillAttempted.TryAdd(peer.PeerId, 0);  // 成功時のみ marker
                            Util.Logger.Log($"pairs/{pairId} backfill 成功 (legacy peer)");
                            _consecutive404[peer.PeerId] = 0;
                            // Codex P2 fix (第9弾 #6): backfill 成功も同じ re-check。snapshot 取得後の manual remove で
                            // peer が消えていれば AddOrUpdate の resurrect 分岐を踏ませない。
                            if (_peerRegistry.FindPeer(peer.PeerId) == null)
                            {
                                Util.Logger.Log($"backfill 成功したが peer はローカルから削除済 → 永続化 skip", Util.LogLevel.Debug);
                                continue;
                            }
                            peer.PairsSsotObserved = true;
                            try { await _peerRegistry.AddOrUpdatePeerAsync(peer); }
                            catch (Exception ex) { Util.Logger.Log($"PairsSsotObserved 永続化に失敗 (継続): {ex.Message}", Util.LogLevel.Debug); }
                            continue;
                        }
                        catch (OperationCanceledException) { throw; }  // CodeRabbit: shutdown キャンセルは即座にループへ伝播
                        catch (Exception ex)
                        {
                            Util.Logger.Log($"pairs/{pairId} backfill 失敗 → 次サイクルで再試行: {ex.Message}", Util.LogLevel.Debug);
                            // fall through to counting (marker は立てないので次サイクルで再試行可能)
                        }
                    }
                    // Firebase は GET で「存在しない」を 200 + null body で返すケースがある
                    var count = _consecutive404.AddOrUpdate(peer.PeerId, 1, (_, n) => n + 1);
                    Util.Logger.Log($"pairs/{pairId} 不在検出 ({count}/{Consecutive404Threshold})", Util.LogLevel.Debug);
                    if (!inGrace && count >= Consecutive404Threshold)
                    {
                        // Codex P1 fix (第3弾): 観察未済 (legacy peer) は責任者側 PC がまだ upgrade してないだけの
                        // 可能性が高い。非責任者は backfill しない設計上、観察前に 3 連続 404 で消すと「片方だけ
                        // upgrade で片方未 upgrade」のロールアウト期に正当ペアを失う。SSoT が 1 度でも観察できる
                        // まで削除を延期する (= 責任者が upgrade して書くか、明示的に DELETE するまで残す)。
                        if (!peer.PairsSsotObserved)
                        {
                            Util.Logger.Log($"pairs/{pairId} 連続 {count} 回不在だが未観察 (legacy peer) のため削除を延期");
                            continue;
                        }
                        Util.Logger.Log($"pairs/{pairId} が連続 {count} 回不在 → ローカル削除");
                        await _peerRegistry.RemovePeerAsync(peer.PeerId);
                        _consecutive404.TryRemove(peer.PeerId, out _);
                    }
                }
                // 401/403/5xx/その他 → 『不明』として未操作（カウンタもリセットしない）
                else
                {
                    Util.Logger.Log($"pairs/{pairId} 取得が HTTP {(int)status} → 不明判定で未操作", Util.LogLevel.Debug);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Util.Logger.Log($"PairSyncService GET エラー（peer={Util.Logger.MaskDeviceId(peer.PeerId)}）: {ex.Message}", Util.LogLevel.Debug);
            }
        }
    }

    private static string GeneratePairId(string a, string b)
        => string.Compare(a, b, StringComparison.Ordinal) < 0 ? $"{a}_{b}" : $"{b}_{a}";

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
