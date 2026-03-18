using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ferry.Models;
using Ferry.Services;

namespace Ferry.ViewModels;

/// <summary>
/// 転送パネルの ViewModel。
/// ファイルのドラッグ＆ドロップ、転送リスト、進捗管理を提供する。
/// </summary>
public sealed partial class TransferViewModel : ViewModelBase, IDisposable
{
    private readonly IConnectionService _connectionService;
    private readonly ITransferService _transferService;
    private readonly ConnectionViewModel _connectionViewModel;

    [ObservableProperty]
    private bool _isDragOver;

    [ObservableProperty]
    private bool _isTransferring;

    /// <summary>転送アイテムの一覧があるか。</summary>
    [ObservableProperty]
    private bool _hasTransfers;

    /// <summary>承認待ちの受信アイテムがあるか（サイドメニュー下部パネル表示用）。</summary>
    [ObservableProperty]
    private bool _hasPendingApproval;

    /// <summary>
    /// 転送アイテムの一覧。
    /// </summary>
    public ObservableCollection<TransferItem> Transfers { get; } = [];

    /// <summary>
    /// 承認待ちの受信アイテム一覧（サイドバー下部パネルに表示）。
    /// </summary>
    public ObservableCollection<TransferItem> PendingApprovals { get; } = [];

    public TransferViewModel(
        IConnectionService connectionService,
        ITransferService transferService,
        ConnectionViewModel connectionViewModel)
    {
        _connectionService = connectionService;
        _transferService = transferService;
        _connectionViewModel = connectionViewModel;

        _transferService.ProgressChanged += OnProgressChanged;
        _transferService.FileReceived += OnFileReceived;
        _transferService.TransferError += OnTransferError;
        _transferService.ApprovalRequested += OnApprovalRequested;

        Transfers.CollectionChanged += (_, _) => HasTransfers = Transfers.Count > 0;
    }

    /// <summary>
    /// ファイル選択ダイアログを開き、選択されたファイルを送信する。
    /// </summary>
    [RelayCommand]
    private async Task BrowseAndSendFilesAsync()
    {
        if ((App.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow is not { } mainWindow)
            return;

        var storageProvider = mainWindow.StorageProvider;
        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "転送するファイルを選択",
        });

