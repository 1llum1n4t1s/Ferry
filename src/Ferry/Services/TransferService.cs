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

    public event EventHandler<TransferItem>? ProgressChanged;
    public event EventHandler<TransferItem>? FileReceived;
    public event EventHandler<TransferItem>? TransferError;

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
    public async Task SendFileAsync(string filePath, CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("送信ファイルが見つかりません", filePath);

        var totalChunks = FileChunker.CalculateTotalChunks(fileInfo.Length);
        var sha256Hex = FileChunker.ComputeSha256Hex(filePath);
        var transferId = Guid.NewGuid();

        Util.Logger.Log($"ファイル送信開始: {fileInfo.Name}, サイズ={fileInfo.Length}, チャンク数={totalChunks}, SHA256={sha256Hex[..16]}…");

        var item = new TransferItem
        {
            TransferId = transferId,
            FileName = fileInfo.Name,
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
                fileInfo.Name, fileInfo.Length, totalChunks, sha256Hex, transferId);
            await _connectionService.SendAsync(metaMessage, ct);
            Util.Logger.Log("ファイルメタデータ送信完了");

            // 2. チャンクを順次送信
            await SendChunksAsync(filePath, transferId, startChunk: 0, item, ct);

            Util.Logger.Log($"ファイル送信完了: {fileInfo.Name}");
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

        Util.Logger.Log($"ファイル受信開始: {meta.FileName}, サイズ={meta.FileSize}, チャンク数={meta.TotalChunks}, TransferId={meta.TransferId}");

        var saveDir = _settingsService.Settings.SaveDirectory;
        if (!Directory.Exists(saveDir))
        {
            try { Directory.CreateDirectory(saveDir); }
            catch (Exception ex)
            {
                Util.Logger.Log($"保存先ディレクトリ作成失敗: {ex.Message}", Util.LogLevel.Error);
                return;
            }
        }

        // 同名ファイルがある場合はリネーム
        var savePath = GetUniquePath(Path.Combine(saveDir, meta.FileName));

        var state = new ReceiveState
        {
            TransferId = meta.TransferId,
            FileName = meta.FileName,
            FileSize = meta.FileSize,
            TotalChunks = meta.TotalChunks,
            ExpectedSha256 = meta.Sha256,
            SavePath = savePath,
            ReceivedChunks = 0,
            Item = new TransferItem
            {
                TransferId = Guid.TryParse(meta.TransferId, out var tid) ? tid : Guid.NewGuid(),
                FileName = meta.FileName,
                FileSize = meta.FileSize,
                TotalChunks = meta.TotalChunks,
                Direction = TransferDirection.Receive,
                State = TransferState.InProgress,
                Sha256Hash = meta.Sha256,
            },
        };

        // 受信用ファイルストリームを開く
        try
        {
            state.FileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"受信ファイル作成エラー: {ex.Message}", Util.LogLevel.Error);
            return;
        }

        _receiveStates[meta.TransferId] = state;
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
            Util.Logger.Log($"チャンク受信: 対応する転送が見つかりません (index={chunkIndex})", Util.LogLevel.Warning);
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

        // SHA-256 検証
        var actualHash = FileChunker.ComputeSha256Hex(state.SavePath);
        var hashMatch = string.Equals(actualHash, state.ExpectedSha256, StringComparison.OrdinalIgnoreCase);

        if (hashMatch)
        {
            Util.Logger.Log($"SHA-256 検証成功: {state.FileName}");
            state.Item.State = TransferState.Completed;
            state.Item.TransferredBytes = state.FileSize;
        }
        else
        {
            Util.Logger.Log($"SHA-256 検証失敗: 期待={state.ExpectedSha256[..16]}…, 実際={actualHash[..16]}…", Util.LogLevel.Error);
            state.Item.State = TransferState.Error;
            state.Item.ErrorMessage = "ファイルの整合性検証に失敗しました（SHA-256 不一致）";
        }

        // ACK を送信（送信側に結果を通知）
        try
        {
            var sha256Bytes = FileChunker.ComputeSha256(state.SavePath);
            var ackMessage = FileChunker.CreateAckMessage(hashMatch, sha256Bytes);
            _connectionService.SendAsync(ackMessage).GetAwaiter().GetResult();
            Util.Logger.Log("ACK 送信完了");
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"ACK 送信エラー: {ex.Message}", Util.LogLevel.Warning);
        }

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
    }

    private void HandlePing()
    {
        try
        {
            var pong = FileChunker.CreatePongMessage();
            _connectionService.SendAsync(pong).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"Pong 送信エラー: {ex.Message}", Util.LogLevel.Warning);
        }
    }

    private void HandleResumeRequest(byte[] data)
    {
        var (transferId, lastChunkIndex) = FileChunker.ParseResumeRequest(data);
        Util.Logger.Log($"レジュームリクエスト受信: transferId={transferId}, lastChunk={lastChunkIndex}");

        // レジューム応答（現時点では非対応として拒否）
        try
        {
            var response = FileChunker.CreateResumeResponseMessage(transferId, false, lastChunkIndex);
            _connectionService.SendAsync(response).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"レジューム応答送信エラー: {ex.Message}", Util.LogLevel.Warning);
        }
    }

    private void HandleResumeResponse(byte[] data)
    {
        var (transferId, accepted, lastChunkIndex) = FileChunker.ParseResumeResponse(data);
        Util.Logger.Log($"レジューム応答受信: transferId={transferId}, accepted={accepted}, lastChunk={lastChunkIndex}");
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
    }
}
