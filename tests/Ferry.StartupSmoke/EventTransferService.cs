using Ferry.Models;
using Ferry.Services;

/// <summary>実UIスレッドで転送イベントの購読・反映だけを検証するfixture。</summary>
internal sealed class EventTransferService : ITransferService
{
    public event EventHandler<TransferItem>? ProgressChanged;
    public event EventHandler<TransferItem>? TransferError;
    public event EventHandler<TransferItem>? FileReceived { add { } remove { } }
    public event EventHandler<TransferItem>? ApprovalRequested { add { } remove { } }
    public void RaiseProgress(TransferItem item) => ProgressChanged?.Invoke(this, item);
    public void RaiseError(TransferItem item) => TransferError?.Invoke(this, item);
    public bool HasActiveTransfer => false;
    public IReadOnlyList<TransferItem> GetResumableTransfers() => [];
    public Task SendFileAsync(string filePath, string? relativePath = null, Guid? transferId = null, string peerId = "", CancellationToken ct = default, Guid? folderBatchId = null) => throw new NotSupportedException();
    public Task EndFolderBatchAsync(Guid batchId, string peerId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> ResumeTransferAsync(Guid transferId, CancellationToken ct = default) => throw new NotSupportedException();
    public void HandleReceivedData(byte[] data, string peerId = "") => throw new NotSupportedException();
    public void ApproveTransfer(string transferId) => throw new NotSupportedException();
    public void RejectTransfer(string transferId) => throw new NotSupportedException();
    public void CancelTransfer(string transferId) => throw new NotSupportedException();
    public bool PauseSendTransfer(string transferId) => throw new NotSupportedException();
    public void ResumeSendTransfer(string transferId) => throw new NotSupportedException();
    public void SyncRateLimits() => throw new NotSupportedException();
    public void Dispose() { }
}
