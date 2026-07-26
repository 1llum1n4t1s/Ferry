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
        // IPv6 デュアルスタックで待ち受ける（1 ポートで IPv4/IPv6 両方の着信を受ける）。
        // LAN の IPv4 直結に加え、IPoE 等で IPv4 が CGNAT でも end-to-end IPv6 なら直結できる。
        // IPv6 スタック無効の環境では従来どおり IPv4 のみで待ち受ける。
        TcpListener? listener = null;
        try
        {
            // コンストラクタも try 内で行う: IPv6 が OS レベルで丸ごと無効な環境では
            // new TcpListener(IPv6Any, ...) 自体が SocketException を投げうる。try の外で
            // 生成すると IPv4 フォールバックへ抜けられず起動自体が失敗する（Codex #3516961666）。
            listener = new TcpListener(IPAddress.IPv6Any, 0);
            listener.Server.DualMode = true;  // Start 前に設定（IPV6_V6ONLY=0）
            listener.Start();
            _listener = listener;
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Util.Logger.Log($"TCP リスナー起動: [::]:{port} (dual-stack)");
            return port;
        }
        catch (SocketException ex)
        {
            // TcpListener はコンストラクタで既にソケットを確保しているため、DualMode 設定 / Start
            // 失敗時に破棄しないとハンドルがリークする（CodeRabbit #3516884775）。コンストラクタ自体が
            // 失敗した場合は listener が null のままなので null 条件演算子で安全に済ませる。
            listener?.Dispose();
            Util.Logger.Log($"IPv6 dual-stack リスナー起動失敗 → IPv4 のみで待ち受け: {ex.Message}", Util.LogLevel.Warning);
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Util.Logger.Log($"TCP リスナー起動: 0.0.0.0:{port}");
            return port;
        }
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
                // IPv4/IPv6 混在リストに対応するため、アドレスファミリに合わせてソケットを作る
                // （パラメータなしの new TcpClient() は IPv4 ソケット固定で IPv6 宛てに接続できない）
                var address = IPAddress.Parse(ip);
                var client = new TcpClient(address.AddressFamily);
                try
                {
                    ConfigureTcpClient(client);

                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(3));

                    await client.ConnectAsync(address, port, connectCts.Token);

                    _client = client;
                    _stream = _client.GetStream();
                }
                catch
                {
                    // 接続失敗時に client を破棄せず次の IP へ進むとソケットがリークするため、
                    // 複数 IP を順次試行するこのループでは確実に Dispose してから外側の catch へ委譲する
                    client.Dispose();
                    throw;
                }

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

    /// <summary>offer に載せる IPv6 アドレスの上限。相手側の TCP 試行は全体 5s / 各 3s の予算内で
    /// 順次実行されるため、privacy 拡張で大量にある temporary アドレス等を全部載せても試しきれない。</summary>
    private const int MaxAdvertisedIpv6 = 3;

    /// <summary>
    /// このマシンの直結候補 IP アドレスを列挙する（30 秒キャッシュ + NIC 変化で無効化）。
    /// IPv4 に加えて IPv6（GUA/ULA）も含む。並び順は「v4[0], v6[0], v4[1], v6[1], …」の
    /// インターリーブ — 相手側は各 IP を 3s タイムアウトで順次試行し全体予算 5s で打ち切るため、
    /// LAN の IPv4 即成功を最速に保ちつつ、v4 が不達（cross-NAT の私設 IP 等）でも
    /// 予算内に必ず IPv6 の試行順が回ってくるようにする。
    /// </summary>
    public static string[] GetLocalIpAddresses()
    {
        var now = DateTime.UtcNow.Ticks;
        lock (s_localIpsLock)
        {
            if (s_cachedLocalIps is not null && now - s_cachedLocalIpsTicks < LocalIpsCacheTtlTicks)
                return s_cachedLocalIps;
        }

        var v4 = new List<string>();
        var v6 = new List<IPAddress>();
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
                    v4.Add(addr.Address.ToString());
                else if (IsAdvertisableIpv6(addr.Address))
                    v6.Add(addr.Address);
            }
        }

        // GUA（グローバル）を ULA（fc00::/7、LAN 内限定）より先に。cross-NAT で効くのは GUA だけ
        var v6Ordered = v6
            .OrderBy(a => IsUniqueLocalIpv6(a) ? 1 : 0)
            .Take(MaxAdvertisedIpv6)
            .Select(a => a.ToString())
            .ToList();

        var result = InterleaveAddresses(v4, v6Ordered);
        lock (s_localIpsLock)
        {
            s_cachedLocalIps = result;
            s_cachedLocalIpsTicks = now;
        }
        return result;
    }

    /// <summary>
    /// offer に載せてよい IPv6 アドレスか（純関数・テスト対象）。
    /// リンクローカル（fe80::/10、scope id が要る）・ループバック・マルチキャスト・
    /// Teredo・IPv4-mapped を除外し、GUA（2000::/3）と ULA（fc00::/7）だけを通す。
    /// </summary>
    public static bool IsAdvertisableIpv6(IPAddress addr)
    {
        if (addr.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (addr.IsIPv6LinkLocal || addr.IsIPv6Multicast || addr.IsIPv6Teredo) return false;
        if (addr.IsIPv4MappedToIPv6) return false;
        if (IPAddress.IPv6Loopback.Equals(addr)) return false;
        // GUA (2000::/3) または ULA (fc00::/7) のみ許可（サイトローカル等の遺物を弾く）
        var first = addr.GetAddressBytes()[0];
        var isGua = (first & 0xE0) == 0x20;       // 2000::/3
        return isGua || IsUniqueLocalIpv6(addr);
    }

    /// <summary>ULA (fc00::/7) 判定。</summary>
    public static bool IsUniqueLocalIpv6(IPAddress addr)
        => addr.AddressFamily == AddressFamily.InterNetworkV6
           && (addr.GetAddressBytes()[0] & 0xFE) == 0xFC;

    /// <summary>
    /// v4/v6 リストを「v4[0], v6[0], v4[1], v6[1], …」に編む（純関数・テスト対象）。
    /// 順次試行 + 全体タイムアウトの相手側実装を前提に、両ファミリが早い順番で試されることを保証する。
    /// </summary>
    public static string[] InterleaveAddresses(List<string> v4, List<string> v6)
    {
        var result = new List<string>(v4.Count + v6.Count);
        var max = Math.Max(v4.Count, v6.Count);
        for (var i = 0; i < max; i++)
        {
            if (i < v4.Count) result.Add(v4[i]);
            if (i < v6.Count) result.Add(v6[i]);
        }
        return result.ToArray();
    }

    private static void ConfigureTcpClient(TcpClient client)
    {
        client.NoDelay = true; // Nagle アルゴリズム無効化（低レイテンシ）

        // rere レビュー #C-24: ReceiveBufferSize (SO_RCVBUF) を明示設定しない。
        //
        // 旧実装は送受信とも 256KB を明示していたが、SO_RCVBUF を明示すると Windows の
        // TCP 受信ウィンドウ自動チューニングが無効化され、受信ウィンドウが 256KB に固定される。
        // すると帯域遅延積の大きい経路でスループットが「256KB / RTT」で頭打ちになる:
        //   RTT   0.5ms (LAN)        → 約 4 Gbps 相当（実質無害）
        //   RTT  30ms (国内 WAN)     → 約  70 Mbps
        //   RTT 100ms (海外 / VPN)   → 約  21 Mbps
        // ※ 上記は BDP からの理論値であって実測ではない。
        // CLAUDE.md が IPoE 環境の救済として推している「end-to-end IPv6 での TCP 直結」は
        // まさにこの WAN 経路なので、自動チューニングに任せた方が速い。
        // 送信側 (SO_SNDBUF) も同様に OS の自動調整へ委ねる。
        // 低速回線での過大バッファリングはアプリ層のフロー制御（FileFlowAck）とは無関係
        // （FlowAck はリレー経路専用で、TCP 直結では TCP 自身の輻輳制御が効く）。
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
