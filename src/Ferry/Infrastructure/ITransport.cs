using System;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Infrastructure;

/// <summary>
/// P2P データ転送のトランスポート抽象。
/// TCP 直接接続と WebSocket リレーの共通インターフェース。
/// </summary>
public interface ITransport : IDisposable
{
    /// <summary>接続が確立しているかどうか。</summary>
    bool IsConnected { get; }

    /// <summary>接続経路（Direct / Relay）。</summary>
    ConnectionRoute Route { get; }

    /// <summary>バイナリデータを受信したときに発火するイベント。
    /// 複数ペア同時接続対応 Stage 1: 受信元 peerId を <see cref="DataReceivedEventArgs"/> で付帯する。
    /// 各 transport は 1:1 (1 transport = 1 peer) なのでコンストラクタ注入された peerId を Invoke 時に付帯する。
    /// 受信側は引数の PeerId を権威値として TransferItem.PeerId / _transferPeerId 索引に設定し、
    /// 旧来の ConnectedPeer 単数プロパティ逆引きを撤去する根拠にする。</summary>
    event EventHandler<DataReceivedEventArgs>? DataReceived;

    /// <summary>接続が確立したときに発火するイベント。</summary>
    event EventHandler? ChannelOpened;

    /// <summary>接続が切断されたときに発火するイベント。</summary>
    event EventHandler? ChannelClosed;

    /// <summary>接続経路が確定したときに発火するイベント。</summary>
    event EventHandler<ConnectionRoute>? RouteChanged;

    /// <summary>バイナリデータを送信する。</summary>
    Task SendAsync(byte[] data, CancellationToken ct = default);

    /// <summary>
    /// バイナリデータを送信する（<see cref="ReadOnlyMemory{T}"/> 版）。
    /// P-1: 送信パスの alloc 削減のため、ArrayPool 借用バッファをコピーなしで渡せるオーバーロード。
    /// rere #B1-003: 以前はデフォルト実装が <c>ToArray()</c> で旧 API に委譲していたため、新規 transport が
    /// override を忘れると zero-copy 契約が無言で破れた（型エラーにならない）。これを防ぐため抽象メソッドにし、
    /// 各 transport に直接 Memory ベース API（<c>Stream.WriteAsync(ReadOnlyMemory)</c> /
    /// <c>WebSocket.SendAsync(ReadOnlyMemory)</c> / <c>UdpClient.SendAsync(ReadOnlyMemory)</c>）への実装を強制する。
    /// </summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>接続を閉じる。</summary>
    void Close();
}
