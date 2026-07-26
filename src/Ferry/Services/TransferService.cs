using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Infrastructure;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// ファイル転送サービスの本実装。
/// FileChunker / TransferProtocol を使って、接続済みの IConnectionService 経由で
/// チャンクベースのファイル送受信、プログレス通知、SHA-256 検証、レジュームを行う。
/// </summary>
public sealed class TransferService : ITransferService, IDisposable
{
    /// <summary>承認待ち中にバッファできるチャンクの合計上限（OOM 防止）。1 transfer あたり。</summary>
    private const long MaxApprovalBufferBytes = 64L * 1024 * 1024;

    /// <summary>rere #C2-002: 全承認待ち transfer 合算のバッファ上限。per-transfer 上限(64MB)だけだと
    /// 細工 peer が多数の承認待ち transfer を並行生成して N×64MB を積めるため、全体でも頭打ちにする。</summary>
    private const long MaxTotalApprovalBufferBytes = 256L * 1024 * 1024;

    /// <summary>v1.0.38 review fix #2: 送信側が FileApprove を待つ最大秒数。
    /// このタイムアウトを超えたら送信を Cancelled に遷移させる (永久停止を防ぐ)。</summary>
    private const int SendApprovalTimeoutSeconds = 60;

    /// <summary>v1.0.46: フロー制御で受信側 FlowAck を待つ間、進捗が一切進まない状態の最大許容ミリ秒。
    /// これを超えたら受信側停止/切断とみなして送信を打ち切る (永久待機を防ぐ)。</summary>
    private const int FlowAckStallTimeoutMs = 60_000;

    private readonly IConnectionService _connectionService;
    private readonly ISettingsService _settingsService;

    /// <summary>送信側のアップロード帯域制限。0 で無制限。Settings.UploadKBps に追従する。</summary>
    private readonly Util.TokenBucket _uploadBucket = new();

    /// <summary>受信側のダウンロード帯域制限。0 で無制限。Settings.DownloadKBps に追従する。</summary>
    private readonly Util.TokenBucket _downloadBucket = new();

    /// <summary>送信中の転送アイテム（レジューム用に保持）。</summary>
    private readonly ConcurrentDictionary<Guid, TransferItem> _activeTransfers = new();

    /// <summary>受信中の転送状態。TransferId → 受信状態。
    /// P-6: キーを string → Guid に変更し、毎チャンクの ToString() ヒープ確保 (1GB で 9MB) を撤去。</summary>
    private readonly ConcurrentDictionary<Guid, ReceiveState> _receiveStates = new();

    /// <summary>フォルダ受信時のルートフォルダ名マッピング（(peerId, 元の名前) → リネーム後の名前）。
    /// 同一フォルダの全ファイルを同じ先に保存しつつ、別ピアの同名ルートが混ざらないようピアごとに分離する。</summary>
    private readonly ConcurrentDictionary<(string PeerId, string RootFolder), string> _folderMappings = new();

    /// <summary>承認待ちの転送状態（TransferId → ReceiveState）。承認/拒否後に _receiveStates へ移動。</summary>
    private readonly ConcurrentDictionary<Guid, ReceiveState> _pendingApprovals = new();

    /// <summary>送信側の承認待ち（TransferId → TaskCompletionSource）。
    /// v1.0.38: 送信側は FileMeta 送信後、FileApprove または FileReject を受信するまで
    /// チャンク送信を待機する。これで承認前の大量チャンク到着 → バッファ上限超過バグを解決する。</summary>
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _pendingSendApprovals = new();

    /// <summary>v1.0.47: 送信中転送のキャンセル用 CTS（TransferId → 呼び出し ct と連結した CTS）。
    /// CancelTransfer（自側操作）/ HandleFileReject（相手側キャンセル通知）から Cancel して
    /// SendChunksAsync の送信ループを即停止させる。SendFileAsync の finally で除去・Dispose する。</summary>
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sendCts = new();

    /// <summary>v1.0.47: 一時停止中の送信 TransferId 集合（値は未使用、存在判定のみ）。
    /// PauseSendTransfer で追加、ResumeSendTransfer で除去。SendChunksAsync のループがここを見て待機する。</summary>
    private readonly ConcurrentDictionary<Guid, byte> _pausedSends = new();

    /// <summary>複数ペア同時接続対応 Stage 0: TransferId → 宛先/受信元 peerId(SessionId) の索引。
    /// 送信は SendFileAsync が記入、受信は HandleFileMeta が記入する（Stage 2 で配線）。
    /// Stage 2 以降で SendFlowAckAsync / SendRejectFireAndForget / フロー制御 Route 判定が
    /// この索引から transfer の宛先 peer を引いて返送・判定する（受信中の FlowAck が他 peer に
    /// 漏れる blocker の根治）。OnConnectionLost(peerId) も当該 peer の transfer のみに絞り込む。
    /// 現状(Stage 0)は誰も参照しないので挙動不変。</summary>
    private readonly ConcurrentDictionary<Guid, string> _transferPeerId = new();

    public event EventHandler<TransferItem>? ProgressChanged;
    public event EventHandler<TransferItem>? FileReceived;
    public event EventHandler<TransferItem>? TransferError;
    public event EventHandler<TransferItem>? ApprovalRequested;

    /// <summary>送信中・受信中・承認待ちのいずれかが進行中なら true（ConcurrentDictionary 参照のみ・ロック不要）。</summary>
    public bool HasActiveTransfer =>
        !_activeTransfers.IsEmpty || !_receiveStates.IsEmpty || !_pendingApprovals.IsEmpty || !_pendingSendApprovals.IsEmpty;

    /// <summary>
    /// AppSettings の UploadKBps / DownloadKBps を _uploadBucket / _downloadBucket に反映する。
    /// SettingsViewModel が帯域制限を変更した直後にこれを呼ぶことで、進行中の転送にも次回チャンクから即時反映される。
    /// </summary>
    public void SyncRateLimits()
    {
        var s = _settingsService.Settings;
        _uploadBucket.SetRate(s.UploadKBps);
        _downloadBucket.SetRate(s.DownloadKBps);
    }

    public TransferService(IConnectionService connectionService, ISettingsService settingsService)
    {
        _connectionService = connectionService;
        _settingsService = settingsService;

        // 帯域制限器に現在の設定値を反映 (起動時 + 以降は SettingsViewModel から SyncRateLimits 経由で更新)
        SyncRateLimits();

        // 受信データハンドラを登録
        _connectionService.DataReceived += OnDataReceived;
        // rere レビュー #D-005 / #F-014: 切断時に進行中転送と承認待ちを全部 cleanup する。
        // 旧実装は ConnectionLost を購読しておらず、Wi-Fi 瞬断等で部分受信ファイルが
        // Downloads にゴミとして残り続けていた。さらに UI 状態も整合性が崩れる。
        _connectionService.ConnectionLost += OnConnectionLost;
    }

    /// <summary>
    /// rere レビュー #C2-005: TransferService が DataReceived / ConnectionLost を購読するので、
    /// IDisposable で明示的に unsubscribe する。Singleton 想定では本番影響なしだが、テスト時の
    /// 偽陽性および将来のマルチピア化対応のために必須。
    /// </summary>
    public void Dispose()
    {
        _connectionService.DataReceived -= OnDataReceived;
        _connectionService.ConnectionLost -= OnConnectionLost;

        // 進行中送信の CTS を後始末（終了時にループを抜けさせ、ハンドルを解放する）
        foreach (var tid in _sendCts.Keys.ToArray())
        {
            if (_sendCts.TryRemove(tid, out var cts))
            {
                try { cts.Cancel(); } catch { /* ignore */ }
                cts.Dispose();
            }
        }

        // 一時停止フラグも掃除（CTS Cancel でループは抜けるが、辞書エントリを残さない）
        _pausedSends.Clear();
    }

    /// <summary>
    /// rere レビュー #D-005 / #F-014: 切断検知時の cleanup。受信中ファイル (_receiveStates) を
    /// 全削除 + 部分ファイル削除、承認待ち (_pendingApprovals) も Cancelled に遷移して
    /// UI に通知する。送信中 (_activeTransfers) は SendFileAsync 内の SendAsync 例外で
    /// 既に catch されるのでここでは触らない。
    /// CodeRabbit 指摘: 送信側承認待ち (_pendingSendApprovals) も解放しないと、FileMeta 送信後の
    /// 切断で最大 60 秒 Pending が残り UX が悪化する。TCS を TrySetResult(false) で完了させて
    /// WaitForApprovalAsync が Cancelled に遷移するようにする。
    /// </summary>
    private void OnConnectionLost(object? sender, Infrastructure.ConnectionLostEventArgs e)
    {
        // 複数ペア同時接続対応 Stage 5: ConnectionLost に peerId が付帯する（旧 EventHandler? は撤廃）。
        // peerId 空文字は『全 peer 切断 / 不明』を表し、旧挙動どおり全 transfers を cleanup する。
        // peerId 付きは当該 peer 由来の transfer のみに絞り込む（Stage 4 で並列接続が解禁されたあと、
        // peer A の切断で peer B の進行中転送を巻き込まないため）。
        var peerId = e.PeerId;
        var scopeLabel = string.IsNullOrEmpty(peerId) ? "全 peer" : $"peer={Util.Logger.MaskDeviceId(peerId)}";
        Util.Logger.Log($"接続切断検知 ({scopeLabel}): 受信中 {_receiveStates.Count} 件 + 承認待ち(受) {_pendingApprovals.Count} 件 + 承認待ち(送) {_pendingSendApprovals.Count} 件 + 送信中 {_activeTransfers.Count} 件を cleanup", Util.LogLevel.Warning);

        // peerId 指定時のスコープ判定ヘルパー。peerId 空ならすべて対象（旧挙動）。
        bool BelongsTo(Guid tid)
        {
            if (string.IsNullOrEmpty(peerId)) return true;
            return _transferPeerId.TryGetValue(tid, out var tpid)
                && string.Equals(tpid, peerId, StringComparison.Ordinal);
        }

        // v1.0.47: 「一時停止中だった」送信だけを抜けさせる（CTS Cancel）。それ以外の進行中送信は
        // SendChunksAsync の _connectionService.SendAsync(...) が転送断で IOException を投げ、
        // SendItemAsync の transient catch（MaxSendAttempts までリトライ）に乗るのが望ましい。
        // Stage 5: 当該 peer に紐づく一時停止中送信のみ対象。
        foreach (var tid in _pausedSends.Keys.ToArray())
        {
            if (!BelongsTo(tid)) continue;
            _pausedSends.TryRemove(tid, out _);
            if (_sendCts.TryGetValue(tid, out var cts))
            {
                try { cts.Cancel(); } catch { /* ignore */ }
            }
        }

        // 受信中の部分ファイルを削除
        foreach (var tid in _receiveStates.Keys.ToArray())
        {
            if (!BelongsTo(tid)) continue;
            if (_receiveStates.TryRemove(tid, out var state))
            {
                state.Item.State = TransferState.Cancelled;
                state.Item.ErrorMessage = "接続が切断されました";
                CleanupReceiveState(state);
                TransferError?.Invoke(this, state.Item);
            }
        }

        // 受信側承認待ちもキャンセル扱い (送信側はもう存在しないので承認しても無意味)
        foreach (var tid in _pendingApprovals.Keys.ToArray())
        {
            if (!BelongsTo(tid)) continue;
            if (_pendingApprovals.TryRemove(tid, out var pending))
            {
                pending.Item.State = TransferState.Cancelled;
                pending.Item.ErrorMessage = "接続が切断されました";
                // 複数ペア同時接続対応 Stage 2 leak fix (PR #12 review): pending approval は
                // CleanupReceiveState に到達しないため、_transferPeerId 索引を直接掃除する。
                _transferPeerId.TryRemove(tid, out _);
                TransferError?.Invoke(this, pending.Item);
            }
        }

        // 送信側承認待ち TCS も解放。FileMeta 送信後 → 受信側からの FileApprove 待ちで切断したケース。
        // v1.0.47 修正 (P2-H): 旧実装の TrySetResult(false) は WaitForApprovalAsync で「拒否扱い」になり、
        // SendFileAsync が OperationCanceledException を投げて VM の no-retry catch に落ちていた。
        // 承認待ち中の接続断は transient なので、専用例外 ConnectionLostDuringTransferException
        // (IOException 派生 / 非 OperationCanceledException) を TrySetException で投げる。これにより:
        //   1. WaitForApprovalAsync の OperationCanceledException catch を素通り
        //   2. SendFileAsync の catch (Exception) で throw されて
        //   3. VM SendItemAsync の catch (Exception) when (attempt < MaxSendAttempts) の transient 経路に乗り
        //      MaxSendAttempts まで自動リトライが走る（接続復帰後の再送信に対応）
        foreach (var tid in _pendingSendApprovals.Keys.ToArray())
        {
            if (!BelongsTo(tid)) continue;
            if (_pendingSendApprovals.TryRemove(tid, out var tcs))
            {
                // ErrorMessage は WaitForApprovalAsync の fallback ("相手が受信を拒否しました") を上書き
                if (_activeTransfers.TryGetValue(tid, out var sendItem))
                {
                    sendItem.ErrorMessage = "接続が切断されました";
                }
                tcs.TrySetException(new ConnectionLostDuringTransferException("接続が切断されました（承認待ち中）"));
            }
        }
    }

