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
        ITransferService transferService)
    {
        _connectionService = connectionService;
        _transferService = transferService;

        _transferService.ProgressChanged += OnProgressChanged;
        _transferService.FileReceived += OnFileReceived;
        _transferService.TransferError += OnTransferError;
    }

    /// <summary>
    /// ファイルパスの配列を受け取り、送信を開始する。
    /// </summary>
    [RelayCommand]
    private async Task SendFilesAsync(string[] filePaths)
    {
        if (_connectionService.State != PeerState.Connected || filePaths.Length == 0)
            return;

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
