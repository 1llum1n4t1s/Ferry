using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Infrastructure;

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
    private readonly IPeerRegistryService _peerRegistry;
    private readonly string _deviceId;
    private readonly ConcurrentDictionary<string, int> _consecutive404 = new();
    private const int Consecutive404Threshold = 3;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private readonly TimeSpan _gracePeriod = TimeSpan.FromMinutes(5);
    private CancellationTokenSource? _cts;
    private volatile bool _isActive = true;

    public PairSyncService(FirebaseSignaling signaling, IPeerRegistryService peerRegistry, string deviceId)
        : this((pairId, ct) => signaling.GetPairWithStatusAsync(pairId, ct), peerRegistry, deviceId)
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
    {
        _fetchPair = fetchPair;
        _peerRegistry = peerRegistry;
        _deviceId = deviceId;
    }

    /// <summary>同期ループを開始する。起動時に 1 回呼ぶ。</summary>
    public void Start()
    {
        _cts?.Cancel();
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

            // 以降 1h ごと（presence ETag 304 で帯域はほぼゼロ）
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromHours(1), ct); }
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
        foreach (var peer in _peerRegistry.GetPairedPeers())
        {
            if (ct.IsCancellationRequested) return;
            var pairId = GeneratePairId(_deviceId, peer.PeerId);
            try
            {
                var (status, body) = await _fetchPair(pairId, ct);
                if (status == HttpStatusCode.OK && body != "null")
                {
                    _consecutive404[peer.PeerId] = 0;  // 存在確認できたらカウンタリセット
                }
                else if (status == HttpStatusCode.NotFound || (status == HttpStatusCode.OK && body == "null"))
                {
                    // Firebase は GET で「存在しない」を 200 + null body で返すケースがある
                    var count = _consecutive404.AddOrUpdate(peer.PeerId, 1, (_, n) => n + 1);
                    Util.Logger.Log($"pairs/{pairId} 不在検出 ({count}/{Consecutive404Threshold})", Util.LogLevel.Debug);
                    if (!inGrace && count >= Consecutive404Threshold)
                    {
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
