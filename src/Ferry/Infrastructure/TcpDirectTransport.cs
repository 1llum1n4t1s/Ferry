using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Ferry.Models;

namespace Ferry.Infrastructure;

/// <summary>
/// TCP ソケットによる LAN 内直接接続トランスポート。
/// ICE/DTLS/SCTP を使わず、単純な TCP + 長さプレフィックスフレーミングで通信する。
/// </summary>
public sealed class TcpDirectTransport : ITransport
{
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _receiveCts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected { get; private set; }
    public ConnectionRoute Route => ConnectionRoute.Direct;

    /// <summary>複数ペア同時接続対応 Stage 1: 1 transport = 1 peer の対応関係を保持する。
    /// <see cref="DataReceivedEventArgs.PeerId"/> に常時付帯される。
    /// 生成側 (ConnectionService) が接続文脈の peerId(SessionId) を init で渡す。
    /// 未設定（空文字）の場合は『peer 識別不能』として後段が逆引きにフォールバックする
    /// （Stage 2 までの過渡期のセーフティ。Stage 3 完了後は全生成サイトが必ず設定する）。</summary>
    public string PeerId { get; init; } = string.Empty;

    public event EventHandler<DataReceivedEventArgs>? DataReceived;
    public event EventHandler? ChannelOpened;
    public event EventHandler? ChannelClosed;
    public event EventHandler<ConnectionRoute>? RouteChanged;

    /// <summary>
    /// TCP リスナーを起動し、待ち受けポートを返す。
    /// Answer 側（着信側）が使用する。
    /// </summary>
    /// <returns>待ち受けポート番号。</returns>
    public int StartListener()
    {
        // OS にポートを自動割り当てさせる（ポート 0 指定）
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();

        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Util.Logger.Log($"TCP リスナー起動: 0.0.0.0:{port}");
        return port;
    }

    /// <summary>
    /// リスナーで接続を受け入れる（Answer 側）。
    /// </summary>
    public async Task AcceptAsync(CancellationToken ct = default)
    {
        if (_listener == null)
            throw new InvalidOperationException("リスナーが起動していません");

        Util.Logger.Log("TCP 接続待機中…");
        _client = await _listener.AcceptTcpClientAsync(ct);
        ConfigureTcpClient(_client);

        _stream = _client.GetStream();
        IsConnected = true;

        var remoteEp = _client.Client.RemoteEndPoint as IPEndPoint;
        Util.Logger.Log($"TCP 接続受入: {remoteEp}");

        // リスナーを停止（1対1接続なので追加の接続は不要）
        _listener.Stop();

        StartReceiveLoop();
        ChannelOpened?.Invoke(this, EventArgs.Empty);
        RouteChanged?.Invoke(this, ConnectionRoute.Direct);
    }

