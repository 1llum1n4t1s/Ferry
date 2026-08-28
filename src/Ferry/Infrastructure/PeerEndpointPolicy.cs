using System.Net;
using System.Net.Sockets;

namespace Ferry.Infrastructure;

/// <summary>
/// ペアが signaling へ申告した接続先を TCP/UDP で使う前の共通ポリシー。
/// LAN 直結に必要な private IPv4・IPv4 link-local・IPv6 ULA は許可しつつ、
/// ローカルサービスやクラウドメタデータへ転送プロトコルを誤送信する宛先を除外する。
/// </summary>
internal static class PeerEndpointPolicy
{
    private static readonly IPAddress Ipv4Metadata = IPAddress.Parse("169.254.169.254");
    private static readonly IPAddress Ipv4ContainerMetadata = IPAddress.Parse("169.254.170.2");
    private static readonly IPAddress Ipv6Metadata = IPAddress.Parse("fd00:ec2::254");

    public static bool IsAllowedPort(int port) => port is > 0 and <= IPEndPoint.MaxPort;

    public static bool IsAllowedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (IPAddress.IsLoopback(address))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            // 0/8（this host）、multicast 224/4、予約済み 240/4・limited broadcast は peer 宛先にならない。
            if (bytes[0] == 0 || bytes[0] >= 224)
                return false;

            // 169.254/16 全体は PC 同士の DHCP 不在直結で使えるため許可し、
            // クラウド/コンテナの link-local metadata endpoint だけを明示的に除外する。
            return !address.Equals(Ipv4Metadata) && !address.Equals(Ipv4ContainerMetadata);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        // IPv4-mapped は IPv4 リテラルを使えばよく、別表現による IPv4 policy の迂回を許可しない。
        if (address.IsIPv4MappedToIPv6
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal
            || address.IsIPv6Teredo)
        {
            return false;
        }

        // Ferry が広告する IPv6 と対称に、GUA (2000::/3) と ULA (fc00::/7) だけを許可する。
        var first = address.GetAddressBytes()[0];
        var isGlobalUnicast = (first & 0xE0) == 0x20;
        var isUniqueLocal = (first & 0xFE) == 0xFC;
        if (!isGlobalUnicast && !isUniqueLocal)
            return false;

        return !address.Equals(Ipv6Metadata);
    }
}
