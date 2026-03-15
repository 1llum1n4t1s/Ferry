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

    /// <summary>接続を閉じる。</summary>
    void Close();
}