    /// <summary>
    /// 相手の IP:port に TCP 接続する（Offer 側 / 接続側）。
    /// 複数 IP を試行し、最初に接続できたものを使う。
    /// </summary>
    /// <param name="ips">相手のローカル IP アドレス群。</param>
    /// <param name="port">相手の待ち受けポート。</param>
    /// <param name="ct">キャンセルトークン。</param>
    public async Task ConnectAsync(string[] ips, int port, CancellationToken ct = default)
    {
        Util.Logger.Log($"TCP 接続試行: {string.Join(", ", ips)}:{port}");

        Exception? lastException = null;

        foreach (var ip in ips)
        {
            try
            {
                var client = new TcpClient();
                ConfigureTcpClient(client);

                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(3));

                await client.ConnectAsync(IPAddress.Parse(ip), port, connectCts.Token);

                _client = client;
                _stream = _client.GetStream();
                IsConnected = true;

                Util.Logger.Log($"TCP 接続成功: {Util.Logger.MaskIp(ip)}:{port}");
                StartReceiveLoop();
                ChannelOpened?.Invoke(this, EventArgs.Empty);
                RouteChanged?.Invoke(this, ConnectionRoute.Direct);
                return;
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"TCP 接続失敗 ({Util.Logger.MaskIp(ip)}:{port}): {ex.Message}", Util.LogLevel.Warning);
                lastException = ex;
            }
        }

        throw new InvalidOperationException(
            $"全ての IP への TCP 接続に失敗: {string.Join(", ", ips)}:{port}",
            lastException);
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
        => SendAsync(data.AsMemory(), ct);

    /// <summary>
    /// P-1: ArrayPool 借用バッファをコピーなしで受け取れる ReadOnlyMemory 版。
    /// 内部の <see cref="LengthPrefixedStream.WriteMessageAsync(Stream, ReadOnlyMemory{byte}, CancellationToken)"/>
    /// に直接渡すため、フレーム書き込み用の ArrayPool 1 回分の rent 以外には alloc が発生しない。
    /// </summary>
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_stream == null || !IsConnected)
            throw new InvalidOperationException("接続されていません");

        // 同一ストリームへの並行 Write を直列化（length-prefix フレームの交錯を防止）
        await _sendLock.WaitAsync(ct);
        try
        {
            await LengthPrefixedStream.WriteMessageAsync(_stream, data, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Close()
    {
        if (!IsConnected && _listener == null && _client == null)
            return;

        Util.Logger.Log("TCP 接続クローズ");
        IsConnected = false;

        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _receiveCts = null;

        _stream?.Dispose();
        _stream = null;

        _client?.Dispose();
        _client = null;

        _listener?.Stop();
        _listener = null;
    }

    public void Dispose()
    {
        var wasConnected = IsConnected;
        Close();
        if (wasConnected)
            ChannelClosed?.Invoke(this, EventArgs.Empty);
    }

    // P-8: NIC 列挙は Windows で 20-50ms かかる重い OS コール。接続のたびに呼ばれる経路だが、
    // NIC 構成は普段ほぼ変わらないため short-TTL キャッシュで再利用する。NetworkAddressChanged で無効化
    private static string[]? s_cachedLocalIps;
    private static long s_cachedLocalIpsTicks;
    private const long LocalIpsCacheTtlTicks = TimeSpan.TicksPerSecond * 30; // 30 秒
    // .NET 9+ の System.Threading.Lock: lock 文で専用高速パスを通る (AOT 安全)
    private static readonly Lock s_localIpsLock = new();

    static TcpDirectTransport()
    {
        // NIC 構成変化（Wi-Fi 切替、VPN 接続/切断 等）でキャッシュを破棄
        NetworkChange.NetworkAddressChanged += (_, _) =>
        {
            lock (s_localIpsLock) { s_cachedLocalIps = null; }
        };
    }

    /// <summary>
    /// このマシンの LAN 内 IPv4 アドレスを列挙する（30 秒キャッシュ + NIC 変化で無効化）。
    /// </summary>
    public static string[] GetLocalIpAddresses()
    {
        var now = DateTime.UtcNow.Ticks;
        lock (s_localIpsLock)
        {
            if (s_cachedLocalIps is not null && now - s_cachedLocalIpsTicks < LocalIpsCacheTtlTicks)
                return s_cachedLocalIps;
        }

        var ips = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var props = nic.GetIPProperties();
            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    ips.Add(addr.Address.ToString());
                }
            }
        }

        var result = ips.ToArray();
        lock (s_localIpsLock)
        {
            s_cachedLocalIps = result;
            s_cachedLocalIpsTicks = now;
        }
        return result;
    }

    private static void ConfigureTcpClient(TcpClient client)
    {
        client.NoDelay = true; // Nagle アルゴリズム無効化（低レイテンシ）
        client.ReceiveBufferSize = 256 * 1024; // 256KB
        client.SendBufferSize = 256 * 1024;
    }

    /// <summary>
    /// 受信ループをバックグラウンドで開始する。
    /// </summary>
    private void StartReceiveLoop()
    {
        _receiveCts = new CancellationTokenSource();
        var ct = _receiveCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested && _stream != null)
                {
                    var data = await LengthPrefixedStream.ReadMessageAsync(_stream, ct);
                    if (data == null)
                    {
                        // 相手が接続を閉じた
                        Util.Logger.Log("TCP 受信: 相手が切断");
                        break;
                    }

                    DataReceived?.Invoke(this, new DataReceivedEventArgs(PeerId, data));
                }
            }
            catch (OperationCanceledException)
            {
                // 正常なキャンセル
            }
            catch (Exception ex)
            {
                Util.Logger.Log($"TCP 受信エラー: {ex.Message}", Util.LogLevel.Warning);
            }
            finally
            {
                if (IsConnected)
                {
                    IsConnected = false;
                    ChannelClosed?.Invoke(this, EventArgs.Empty);
                }
            }
        }, ct);
    }
}
