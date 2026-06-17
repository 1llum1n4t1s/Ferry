using System;

namespace Ferry.Services;

/// <summary>
/// 接続要求に対して相手が一切応答しなかった（offer に対する answer が待機時間内に来なかった）ことを表す例外。
/// 原因は「相手がオフライン / アプリ未起動 / 到達不可」であり、すぐ再接続を繰り返しても
/// 毎回 answer 待ちタイムアウト（20s）を空打ちするだけで成功しない。
///
/// このため送信側のリトライループ（<see cref="ViewModels.TransferViewModel"/>）はこの例外を
/// リトライ対象から除外し、明確なオフラインメッセージを出して即終了する。相手が戻ったら
/// ユーザーの手動「再送」で送り直せる。転送中の一過性切断（相手は生存・接続は確立済み）とは区別する。
/// </summary>
public sealed class PeerUnreachableException : Exception
{
    public PeerUnreachableException(string message) : base(message) { }
}
