using System;

namespace Ferry.Infrastructure;

/// <summary>
/// 複数ペア同時接続対応 Stage 1: <see cref="ITransport.DataReceived"/> が運ぶ受信イベント引数。
///
/// 旧シグネチャ <c>EventHandler&lt;byte[]&gt;</c> は『どの peer から来たデータか』を運べず、
/// ConnectionService / TransferService が ConnectedPeer 単数プロパティで逆引きしていた。
/// 2 ペア同時受信で取り違える根因のため、受信元 peer を transport 起点で付帯させる。
///
/// 各 transport は 1:1 (1 transport = 1 peer) なのでコンストラクタ注入された <see cref="PeerId"/> を
/// 全 Invoke に付帯する。受信側はイベント引数を権威値として TransferItem.PeerId / _transferPeerId 索引に
/// 設定し、SendFlowAckAsync / SendRejectFireAndForget / フロー制御 Route 判定の返送先確定にも使う。
///
/// 値タプル <c>EventHandler&lt;(string, byte[])&gt;</c> ではなく EventArgs 派生にしたのは、
/// 将来 Route 等を載せる余地と AOT 安全性（リフレクション/シリアライズ誘惑回避）のため。
/// </summary>
public sealed class DataReceivedEventArgs : EventArgs
{
    /// <summary>受信元 peer の SessionId（32hex）。</summary>
    public string PeerId { get; }

    /// <summary>受信ペイロード（length-prefix 解除済みのアプリ層メッセージ）。</summary>
    public byte[] Data { get; }

    public DataReceivedEventArgs(string peerId, byte[] data)
    {
        PeerId = peerId ?? throw new ArgumentNullException(nameof(peerId));
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }
}
