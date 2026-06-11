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
    /// <summary>承認待ち中にバッファできるチャンクの合計上限（OOM 防止）。</summary>
    private const long MaxApprovalBufferBytes = 64L * 1024 * 1024;

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

    /// <summary>外部から帯域制限器にアクセスするためのプロパティ (テスト用、および設定変更ハンドラ用)。</summary>
    public Util.TokenBucket UploadBucket => _uploadBucket;
    public Util.TokenBucket DownloadBucket => _downloadBucket;

    /// <summary>送信中の転送アイテム（レジューム用に保持）。</summary>
    private readonly ConcurrentDictionary<Guid, TransferItem> _activeTransfers = new();

    /// <summary>受信中の転送状態。TransferId → 受信状態。
    /// P-6: キーを string → Guid に変更し、毎チャンクの ToString() ヒープ確保 (1GB で 9MB) を撤去。</summary>
    private readonly ConcurrentDictionary<Guid, ReceiveState> _receiveStates = new();

    /// <summary>フォルダ受信時のルートフォルダ名マッピング（元の名前 → リネーム後の名前）。同一フォルダの全ファイルを同じ先に保存するため。</summary>
    private readonly ConcurrentDictionary<string, string> _folderMappings = new();

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
    private void OnConnectionLost(object? sender, EventArgs e)
    {
        Util.Logger.Log($"接続切断検知: 受信中 {_receiveStates.Count} 件 + 承認待ち(受) {_pendingApprovals.Count} 件 + 承認待ち(送) {_pendingSendApprovals.Count} 件 + 送信中 {_activeTransfers.Count} 件を cleanup", Util.LogLevel.Warning);

        // v1.0.47: 「一時停止中だった」送信だけを抜けさせる（CTS Cancel）。それ以外の進行中送信は
        // SendChunksAsync の _connectionService.SendAsync(...) が転送断で IOException を投げ、
        // SendItemAsync の transient catch（MaxSendAttempts までリトライ）に乗るのが望ましい。
        // ここで全 _sendCts を一括 Cancel すると OperationCanceled になり、リトライ機構を素通りして
        // 即 Cancelled で履歴が終わってしまう（接続断 = 即諦め）ので、対象を paused に限定する。
        var pausedTids = _pausedSends.Keys.ToArray();
        _pausedSends.Clear();
        foreach (var tid in pausedTids)
        {
            if (_sendCts.TryGetValue(tid, out var cts))
            {
                try { cts.Cancel(); } catch { /* ignore */ }
            }
        }

        // 受信中の部分ファイルを削除
        foreach (var tid in _receiveStates.Keys.ToArray())
        {
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
            if (_pendingApprovals.TryRemove(tid, out var pending))
            {
                pending.Item.State = TransferState.Cancelled;
                pending.Item.ErrorMessage = "接続が切断されました";
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
    public async Task SendFileAsync(string filePath, string? relativePath = null, Guid? requestedTransferId = null, CancellationToken ct = default)
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

            await _connectionService.SendAsync(metaMessage, ct);
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
            await SendChunksAsync(filePath, transferId, startChunk: 0, item, ct, hashSink);

            // 4. 確定したハッシュを後送り
            var sha256Bytes = hashSink.GetHashAndReset();
            item.Sha256Hash = Convert.ToHexString(sha256Bytes).ToLowerInvariant();
            var hashMessage = FileChunker.CreateFileHashMessage(transferId, sha256Bytes);
            await _connectionService.SendAsync(hashMessage, ct);

            Util.Logger.Log($"ファイル送信完了: {displayName}, SHA256={item.Sha256Hash[..16]}…");
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"ファイル送信エラー: {ex.Message}", Util.LogLevel.Error);
            // v1.0.38 review fix v6: 承認待ちタイムアウト / 拒否で既に Cancelled + TransferError 発火済みなら
            // 二重発火を防ぐ (TransferViewModel に重複行が出る問題の根本対策)
            if (item.State != TransferState.Cancelled)
            {
                item.State = TransferState.Error;
                item.ErrorMessage = ex.Message;
                TransferError?.Invoke(this, item);
            }
            throw;
        }
        finally
        {
            _pendingSendApprovals.TryRemove(transferId, out _);
            _activeTransfers.TryRemove(transferId, out _);
            _pausedSends.TryRemove(transferId, out _);
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

        // v1.0.38 review fix #3: 承認待ち TCS を準備
        var approvalTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSendApprovals[transferId] = approvalTcs;
        item.State = TransferState.Pending;
        ProgressChanged?.Invoke(this, item);

        try
        {
            var metaMessage = FileChunker.CreateFileMetaMessage(
                item.FileName, item.FileSize, item.TotalChunks, item.Sha256Hash ?? "", item.TransferId);
            await _connectionService.SendAsync(metaMessage, ct);
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

            await SendChunksAsync(item.SourceFilePath, item.TransferId, startChunk, item, ct);
            return true;
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"レジュームエラー: {ex.Message}", Util.LogLevel.Error);
            item.State = TransferState.Error;
            item.ErrorMessage = ex.Message;
            TransferError?.Invoke(this, item);
            return false;
        }
        finally
        {
            _pendingSendApprovals.TryRemove(transferId, out _);
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
    /// </summary>
    public void HandleReceivedData(byte[] data)
    {
        if (data.Length == 0) return;

        var messageType = FileChunker.GetMessageType(data);

        switch (messageType)
        {
            case TransferProtocol.FileMeta:
                HandleFileMeta(data);
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
                HandlePing();
                break;

            case TransferProtocol.Pong:
                // Pong は現時点では特に処理しない
                break;

            case TransferProtocol.ResumeRequest:
                HandleResumeRequest(data);
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

    /// <summary>
    /// チャンクを順次送信する。バックプレッシャーとして一定間隔で進捗を通知する。
    /// </summary>
    private async Task SendChunksAsync(string filePath, Guid transferId, int startChunk, TransferItem item, CancellationToken ct, System.Security.Cryptography.IncrementalHash? hashSink = null)
    {
        var sentCount = 0;
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
            if (_connectionService.Route is not (ConnectionRoute.Direct or ConnectionRoute.StunAssisted))
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
                await _connectionService.SendAsync(buffer.AsMemory(0, messageSize), ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            item.TransferredBytes = (long)(index + 1) * TransferProtocol.ChunkSize;
            if (item.TransferredBytes > item.FileSize)
                item.TransferredBytes = item.FileSize;
            item.LastConfirmedChunkIndex = index;

            sentCount++;

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
    /// </summary>
    private void SendRejectFireAndForget(Guid transferId, string reason)
    {
        var rejectMessage = FileChunker.CreateRejectMessage(transferId, reason);
        _ = Task.Run(async () =>
        {
            try { await _connectionService.SendAsync(rejectMessage); }
            catch (Exception ex) { Util.Logger.Log($"FileReject 送信エラー: {ex.Message}", Util.LogLevel.Warning); }
        });
    }

    /// <summary>
    /// v1.0.46: 受信側 → 送信側のフロー制御 ACK (FileFlowAck) を送る。受信スレッドから fire-and-forget で
    /// 呼ばれるため、例外は内部で握り潰してタスクが faulted にならないようにする (UnobservedTaskException 防止)。
    /// </summary>
    private async Task SendFlowAckAsync(Guid transferId, int receivedChunkCount)
    {
        try
        {
            var msg = FileChunker.CreateFlowAckMessage(transferId, receivedChunkCount);
            await _connectionService.SendAsync(msg);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"FlowAck 送信失敗（無視）: {ex.Message}", Util.LogLevel.Debug);
        }
    }

    private void HandleFileMeta(byte[] data)
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

        var displayName = meta.RelativePath ?? meta.FileName;
        Util.Logger.Log($"ファイル受信開始: {displayName}, サイズ={meta.FileSize}, チャンク数={meta.TotalChunks}, TransferId={meta.TransferId}");

        var saveDir = _settingsService.Settings.SaveDirectory;

        // RelativePath がある場合はフォルダ構造を再現
        string savePath;
        if (!string.IsNullOrEmpty(meta.RelativePath))
        {
            // パストラバーサル防止
            var normalized = meta.RelativePath.Replace('\\', '/');
            if (normalized.Contains(".."))
            {
                Util.Logger.Log($"不正な RelativePath を検出: {meta.RelativePath}", Util.LogLevel.Warning);
                SendRejectFireAndForget(transferIdGuid, "不正なファイルパス (パストラバーサル)");
                return;
            }

            // ルートフォルダ名を取得（例: "photos/sub/file.jpg" → "photos"）
            var parts = normalized.Split('/');
            var rootFolder = parts[0];

            // 同名フォルダ/ファイルが存在する場合、ルートフォルダ名をリネーム
            // 同一フォルダの全ファイルが同じリネーム先になるようキャッシュ
            var actualRoot = _folderMappings.GetOrAdd(rootFolder, key =>
            {
                var candidatePath = Path.Combine(saveDir, key);
                if (!Directory.Exists(candidatePath) && !File.Exists(candidatePath))
                    return key;

                // "フォルダ名 (2)" のように連番リネーム
                for (var i = 2; i < 10000; i++)
                {
                    var renamed = $"{key} ({i})";
                    var renamedPath = Path.Combine(saveDir, renamed);
                    if (!Directory.Exists(renamedPath) && !File.Exists(renamedPath))
                        return renamed;
                }
                return $"{key}_{Guid.NewGuid():N}";
            });

            // ルートフォルダ名を置換して保存パスを組み立て
            parts[0] = actualRoot;
            savePath = Path.Combine(saveDir, Path.Combine(parts));
        }
        else
        {
            // パストラバーサル防止: ピア制御のファイル名はディレクトリ要素を除去する
            savePath = Path.Combine(saveDir, Path.GetFileName(meta.FileName));
            // 単独ファイルの同名リネーム
            savePath = GetUniquePath(savePath);
        }

        // パストラバーサル最終防御: 組み立てた保存先が saveDir 配下に収まることを検証
        // （RelativePath 経路の絶対パス混入や Path.Combine の親破棄挙動を弾く）
        var fullSaveDir = Path.GetFullPath(saveDir);
        var dirWithSep = fullSaveDir.EndsWith(Path.DirectorySeparatorChar)
            ? fullSaveDir
            : fullSaveDir + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(savePath).StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase))
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
                // 接続元ピアを FileMeta 到着時点で確定させる（VM 側 ResolveReceivePeer の後付け推測より
                // 権威ある値。宛先別履歴が誤ピアに混入しないようにする。VM はこれが空のときだけ補完する）。
                PeerId = _connectionService.ConnectedPeer?.SessionId
                         ?? _connectionService.CurrentListeningPeerId
                         ?? string.Empty,
            },
        };

        // 承認待ちキューに追加し、UI に通知
        _pendingApprovals[transferIdGuid] = state;
        Util.Logger.Log($"受信承認待ち: {displayName} ({Util.Formatting.FormatBytes(meta.FileSize)})");
        ApprovalRequested?.Invoke(this, state.Item);
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
                if (pending.BufferedBytes + chunkLength > MaxApprovalBufferBytes)
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
            state.Item.State = TransferState.Error;
            state.Item.ErrorMessage = "受信データが申告サイズを超過しました";
            TransferError?.Invoke(this, state.Item);
            CleanupReceiveState(state);
            return;
        }

        try
        {
            // chunkIndex をオフセットに変換して書き込む（順不同到着でも正しい位置に置く）。重複チャンクは無視
            if (state.ReceivedChunkSet != null && !state.ReceivedChunkSet[chunkIndex])
            {
                state.FileStream!.Seek(offset, SeekOrigin.Begin);
                state.FileStream.Write(data.AsSpan(TransferProtocol.ChunkHeaderSize));
                state.ReceivedChunkSet[chunkIndex] = true;
                state.ReceivedChunks++;
                state.WrittenBytes += chunkLength;

                // ダウンロード帯域制限。重複でないチャンクだけカウントする。同期 Wait で受信ループを
                // 直接減速させ、TCP/WebSocket のバックプレッシャーを上流 (送信側) へ伝える。
                // 0 (無制限) なら即 return。
                _downloadBucket.Wait(chunkLength);

                // v1.0.46: 一定チャンクごとに送信側へ「書き込み済みチャンク数」を FlowAck で返す。
                // 送信側はこれを credit にウィンドウ制御し、リレー中継バッファの溢れ (~55秒切断) を防ぐ。
                // 末尾の端数チャンク (TotalChunks が間隔の倍数でない場合) でも確実に最終 ACK が届くよう、
                // 全チャンク書き込み完了時にも送る (送信側ウィンドウ待機の取りこぼし防止の安全網)。
                if (state.ReceivedChunks % TransferProtocol.FlowAckIntervalChunks == 0
                    || state.ReceivedChunks == state.TotalChunks)
                    _ = SendFlowAckAsync(transferId, state.ReceivedChunks);
            }

            state.Item.TransferredBytes = state.WrittenBytes;
            state.Item.LastConfirmedChunkIndex = chunkIndex;

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
            Util.Logger.Log($"チャンク書き込みエラー: {ex.Message}", Util.LogLevel.Error);
            state.Item.State = TransferState.Error;
            state.Item.ErrorMessage = ex.Message;
            TransferError?.Invoke(this, state.Item);
            CleanupReceiveState(state);
        }
    }

    /// <summary>
    /// ファイル受信を完了する。SHA-256 を検証し、ACK を送信する。
    /// </summary>
    private void CompleteReceive(ReceiveState state)
    {
        state.FileStream?.Flush();
        state.FileStream?.Dispose();
        state.FileStream = null;

        Util.Logger.Log($"全チャンク受信完了: {state.FileName}, 検証中…");

        // SHA-256 検証（1回のハッシュ計算で検証と ACK 送信の両方に使用）
        var sha256Bytes = FileChunker.ComputeSha256(state.SavePath);
        var actualHash = Convert.ToHexString(sha256Bytes).ToLowerInvariant();
        var hashMatch = string.Equals(actualHash, state.ExpectedSha256, StringComparison.OrdinalIgnoreCase);

        if (hashMatch)
        {
            Util.Logger.Log($"SHA-256 検証成功: {state.FileName}");
            state.Item.State = TransferState.Completed;
            state.Item.TransferredBytes = state.FileSize;
            state.Item.SavedFilePath = state.SavePath;
        }
        else
        {
            Util.Logger.Log($"SHA-256 検証失敗: 期待={state.ExpectedSha256[..16]}…, 実際={actualHash[..16]}…", Util.LogLevel.Error);
            state.Item.State = TransferState.Error;
            state.Item.ErrorMessage = "ファイルの整合性検証に失敗しました（SHA-256 不一致）";
        }

        // ACK を送信（送信側に結果を通知）— fire-and-forget でブロッキングを回避
        var ackMessage = FileChunker.CreateAckMessage(hashMatch, sha256Bytes);
        _ = Task.Run(async () =>
        {
            try
            {
                await _connectionService.SendAsync(ackMessage);
                Util.Logger.Log("ACK 送信完了");
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"ACK 送信エラー: {ex.Message}", Util.LogLevel.Warning);
            }
        });

        if (hashMatch)
        {
            FileReceived?.Invoke(this, state.Item);
        }
        else
        {
            TransferError?.Invoke(this, state.Item);
            // 不正なファイルを削除
            try { File.Delete(state.SavePath); }
            catch { /* 削除失敗は無視 */ }
        }

        _receiveStates.TryRemove(state.TransferId, out _);

        // 全受信完了時にフォルダマッピングキャッシュをクリア
        if (_receiveStates.IsEmpty)
            _folderMappings.Clear();
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
            Util.Logger.Log("FileHash メッセージのパースに失敗", Util.LogLevel.Warning);
            return;
        }

        // rere #B1-001: メッセージに含まれる TransferId で受信状態を直接引く。
        // 旧実装は「最初の ExpectedSha256 未確定 state」に紐付けており、並列転送
        // (ParallelTransferCount>1) で別ファイルのハッシュを取り違える余地があった。
        var (transferId, sha256Bytes) = parsed.Value;
        var hex = Convert.ToHexString(sha256Bytes).ToLowerInvariant();
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
            if (_sendCts.TryGetValue(transferId, out var sc))
                sc.Cancel();
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
        Util.Logger.Log($"FlowAck 受信: transferId={transferId} acked={ackedChunks} found={found}", Util.LogLevel.Debug);
        if (found && item != null)
        {
            if (ackedChunks > Volatile.Read(ref item.FlowAckedChunks))
                Volatile.Write(ref item.FlowAckedChunks, ackedChunks);
        }
    }

    private void HandlePing()
    {
        // fire-and-forget でブロッキングを回避
        var pong = FileChunker.CreatePongMessage();
        _ = Task.Run(async () =>
        {
            try
            {
                await _connectionService.SendAsync(pong);
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"Pong 送信エラー: {ex.Message}", Util.LogLevel.Warning);
            }
        });
    }

    private void HandleResumeRequest(byte[] data)
    {
        var (transferId, lastChunkIndex) = FileChunker.ParseResumeRequest(data);
        Util.Logger.Log($"レジュームリクエスト受信: transferId={transferId}, lastChunk={lastChunkIndex}");

        // レジューム応答（現時点では非対応として拒否）— fire-and-forget でブロッキングを回避
        var response = FileChunker.CreateResumeResponseMessage(transferId, false, lastChunkIndex);
        _ = Task.Run(async () =>
        {
            try
            {
                await _connectionService.SendAsync(response);
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"レジューム応答送信エラー: {ex.Message}", Util.LogLevel.Warning);
            }
        });
    }

    private void HandleResumeResponse(byte[] data)
    {
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
            Util.Logger.Log($"受信ファイル作成エラー: {ex.Message}", Util.LogLevel.Error);
            state.Item.State = TransferState.Error;
            state.Item.ErrorMessage = ex.Message;
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
        var approveMessage = FileChunker.CreateApproveMessage(tid);
        _ = Task.Run(async () =>
        {
            try { await _connectionService.SendAsync(approveMessage); }
            catch (Exception ex) { Util.Logger.Log($"FileApprove 送信エラー: {ex.Message}", Util.LogLevel.Warning); }
        });
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
        SendRejectFireAndForget(tid, "受信側が拒否しました");
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
            SendRejectFireAndForget(tid, "受信側がキャンセルしました");
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
            if (_sendCts.TryGetValue(tid, out var cts))
                cts.Cancel();

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
        state.FileStream?.Dispose();
        state.FileStream = null;
        _receiveStates.TryRemove(state.TransferId, out _);

        // 不完全な受信ファイルを削除
        try
        {
            if (File.Exists(state.SavePath))
                File.Delete(state.SavePath);
        }
        catch { /* 削除失敗は無視 */ }
    }

    private void OnDataReceived(object? sender, byte[] data)
    {
        HandleReceivedData(data);
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
        public FileStream? FileStream { get; set; }
        public TransferItem Item { get; set; } = new();
        /// <summary>受信済みチャンクの追跡ビットマップ（承認時に確保）。重複除外・完了判定に使用。</summary>
        public bool[]? ReceivedChunkSet { get; set; }
        /// <summary>実書き込みバイト数（Seek 書き込みのため Position と別管理）。</summary>
        public long WrittenBytes { get; set; }
        /// <summary>承認前に到着したチャンクのバッファ。</summary>
        public List<byte[]>? BufferedChunks { get; set; }
        /// <summary>承認待ちバッファの累積バイト数（OOM 防止の上限管理用）。</summary>
        public long BufferedBytes { get; set; }
        /// <summary>P-11: 進捗通知の最終発火時刻（時間ベース throttle 用）。</summary>
        public long LastProgressTick { get; set; }
    }
}
