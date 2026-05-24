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

    /// <summary>バイナリデータを受信したときに発火するイベント。</summary>
    event EventHandler<byte[]>? DataReceived;

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
    /// P-1: 送信パスの alloc 削減のため、ArrayPool 借用バッファをコピーなしで渡せるオーバーロードを追加。
    /// デフォルト実装は <c>ToArray()</c> で旧 API に委譲するが、各 transport で override して
    /// 直接 Memory ベース API（<c>Stream.WriteAsync(ReadOnlyMemory)</c> / <c>WebSocket.SendAsync(ReadOnlyMemory)</c>）に流すこと。
    /// </summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        => SendAsync(data.ToArray(), ct);

    /// <summary>接続を閉じる。</summary>
    void Close();
}
