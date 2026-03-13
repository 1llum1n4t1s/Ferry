using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// ファイル転送サービス。チャンク分割・送受信・プログレス管理・レジュームを行う。
/// </summary>
public interface ITransferService
{
    /// <summary>転送の進捗が更新されたときに発火するイベント。</summary>
    event EventHandler<TransferItem>? ProgressChanged;

    /// <summary>ファイル受信が完了したときに発火するイベント。</summary>
    event EventHandler<TransferItem>? FileReceived;

    /// <summary>転送でエラーが発生したときに発火するイベント。</summary>
    event EventHandler<TransferItem>? TransferError;

    /// <summary>
    /// 指定したファイルをピアに送信する。
    /// </summary>
    /// <param name="filePath">送信するファイルのパス。</param>
    /// <param name="ct">キャンセルトークン。</param>
    Task SendFileAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// 中断された転送を再開する。
    /// </summary>
    /// <param name="transferId">再開する転送の ID。</param>
    /// <param name="ct">キャンセルトークン。</param>
    /// <returns>レジュームが成功した場合 true。</returns>
    Task<bool> ResumeTransferAsync(Guid transferId, CancellationToken ct = default);

    /// <summary>
    /// 受信データを処理する（ConnectionService の DataReceived から呼び出される）。
    /// </summary>
    /// <param name="data">受信したバイナリデータ。</param>
    void HandleReceivedData(byte[] data);

    /// <summary>
    /// レジューム可能な転送の一覧を取得する。
    /// </summary>
    IReadOnlyList<TransferItem> GetResumableTransfers();
}
