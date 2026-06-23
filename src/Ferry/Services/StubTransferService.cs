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
    public event EventHandler<TransferItem>? ApprovalRequested;

    public Task SendFileAsync(string filePath, string? relativePath = null, Guid? transferId = null, string peerId = "", CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> ResumeTransferAsync(Guid transferId, CancellationToken ct = default) => Task.FromResult(false);
    public void HandleReceivedData(byte[] data, string peerId = "") { }
    public IReadOnlyList<TransferItem> GetResumableTransfers() => [];
    public bool HasActiveTransfer => false;
    public void ApproveTransfer(string transferId) { }
    public void RejectTransfer(string transferId) { }
    public void CancelTransfer(string transferId) { }
    public bool PauseSendTransfer(string transferId) => false;
    public void ResumeSendTransfer(string transferId) { }
    public void SyncRateLimits() { }
    public void Dispose() { } // rere #C2-005: IDisposable 必須化
}
