using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Infrastructure;

/// <summary>
/// マルチストリーム転送 PoC (Relay 経路): 複数の <see cref="ITransport"/>（実体は WebSocketRelayTransport）を
/// 1 本の論理 transport として束ね、<see cref="ITransport"/> 契約を満たす委譲ラッパ。
///
/// 目的: 単一 WebSocket は <c>_sendLock</c> でフレーム送信が直列化され、かつ 1 接続 = 1 Cloudflare DO 中継
/// なので、1 ファイルのチャンク送信が単一フロー/単一 DO で律速される。これを複数の独立 WS（各々
/// 別 pairId サブルーム <c>pairId#s{i}</c> = 別 DO ルーム）へ <b>チャンクだけ</b> round-robin 分散すること
/// で、送信側の直列化と DO 中継の頭打ちを解消し実効スループット向上を狙う（PoC で実測判定する）。
///
/// 設計の肝:
/// <list type="bullet">
/// <item>受信側 <c>TransferService.HandleFileChunk</c> は chunkIndex×ChunkSize の Seek 書き込み +
/// ReceivedChunkSet ビットマップで順不同到着を既に完全許容済みなので、round-robin で順序が乱れても
/// 再構成は無改修で成立する。</item>
/// <item>本ラッパは <see cref="SecureChannel"/> の <b>下</b>（transport 層）に位置する。封筒化済み/平文どちらの
/// 完成メッセージも素のバイト列として分散するだけなので、暗号状態機械には一切触れない（PoC は平文
/// フォールバック経路に限定するが、構造的にも暗号と直交する）。</item>
/// <item><b>先頭バイト振り分け</b>: <see cref="TransferProtocol.FileChunk"/>(0x02) のみ複数 stream に分散し、
/// それ以外（FileMeta/FileApprove/FileHash/FileReject/FileAck/FileFlowAck/Ping/Pong/Secure ハンドシェイク等）は
/// 必ず <c>stream[0]</c> 固定で送る。これで別 WS 間のレイテンシ差による「FileHash が最後のチャンクを追い越す」
/// 等のプロトコル順序事故（受信状態未確立での検証先行）を防ぐ。チャンクは順不同 OK なので分散して安全。</item>
/// </list>
///
/// ライフサイクル: 渡される inner transport は <b>接続確立済み</b>（呼び出し側が各 ConnectAsync 完了後に束ねる）
/// である前提。本ラッパは構築時点で <see cref="IsConnected"/>=true。<see cref="ChannelClosed"/> は「生存本数が 0 に
/// 到達したときだけ 1 回」発火する（1 本の一過性切断で転送全体を巻き込まない）。撤退は容易で、
/// RelayStreamCount=1（既定）なら本クラスは生成されず単一 WebSocketRelayTransport に完全後方互換で戻る。
/// </summary>
public sealed class MultiStreamRelayTransport : ITransport
{
    private readonly ITransport[] _streams;
    private readonly EventHandler<DataReceivedEventArgs>[] _dataHandlers;
    private readonly EventHandler[] _closedHandlers;

    /// <summary>round-robin の現在位置（<see cref="Interlocked.Increment(ref int)"/> で進める）。初期 -1 で最初の送信が index 0。</summary>
    private int _sendIndex = -1;

    /// <summary>生存している inner stream の本数。inner ChannelClosed ごとにデクリメントし、0 で集約 ChannelClosed を発火。</summary>
    private int _liveCount;

    /// <summary>集約 ChannelClosed の二重発火を防ぐ Interlocked ガード（0=未発火 / 1=発火済み）。</summary>
    private int _closedFired;

    private volatile bool _closed;

    public bool IsConnected { get; private set; }

    /// <summary>マルチストリームは Relay 経路のみで使うため固定で Relay を返す。</summary>
    public ConnectionRoute Route => ConnectionRoute.Relay;

    /// <summary>複数ペア同時接続対応 Stage 1: 1 transport = 1 peer の対応。inner と同じ peerId を委譲保持する。</summary>
    public string PeerId { get; }

    /// <summary>束ねている stream 本数（計測・テスト・ログ用）。</summary>
    public int StreamCount => _streams.Length;

    public event EventHandler<DataReceivedEventArgs>? DataReceived;
    public event EventHandler? ChannelClosed;

    // ChannelOpened / RouteChanged は ITransport 契約上は持つが、本ラッパは「接続確立済みの inner を束ねた直後に
    // ConnectionService へ attach される」ため、構築時点でこれらを発火しても購読者がいない。経路情報は Route
    // プロパティ（常時 Relay）として公開し、AttachTransportEvents が attach 時にプロパティを直読みして同期するので
    // イベント発火は不要。未発火による CS0067 を明示的に抑止する（契約は満たしつつ no-op であることを宣言）。
#pragma warning disable CS0067
    public event EventHandler? ChannelOpened;
    public event EventHandler<ConnectionRoute>? RouteChanged;
#pragma warning restore CS0067

