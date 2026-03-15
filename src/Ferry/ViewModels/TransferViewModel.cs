using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

    /// <summary>
    /// 転送アイテムの一覧。
    /// </summary>
    public ObservableCollection<TransferItem> Transfers { get; } = [];

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
    }

    /// <summary>
    /// ファイルパスの配列を受け取り、送信を開始する。
    /// 未接続の場合はオンデマンドで WebRTC 接続を確立してから転送する。
    /// </summary>
    [RelayCommand]
    private async Task SendFilesAsync(string[] filePaths)
    {
        Util.Logger.Log($"SendFilesAsync 開始: {filePaths.Length} ファイル, SelectedPeer={_connectionViewModel.SelectedPeer?.DisplayName ?? "null"}, State={_connectionService.State}");

        if (filePaths.Length == 0 || _connectionViewModel.SelectedPeer == null)
        {
            Util.Logger.Log($"送信スキップ: filePaths={filePaths.Length}, peer={_connectionViewModel.SelectedPeer?.DisplayName ?? "null"}");
            return;
        }

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

        foreach (var filePath in filePaths)
        {
            if (!File.Exists(filePath))
                continue;

            var fileInfo = new FileInfo(filePath);
            var item = new TransferItem
            {
                FileName = fileInfo.Name,
                FileSize = fileInfo.Length,
                Direction = TransferDirection.Send,
                State = TransferState.Pending,
            };
            Transfers.Add(item);

            IsTransferring = true;
            try
            {
                item.State = TransferState.InProgress;
                await _transferService.SendFileAsync(filePath);
                item.State = TransferState.Completed;
                item.TransferredBytes = item.FileSize;
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"ファイル送信エラー ({filePath}): {ex.Message}", Util.LogLevel.Error);
                item.State = TransferState.Error;
                item.ErrorMessage = ex.Message;
            }
        }

        IsTransferring = Transfers.Any(t => t.State == TransferState.InProgress);
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
            }
            else
            {
                item.State = TransferState.Error;
                item.ErrorMessage = "レジュームに失敗しました";
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

    private void OnProgressChanged(object? sender, TransferItem e)
    {
        // TransferId で照合（同名ファイルが複数ある場合に誤動作を防ぐ）
        var item = Transfers.FirstOrDefault(t => t.TransferId == e.TransferId && t.State == TransferState.InProgress);
        if (item is not null)
        {
            item.TransferredBytes = e.TransferredBytes;
        }
    }

    private void OnFileReceived(object? sender, TransferItem e)
    {
        Transfers.Add(e);
    }

    private void OnTransferError(object? sender, TransferItem e)
    {
        var item = Transfers.FirstOrDefault(t => t.TransferId == e.TransferId && t.State == TransferState.InProgress);
        if (item is not null)
        {
            item.State = TransferState.Error;
            item.ErrorMessage = e.ErrorMessage;
        }
    }

    public void Dispose()
    {
        _transferService.ProgressChanged -= OnProgressChanged;
        _transferService.FileReceived -= OnFileReceived;
        _transferService.TransferError -= OnTransferError;
    }
}
