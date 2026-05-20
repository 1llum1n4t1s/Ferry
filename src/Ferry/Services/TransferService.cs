using System;
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
public sealed class TransferService : ITransferService
{
    private readonly IConnectionService _connectionService;
    private readonly ISettingsService _settingsService;

    /// <summary>送信中の転送アイテム（レジューム用に保持）。</summary>
    private readonly ConcurrentDictionary<Guid, TransferItem> _activeTransfers = new();

    /// <summary>受信中の転送状態。TransferId → 受信状態。</summary>
    private readonly ConcurrentDictionary<string, ReceiveState> _receiveStates = new();

    /// <summary>フォルダ受信時のルートフォルダ名マッピング（元の名前 → リネーム後の名前）。同一フォルダの全ファイルを同じ先に保存するため。</summary>
    private readonly ConcurrentDictionary<string, string> _folderMappings = new();

    /// <summary>承認待ちの転送状態（TransferId → ReceiveState）。承認/拒否後に _receiveStates へ移動。</summary>
    private readonly ConcurrentDictionary<string, ReceiveState> _pendingApprovals = new();

    public event EventHandler<TransferItem>? ProgressChanged;
    public event EventHandler<TransferItem>? FileReceived;
    public event EventHandler<TransferItem>? TransferError;
    public event EventHandler<TransferItem>? ApprovalRequested;

    public TransferService(IConnectionService connectionService, ISettingsService settingsService)
    {
        _connectionService = connectionService;
        _settingsService = settingsService;

        // 受信データハンドラを登録
        _connectionService.DataReceived += OnDataReceived;
    }

