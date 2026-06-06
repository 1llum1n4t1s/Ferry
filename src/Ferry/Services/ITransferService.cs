using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Services;

/// <summary>
/// ファイル転送サービス。チャンク分割・送受信・プログレス管理・レジュームを行う。
/// rere レビュー #C2-005: DataReceived / ConnectionLost イベント購読を持つので
/// IDisposable を必須化する。本番は Singleton で leak しないが、テスト / 将来の
/// マルチピア化対応で確実に unsubscribe できるようにしておく。
/// </summary>
public interface ITransferService : IDisposable
{
    /// <summary>転送の進捗が更新されたときに発火するイベント。</summary>
    event EventHandler<TransferItem>? ProgressChanged;

    /// <summary>ファイル受信が完了したときに発火するイベント。</summary>
    event EventHandler<TransferItem>? FileReceived;

    /// <summary>転送でエラーが発生したときに発火するイベント。</summary>
    event EventHandler<TransferItem>? TransferError;

    /// <summary>ファイル受信の承認が要求されたときに発火するイベント。UI で承認/拒否を表示する。</summary>
    event EventHandler<TransferItem>? ApprovalRequested;

    /// <summary>
    /// 指定したファイルをピアに送信する。
    /// </summary>
    /// <param name="filePath">送信するファイルのパス。</param>
    /// <param name="relativePath">フォルダ送信時の相対パス（フォルダ名/サブフォルダ/ファイル名）。null で単独ファイル扱い。</param>
    /// <param name="transferId">UI 側で生成済みの転送 ID。指定すると進捗・キャンセル・一時停止を UI 行と TransferId で正確に対応付けできる。null なら内部生成。</param>
    /// <param name="ct">キャンセルトークン。</param>
    Task SendFileAsync(string filePath, string? relativePath = null, Guid? transferId = null, CancellationToken ct = default);

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

    /// <summary>
    /// 送信中・受信中・承認待ちのいずれかの転送が進行中なら true。
    /// アプリ終了/自動更新の再起動を転送中だけ抑止する判定に使う。
    /// </summary>
    bool HasActiveTransfer { get; }

    /// <summary>
    /// 受信承認待ちの転送を承認する。
    /// </summary>
    void ApproveTransfer(string transferId);

    /// <summary>
    /// 受信承認待ちの転送を拒否する。
    /// </summary>
    void RejectTransfer(string transferId);

    /// <summary>
    /// 進行中の転送をキャンセルする。送信・受信のどちら側からでも呼べ、相手にも通知して両側を停止する。
    /// </summary>
    /// <param name="transferId">キャンセルする転送の ID。</param>
    void CancelTransfer(string transferId);

    /// <summary>
    /// 送信中の転送を一時停止する。チャンク送信ループを停止させ、接続は維持する。
    /// 接続待ち / リトライ backoff 中など service 側に active transfer がまだ無い場合は受理されず false を返す。
    /// VM 側は受理時のみ UI 行を Paused に遷移させる（旧実装は service の no-op に気付かず、
    /// 接続待ち中に pause しても UI だけ Paused 表示で送信は続行という不整合があった）。
    /// </summary>
    /// <param name="transferId">一時停止する送信転送の ID。</param>
    /// <returns>service 側で実際に pause が記録されたら true、受理できなかったら false。</returns>
    bool PauseSendTransfer(string transferId);

    /// <summary>
    /// 一時停止中の送信転送を再開する。
    /// </summary>
    /// <param name="transferId">再開する送信転送の ID。</param>
    void ResumeSendTransfer(string transferId);
}