    /// <summary>
    /// 接続確立済みの inner transport 群を束ねる。少なくとも 1 本必須。
    /// </summary>
    /// <param name="streams">接続確立済みの <see cref="ITransport"/> 群（通常は WebSocketRelayTransport×N）。</param>
    /// <param name="peerId">受信元 peerId(SessionId)。<see cref="DataReceivedEventArgs"/> 付帯用。</param>
    public MultiStreamRelayTransport(IReadOnlyList<ITransport> streams, string peerId)
    {
        if (streams == null || streams.Count == 0)
            throw new ArgumentException("少なくとも 1 本の stream が必要です", nameof(streams));

        _streams = streams.ToArray();
        PeerId = peerId ?? string.Empty;
        _liveCount = _streams.Length;
        _dataHandlers = new EventHandler<DataReceivedEventArgs>[_streams.Length];
        _closedHandlers = new EventHandler[_streams.Length];

        for (var i = 0; i < _streams.Length; i++)
        {
            var s = _streams[i];
            // クロージャは inner transport 1 本ごとに別インスタンスで保持し、Dispose で確実に解除する。
            EventHandler<DataReceivedEventArgs> dh = (_, e) => OnInnerDataReceived(e);
            EventHandler ch = (_, _) => OnInnerChannelClosed();
            _dataHandlers[i] = dh;
            _closedHandlers[i] = ch;
            s.DataReceived += dh;
            s.ChannelClosed += ch;
        }

        IsConnected = true;
    }

    /// <summary>inner のどれか 1 本が受信したメッセージを、単一の <see cref="DataReceived"/> へ集約発火する。
    /// peerId は本ラッパの値で正規化する（inner も同値だが束ね側を権威にする）。</summary>
    private void OnInnerDataReceived(DataReceivedEventArgs e)
    {
        DataReceived?.Invoke(this, new DataReceivedEventArgs(PeerId, e.Data));
    }

    /// <summary>inner 1 本が閉じたら生存本数を減らし、全本が閉じたときだけ集約 <see cref="ChannelClosed"/> を 1 回発火する。</summary>
    private void OnInnerChannelClosed()
    {
        if (Interlocked.Decrement(ref _liveCount) > 0)
            return; // まだ生存 stream がある → 転送全体は継続（1 本の一過性切断で巻き込まない）

        IsConnected = false;
        if (Interlocked.Exchange(ref _closedFired, 1) == 0)
            ChannelClosed?.Invoke(this, EventArgs.Empty);
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
        => SendAsync(data.AsMemory(), ct);

    /// <summary>
    /// 先頭バイトで振り分けて 1 本の inner stream へ委譲送信する。
    /// FileChunk(0x02) のみ round-robin 分散、それ以外（制御・ハンドシェイク）は stream[0] 固定。
    /// inner の SendAsync は各々 <c>_sendLock</c> を持つので、N 本なら最大 N 並列でワイヤ送信できる。
    /// </summary>
    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_closed || !IsConnected)
            throw new InvalidOperationException("マルチストリームリレー接続されていません");

        var stream = SelectStream(data.Span);
        return stream.SendAsync(data, ct);
    }

    /// <summary>送信先 stream を先頭バイトで決める。チャンクのみ分散、他は stream[0]。空メッセージも stream[0]。</summary>
    private ITransport SelectStream(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || data[0] != TransferProtocol.FileChunk)
            return _streams[0];

        // FileChunk: round-robin。負の剰余を避けるため uint で剰余を取る。
        var idx = (int)((uint)Interlocked.Increment(ref _sendIndex) % (uint)_streams.Length);
        return _streams[idx];
    }

    public void Close()
    {
        _closed = true;
        IsConnected = false;
        foreach (var s in _streams)
        {
            try { s.Close(); }
            catch (Exception ex) { Util.Logger.Log($"マルチストリーム inner Close 失敗（無視）: {ex.Message}", Util.LogLevel.Debug); }
        }
    }

    public void Dispose()
    {
        _closed = true;
        var wasConnected = IsConnected;
        IsConnected = false;

        for (var i = 0; i < _streams.Length; i++)
        {
            try { _streams[i].DataReceived -= _dataHandlers[i]; } catch { /* ignore */ }
            try { _streams[i].ChannelClosed -= _closedHandlers[i]; } catch { /* ignore */ }
            try { _streams[i].Dispose(); }
            catch (Exception ex) { Util.Logger.Log($"マルチストリーム inner Dispose 失敗（無視）: {ex.Message}", Util.LogLevel.Debug); }
        }

        // 接続中に Dispose されたら（明示 Close 経由でない経路）集約 ChannelClosed を 1 回だけ補償発火する。
        if (wasConnected && Interlocked.Exchange(ref _closedFired, 1) == 0)
            ChannelClosed?.Invoke(this, EventArgs.Empty);
    }
}