    /// <summary>
    /// ファイルを送信する。チャンク分割→メタデータ送信→チャンク順次送信→ACK 待ち。
    /// </summary>
    /// <param name="filePath">送信するファイルの絶対パス。</param>
    /// <param name="relativePath">フォルダ送信時の相対パス（例: "フォルダ名/サブフォルダ/ファイル名"）。null で単独ファイル。</param>
    /// <param name="ct">キャンセルトークン。</param>
    public async Task SendFileAsync(string filePath, string? relativePath = null, CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("送信ファイルが見つかりません", filePath);

        var totalChunks = FileChunker.CalculateTotalChunks(fileInfo.Length);
        var sha256Hex = FileChunker.ComputeSha256Hex(filePath);
        var transferId = Guid.NewGuid();

        var displayName = relativePath ?? fileInfo.Name;
        Util.Logger.Log($"ファイル送信開始: {displayName}, サイズ={fileInfo.Length}, チャンク数={totalChunks}, SHA256={sha256Hex[..16]}…");

        var item = new TransferItem
        {
            TransferId = transferId,
            FileName = displayName,
            FileSize = fileInfo.Length,
            TotalChunks = totalChunks,
            Direction = TransferDirection.Send,
            State = TransferState.InProgress,
            Sha256Hash = sha256Hex,
            SourceFilePath = filePath,
        };
        _activeTransfers[transferId] = item;

        try
        {
            // 1. メタデータを送信
            var metaMessage = FileChunker.CreateFileMetaMessage(
                fileInfo.Name, fileInfo.Length, totalChunks, sha256Hex, transferId, relativePath);
            await _connectionService.SendAsync(metaMessage, ct);
            Util.Logger.Log("ファイルメタデータ送信完了");

            // 2. チャンクを順次送信
            await SendChunksAsync(filePath, transferId, startChunk: 0, item, ct);

            Util.Logger.Log($"ファイル送信完了: {displayName}");
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"ファイル送信エラー: {ex.Message}", Util.LogLevel.Error);
            item.State = TransferState.Error;
            item.ErrorMessage = ex.Message;
            TransferError?.Invoke(this, item);
            throw;
        }
        finally
        {
            _activeTransfers.TryRemove(transferId, out _);
        }
    }

    /// <summary>
    /// 中断された転送をレジュームする。
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

        var startChunk = item.LastConfirmedChunkIndex + 1;
        Util.Logger.Log($"転送レジューム: {item.FileName}, チャンク {startChunk}/{item.TotalChunks} から再開");

        item.State = TransferState.InProgress;

        try
        {
            // メタデータを再送信（相手側でレジューム状態を認識させる）
            var metaMessage = FileChunker.CreateFileMetaMessage(
                item.FileName, item.FileSize, item.TotalChunks, item.Sha256Hash ?? "", item.TransferId);
            await _connectionService.SendAsync(metaMessage, ct);

            // チャンクを再開位置から送信
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
    private async Task SendChunksAsync(string filePath, Guid transferId, int startChunk, TransferItem item, CancellationToken ct)
    {
        var sentCount = 0;
        foreach (var (index, chunkData) in FileChunker.ReadChunks(filePath))
        {
            ct.ThrowIfCancellationRequested();

            // レジューム: 開始チャンクまでスキップ
            if (index < startChunk)
                continue;

            var chunkMessage = FileChunker.CreateChunkMessage(index, chunkData);
            await _connectionService.SendAsync(chunkMessage, ct);

            item.TransferredBytes = (long)(index + 1) * TransferProtocol.ChunkSize;
            if (item.TransferredBytes > item.FileSize)
                item.TransferredBytes = item.FileSize;
            item.LastConfirmedChunkIndex = index;

            sentCount++;

            // 進捗通知（32チャンクごと ≈ 512KB ごと）
            if (sentCount % 32 == 0)
            {
                ProgressChanged?.Invoke(this, item);
            }

            // バックプレッシャー: TCP の送信バッファが溜まりすぎないよう微小な待機
            // 大ファイル送信時の CPU 占有率を下げる
            if (sentCount % 64 == 0)
            {
                await Task.Yield();
            }
        }

        // 最終進捗通知
        item.TransferredBytes = item.FileSize;
        item.State = TransferState.Completed;
        ProgressChanged?.Invoke(this, item);
    }

    // === 受信ハンドラ ===

    private void HandleFileMeta(byte[] data)
    {
        var meta = FileChunker.ParseFileMeta(data);
        if (meta == null)
        {
            Util.Logger.Log("ファイルメタデータのパースに失敗", Util.LogLevel.Warning);
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
            return;
        }

        // 保存先ディレクトリを作成
        var saveFileDir = Path.GetDirectoryName(savePath) ?? saveDir;
        if (!Directory.Exists(saveFileDir))
        {
            try { Directory.CreateDirectory(saveFileDir); }
            catch (Exception ex)
            {
                Util.Logger.Log($"保存先ディレクトリ作成失敗: {ex.Message}", Util.LogLevel.Error);
                return;
            }
        }

        // フォルダ内の個別ファイルも重複チェック
        if (!string.IsNullOrEmpty(meta.RelativePath))
            savePath = GetUniquePath(savePath);

        var state = new ReceiveState
        {
            TransferId = meta.TransferId,
            FileName = displayName,
            FileSize = meta.FileSize,
            TotalChunks = meta.TotalChunks,
            ExpectedSha256 = meta.Sha256,
            SavePath = savePath,
            ReceivedChunks = 0,
            Item = new TransferItem
            {
                TransferId = Guid.TryParse(meta.TransferId, out var tid) ? tid : Guid.NewGuid(),
                FileName = displayName,
                FileSize = meta.FileSize,
                TotalChunks = meta.TotalChunks,
                Direction = TransferDirection.Receive,
                State = TransferState.WaitingApproval,
                Sha256Hash = meta.Sha256,
            },
        };

        // 承認待ちキューに追加し、UI に通知
        _pendingApprovals[meta.TransferId] = state;
        Util.Logger.Log($"受信承認待ち: {displayName} ({Util.Formatting.FormatBytes(meta.FileSize)})");
        ApprovalRequested?.Invoke(this, state.Item);
    }

    private void HandleFileChunk(byte[] data)
    {
        if (data.Length < 5) return;

        var chunkIndex = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(1, 4));
        var chunkData = data.AsSpan(5);

        // 受信中の転送を特定（現時点では1つだけの想定）
        var state = _receiveStates.Values.FirstOrDefault(s => s.FileStream != null);
        if (state == null)
        {
            // 承認待ち中のチャンクをバッファリング
            var pending = _pendingApprovals.Values.FirstOrDefault();
            if (pending != null)
            {
                pending.BufferedChunks ??= [];
                pending.BufferedChunks.Add(data.ToArray());
            }
            return;
        }

        try
        {
            // チャンクをファイルに書き込み
            state.FileStream!.Write(chunkData);
            state.ReceivedChunks++;

            state.Item.TransferredBytes = state.FileStream.Position;
            state.Item.LastConfirmedChunkIndex = chunkIndex;

            // 進捗通知（32チャンクごと）
            if (state.ReceivedChunks % 32 == 0)
            {
                ProgressChanged?.Invoke(this, state.Item);
            }

            // 全チャンク受信完了
            if (state.ReceivedChunks >= state.TotalChunks)
            {
                CompleteReceive(state);
            }
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
        var reason = data.Length > 1
            ? Encoding.UTF8.GetString(data, 1, data.Length - 1)
            : "不明な理由";
        Util.Logger.Log($"ファイル拒否: {reason}", Util.LogLevel.Warning);

        // 送信中のアイテムにエラーを通知
        var sendingItem = _activeTransfers.Values.FirstOrDefault(t => t.State == TransferState.InProgress);
        if (sendingItem != null)
        {
            sendingItem.State = TransferState.Error;
            sendingItem.ErrorMessage = $"相手が受信を拒否しました: {reason}";
            TransferError?.Invoke(this, sendingItem);
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

    /// <summary>受信承認待ちの転送を承認する。ファイルストリームを開いて受信可能にする。</summary>
    public void ApproveTransfer(string transferId)
    {
        if (!_pendingApprovals.TryRemove(transferId, out var state))
        {
            Util.Logger.Log($"承認対象が見つかりません: {transferId}", Util.LogLevel.Warning);
            return;
        }

        Util.Logger.Log($"受信承認: {state.FileName}");

        // 受信用ファイルストリームを開く
        try
        {
            state.FileStream = new FileStream(state.SavePath, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"受信ファイル作成エラー: {ex.Message}", Util.LogLevel.Error);
            state.Item.State = TransferState.Error;
            state.Item.ErrorMessage = ex.Message;
            TransferError?.Invoke(this, state.Item);
            return;
        }

        state.Item.State = TransferState.InProgress;
        _receiveStates[transferId] = state;

        // 承認前にバッファされたチャンクを処理
        if (state.BufferedChunks is { Count: > 0 })
        {
            foreach (var chunkData in state.BufferedChunks)
                HandleFileChunk(chunkData);
            state.BufferedChunks = null;
        }
    }

    /// <summary>受信承認待ちの転送を拒否する。送信側に FileReject を送信する。</summary>
    public void RejectTransfer(string transferId)
    {
        if (!_pendingApprovals.TryRemove(transferId, out var state))
        {
            Util.Logger.Log($"拒否対象が見つかりません: {transferId}", Util.LogLevel.Warning);
            return;
        }

        Util.Logger.Log($"受信拒否: {state.FileName}");
        state.Item.State = TransferState.Cancelled;
        state.Item.ErrorMessage = "受信を拒否しました";

        // FileReject メッセージを送信側に通知 — fire-and-forget でブロッキングを回避
        var rejectMessage = FileChunker.CreateRejectMessage("受信側が拒否しました");
        _ = Task.Run(async () =>
        {
            try
            {
                await _connectionService.SendAsync(rejectMessage);
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"拒否メッセージ送信エラー: {ex.Message}", Util.LogLevel.Warning);
            }
        });
    }

    /// <summary>進行中の転送をキャンセルする。</summary>
    public void CancelTransfer(string transferId)
    {
        if (_receiveStates.TryRemove(transferId, out var receiveState))
        {
            Util.Logger.Log($"受信キャンセル: {receiveState.FileName}");
            receiveState.Item.State = TransferState.Cancelled;
            receiveState.Item.ErrorMessage = "キャンセルされました";
            CleanupReceiveState(receiveState);
            TransferError?.Invoke(this, receiveState.Item);
            return;
        }
        if (_pendingApprovals.TryRemove(transferId, out var pendingState))
        {
            Util.Logger.Log($"承認待ちキャンセル: {pendingState.FileName}");
            pendingState.Item.State = TransferState.Cancelled;
            pendingState.Item.ErrorMessage = "キャンセルされました";
            TransferError?.Invoke(this, pendingState.Item);
            return;
        }
        if (Guid.TryParse(transferId, out var tid) && _activeTransfers.TryRemove(tid, out var sendItem))
        {
            Util.Logger.Log($"送信キャンセル: {sendItem.FileName}");
            sendItem.State = TransferState.Cancelled;
            sendItem.ErrorMessage = "キャンセルされました";
            TransferError?.Invoke(this, sendItem);
        }
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
        public string TransferId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public int TotalChunks { get; set; }
        public string ExpectedSha256 { get; set; } = string.Empty;
        public string SavePath { get; set; } = string.Empty;
        public int ReceivedChunks { get; set; }
        public FileStream? FileStream { get; set; }
        public TransferItem Item { get; set; } = new();
        /// <summary>承認前に到着したチャンクのバッファ。</summary>
        public List<byte[]>? BufferedChunks { get; set; }
    }
}
