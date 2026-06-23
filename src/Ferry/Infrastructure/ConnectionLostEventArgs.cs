using System;

namespace Ferry.Infrastructure;

/// <summary>
/// 複数ペア同時接続対応 Stage 5: <see cref="Ferry.Services.IConnectionService.ConnectionLost"/> が運ぶ切断イベント引数。
///
/// 旧シグネチャ <c>EventHandler</c> は「どの peer が切れたか」を運べず、TransferService.OnConnectionLost が
/// 全 transfers を一括 cleanup していた。Stage 4 で並列接続が解禁された後は「peer A の切断で peer B の
/// 進行中転送まで Cancelled に巻き込む」回帰になるため、peerId 付帯化して受信側が当該 peer の transfer
/// のみに絞り込めるようにする。
///
/// peerId が空文字のときは「全 peer 切断（DisconnectAsync(no peerId) 等）」を表す（既存後方互換）。
/// </summary>
public sealed class ConnectionLostEventArgs : EventArgs
{
    /// <summary>切断された peer の SessionId（32hex）。空文字は『全 peer 切断 / 不明』を表す。</summary>
    public string PeerId { get; }

    public ConnectionLostEventArgs(string peerId)
    {
        PeerId = peerId ?? string.Empty;
    }

    /// <summary>peerId 不明 / 全 peer 切断を表す共有インスタンス（割当回避）。</summary>
    public static readonly ConnectionLostEventArgs All = new(string.Empty);
}