        if (files.Count == 0) return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => p != null)
            .ToArray();

        if (paths.Length > 0)
            await SendFilesAsync(paths!);
    }

    /// <summary>
    /// ファイル/フォルダパスの配列を受け取り、送信を開始する。
    /// フォルダの場合は中のファイルを再帰的に列挙し、フォルダ構造を保持して送信する。
    /// 未接続の場合はオンデマンドで接続を確立してから転送する。
    /// </summary>
    [RelayCommand]
    private async Task SendFilesAsync(string[] filePaths)
    {
        Util.Logger.Log($"SendFilesAsync 開始: {filePaths.Length} パス, SelectedPeer={_connectionViewModel.SelectedPeer?.DisplayName ?? "null"}, State={_connectionService.State}");

        if (filePaths.Length == 0 || _connectionViewModel.SelectedPeer == null)
        {
            Util.Logger.Log($"送信スキップ: filePaths={filePaths.Length}, peer={_connectionViewModel.SelectedPeer?.DisplayName ?? "null"}");
            return;
        }

        var peerName = _connectionViewModel.SelectedPeer.DisplayName;

        // パスをファイルに展開（フォルダは再帰列挙、相対パス付き）
        var entries = ExpandPaths(filePaths);
        if (entries.Count == 0)
        {
            Util.Logger.Log("送信対象のファイルがありません", Util.LogLevel.Warning);
            return;
        }

        Util.Logger.Log($"送信対象: {entries.Count} ファイル");

        // 未接続ならオンデマンド接続
        if (_connectionService.State != PeerState.Connected)
        {
            Util.Logger.Log("未接続のためオンデマンド接続を開始…");
            try
            {
                await _connectionViewModel.ConnectToSelectedPeerAsync();
                Util.Logger.Log($"オンデマンド接続完了: State={_connectionService.State}");
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"転送前の接続に失敗: {ex.Message}", Util.LogLevel.Error);
                return;
            }
        }

        if (_connectionService.State != PeerState.Connected)
        {
            Util.Logger.Log($"接続状態が Connected ではないため転送中止: State={_connectionService.State}", Util.LogLevel.Warning);
            return;
        }

        foreach (var (absolutePath, relativePath) in entries)
        {
            var fileInfo = new FileInfo(absolutePath);
            var displayName = relativePath ?? fileInfo.Name;
            var item = new TransferItem
            {
                FileName = displayName,
                FileSize = fileInfo.Length,
                Direction = TransferDirection.Send,
                State = TransferState.InProgress,
                PeerName = peerName,
            };
            Transfers.Add(item);

            IsTransferring = true;
            try
            {
                await _transferService.SendFileAsync(absolutePath, relativePath);
                item.State = TransferState.Completed;
                item.TransferredBytes = item.FileSize;
                item.CompletedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"ファイル送信エラー ({displayName}): {ex.Message}", Util.LogLevel.Error);
                item.State = TransferState.Error;
                item.ErrorMessage = ex.Message;
            }
        }

        IsTransferring = Transfers.Any(t => t.State == TransferState.InProgress);
    }

    /// <summary>
    /// ファイル/フォルダパスの配列を、(絶対パス, 相対パス) のリストに展開する。
    /// フォルダの場合はフォルダ名をルートとした相対パスを生成する。
    /// 単独ファイルの場合は relativePath = null。
    /// </summary>
    private static List<(string AbsolutePath, string? RelativePath)> ExpandPaths(string[] paths)
    {
        var result = new List<(string, string?)>();

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                result.Add((path, null));
            }
            else if (Directory.Exists(path))
            {
                var dirInfo = new DirectoryInfo(path);
                var rootName = dirInfo.Name;
                foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    // "フォルダ名/サブ/ファイル.txt" の形式
                    var relative = Path.Combine(rootName, Path.GetRelativePath(path, file.FullName));
                    // パス区切りを / に統一（クロスプラットフォーム）
                    relative = relative.Replace('\\', '/');
                    result.Add((file.FullName, relative));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 中断された転送を再開する。
    /// </summary>
    [RelayCommand]
    private async Task ResumeTransferAsync(Guid transferId)
    {
        var item = Transfers.FirstOrDefault(t => t.TransferId == transferId && t.State == TransferState.Suspended);
        if (item is null) return;

        item.State = TransferState.InProgress;
        IsTransferring = true;

        try
        {
            var success = await _transferService.ResumeTransferAsync(transferId);
            if (success)
            {
                item.State = TransferState.Completed;
                item.TransferredBytes = item.FileSize;
                item.CompletedAt = DateTime.UtcNow;
            }
            else
            {
                item.State = TransferState.Error;
                item.ErrorMessage = App.Text("Transfer.ResumeFailed");
            }
        }
        catch (Exception ex)
        {
            Util.Logger.Log($"転送レジュームエラー ({transferId}): {ex.Message}", Util.LogLevel.Error);
            item.State = TransferState.Error;
            item.ErrorMessage = ex.Message;
        }

        IsTransferring = Transfers.Any(t => t.State == TransferState.InProgress);
    }

    /// <summary>
    /// 転送履歴をクリアする。
    /// </summary>
    [RelayCommand]
    private void ClearHistory()
    {
        // 完了・エラー・キャンセル済みのアイテムのみ削除
        var completed = Transfers
            .Where(t => t.State is TransferState.Completed or TransferState.Error or TransferState.Cancelled or TransferState.Suspended)
            .ToList();

        foreach (var item in completed)
        {
            Transfers.Remove(item);
        }


    }

    /// <summary>
    /// 進捗更新イベント。バックグラウンドスレッドから呼ばれるため UI スレッドにディスパッチ。
    /// 送信: Direction + InProgress で照合（TransferId はサービス内部で別に生成されるため）。
    /// 受信: 一致するアイテムがなければ追加。
    /// </summary>
    private void OnProgressChanged(object? sender, TransferItem e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (e.Direction == TransferDirection.Send)
            {
                // 送信中のアイテムを更新（逐次送信なので InProgress は 1 つだけ）
                var item = Transfers.FirstOrDefault(t =>
                    t.Direction == TransferDirection.Send && t.State == TransferState.InProgress);
                if (item != null)
                {
                    item.TransferredBytes = e.TransferredBytes;
                }
            }
            else
            {
                // 受信中: 既存アイテムを探す、なければ追加
                var item = Transfers.FirstOrDefault(t =>
                    t.Direction == TransferDirection.Receive && t.State == TransferState.InProgress);
                if (item == null)
                {
                    var peerName = _connectionViewModel.SelectedPeer?.DisplayName ?? string.Empty;
                    item = new TransferItem
                    {
                        TransferId = e.TransferId,
                        FileName = e.FileName,
                        FileSize = e.FileSize,
                        TotalChunks = e.TotalChunks,
                        Direction = TransferDirection.Receive,
                        State = TransferState.InProgress,
                        PeerName = peerName,
                    };
                    Transfers.Add(item);
                    IsTransferring = true;
                    // 通知はサイドバー下部パネルで表示
                }
                item.TransferredBytes = e.TransferredBytes;
            }
        });
    }

    /// <summary>
    /// ファイル受信完了イベント。
    /// </summary>
    private void OnFileReceived(object? sender, TransferItem e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 進捗表示中の受信アイテムを探す
            var item = Transfers.FirstOrDefault(t =>
                t.TransferId == e.TransferId && t.Direction == TransferDirection.Receive);

            if (item == null)
            {
                // 進捗表示なしで完了した場合（小さいファイル等）
                item = Transfers.FirstOrDefault(t =>
                    t.Direction == TransferDirection.Receive && t.State == TransferState.InProgress);
            }

            if (item != null)
            {
                item.State = TransferState.Completed;
                item.TransferredBytes = e.FileSize;
                item.FileName = e.FileName;
                item.CompletedAt = DateTime.UtcNow;
            }
            else
            {
                // どこにも見つからない → 新規追加
                e.CompletedAt = DateTime.UtcNow;
                e.PeerName = _connectionViewModel.SelectedPeer?.DisplayName ?? string.Empty;
                Transfers.Add(e);
            }

            IsTransferring = Transfers.Any(t => t.State == TransferState.InProgress);
        });
    }

    /// <summary>
    /// 転送エラーイベント。
    /// </summary>
    private void OnTransferError(object? sender, TransferItem e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = Transfers.FirstOrDefault(t =>
                t.Direction == e.Direction && t.State == TransferState.InProgress);
            if (item != null)
            {
                item.State = TransferState.Error;
                item.ErrorMessage = e.ErrorMessage;
            }
            else
            {
                e.State = TransferState.Error;
                Transfers.Add(e);
            }

            IsTransferring = Transfers.Any(t => t.State == TransferState.InProgress);
        });
    }

    /// <summary>
    /// ファイル受信承認要求イベント。承認待ちアイテムを UI に追加する。
    /// </summary>
    private void OnApprovalRequested(object? sender, TransferItem e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            e.PeerName = _connectionViewModel.SelectedPeer?.DisplayName ?? string.Empty;
            PendingApprovals.Add(e);
            HasPendingApproval = PendingApprovals.Count > 0;
        });
    }

    /// <summary>
    /// 受信を承認する。
    /// </summary>
    [RelayCommand]
    private void ApproveTransfer(Guid transferId)
    {
        var item = PendingApprovals.FirstOrDefault(t => t.TransferId == transferId);
        if (item == null) return;

        PendingApprovals.Remove(item);
        HasPendingApproval = PendingApprovals.Count > 0;

        item.State = TransferState.InProgress;
        Transfers.Add(item);
        _transferService.ApproveTransfer(transferId.ToString());
        IsTransferring = true;
    }

    /// <summary>
    /// 受信を拒否する。
    /// </summary>
    [RelayCommand]
    private void RejectTransfer(Guid transferId)
    {
        var item = PendingApprovals.FirstOrDefault(t => t.TransferId == transferId);
        if (item == null) return;

        PendingApprovals.Remove(item);
        HasPendingApproval = PendingApprovals.Count > 0;

        _transferService.RejectTransfer(transferId.ToString());
        item.State = TransferState.Cancelled;
        item.ErrorMessage = App.Text("Transfer.Rejected");
        Transfers.Add(item);
    }

    public void Dispose()
    {
        _transferService.ProgressChanged -= OnProgressChanged;
        _transferService.FileReceived -= OnFileReceived;
        _transferService.TransferError -= OnTransferError;
        _transferService.ApprovalRequested -= OnApprovalRequested;
    }
}
