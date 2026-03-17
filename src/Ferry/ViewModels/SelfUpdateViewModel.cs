using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ferry.Models;

namespace Ferry.ViewModels;

/// <summary>
/// アプリケーションの自動アップデートを管理する ViewModel。
/// Velopack を使用してアップデートのダウンロードと適用を行う。
/// </summary>
public sealed partial class SelfUpdateViewModel : ViewModelBase
{
    /// <summary>表示するデータ（VelopackUpdate / AlreadyUpToDate / SelfUpdateFailed）。</summary>
    [ObservableProperty]
    private object? _data;

    /// <summary>アップデートのダウンロード中かどうか。</summary>
    [ObservableProperty]
    private bool _isDownloading;

    /// <summary>ダウンロードの進捗率（0-100）。</summary>
    [ObservableProperty]
    private int _downloadProgress;

    private CancellationTokenSource? _cts;

    /// <summary>
    /// アップデートをダウンロードして適用する。ダウンロード完了後にアプリを再起動する。
    /// </summary>
    public void DownloadAndApplyUpdate(VelopackUpdate update)
    {
        if (IsDownloading)
            return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsDownloading = true;
        DownloadProgress = 0;

        Task.Run(async () =>
        {
            try
            {
                await update.DownloadAsync(
                    p => Dispatcher.UIThread.Post(() => DownloadProgress = p),
                    token);

                update.ApplyAndRestart();
            }
            catch (OperationCanceledException)
            {
                Dispatcher.UIThread.Post(() => IsDownloading = false);
            }
            catch (Exception e)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    IsDownloading = false;
                    Data = new SelfUpdateFailed(e);
                });
            }
        });
    }

    /// <summary>進行中のダウンロードをキャンセルする。</summary>
    public void CancelDownload()
    {
        _cts?.Cancel();
    }
}
