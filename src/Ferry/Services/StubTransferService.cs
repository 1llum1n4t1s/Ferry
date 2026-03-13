using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// 転送サービスのスタブ実装。
/// </summary>
#pragma warning disable CS0067 // スタブ実装のため未使用イベントを許容
public sealed class StubTransferService : ITransferService
{
    public event EventHandler<TransferItem>? ProgressChanged;
    public event EventHandler<TransferItem>? FileReceived;
    public event EventHandler<TransferItem>? TransferError;

    public Task SendFileAsync(string filePath, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> ResumeTransferAsync(Guid transferId, CancellationToken ct = default) => Task.FromResult(false);
    public void HandleReceivedData(byte[] data) { }
    public IReadOnlyList<TransferItem> GetResumableTransfers() => [];
}
