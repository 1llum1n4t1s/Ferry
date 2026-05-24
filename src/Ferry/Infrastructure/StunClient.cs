using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Ferry.Infrastructure;

/// <summary>
/// STUN Binding Request クライアント（RFC 5389 最小実装）。
/// UDP ソケットの外部 IP:port を取得する。
/// </summary>
public static class StunClient
{
    private const uint MagicCookie = 0x2112A442;
    private const int HeaderSize = 20;
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingResponse = 0x0101;
    private const ushort AttrXorMappedAddress = 0x0020;
    private const ushort AttrMappedAddress = 0x0001;

    /// <summary>
    /// STUN Binding Request を送信し、NAT 変換後の外部 IP:port を取得する。
    /// 渡された UdpClient と同じソケットを使うため、
    /// 取得した外部エンドポイントはそのままホールパンチに利用できる。
    /// </summary>
    /// <param name="udpClient">STUN クエリに使う UDP ソケット。</param>
    /// <param name="stunServer">STUN サーバーのホスト名。</param>
    /// <param name="stunPort">STUN サーバーのポート（通常 3478 or 19302）。</param>
    /// <param name="ct">キャンセルトークン。</param>
    /// <returns>外部 (IP, port) のタプル。取得失敗時は null。</returns>
    public static async Task<(string ip, int port)?> GetExternalEndpointAsync(
        UdpClient udpClient, string stunServer, int stunPort = 19302, CancellationToken ct = default)
    {
        var serverAddresses = await Dns.GetHostAddressesAsync(stunServer, ct);
        // IPv4 ソケットと互換性のある IPv4 アドレスを優先選択
        var ipv4Addr = Array.Find(serverAddresses, a => a.AddressFamily == AddressFamily.InterNetwork);
        if (ipv4Addr == null) return null;

        var serverEp = new IPEndPoint(ipv4Addr, stunPort);

        // Binding Request の組み立て
        var transactionId = new byte[12];
        RandomNumberGenerator.Fill(transactionId);

        var request = new byte[HeaderSize];
        // Message Type: Binding Request (0x0001)
        request[0] = (byte)(BindingRequest >> 8);
        request[1] = (byte)(BindingRequest & 0xFF);
        // Message Length: 0
        // Magic Cookie
        request[4] = (byte)((MagicCookie >> 24) & 0xFF);
        request[5] = (byte)((MagicCookie >> 16) & 0xFF);
        request[6] = (byte)((MagicCookie >> 8) & 0xFF);
        request[7] = (byte)(MagicCookie & 0xFF);
        // Transaction ID（M-4: BlockCopy → Span.CopyTo）
        transactionId.AsSpan(0, 12).CopyTo(request.AsSpan(8));

        // 最大3回リトライ
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await udpClient.SendAsync(request, request.Length, serverEp);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(1500);

            try
            {
                var result = await udpClient.ReceiveAsync(timeoutCts.Token);
                var response = result.Buffer;

                if (response.Length < HeaderSize) continue;

                // Binding Response (0x0101) を確認
                var msgType = (ushort)((response[0] << 8) | response[1]);
                if (msgType != BindingResponse) continue;

                // Transaction ID の一致確認
                var txMatch = true;
                for (var j = 0; j < 12; j++)
                {
                    if (response[8 + j] != transactionId[j]) { txMatch = false; break; }
                }
                if (!txMatch) continue;

                // 属性をパースして XOR-MAPPED-ADDRESS または MAPPED-ADDRESS を探す
                var msgLen = (ushort)((response[2] << 8) | response[3]);
                var offset = HeaderSize;
                var end = Math.Min(HeaderSize + msgLen, response.Length);

                while (offset + 4 <= end)
                {
                    var attrType = (ushort)((response[offset] << 8) | response[offset + 1]);
                    var attrLen = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
                    var attrStart = offset + 4;

                    if (attrType == AttrXorMappedAddress && attrStart + 8 <= response.Length)
                    {
                        if (response[attrStart + 1] == 0x01) // IPv4
                        {
                            var xPort = (ushort)((response[attrStart + 2] << 8) | response[attrStart + 3]);
                            var port = xPort ^ (ushort)(MagicCookie >> 16);

                            var xAddr = (uint)(
                                (response[attrStart + 4] << 24) |
                                (response[attrStart + 5] << 16) |
                                (response[attrStart + 6] << 8) |
                                response[attrStart + 7]);
                            var addr = xAddr ^ MagicCookie;

                            // M-5: new[] {} を collection expression に統一（コードベース一貫性）
                            var ip = new IPAddress((ReadOnlySpan<byte>)
                            [
                                (byte)(addr >> 24), (byte)(addr >> 16),
                                (byte)(addr >> 8), (byte)(addr & 0xFF),
                            ]);
                            return (ip.ToString(), port);
                        }
                    }
                    else if (attrType == AttrMappedAddress && attrStart + 8 <= response.Length)
                    {
                        // XOR-MAPPED-ADDRESS がない古いサーバー用フォールバック
                        if (response[attrStart + 1] == 0x01) // IPv4
                        {
                            var port = (response[attrStart + 2] << 8) | response[attrStart + 3];
                            // M-5: collection expression に統一
                            var ip = new IPAddress((ReadOnlySpan<byte>)
                            [
                                response[attrStart + 4], response[attrStart + 5],
                                response[attrStart + 6], response[attrStart + 7],
                            ]);
                            return (ip.ToString(), port);
                        }
                    }

                    // 属性は4バイト境界にパディングされる
                    offset = attrStart + ((attrLen + 3) & ~3);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // タイムアウト → リトライ
            }
        }

        return null;
    }
}