    /// <summary>
    /// ファイルを送信する。チャンク分割→メタデータ送信→チャンク順次送信→ACK 待ち。
    /// </summary>
    /// <param name="filePath">送信するファイルの絶対パス。</param>
    /// <param name="relativePath">フォルダ送信時の相対パス（例: "フォルダ名/サブフォルダ/ファイル名"）。null で単独ファイル。</param>
    /// <param name="ct">キャンセルトークン。</param>
    public async Task SendFileAsync(string filePath, string? relativePath = null, Guid? requestedTransferId = null, string peerId = "", CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("送信ファイルが見つかりません", filePath);

        var totalChunks = FileChunker.CalculateTotalChunks(fileInfo.Length);
        // v1.0.47: UI から TransferId を渡せるようにし、進捗・キャンセル・一時停止を UI 行と正確に対応付ける。
        var transferId = requestedTransferId ?? Guid.NewGuid();

        // v1.0.47: 呼び出し ct と連結したキャンセル用 CTS を登録。CancelTransfer / 相手側 reject で Cancel できる。
        var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _sendCts[transferId] = sendCts;
        ct = sendCts.Token;

        // P-3: ハッシュは事前計算せず、SendChunksAsync 中に IncrementalHash で並行計算する。
        // FileMeta にはハッシュなしで送り、全 chunk 送信後に FileHash メッセージで送る
        var displayName = relativePath ?? fileInfo.Name;
        Util.Logger.Log($"ファイル送信開始: {displayName}, サイズ={fileInfo.Length}, チャンク数={totalChunks}");

        var item = new TransferItem
        {
            TransferId = transferId,
            FileName = displayName,
            FileSize = fileInfo.Length,
            TotalChunks = totalChunks,
            Direction = TransferDirection.Send,
            State = TransferState.InProgress,
            Sha256Hash = string.Empty, // ハッシュは送信後確定
            SourceFilePath = filePath,
            RelativePath = relativePath,
        };
        _activeTransfers[transferId] = item;

        // 複数ペア同時接続対応 Stage 5: 送信先 peerId を権威化。引数で明示されたなら採用し、
        // 空文字なら旧経路の <see cref="IConnectionService.ConnectedPeer"/> 逆引きに fallback する。
        var sendPeerId = !string.IsNullOrEmpty(peerId)
            ? peerId
            : (_connectionService.ConnectedPeer?.SessionId ?? string.Empty);
        if (!string.IsNullOrEmpty(sendPeerId))
        {
            _transferPeerId[transferId] = sendPeerId;
            item.PeerId = sendPeerId;
        }

        // 送信側の承認待ち TCS を準備 (FileMeta 送信前に登録する)
        var approvalTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSendApprovals[transferId] = approvalTcs;

        try
        {
            // 1. メタデータを送信（ハッシュは空、後送り）→ 受信側で承認待ち状態になる
            var metaMessage = FileChunker.CreateFileMetaMessage(
                fileInfo.Name, fileInfo.Length, totalChunks, string.Empty, transferId, relativePath);

            // 承認待ち UI 用に送信状態を Pending に
            item.State = TransferState.Pending;
            ProgressChanged?.Invoke(this, item);

            await SendToPeerAsync(sendPeerId, metaMessage, ct);
            Util.Logger.Log("ファイルメタデータ送信完了、相手の承認待ち…");

            // 2. 相手側の承認 (FileApprove) または拒否 (FileReject) を待つ
            //    AutoAcceptFileTransfer=true の場合は受信側が即承認するので体感ラグなし
            //    v1.0.38 review fix #2: 60 秒タイムアウトを追加。FileApprove/FileReject が欠落しても
            //    永久停止せず Cancelled に遷移させる
            bool approved = await WaitForApprovalAsync(displayName, item, approvalTcs, ct);
            if (!approved)
            {
                // v1.0.38 review fix #4: 拒否時は throw して呼び出し側 (TransferViewModel) の
                // 「正常 return = Completed」扱いを防ぐ。state は WaitForApprovalAsync で既に Cancelled 済
                throw new OperationCanceledException(item.ErrorMessage ?? "受信が拒否されました");
            }

            Util.Logger.Log($"承認受信、チャンク送信開始: {displayName}");
            item.State = TransferState.InProgress;
            ProgressChanged?.Invoke(this, item);

            // 3. チャンクを順次送信しつつハッシュを並行計算
            using var hashSink = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            await SendChunksAsync(filePath, transferId, startChunk: 0, item, sendPeerId, ct, hashSink);

            // 4. 確定したハッシュを後送り
            var sha256Bytes = hashSink.GetHashAndReset();
            item.Sha256Hash = Convert.ToHexStringLower(sha256Bytes);
            var hashMessage = FileChunker.CreateFileHashMessage(transferId, sha256Bytes);
            await SendToPeerAsync(sendPeerId, hashMessage, ct);

            Util.Logger.Log($"ファイル送信完了: {displayName}, SHA256={item.Sha256Hash[..16]}…");
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"ファイル送信エラー: {ex.GetType().Name}: {ex.Message}", Util.LogLevel.Error);
            // v1.0.38 review fix v6: 承認待ちタイムアウト / 拒否で既に Cancelled + TransferError 発火済みなら
            // 二重発火を防ぐ (TransferViewModel に重複行が出る問題の根本対策)
            if (item.State != TransferState.Cancelled)
            {
                item.State = TransferState.Error;
                item.ErrorMessage = Util.ErrorText.Describe(ex);
                TransferError?.Invoke(this, item);
            }
            throw;
        }
        finally
        {
            _pendingSendApprovals.TryRemove(transferId, out _);
            _activeTransfers.TryRemove(transferId, out _);
            _pausedSends.TryRemove(transferId, out _);
            _transferPeerId.TryRemove(transferId, out _); // 複数ペア対応 Stage 2: 送信終了時に索引も解放
            if (_sendCts.TryRemove(transferId, out var cts))
                cts.Dispose();
        }
    }

    /// <summary>
    /// 中断された転送をレジュームする。
    /// v1.0.38 review fix #3: SendFileAsync と同じく FileApprove 待ちを挟む。
    /// (resume も受信側で承認待ちに入るため、待たずにチャンクを送ると 64MB バッファ破棄バグが再発する)
    /// </summary>
    public async Task<bool> ResumeTransferAsync(Guid transferId, CancellationToken ct = default)
    {
        if (!_activeTransfers.TryGetValue(transferId, out var item))
        {
            Util.Logger.Log($"レジューム対象が見つかりません: {transferId}", Util.LogLevel.Warning);
            return false;
        }

        if (string.IsNullOrEmpty(item.SourceFilePath) || !File.Exists(item.SourceFilePath))
        {
            Util.Logger.Log($"レジューム元ファイルが見つかりません: {item.SourceFilePath}", Util.LogLevel.Warning);
            return false;
        }

        var startChunk = 0;
        Util.Logger.Log($"転送レジューム: {item.FileName}, 先頭から再送 (全 {item.TotalChunks} チャンク)");

        // rere #B1-004: SendFileAsync と同様に _sendCts を登録し、レジューム送信中も
        // CancelTransfer / 相手 reject / 接続断（_pausedSends）で送信ループを止められるようにする。
        // 旧実装は未登録で、CancelTransfer / HandleFileReject の _sendCts.TryGetValue が空振りし、
        // UI 上は Cancelled でも送信ループが全チャンク送りきっていた。
        var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _sendCts[transferId] = sendCts;
        ct = sendCts.Token;

        // v1.0.38 review fix #3: 承認待ち TCS を準備
        var approvalTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSendApprovals[transferId] = approvalTcs;
        item.State = TransferState.Pending;
        ProgressChanged?.Invoke(this, item);

        // Stage 5: レジューム経路でも transferId に紐づく peerId を引いて per-peer 送信に流す。
        // _transferPeerId は SendFileAsync 初回で埋めているが、念のため空ならフォールバック。
        var resumePeerId = ResolvePeerIdForTransfer(item.TransferId);
        if (string.IsNullOrEmpty(resumePeerId) && !string.IsNullOrEmpty(item.PeerId))
            resumePeerId = item.PeerId;

        try
        {
            var metaMessage = FileChunker.CreateFileMetaMessage(
                item.FileName, item.FileSize, item.TotalChunks, item.Sha256Hash ?? "", item.TransferId);
            await SendToPeerAsync(resumePeerId, metaMessage, ct);
            Util.Logger.Log($"レジューム: メタデータ送信完了、相手の承認待ち…");

            bool approved = await WaitForApprovalAsync(item.FileName, item, approvalTcs, ct);
            if (!approved)
            {
                // resume の場合は throw せず false を返す (SendFileAsync と挙動を分ける)
                Util.Logger.Log($"レジューム拒否: {item.FileName}");
                return false;
            }

            item.State = TransferState.InProgress;
            ProgressChanged?.Invoke(this, item);

            await SendChunksAsync(item.SourceFilePath, item.TransferId, startChunk, item, resumePeerId, ct);
            return true;
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"レジュームエラー: {ex.GetType().Name}: {ex.Message}", Util.LogLevel.Error);
            // rere #B1-004: CancelTransfer / HandleFileReject が state を Cancelled に遷移済みなら
            // 上書きしない（SendFileAsync L300 と同じガード）。キャンセルを Error に化けさせない。
            if (item.State != TransferState.Cancelled)
            {
                item.State = TransferState.Error;
                item.ErrorMessage = Util.ErrorText.Describe(ex);
                TransferError?.Invoke(this, item);
            }
            return false;
        }
        finally
        {
            _pendingSendApprovals.TryRemove(transferId, out _);
            _pausedSends.TryRemove(transferId, out _);
            if (_sendCts.TryRemove(transferId, out var cts))
                cts.Dispose();
        }
    }

    /// <summary>
    /// v1.0.38 review fix #2/#3: 送信側の FileApprove/FileReject 待ち共通ロジック。
    /// 60 秒タイムアウトで TransferState.Cancelled に遷移、TransferError 発火。
    /// 戻り値: true=承認、false=拒否 or タイムアウト。
    /// </summary>
    private async Task<bool> WaitForApprovalAsync(string displayName, TransferItem item, TaskCompletionSource<bool> approvalTcs, CancellationToken ct)
    {
        using var approvalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        approvalCts.CancelAfter(TimeSpan.FromSeconds(SendApprovalTimeoutSeconds));

        try
        {
            var approved = await approvalTcs.Task.WaitAsync(approvalCts.Token);
            if (!approved)
            {
                // v1.0.38 review fix v12: 受信側拒否時も timeout branch と同じパターンで
                // State=Cancelled + TransferError 発火 (catch では state==Cancelled で二重発火 skip)。
                // ErrorMessage は HandleFileReject が事前に reason 付きで設定済み (fallback で generic)
                Util.Logger.Log($"受信側が拒否: {displayName} / 理由={item.ErrorMessage}", Util.LogLevel.Warning);
                item.State = TransferState.Cancelled;
                if (string.IsNullOrEmpty(item.ErrorMessage))
                    item.ErrorMessage = "相手が受信を拒否しました";
                TransferError?.Invoke(this, item);
            }
            return approved;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // タイムアウト (外部 ct はキャンセルされてない = 内部 CancelAfter のみ発火)
            Util.Logger.Log($"承認待ちタイムアウト ({SendApprovalTimeoutSeconds}s): {displayName}", Util.LogLevel.Warning);
            item.State = TransferState.Cancelled;
            // v1.0.38 review fix v2: 旧バージョンとの混在で起きうるため、ヒントを含める
            // (Ferry v1.0.37 以前は FileApprove メッセージを送らない → タイムアウト)
            item.ErrorMessage = $"承認待ちが {SendApprovalTimeoutSeconds} 秒でタイムアウトしました。" +
                "相手の Ferry が古いバージョン (v1.0.37 以前) の可能性があります。両側で v1.0.38 以降に更新してください。";

            // v1.0.38 review fix v8: 受信側にも FileReject を送って pending approval を expire させる。
            // これを送らないと、受信側ユーザーが 60 秒後に承認ボタンを押した時、FileApprove は届くが
            // 送信側はもう pending を破棄しているのでチャンクが来ず、受信側で空ファイルが in-progress
            // のまま残ってしまう (Codex P2 指摘 v8)
            SendRejectFireAndForget(item.TransferId, $"送信側がタイムアウト ({SendApprovalTimeoutSeconds}s)");

            TransferError?.Invoke(this, item);
            return false;
        }
        catch (OperationCanceledException)
        {
            // 外部 ct によるキャンセル → そのまま伝播
            Util.Logger.Log($"承認待ち中にキャンセル: {displayName}", Util.LogLevel.Warning);
            throw;
        }
    }

    /// <summary>
    /// 受信データを処理する。ConnectionService の DataReceived から呼び出される。
    /// 複数ペア同時接続対応 Stage 2: <paramref name="peerId"/> を権威値として
    /// HandleFileMeta が TransferItem.PeerId と _transferPeerId 索引に書き込む。
    /// 後方互換のため peerId 既定値 "" を許容する（旧テスト/旧呼び出し経路は逆引きにフォールバックする）。
    /// </summary>
    public void HandleReceivedData(byte[] data, string peerId = "")
    {
        if (data.Length == 0) return;

        var messageType = FileChunker.GetMessageType(data);

        // 細工された短い/壊れたメッセージで個別ハンドラのパースが例外を投げても、受信ループまで
        // 遡って ChannelClosed→ConnectionLost で進行中転送を切断させない（ペア済み peer から 1 通で
        // 発火しうるリモート DoS の防止）。当該メッセージのみ破棄して Warning に留める。
        // SafePath の NUL 制御文字対策と同じ「細工 1 通で受信ループを殺せない」不変条件をプロトコル全体へ拡張。
        try
        {
            DispatchMessage(messageType, data, peerId);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"受信メッセージ処理エラー (type=0x{messageType:X2}, len={data.Length}): {ex.Message}", Util.LogLevel.Warning);
        }
    }

    private void DispatchMessage(byte messageType, byte[] data, string peerId)
    {
        switch (messageType)
        {
            case TransferProtocol.FileMeta:
                HandleFileMeta(data, peerId);
                break;

            case TransferProtocol.FileChunk:
                HandleFileChunk(data);
                break;

            case TransferProtocol.FileAck:
                HandleFileAck(data);
                break;

            case TransferProtocol.FileReject:
                HandleFileReject(data);
                break;

            case TransferProtocol.FileHash:
                HandleFileHash(data);
                break;

            case TransferProtocol.FileApprove:
                HandleFileApprove(data);
                break;

            case TransferProtocol.FileFlowAck:
                HandleFileFlowAck(data);
                break;

            case TransferProtocol.Ping:
                HandlePing(peerId);
                break;

            case TransferProtocol.Pong:
                // Pong は現時点では特に処理しない
                break;

            case TransferProtocol.ResumeRequest:
                HandleResumeRequest(data, peerId);
                break;

            case TransferProtocol.ResumeResponse:
                HandleResumeResponse(data);
                break;

            default:
                Util.Logger.Log($"不明なメッセージタイプ: 0x{messageType:X2}", Util.LogLevel.Warning);
                break;
        }
    }

    public IReadOnlyList<TransferItem> GetResumableTransfers()
    {
        return _activeTransfers.Values
            .Where(t => t.State == TransferState.Suspended && !string.IsNullOrEmpty(t.SourceFilePath))
            .ToList();
    }

    // === 送信ヘルパー ===

    /// <summary>複数ペア同時接続対応 Stage 5: peerId 指定での送信ヘルパー。peerId が空文字なら旧経路に fallback
    /// （後方互換）。並列接続が解禁された後（Stage 4）でも、送信先 peer を明示する経路が常に正しい transport を選ぶ。</summary>
    private Task SendToPeerAsync(string peerId, byte[] data, CancellationToken ct = default)
        => string.IsNullOrEmpty(peerId)
            ? _connectionService.SendAsync(data, ct)
            : _connectionService.SendAsync(peerId, data, ct);

    /// <summary>Stage 5: <see cref="ReadOnlyMemory{T}"/> 版の peerId 指定送信ヘルパー。</summary>
    private Task SendToPeerAsync(string peerId, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        => string.IsNullOrEmpty(peerId)
            ? _connectionService.SendAsync(data, ct)
            : _connectionService.SendAsync(peerId, data, ct);

    /// <summary>Stage 5: 送信側 transferId から peerId を引く（既知なら 32hex、不明なら空文字）。
    /// FileReject / FlowAck などの制御メッセージで、紐づく転送の宛先 transport を確実に選ぶための逆引き。</summary>
    private string ResolvePeerIdForTransfer(Guid transferId)
        => _transferPeerId.TryGetValue(transferId, out var p) ? p : string.Empty;

    /// <summary>
    /// チャンクを順次送信する。バックプレッシャーとして一定間隔で進捗を通知する。
    /// 複数ペア同時接続対応 Stage 5: <paramref name="peerId"/> を受け、フロー制御 Route 判定と
    /// チャンクメッセージ送信を per-peer の API へ流す。
    /// </summary>
    private async Task SendChunksAsync(string filePath, Guid transferId, int startChunk, TransferItem item, string peerId, CancellationToken ct, System.Security.Cryptography.IncrementalHash? hashSink = null)
    {
        // P-11: 進捗通知の throttle (UI スレッドへの Post と PropertyChanged 発火を抑制)。
        // 時間ベース (60ms = 16fps 相当) に切り替え、UI から見える滑らかさは維持しつつ通知頻度を一定化
        var lastProgressTick = Environment.TickCount64;
        const long ProgressIntervalMs = 60;

        // P-3: hashSink を渡された場合は ReadChunksWithHash でハッシュ並行計算（送信側 1 度読み）
        var chunkSource = hashSink is null
            ? FileChunker.ReadChunks(filePath)
            : FileChunker.ReadChunksWithHash(filePath, hashSink);

        // v1.0.46: フロー制御カウンタを初期化（レジューム時に前回値が残らないように）
        Volatile.Write(ref item.FlowAckedChunks, 0);

        // v1.0.47: フロー制御 window が「実際に発火したか」を後からログで判別するための一度きりフラグ。
        // 発火＝受信ドレインに律速されて送信が頭打ちになった証拠。発火しないまま完走した場合は
        // (a) ファイルが窓(32MB)未満で構造的に未行使 (b) 受信が十分速い、のどちらか。
        var flowControlEngagedLogged = false;

        foreach (var (index, chunkData) in chunkSource)
        {
            ct.ThrowIfCancellationRequested();

            // レジューム: 開始チャンクまでスキップ
            if (index < startChunk)
                continue;

            // v1.0.47: 一時停止中はここで待機する。接続は維持し、キャンセル（ct）されたら例外で抜ける。
            if (_pausedSends.ContainsKey(transferId))
            {
                if (item.State != TransferState.Paused)
                {
                    item.State = TransferState.Paused;
                    ProgressChanged?.Invoke(this, item);
                }
                while (_pausedSends.ContainsKey(transferId))
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(100, ct);
                }
                // 再開：状態を戻し、フロー制御 stall タイマーが誤発火しないよう以降は通常進行
                if (item.State == TransferState.Paused)
                {
                    item.State = TransferState.InProgress;
                    ProgressChanged?.Invoke(this, item);
                }
            }

            // v1.0.46: リレー経路のバックプレッシャー。受信側が書き込み済みのチャンク (FlowAck) から
            // FlowControlWindowChunks を超えて先行しないよう待機する。リレー (WebSocket) は ClientWebSocket.SendAsync が
            // ローカル送信バッファ受理で即返り end-to-end の流量制御が効かないため、これが無いと送信側が受信ドレイン
            // 速度を超えて Cloudflare 中継バッファへ流し込み、~55秒で接続が切断される。TCP/UDP 経路では各 transport の
            // 自然なバックプレッシャー (WriteAsync の await / UDP 内部ウィンドウ) が先に効くためこの待機はほぼ発生しない。
            // rere #D-003: window 待機はリレー経路のみ。TCP/UDP は transport 自身のバックプレッシャーが
            // 効くため、高 RTT 回線で FlowAck の往復遅延が 32MB 窓を不要に律速するのを避ける。
            // 受信側の FlowAck 送信は経路非依存で常時行われる (送信側だけのガードなら両側判定の
            // 食い違いによる stall が構造的に起きない) ので、ここでの判定だけで安全に無効化できる。
            // PR#5 Codex 指摘: Route が確定できていない (Unknown) 場合は安全側に倒してフロー制御を有効にする
            // (実際はリレーなのに Unknown のままだと ~55秒切断が再発するため)
            // Stage 5: peerId が指定されていれば per-peer の Route を引く（Stage 4 で並列接続が解禁された後の
            // 正しい判定）。空文字なら単数 Route（旧経路）にフォールバック。
            var routeForFlow = string.IsNullOrEmpty(peerId)
                ? _connectionService.Route
                : _connectionService.RouteOf(peerId);
            if (routeForFlow is not (ConnectionRoute.Direct or ConnectionRoute.StunAssisted))
            {
                var flowWaitStart = Environment.TickCount64;
                // v1.0.47: 発火を一度だけ Info ログに残す。これが出ていれば「送信が受信ドレインに律速された＝
                // フロー制御が機能した＝Cloudflare 中継バッファは窓 (32MB) で頭打ち」と確定できる。
                if (!flowControlEngagedLogged
                    && index - Volatile.Read(ref item.FlowAckedChunks) >= TransferProtocol.FlowControlWindowChunks)
                {
                    flowControlEngagedLogged = true;
                    Util.Logger.Log(
                        $"フロー制御 window 発火（受信ドレイン律速に移行）: transferId={transferId} index={index} " +
                        $"acked={Volatile.Read(ref item.FlowAckedChunks)} window={TransferProtocol.FlowControlWindowChunks}",
                        Util.LogLevel.Info);
                }
                while (index - Volatile.Read(ref item.FlowAckedChunks) >= TransferProtocol.FlowControlWindowChunks)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Environment.TickCount64 - flowWaitStart > FlowAckStallTimeoutMs)
                        throw new TimeoutException("受信側からの進捗確認が途絶えました（フロー制御タイムアウト）");
                    await Task.Delay(10, ct);
                }
            }

            // アップロード帯域制限。0 (無制限) なら即 return。ペイロード本体のバイト数で計測する
            // (ヘッダ分は無視しても大勢に影響なし)。複数並列転送時は TokenBucket 内部の SemaphoreSlim で
            // トークン会計が直列化されるので、合算スループットが設定値を超えない。
            await _uploadBucket.WaitAsync(chunkData.Length, ct);

            // P-1: チャンクメッセージ用バッファを ArrayPool から借用し、
            // FileChunker.WriteChunkMessage で直接書き込んだ後 Memory として渡す。
            // 1GB 転送 (16,384 チャンク) で約 1GB の Gen0 alloc を削減。
            var messageSize = TransferProtocol.ChunkHeaderSize + chunkData.Length;
            var buffer = ArrayPool<byte>.Shared.Rent(messageSize);
            try
            {
                FileChunker.WriteChunkMessage(buffer.AsSpan(0, messageSize), transferId, index, chunkData);
                await SendToPeerAsync(peerId, buffer.AsMemory(0, messageSize), ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            item.TransferredBytes = (long)(index + 1) * TransferProtocol.ChunkSize;
            if (item.TransferredBytes > item.FileSize)
                item.TransferredBytes = item.FileSize;

            // 進捗通知（時間ベース throttle、60ms 経過時のみ）
            var nowTick = Environment.TickCount64;
            if (nowTick - lastProgressTick >= ProgressIntervalMs)
            {
                ProgressChanged?.Invoke(this, item);
                lastProgressTick = nowTick;
            }

            // P-14: 旧コードの 64 チャンクごと Task.Yield() は撤去。
            // 本来意図した TCP 送信バックプレッシャーは _stream.WriteAsync の自然な await で既に効いている
            // （バッファが詰まれば await が長くなる）。Task.Yield はスレッド再スケジュールで CPU を浪費していた
        }

        // 最終進捗通知
        // rere #B1-004: 最終チャンク送信と相手側キャンセル (FileReject → Cancelled) のレースで
        // Cancelled を Completed で上書きしないよう、ct と現在 state を確認してから確定する
        ct.ThrowIfCancellationRequested();
        if (item.State == TransferState.Cancelled)
            return;
        item.TransferredBytes = item.FileSize;
        item.State = TransferState.Completed;
        ProgressChanged?.Invoke(this, item);
    }

    // === 受信ハンドラ ===

    /// <summary>
    /// HandleFileMeta の early-return パス（パストラバーサル / 保存先異常等）で送信側に
    /// FileReject を投げて 60 秒の approval タイムアウト + 「相手が旧バージョン」誤エラーを防ぐためのヘルパー。
    /// fire-and-forget で握り潰してハンドラ側をブロックしない。
    /// Stage 5: transferId から peerId を引いて per-peer 送信に流す（不明なら旧単数経路に fallback）。
    /// </summary>
    private void SendRejectFireAndForget(Guid transferId, string reason)
        => SendFireAndForget(ResolvePeerIdForTransfer(transferId), FileChunker.CreateRejectMessage(transferId, reason), "FileReject");

    /// <summary>
    /// opop C-6: 制御メッセージ (Reject / ACK / Pong / ResumeResponse / Approve) の fire-and-forget
    /// 送信を統一するヘルパー。例外は握り潰してログのみ (受信スレッドをブロックしない +
    /// UnobservedTaskException 防止) という方針をここ 1 箇所で保証する。
    /// Stage 5: peerId 指定の per-peer 送信に切替（空文字なら旧単数経路に fallback）。
    /// </summary>
    private void SendFireAndForget(string peerId, byte[] message, string label)
    {
        _ = Task.Run(async () =>
        {
            try { await SendToPeerAsync(peerId, message); }
            catch (Exception ex) { Util.Logger.Log($"{label} 送信エラー: {ex.Message}", Util.LogLevel.Warning); }
        });
    }

    /// <summary>
    /// v1.0.46: 受信側 → 送信側のフロー制御 ACK (FileFlowAck) を送る。受信スレッドから fire-and-forget で
    /// 呼ばれるため、例外は内部で握り潰してタスクが faulted にならないようにする (UnobservedTaskException 防止)。
    /// Stage 5: transferId 紐付けの peerId に per-peer 送信。
    /// </summary>
    private async Task SendFlowAckAsync(Guid transferId, int receivedChunkCount)
    {
        try
        {
            var msg = FileChunker.CreateFlowAckMessage(transferId, receivedChunkCount);
            await SendToPeerAsync(ResolvePeerIdForTransfer(transferId), msg);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"FlowAck 送信失敗（無視）: {ex.Message}", Util.LogLevel.Debug);
        }
    }

    private void HandleFileMeta(byte[] data, string peerIdFromTransport = "")
    {
        var meta = FileChunker.ParseFileMeta(data);
        if (meta == null)
        {
            Util.Logger.Log("ファイルメタデータのパースに失敗", Util.LogLevel.Warning);
            return;
        }

        // メタデータの整合性検証（攻撃者制御の値で巨大確保・ディスク枯渇を起こさせない）
        // rere #A2-001: FileSize の絶対上限を先に検証する。上限なしだと CalculateTotalChunks の
        // int キャスト桁溢れにより負の TotalChunks が一致検証を素通りし、承認時の
        // new bool[TotalChunks] で未処理例外になる。TotalChunks < 0 も明示拒否 (多層防御)
        // TotalChunks は FileSize から導出される値と一致しなければ拒否する
        if (meta.FileSize < 0 || meta.FileSize > TransferProtocol.MaxFileSizeBytes
            || meta.TotalChunks < 0 || meta.TotalChunks != FileChunker.CalculateTotalChunks(meta.FileSize))
        {
            Util.Logger.Log($"不正なメタデータを拒否: FileSize={meta.FileSize}, TotalChunks={meta.TotalChunks}", Util.LogLevel.Warning);
            // この時点では TransferId のパース可否すら未確認なので Reject 送信はスキップ
            // (送信側にとって不明な TransferId の Reject は無視されるだけだが、無駄な通信なので避ける)
            return;
        }

        // TransferId は以降の Reject 送信・state 構築で必要なため、ここで早期にパース
        if (!Guid.TryParse(meta.TransferId, out var transferIdGuid))
        {
            Util.Logger.Log($"不正な TransferId 形式: {meta.TransferId}", Util.LogLevel.Warning);
            return;
        }

        // 制御文字（特に NUL '\0'）を含むファイル名/相対パスを早期に弾く。これらは後段の Path.* で
        // ArgumentException("Null character in path") を誘発し、未捕捉だと受信ループ→ChannelClosed で
        // 接続が切れる（細工 FileMeta 1 通で進行中転送を切断できるリモート DoS）。SafePath 側でも
        // 多層防御するが、ここで明示 Reject すると送信側が 60s タイムアウトを待たず即失敗を受け取れる。
        if (Util.SafePath.ContainsControlChar(meta.FileName) || Util.SafePath.ContainsControlChar(meta.RelativePath))
        {
            Util.Logger.Log("制御文字を含むファイル名/パスを拒否", Util.LogLevel.Warning);
            SendRejectFireAndForget(transferIdGuid, "不正なファイル名 (制御文字)");
            return;
        }

        var displayName = meta.RelativePath ?? meta.FileName;
        Util.Logger.Log($"ファイル受信開始: {displayName}, サイズ={meta.FileSize}, チャンク数={meta.TotalChunks}, TransferId={meta.TransferId}");

        var saveDir = _settingsService.Settings.SaveDirectory;

        // 接続元ピアを FileMeta 到着時点で確定（フォルダ構造マッピングのキーと ReceiveState の双方で使う）。
        // 複数ペア同時接続対応 Stage 2: transport から運ばれた peerId を権威値として優先採用。
        // 空文字（後方互換経路、テスト経路）の場合のみ旧 ConnectedPeer 単数の逆引きにフォールバックする。
        // Stage 4 で並行接続が解禁された時点で逆引きは取り違える可能性があるが、その時点では
        // 必ず transport から peerId が運ばれてくる（Stage 1 で配線済み）ので逆引きは事実上死に経路。
        var receivePeerId = !string.IsNullOrEmpty(peerIdFromTransport)
            ? peerIdFromTransport
            : (_connectionService.ConnectedPeer?.SessionId
               ?? _connectionService.CurrentListeningPeerId
               ?? string.Empty);

        // 複数ペア同時接続対応 Stage 2: TransferId→peerId 索引を記入。
        // Stage 5 の SendFlowAckAsync / SendRejectFireAndForget / フロー制御 Route 判定がこの索引から
        // 返送先 peer を引いて、受信中の FlowAck が他 peer に漏れる blocker を根治する。
        if (!string.IsNullOrEmpty(receivePeerId))
            _transferPeerId[transferIdGuid] = receivePeerId;

        // 送信元 OS のパス区切りに依存しないよう、受信した相対パスを '/' へ正規化してから分解・検証する
        // （Windows 送信 → mac/Linux 受信の混在を吸収。区切り/トラバーサル判定は Util.SafePath に集約）。
        var normalizedRelativePath = meta.RelativePath is null
            ? null
            : Util.SafePath.NormalizeSeparators(meta.RelativePath);

        // RelativePath がある場合はフォルダ構造を再現
        string savePath;
        if (!string.IsNullOrEmpty(normalizedRelativePath))
        {
            // パストラバーサル防止: ".." を「パス要素単位」で弾く
            // （substring 判定だと "my..file.txt" のような正規名を誤って拒否してしまう）。
            // 併せて先頭要素が空（先頭 "/"）/ "." の不正 root も弾く（保存先サイレントフラット化の防止）。
            if (Util.SafePath.HasParentTraversal(normalizedRelativePath)
                || Util.SafePath.HasUnsafeRoot(normalizedRelativePath))
            {
                Util.Logger.Log($"不正な RelativePath を検出: {meta.RelativePath}", Util.LogLevel.Warning);
                SendRejectFireAndForget(transferIdGuid, "不正なファイルパス (パストラバーサル)");
                return;
            }

            // ルートフォルダ名を取得（例: "photos/sub/file.jpg" → "photos"）
            var parts = normalizedRelativePath.Split('/');
            var rootFolder = parts[0];

            // 同名フォルダ/ファイルが存在する場合、ルートフォルダ名をリネーム
            // フォルダ構造マッピングはピアごとに分離する。グローバル共有のままだと、別ピアから同名ルート
            // （例: "photos"）を連続/同時受信したとき同じ実フォルダに解決され、ディスク上でファイルが
            // 混ざる。キーを (peerId, rootFolder) のタプルにして分離し（区切り文字不要で衝突しない）、
            // 値（実フォルダ名）は rootFolder から組み立てる。同一フォルダの全ファイルは同じ先にキャッシュされる。
            var actualRoot = _folderMappings.GetOrAdd((receivePeerId, rootFolder), _ =>
            {
                var candidatePath = Path.Combine(saveDir, rootFolder);
                if (!Directory.Exists(candidatePath) && !File.Exists(candidatePath))
                    return rootFolder;

                // "フォルダ名 (2)" のように連番リネーム
                for (var i = 2; i < 10000; i++)
                {
                    var renamed = $"{rootFolder} ({i})";
                    var renamedPath = Path.Combine(saveDir, renamed);
                    if (!Directory.Exists(renamedPath) && !File.Exists(renamedPath))
                        return renamed;
                }
                return $"{rootFolder}_{Guid.NewGuid():N}";
            });

            // ルートフォルダ名を置換して保存パスを組み立て
            parts[0] = actualRoot;
            savePath = Path.Combine(saveDir, Path.Combine(parts));
        }
        else
        {
            // パストラバーサル防止: ピア制御のファイル名はディレクトリ要素を除去し、空 / "." / ".." を弾く。
            // SafeFileName は区切り正規化込みなので、Windows 送信側の '\' 区切りでも mac/Linux で正しく剥がれる
            // （旧実装は単独ファイル経路だけ '\' を剥がさず非対称だった）。
            var safeName = Util.SafePath.SafeFileName(meta.FileName);
            if (safeName is null)
            {
                Util.Logger.Log($"不正なファイル名を拒否: {meta.FileName}", Util.LogLevel.Warning);
                SendRejectFireAndForget(transferIdGuid, "不正なファイル名");
                return;
            }
            savePath = Path.Combine(saveDir, safeName);
            // 単独ファイルの同名リネーム
            savePath = GetUniquePath(savePath);
        }

        // パストラバーサル最終防御: 組み立てた保存先が saveDir 配下に収まることを検証する
        // （RelativePath 経路の絶対パス混入や Path.Combine の親破棄挙動を弾く）。
        // 文字列 StartsWith はクロス OS で区切り・大小・正規化の差が出る（case-sensitive な Linux で
        // OrdinalIgnoreCase が誤許可寄りに倒れる等）ため、Util.SafePath.IsWithinDirectory で
        // Path.GetRelativePath ベースに判定し OS 既定の比較規則に委ねる。
        if (!Util.SafePath.IsWithinDirectory(saveDir, savePath))
        {
            Util.Logger.Log($"保存パスが保存先ディレクトリ外を指しています: {savePath}", Util.LogLevel.Warning);
            SendRejectFireAndForget(transferIdGuid, "保存パスが許可範囲外です");
            return;
        }

        // 保存先ディレクトリを作成
        var saveFileDir = Path.GetDirectoryName(savePath) ?? saveDir;
        if (!Directory.Exists(saveFileDir))
        {
            try { Directory.CreateDirectory(saveFileDir); }
            catch (Exception ex)
            {
                // CodeRabbit 指摘: ex.Message に保存先絶対パス / ユーザー名等のローカル PII が含まれうるため、
                // 詳細はローカルログだけに残し、ネットワーク越しの FileReject 理由は固定文言に絞る
                Util.Logger.Log($"保存先ディレクトリ作成失敗: {ex.Message}", Util.LogLevel.Error);
                SendRejectFireAndForget(transferIdGuid, "保存先ディレクトリ作成失敗");
                return;
            }
        }

        // フォルダ内の個別ファイルも重複チェック
        if (!string.IsNullOrEmpty(meta.RelativePath))
            savePath = GetUniquePath(savePath);

        // (transferIdGuid は冒頭でパース済み)

        var state = new ReceiveState
        {
            TransferId = transferIdGuid,
            FileName = displayName,
            FileSize = meta.FileSize,
            TotalChunks = meta.TotalChunks,
            // P-3: ハッシュはメタには含まれず、FileHash メッセージで後送りされる
            ExpectedSha256 = meta.Sha256, // 旧版互換のため受け取った値をそのまま保持（新版では空文字）
            SavePath = savePath,
            ReceivedChunks = 0,
            Item = new TransferItem
            {
                TransferId = transferIdGuid,
                FileName = displayName,
                FileSize = meta.FileSize,
                TotalChunks = meta.TotalChunks,
                Direction = TransferDirection.Receive,
                State = TransferState.WaitingApproval,
                Sha256Hash = meta.Sha256,
                // 接続元ピアは FileMeta 到着時点で確定済み（receivePeerId、上部で算出）。VM 側
                // ResolveReceivePeer の後付け推測より権威ある値で、宛先別履歴が誤ピアに混入しないようにする。
                PeerId = receivePeerId,
            },
        };

        // 承認待ちキューに追加し、UI に通知
        _pendingApprovals[transferIdGuid] = state;
        Util.Logger.Log($"受信承認待ち: {displayName} ({Util.Formatting.FormatBytes(meta.FileSize)})");
        ApprovalRequested?.Invoke(this, state.Item);
    }

    /// <summary>rere #C2-002: 全承認待ち transfer のバッファ済みバイト合計。承認前バッファの異常パスでのみ呼ぶ。</summary>
    private long TotalPendingApprovalBytes()
    {
        long sum = 0;
        foreach (var p in _pendingApprovals.Values)
            sum += p.BufferedBytes;
        return sum;
    }

    private void HandleFileChunk(byte[] data)
    {
        // [種別 1] [TransferId 16] [chunkIndex 4] [data]
        if (data.Length < TransferProtocol.ChunkHeaderSize) return;

        // P-6: Guid のまま辞書引き（ToString() のヒープ確保撤去、1GB 転送で 9MB alloc 削減）
        var transferId = new Guid(data.AsSpan(1, 16));
        var chunkIndex = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(17, 4));
        var chunkLength = data.Length - TransferProtocol.ChunkHeaderSize;

        if (!_receiveStates.TryGetValue(transferId, out var state) || state.FileStream == null)
        {
            // 承認待ち中のチャンクは TransferId 単位でバッファリング（上限超過分は破棄して OOM 防止）
            if (_pendingApprovals.TryGetValue(transferId, out var pending))
            {
                // rere #C2-002: per-transfer 上限に加え、全承認待ち合算の上限でも破棄する。
                // 合算は承認前バッファ（正規経路ではほぼ発生しない異常パス）でのみ評価するので、
                // 都度集計（O(承認待ち件数)）でも実コストは無視できる（カウンタの増減簿記による誤差を避ける）。
                if (pending.BufferedBytes + chunkLength > MaxApprovalBufferBytes
                    || TotalPendingApprovalBytes() + chunkLength > MaxTotalApprovalBufferBytes)
                {
                    Util.Logger.Log($"承認待ちバッファ上限超過のためチャンクを破棄: {pending.FileName}", Util.LogLevel.Warning);
                    return;
                }
                pending.BufferedChunks ??= [];
                pending.BufferedChunks.Add(data);
                pending.BufferedBytes += chunkLength;
            }
            return;
        }

        // chunkIndex の範囲検証
        if (chunkIndex < 0 || chunkIndex >= state.TotalChunks) return;

        var offset = (long)chunkIndex * TransferProtocol.ChunkSize;
        // 申告サイズを超える書き込みを拒否（ディスク枯渇 DoS 防止）
        if (offset + chunkLength > state.FileSize)
        {
            Util.Logger.Log($"チャンクが申告サイズを超過: {state.FileName}", Util.LogLevel.Warning);
            // rere #C2-001 review: 終端確定権を atomic に取り、勝者だけが TransferError を発火する
            // (CancelTransfer / OnConnectionLost / VerifyAndFinalize と二重終端イベントにしない。他 4 経路と揃える)。
            if (!_receiveStates.TryRemove(state.TransferId, out _)) return;
            state.Item.State = TransferState.Error;
            state.Item.ErrorMessage = "受信データが申告サイズを超過しました";
            TransferError?.Invoke(this, state.Item);
            CleanupReceiveState(state);
            return;
        }

        try
        {
            // マルチストリーム PoC: N 本受信での並行 Write 競合を防ぐため、check-and-set（重複判定→Seek→
            // Write→ビットマップ→カウンタ）を per-state lock で原子化する。スナップショットを取ってロック外で
            // 帯域制限/FlowAck/進捗を扱い、throttle 中に他 stream の書き込みを止めない。
            var wrote = false;
            var receivedSnapshot = 0;
            long writtenSnapshot;
            lock (state.WriteLock)
            {
                // chunkIndex をオフセットに変換して書き込む（順不同到着でも正しい位置に置く）。重複チャンクは無視
                if (state.ReceivedChunkSet != null && !state.ReceivedChunkSet[chunkIndex])
                {
                    // 既に目的のオフセットにいるなら Seek しない。FileStream の書き込みバッファ
                    // (1MB で open) は Seek のたびに強制 flush されるため、毎チャンク Seek すると
                    // バッファが機能せず 64KB ごとの write syscall になる。順序到着（TCP / リレー）は
                    // Position == offset が常に成り立つので、書き込み syscall が 1/16 に減る。
                    // 順不同到着（UDP）では従来どおり Seek する。
                    if (state.FileStream!.Position != offset)
                        state.FileStream.Seek(offset, SeekOrigin.Begin);
                    state.FileStream.Write(data.AsSpan(TransferProtocol.ChunkHeaderSize));
                    state.ReceivedChunkSet[chunkIndex] = true;
                    state.ReceivedChunks++;
                    state.WrittenBytes += chunkLength;
                    wrote = true;
                }
                receivedSnapshot = state.ReceivedChunks;
                writtenSnapshot = state.WrittenBytes;
            }

            if (wrote)
            {
                // ダウンロード帯域制限。重複でないチャンクだけカウントする。同期 Wait で受信ループを
                // 直接減速させ、TCP/WebSocket のバックプレッシャーを上流 (送信側) へ伝える。
                // 0 (無制限) なら即 return。ロック外で待つことで他 stream の書き込みをブロックしない。
                _downloadBucket.Wait(chunkLength);

                // v1.0.46: 一定チャンクごとに送信側へ「書き込み済みチャンク数」を FlowAck で返す。
                // 送信側はこれを credit にウィンドウ制御し、リレー中継バッファの溢れ (~55秒切断) を防ぐ。
                // 末尾の端数チャンク (TotalChunks が間隔の倍数でない場合) でも確実に最終 ACK が届くよう、
                // 全チャンク書き込み完了時にも送る (送信側ウィンドウ待機の取りこぼし防止の安全網)。
                // FlowAck は累積カウントなので、スナップショットが多少ずれても次の ACK で回復する。
                if (receivedSnapshot % TransferProtocol.FlowAckIntervalChunks == 0
                    || receivedSnapshot == state.TotalChunks)
                    _ = SendFlowAckAsync(transferId, receivedSnapshot);
            }

            state.Item.TransferredBytes = writtenSnapshot;

            // 進捗通知（P-11: 時間ベース throttle、60ms 経過時のみ。送信側と統一）
            var nowTick = Environment.TickCount64;
            if (nowTick - state.LastProgressTick >= 60)
            {
                ProgressChanged?.Invoke(this, state.Item);
                state.LastProgressTick = nowTick;
            }

            // P-3: 完了判定は「全 chunk 受信 AND 期待ハッシュ確定」の両方が揃ったとき。
            // FileHash メッセージは順不同で到着し得るので、両方の経路から TryCompleteReceive を呼ぶ
            TryCompleteReceiveIfReady(state);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"チャンク書き込みエラー: {ex.GetType().Name}: {ex.Message}", Util.LogLevel.Error);
            // rere #C2-001 review: 同上。終端確定権を atomic に取り、勝者だけが終端イベントを発火する
            // (CancelTransfer が FileStream を dispose して書き込みが例外化したケースの二重終端を防ぐ)。
            if (!_receiveStates.TryRemove(state.TransferId, out _)) return;
            state.Item.State = TransferState.Error;
            state.Item.ErrorMessage = Util.ErrorText.Describe(ex);
            TransferError?.Invoke(this, state.Item);
            CleanupReceiveState(state);
        }
    }

    /// <summary>
    /// ファイル受信を完了する。SHA-256 を検証し、ACK を送信する。
    /// </summary>
    private void CompleteReceive(ReceiveState state)
    {
        // TryCompleteReceiveIfReady は chunk 経路と FileHash 経路の両方から呼ばれ得る。検証開始の権利を
        // atomic に 1 度だけ取り、二重起動(二重 ACK/二重イベント)を防ぐ。
        // rere #C2-001 review (codex P2 #3416006457): 旧実装は _receiveStates から即削除して claim にしていたが、
        // 検証(SHA-256 全再読込、大容量で数秒)中に CancelTransfer / HasActiveTransfer がこの転送を見失い、
        // (a) 検証中のキャンセルが何もできない (b) 自動更新ガードが「転送なし」と誤判定して DownloadAndApply →
        // 再起動が finalize に割り込みうる。claim を Interlocked フラグに移し、_receiveStates には検証完了まで
        // 残して可視性を保つ。終端確定は VerifyAndFinalizeReceive 側の _receiveStates.TryRemove に一本化する。
        if (Interlocked.Exchange(ref state.Finalizing, 1) != 0)
            return;

        // rere #C2-001: SHA-256 の全ファイル再読み込み(大容量で数秒〜十数秒)を受信ループスレッド上で
        // 同期実行すると、その間 他の並列転送のチャンク/ACK/FlowAck 処理まで止まる(UDP は ACK 停止で
        // 再送誤発火、リレーは 32MB 窓 stall で切断リスク)。重い検証・ACK・イベント発火を別タスクへ逃がし、
        // 受信ループは即座に次メッセージへ戻す。
        // rere #C2-001 review (race verify): FileStream の所有権を Interlocked.Exchange で原子的に取得し、
        // 取った側だけが flush/dispose する。CancelTransfer→CleanupReceiveState(UI スレッド) が同一
        // FileStream を並行 dispose して ObjectDisposedException / flush 漏れ(→稀に SHA 不一致) を起こすのを防ぐ。
        // null 化は同期実行なので finalize 後に来た stray chunk は HandleFileChunk の FileStream==null ゲートで弾かれる。
        // claim 後はこのメソッドが絶対に throw して抜けないようにする(claim 済みなのに Task.Run へ到達しないと
        // state が _receiveStates に永久残留 → HasActiveTransfer 固着 → auto-update 無期限スキップ + UI が
        // 「検証中…」で固着する)。flush だけでなく Dispose も握る(Dispose は内部で同じバッファを再 flush する
        // ため、ディスク満杯/ドライブ切断では Flush 失敗後に Dispose も同じ IOException を投げる)。万一 fs 処理外
        // (Log/Task.Run 起動)で throw しても、外側 catch で終端確定(_receiveStates.TryRemove + Error 発火)して固着を防ぐ。
        try
        {
            var fs = Interlocked.Exchange(ref state.FileStream, null);
            if (fs != null)
            {
                try { fs.Flush(); } catch { /* flush 失敗は無視（検証は SavePath を再読込するので実害なし） */ }
                try { fs.Dispose(); } catch { /* dispose の最終 flush 失敗も無視（同上。ハンドルは解放される） */ }
            }

            Util.Logger.Log($"全チャンク受信完了: {state.FileName}, 検証中…");
            _ = Task.Run(() => VerifyAndFinalizeReceive(state));
        }
        catch (Exception ex)
        {
            // claim 後・Task.Run 起動前に throw した場合の最終防衛線。state を確実に終端させ、
            // _receiveStates 永久残留(HasActiveTransfer 固着) と UI の「検証中…」固着を防ぐ。
            Util.Logger.LogException("受信完了処理の起動に失敗", ex);
            if (_receiveStates.TryRemove(state.TransferId, out _))
            {
                if (_receiveStates.IsEmpty) _folderMappings.Clear();
                state.Item.State = TransferState.Error;
                state.Item.ErrorMessage = Util.ErrorText.Describe(ex);
                try { TransferError?.Invoke(this, state.Item); } catch { /* 購読側例外は無視 */ }
            }
        }
    }

    /// <summary>受信完了の重い処理(SHA-256 全再読み込み検証・ACK 送信・イベント発火)。受信ループを
    /// ブロックしないよう <see cref="CompleteReceive"/> から別タスクで実行される(rere #C2-001)。
    /// 検証中も状態は _receiveStates に残り、終端確定の権利は _receiveStates.TryRemove で
    /// CancelTransfer と奪い合う(codex P2 #3416006457: 検証中の可視性確保 + 二重終端イベント防止)。</summary>
    private void VerifyAndFinalizeReceive(ReceiveState state)
    {
        byte[]? sha256Bytes = null;
        var hashMatch = false;
        string? errorMessage = null;
        try
        {
            // SHA-256 検証（1回のハッシュ計算で検証と ACK 送信の両方に使用）
            sha256Bytes = FileChunker.ComputeSha256(state.SavePath);
            var actualHash = Convert.ToHexStringLower(sha256Bytes);
            hashMatch = string.Equals(actualHash, state.ExpectedSha256, StringComparison.OrdinalIgnoreCase);
            if (!hashMatch)
                Util.Logger.Log($"SHA-256 検証失敗: 期待={state.ExpectedSha256[..16]}…, 実際={actualHash[..16]}…", Util.LogLevel.Error);
        }
        catch (Exception ex)
        {
            Util.Logger.LogException("受信完了処理エラー", ex);
            errorMessage = Util.ErrorText.Describe(ex);
        }

        // 終端確定 + イベント発火。本メソッドは Task.Run(fire-and-forget) 上で動くため、終端処理の
        // 例外を必ず吸収する（購読側ハンドラや通知音再生が throw しても UnobservedTaskException 化させない。
        // SendFireAndForget と同じ「fire-and-forget は例外を必ず握る」不変条件に揃える。rere #C2-001 review）。
        try
        {
            // 終端確定の権利を atomic に取る。検証中に CancelTransfer がこの状態を横取り(TryRemove 成功)して
            // いたら、ここでは何もせず二重終端イベントを防ぐ。キャンセルが消せなかった可能性のある
            // 受信ファイル(検証が掴んでいて File.Delete が失敗した等)だけ後始末する。
            if (!_receiveStates.TryRemove(state.TransferId, out _))
            {
                try { if (File.Exists(state.SavePath)) File.Delete(state.SavePath); } catch { }
                return;
            }
            // 全受信完了時にフォルダマッピングキャッシュをクリア
            if (_receiveStates.IsEmpty)
                _folderMappings.Clear();

            if (errorMessage == null && hashMatch)
            {
                Util.Logger.Log($"SHA-256 検証成功: {state.FileName}");
                state.Item.State = TransferState.Completed;
                state.Item.TransferredBytes = state.FileSize;
                state.Item.SavedFilePath = state.SavePath;
                // ACK を送信（送信側に結果を通知）— fire-and-forget でブロッキングを回避
                // Stage 5: 送信元 peer (state.Item.PeerId) に per-peer 送信。
                SendFireAndForget(state.Item.PeerId ?? string.Empty, FileChunker.CreateAckMessage(true, sha256Bytes!), "ACK");
                FileReceived?.Invoke(this, state.Item);
                MaybePlayReceiveNotification(state.Item.PeerId);
            }
            else if (errorMessage == null)
            {
                // SHA-256 不一致（検証は完了したが内容が壊れている）
                state.Item.State = TransferState.Error;
                state.Item.ErrorMessage = "ファイルの整合性検証に失敗しました（SHA-256 不一致）";
                SendFireAndForget(state.Item.PeerId ?? string.Empty, FileChunker.CreateAckMessage(false, sha256Bytes!), "ACK");
                TransferError?.Invoke(this, state.Item);
                // 不正なファイルを削除
                try { File.Delete(state.SavePath); }
                catch { /* 削除失敗は無視 */ }
            }
            else
            {
                // 検証自体が例外で失敗。ACK は送れず、ファイルは保全する（原実装踏襲）
                state.Item.State = TransferState.Error;
                state.Item.ErrorMessage = errorMessage;
                TransferError?.Invoke(this, state.Item);
            }
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"受信終端処理で例外: {ex.Message}", Util.LogLevel.Warning);
        }
    }

    /// <summary>
    /// 受信完了時に通知音を鳴らす。通知音設定が ON かつ当該ピアが <see cref="Models.AppSettings.MutedPeerIds"/>
    /// でミュートされていないときのみ。AutoAccept 経路もここを通る（CompleteReceive が唯一の受信完了点）。
    /// 再生は best-effort・非ブロッキングで、失敗してもファイル受信自体には影響しない。
    /// </summary>
    private void MaybePlayReceiveNotification(string? peerId)
    {
        var settings = _settingsService.Settings;
        if (!settings.EnableNotificationSound)
            return;
        // MutedPeerIds は初期化子付きだが、settings.json に明示的な null が入ると STJ が null を
        // セットしうるため null 条件演算子で防御する（null = ミュート無し = 鳴らす）。
        if (!string.IsNullOrEmpty(peerId) && settings.MutedPeerIds?.Contains(peerId) == true)
            return;
        Util.NotificationSound.Play();
    }

    /// <summary>
    /// P-3: FileHash メッセージを受信して期待ハッシュを確定する。
    /// 全 chunk 受信済みなら即座に検証 → CompleteReceive。
    /// まだチャンク受信中なら、最後のチャンク到着時に CompleteReceive が呼ばれる。
    /// </summary>
    private void HandleFileHash(byte[] data)
    {
        var parsed = FileChunker.ParseFileHash(data);
        if (parsed is null)
        {
            // PR#5 Codex 指摘: v1.0.50 以前の旧形式 (TransferId なし 33byte) との混在期間フォールバック。
            // 自動更新は両端同時ではないため、旧送信側からのハッシュは受信中の転送が 1 件だけの
            // ときに限り旧ロジックで紐付ける (複数受信中は誤紐付けリスクがあるので破棄)
            var legacySha = FileChunker.ParseLegacyFileHash(data);
            // ConcurrentDictionary の Count → Values.First() は非原子なので、スナップショットに対して
            // 「受信中 1 件」判定と取得を行う (要素が並行削除されても InvalidOperationException にならない)
            if (legacySha != null && _receiveStates.ToArray() is [var legacyEntry])
            {
                var legacyState = legacyEntry.Value;
                if (string.IsNullOrEmpty(legacyState.ExpectedSha256))
                {
                    legacyState.ExpectedSha256 = Convert.ToHexStringLower(legacySha);
                    Util.Logger.Log($"FileHash (旧形式) 受信: {legacyState.FileName}, SHA256={legacyState.ExpectedSha256[..16]}…");
                    TryCompleteReceiveIfReady(legacyState);
                    return;
                }
            }
            Util.Logger.Log("FileHash メッセージのパースに失敗", Util.LogLevel.Warning);
            return;
        }

        // rere #B1-001: メッセージに含まれる TransferId で受信状態を直接引く。
        // 旧実装は「最初の ExpectedSha256 未確定 state」に紐付けており、並列転送
        // (ParallelTransferCount>1) で別ファイルのハッシュを取り違える余地があった。
        var (transferId, sha256Bytes) = parsed.Value;
        var hex = Convert.ToHexStringLower(sha256Bytes);
        if (_receiveStates.TryGetValue(transferId, out var state))
        {
            state.ExpectedSha256 = hex;
            Util.Logger.Log($"FileHash 受信: {state.FileName}, SHA256={hex[..16]}…");
            TryCompleteReceiveIfReady(state);
            return;
        }
        Util.Logger.Log($"FileHash 受信したが該当する受信状態なし: transferId={transferId}, SHA256={hex[..16]}…", Util.LogLevel.Warning);
    }

    /// <summary>全 chunk 受信 AND 期待ハッシュ確定の両方が揃ったときだけ CompleteReceive を呼ぶ。</summary>
    private void TryCompleteReceiveIfReady(ReceiveState state)
    {
        if (state.ReceivedChunks >= state.TotalChunks && !string.IsNullOrEmpty(state.ExpectedSha256))
        {
            CompleteReceive(state);
        }
    }

    private void HandleFileAck(byte[] data)
    {
        if (data.Length < 2) return;

        var success = data[1] == 1;
        Util.Logger.Log($"ACK 受信: success={success}");

        // 送信完了の確認として使う（現在は送信側で完了済みにしているため情報ログのみ）
        if (!success)
        {
            Util.Logger.Log("受信側でファイル検証に失敗しました", Util.LogLevel.Warning);
        }
    }

    private void HandleFileReject(byte[] data)
    {
        var parsed = FileChunker.ParseReject(data);
        if (parsed == null)
        {
            Util.Logger.Log("FileReject のパースに失敗", Util.LogLevel.Warning);
            return;
        }
        var (transferId, reason) = parsed.Value;
        Util.Logger.Log($"ファイル拒否: transferId={transferId}, 理由={reason}", Util.LogLevel.Warning);

        // v1.0.38: 承認待ち TCS を完了させる (送信側が SendFileAsync で待機中)
        if (_pendingSendApprovals.TryRemove(transferId, out var tcs))
        {
            // v1.0.38 review fix v12: 拒否理由を sender 側 item.ErrorMessage に設定してから TCS 解決。
            // これで WaitForApprovalAsync が item.ErrorMessage を見て実際の理由 (例: パストラバーサル /
            // dir 作成失敗) を UI に伝えられる。旧実装は generic な「受信が拒否されました」しか出なかった
            if (_activeTransfers.TryGetValue(transferId, out var pendingSendItem))
            {
                pendingSendItem.ErrorMessage = $"相手が受信を拒否しました: {reason}";
            }
            tcs.TrySetResult(false);
            return;
        }

        // 送信中（承認済み・チャンク送信中）に相手が中断/キャンセルしてきたケース（v1.0.47）。
        // 受信側 CancelTransfer が送ってくる。送信ループ（_sendCts）を止めて Cancelled 表示にする。
        if (_activeTransfers.TryGetValue(transferId, out var sendingItem))
        {
            Util.Logger.Log($"送信中に相手が中断/拒否: transferId={transferId}, 理由={reason}", Util.LogLevel.Warning);
            sendingItem.State = TransferState.Cancelled;
            sendingItem.ErrorMessage = $"相手が中断しました: {reason}";
            _pausedSends.TryRemove(transferId, out _);
            // rere #B1-005: SendFileAsync の finally が同 CTS を TryRemove+Dispose する競合で
            // ObjectDisposedException になりうる（OnConnectionLost と同じく握りつぶす）。
            if (_sendCts.TryGetValue(transferId, out var sc))
                try { sc.Cancel(); } catch (ObjectDisposedException) { /* finally で Dispose 済みの競合は無視 */ }
            TransferError?.Invoke(this, sendingItem);
            return;
        }

        // v1.0.38 review fix v8: 受信側で pending approval として待機中のケース。
        // 送信側がタイムアウト等で expire を通知してきた → 受信側 UI からも消す必要がある
        // (これを処理しないと、ユーザーが後から承認ボタンを押した時に FileApprove を送って
        // 空ファイルが in-progress のまま残ってしまう)
        if (_pendingApprovals.TryRemove(transferId, out var pendingState))
        {
            Util.Logger.Log($"受信側 pending approval を expire (送信側通知): {pendingState.FileName} / 理由={reason}");
            pendingState.Item.State = TransferState.Cancelled;
            pendingState.Item.ErrorMessage = $"送信側がキャンセル: {reason}";
            // 複数ペア同時接続対応 Stage 2 leak fix (PR #12 review): pending approval 経路は
            // CleanupReceiveState に到達しないため、_transferPeerId 索引を直接掃除する。
            _transferPeerId.TryRemove(transferId, out _);
            TransferError?.Invoke(this, pendingState.Item);
            return;
        }

        // v1.0.38 review fix v8: race ケース — 受信側が timeout 直前で承認していて
        // 既に _receiveStates に移行している状態で送信側 reject が到着。
        // file stream は開いて空ファイルが空のまま残るので、ここで cleanup する
        if (_receiveStates.TryRemove(transferId, out var receiveState))
        {
            Util.Logger.Log($"受信側 in-progress を expire (送信側通知 / race): {receiveState.FileName} / 理由={reason}");
            receiveState.Item.State = TransferState.Cancelled;
            receiveState.Item.ErrorMessage = $"送信側がキャンセル: {reason}";
            CleanupReceiveState(receiveState);
            TransferError?.Invoke(this, receiveState.Item);
        }
    }

    /// <summary>
    /// v1.0.38: FileApprove メッセージを受信して、送信側の承認待ち TCS を完了させる。
    /// これによって SendFileAsync の `await approvalTcs.Task` が解放され、チャンク送信が開始される。
    /// </summary>
    private void HandleFileApprove(byte[] data)
    {
        var transferId = FileChunker.ParseApprove(data);
        if (transferId == null)
        {
            Util.Logger.Log("FileApprove のパースに失敗", Util.LogLevel.Warning);
            return;
        }
        Util.Logger.Log($"FileApprove 受信: transferId={transferId}");
        if (_pendingSendApprovals.TryRemove(transferId.Value, out var tcs))
            tcs.TrySetResult(true);
    }

    /// <summary>
    /// v1.0.46: 受信側から届いたフロー制御 ACK (FileFlowAck) を処理する。送信側で呼ばれる。
    /// 受信側が書き込み済みのチャンク数を反映し、SendChunksAsync のウィンドウ待機を進める。
    /// 順不同 ACK の巻き戻しを防ぐため単調増加のみ採用する。
    /// </summary>
    private void HandleFileFlowAck(byte[] data)
    {
        var parsed = FileChunker.ParseFlowAck(data);
        if (parsed == null) return;
        var (transferId, ackedChunks) = parsed.Value;
        var found = _activeTransfers.TryGetValue(transferId, out var item);
        // v1.0.47: ack 到達と item 解決を可視化（found=false が続くなら配線/transferId 不一致を疑う）
        // opop P-1: FlowAck ごと (4MB ごと) に呼ばれるため、Release (Info 以上) では補間文字列の構築自体を省く
        if (Util.Logger.IsEnabled(Util.LogLevel.Debug))
            Util.Logger.Log($"FlowAck 受信: transferId={transferId} acked={ackedChunks} found={found}", Util.LogLevel.Debug);
        if (found && item != null)
        {
            if (ackedChunks > Volatile.Read(ref item.FlowAckedChunks))
                Volatile.Write(ref item.FlowAckedChunks, ackedChunks);
        }
    }

    private void HandlePing(string peerId)
    {
        // Stage 5: Ping を受けた peer に Pong を返す（per-peer 送信。Stage 4 で並列接続が解禁された後の正しい宛先選択）。
        SendFireAndForget(peerId, FileChunker.CreatePongMessage(), "Pong");
    }

    private void HandleResumeRequest(byte[] data, string peerId)
    {
        // [type(1)][TransferId(16)][lastChunkIndex(4)] = 21byte 未満は破棄（短いメッセージでのパース例外を防ぐ）
        if (data.Length < 21) return;
        var (transferId, lastChunkIndex) = FileChunker.ParseResumeRequest(data);
        Util.Logger.Log($"レジュームリクエスト受信: transferId={transferId}, lastChunk={lastChunkIndex}");

        // Stage 5: レジューム応答もリクエスト元 peer に per-peer 送信。
        SendFireAndForget(peerId, FileChunker.CreateResumeResponseMessage(transferId, false, lastChunkIndex), "レジューム応答");
    }

    private void HandleResumeResponse(byte[] data)
    {
        // [type(1)][TransferId(16)][accepted(1)][lastChunkIndex(4)] = 22byte 未満は破棄
        if (data.Length < 22) return;
        var (transferId, accepted, lastChunkIndex) = FileChunker.ParseResumeResponse(data);
        Util.Logger.Log($"レジューム応答受信: transferId={transferId}, accepted={accepted}, lastChunk={lastChunkIndex}");
    }

    // === 承認/拒否 ===

    /// <summary>受信承認待ちの転送を承認する。ファイルストリームを開いて受信可能にする。
    /// P-6: 内部 dictionary が Guid 化されたため string → Guid を 1 度パースして以降使い回す。</summary>
    public void ApproveTransfer(string transferId)
    {
        if (!Guid.TryParse(transferId, out var tid)) return;
        if (!_pendingApprovals.TryRemove(tid, out var state))
        {
            Util.Logger.Log($"承認対象が見つかりません: {transferId}", Util.LogLevel.Warning);
            return;
        }

        Util.Logger.Log($"受信承認: {state.FileName}");

        // 受信用ファイルストリームを開く
        // rere #C2-001: バッファを 1MB に拡大 (デフォルト 4KB) + SetLength で全長を事前確保する。
        // 事前確保により sparse 拡張による断片化を防ぎ、HDD/暗号化ボリュームでの受信スループット低下と
        // 転送途中のディスク満杯エラー (途中まで書いて失敗) を承認時点で前倒し検出できる
        try
        {
            state.FileStream = new FileStream(
                state.SavePath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 20, FileOptions.None);
            state.FileStream.SetLength(state.FileSize);
        }
        catch (Exception ex)
        {
            // SetLength 失敗 (ディスク不足等) 時に開きかけのストリームを残さない
            state.FileStream?.Dispose();
            state.FileStream = null;
            Util.Logger.Log($"受信ファイル作成エラー: {ex.GetType().Name}: {ex.Message}", Util.LogLevel.Error);
            state.Item.State = TransferState.Error;
            state.Item.ErrorMessage = Util.ErrorText.Describe(ex);
            TransferError?.Invoke(this, state.Item);

            // v1.0.38 review fix v6: file open 失敗時に sender へ FileReject を送って
            // 60 秒の approval タイムアウト + 「相手が旧バージョン」の誤誘導エラーを防ぐ
            // v1.0.38 review fix v9: SendRejectFireAndForget ヘルパーに統一 (重複削減)
            // CodeRabbit 指摘: ex.Message に保存先絶対パス / ファイル名等のローカル PII が
            // 含まれうるため、ネットワーク越しの理由は固定文言に絞る
            SendRejectFireAndForget(tid, "受信ファイル作成エラー");
            return;
        }

        state.Item.State = TransferState.InProgress;
        // 受信済みチャンク追跡ビットマップを確保（chunkIndex ベースの書き込み・重複除外・完了判定に使用）
        state.ReceivedChunkSet = new bool[state.TotalChunks];
        state.WrittenBytes = 0;
        state.ReceivedChunks = 0;
        _receiveStates[tid] = state;

        // 承認前にバッファされたチャンクを処理 (v1.0.38 で送信側が承認待ちになったので通常は空だが、
        // 旧バージョン送信側との互換 / セーフティネットとして残す)
        if (state.BufferedChunks is { Count: > 0 })
        {
            foreach (var chunkData in state.BufferedChunks)
                HandleFileChunk(chunkData);
            state.BufferedChunks = null;
            state.BufferedBytes = 0;
        }

        // v1.0.38: 送信側に FileApprove を送って、チャンク送信を開始させる
        // (送信側は FileMeta 送信後にこれを待っている)
        // Stage 5: 受信ロジックは sender の peerId (state.Item.PeerId) に per-peer 送信。
        SendFireAndForget(state.Item.PeerId ?? string.Empty, FileChunker.CreateApproveMessage(tid), "FileApprove");
    }

    /// <summary>受信承認待ちの転送を拒否する。送信側に FileReject を送信する。</summary>
    public void RejectTransfer(string transferId)
    {
        if (!Guid.TryParse(transferId, out var tid)) return;
        if (!_pendingApprovals.TryRemove(tid, out var state))
        {
            Util.Logger.Log($"拒否対象が見つかりません: {transferId}", Util.LogLevel.Warning);
            return;
        }

        Util.Logger.Log($"受信拒否: {state.FileName}");
        state.Item.State = TransferState.Cancelled;
        state.Item.ErrorMessage = "受信を拒否しました";

        // FileReject メッセージを送信側に通知 — fire-and-forget でブロッキングを回避
        // v1.0.38: TransferId プレフィックス付きに変更 (同時複数転送の区別のため)
        // v1.0.38 review fix v9: SendRejectFireAndForget ヘルパーに統一 (重複削減)
        // 複数ペア同時接続対応 Stage 2 leak fix (PR #12 review): SendRejectFireAndForget は
        // 内部で _transferPeerId から peerId を引いて送るため、Remove はこの後で行う。
        SendRejectFireAndForget(tid, "受信側が拒否しました");
        _transferPeerId.TryRemove(tid, out _);
    }

    /// <summary>進行中の転送をキャンセルする。送受信どちら側からでも呼べ、相手にも FileReject で通知して
    /// 両側を停止・後始末する（v1.0.47）。</summary>
    public void CancelTransfer(string transferId)
    {
        if (!Guid.TryParse(transferId, out var tid)) return;

        // 受信中: 部分ファイルを削除し、送信側へ中断通知（送信側の _sendCts が cancel されて送信ループが止まる）
        if (_receiveStates.TryRemove(tid, out var receiveState))
        {
            Util.Logger.Log($"受信キャンセル: {receiveState.FileName}");
            receiveState.Item.State = TransferState.Cancelled;
            receiveState.Item.ErrorMessage = "キャンセルされました";
            CleanupReceiveState(receiveState);
            SendRejectFireAndForget(tid, "受信側がキャンセルしました");
            TransferError?.Invoke(this, receiveState.Item);
            return;
        }

        // 受信承認待ち: 送信側へ拒否通知して承認待ちを解除させる
        if (_pendingApprovals.TryRemove(tid, out var pendingState))
        {
            Util.Logger.Log($"承認待ちキャンセル: {pendingState.FileName}");
            pendingState.Item.State = TransferState.Cancelled;
            pendingState.Item.ErrorMessage = "キャンセルされました";
            // 複数ペア同時接続対応 Stage 2 leak fix (PR #12 review): SendRejectFireAndForget 内で
            // _transferPeerId 索引から peerId を引くので、Remove はこの後で行う。
            SendRejectFireAndForget(tid, "受信側がキャンセルしました");
            _transferPeerId.TryRemove(tid, out _);
            TransferError?.Invoke(this, pendingState.Item);
            return;
        }

        // 送信中: 送信ループを止め、承認待ち TCS を解放し、相手へ中断通知（受信側の部分ファイルを掃除させる）。
        // _activeTransfers からの除去は SendFileAsync の finally に任せる（item / _sendCts を生かしたまま停止させる）。
        if (_activeTransfers.TryGetValue(tid, out var sendItem))
        {
            Util.Logger.Log($"送信キャンセル: {sendItem.FileName}");
            sendItem.State = TransferState.Cancelled;
            sendItem.ErrorMessage = "キャンセルされました";
            _pausedSends.TryRemove(tid, out _);  // 一時停止中でも確実にループを抜けさせる

            // CodeRabbit 指摘: 送信側承認待ち TCS も解放しないと、FileMeta 送信後の承認待ち中に
            // CancelTransfer されても 60 秒タイムアウトまで pending が残る。TrySetResult(false) で抜けさせる
            if (_pendingSendApprovals.TryRemove(tid, out var tcs))
                tcs.TrySetResult(false);

            // 送信ループ（SendChunksAsync）を停止。OperationCanceledException が SendFileAsync を抜けて
            // VM 側で Cancelled 反映される（state は上で Cancelled 済みなので catch は Error に書き換えない）
            // rere #B1-005: finally の Dispose と競合した ObjectDisposedException は握りつぶす。
            if (_sendCts.TryGetValue(tid, out var cts))
                try { cts.Cancel(); } catch (ObjectDisposedException) { /* finally で Dispose 済みの競合は無視 */ }

            SendRejectFireAndForget(tid, "送信側がキャンセルしました");
        }
    }

    /// <summary>送信中の転送を一時停止する。SendChunksAsync が次のチャンク境界で待機に入る。
    /// 接続待ち / retry backoff など _activeTransfers にまだ載っていない場合は受理せず false を返す。</summary>
    public bool PauseSendTransfer(string transferId)
    {
        if (!Guid.TryParse(transferId, out var tid)) return false;
        if (!_activeTransfers.TryGetValue(tid, out var item) || item.State != TransferState.InProgress) return false;
        _pausedSends[tid] = 0;
        Util.Logger.Log($"送信一時停止: {item.FileName}");
        return true;
    }

    /// <summary>一時停止中の送信転送を再開する。</summary>
    public void ResumeSendTransfer(string transferId)
    {
        if (!Guid.TryParse(transferId, out var tid)) return;
        if (_pausedSends.TryRemove(tid, out _))
            Util.Logger.Log($"送信再開: {transferId}");
    }

    // === ユーティリティ ===

    /// <summary>
    /// ファイルパスが既に存在する場合、"ファイル名 (2).txt" のようにリネームする。
    /// </summary>
    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        // 万が一のフォールバック
        return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
    }


    private void CleanupReceiveState(ReceiveState state)
    {
        // rere #C2-001 review (race verify): CompleteReceive(受信ループ) と並行しても同一 FileStream を
        // 二重 dispose しないよう Interlocked.Exchange で所有権を取った側だけが dispose する。
        var fs = Interlocked.Exchange(ref state.FileStream, null);
        fs?.Dispose();
        _receiveStates.TryRemove(state.TransferId, out _);
        // 複数ペア同時接続対応 Stage 2: 受信終了時に索引も解放。
        _transferPeerId.TryRemove(state.TransferId, out _);

        // 不完全な受信ファイルを削除
        try
        {
            if (File.Exists(state.SavePath))
                File.Delete(state.SavePath);
        }
        catch { /* 削除失敗は無視 */ }
    }

    private void OnDataReceived(object? sender, Infrastructure.DataReceivedEventArgs e)
    {
        // 複数ペア同時接続対応 Stage 2: transport→ConnectionService 経由で運ばれた peerId を
        // 受信ルーティングに直結。HandleReceivedData が _transferPeerId 索引と
        // TransferItem.PeerId / _folderMappings キーへ権威値として設定する。
        HandleReceivedData(e.Data, e.PeerId);
    }

    /// <summary>
    /// ファイル受信中の状態管理。
    /// </summary>
    private sealed class ReceiveState
    {
        /// <summary>転送一意 ID。P-6: 旧 string 型から Guid 型に変更（dictionary キーと一致）。</summary>
        public Guid TransferId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public int TotalChunks { get; set; }
        public string ExpectedSha256 { get; set; } = string.Empty;
        public string SavePath { get; set; } = string.Empty;
        public int ReceivedChunks { get; set; }
        /// <summary>書き込み中の受信ファイルストリーム。rere #C2-001 review: 受信ループ(CompleteReceive)と
        /// UI スレッド(CancelTransfer→CleanupReceiveState)が並行 dispose しうるため、所有権の取得は
        /// Interlocked.Exchange で原子化する。そのため auto-property ではなく ref 可能な field にする。</summary>
        public FileStream? FileStream;
        public TransferItem Item { get; set; } = new();
        /// <summary>検証(finalize)開始の atomic claim。0=未開始 / 1=開始済み。
        /// rere #C2-001 review (codex P2): chunk 経路と FileHash 経路の二重起動を防ぐ。Interlocked で操作する。</summary>
        public int Finalizing;
        /// <summary>受信済みチャンクの追跡ビットマップ（承認時に確保）。重複除外・完了判定に使用。</summary>
        public bool[]? ReceivedChunkSet { get; set; }
        /// <summary>実書き込みバイト数（Seek 書き込みのため Position と別管理）。</summary>
        public long WrittenBytes { get; set; }
        /// <summary>マルチストリーム転送 PoC: 受信側の per-state 排他ロック。Relay 経路を N 本の WS に分散すると
        /// 複数受信ループが同一 ReceiveState の FileStream(Seek+Write) / ReceivedChunkSet / カウンタを同時更新
        /// しうる（従来は受信 1 本＝直列前提）。check-and-set（重複判定→Seek→Write→ビットマップ→カウンタ）を
        /// このロックで critical section 化し、二重書き込み/カウンタ過大計上による早期完了誤判定を防ぐ。
        /// 単一ストリーム経路では競合しないので実質 no-op（軽量 uncontended lock）。</summary>
        public readonly Lock WriteLock = new();
        /// <summary>承認前に到着したチャンクのバッファ。</summary>
        public List<byte[]>? BufferedChunks { get; set; }
        /// <summary>承認待ちバッファの累積バイト数（OOM 防止の上限管理用）。</summary>
        public long BufferedBytes { get; set; }
        /// <summary>P-11: 進捗通知の最終発火時刻（時間ベース throttle 用）。</summary>
        public long LastProgressTick { get; set; }
    }
}
