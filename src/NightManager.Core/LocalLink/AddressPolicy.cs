using System.Net;
using System.Net.Sockets;

namespace NightManager.Core.LocalLink;

/// <summary>A network adapter as Windows reports it (name, description, kind) with one of its addresses.</summary>
public sealed record LocalAddress(string InterfaceName, string Description, AdapterKind Kind, IPAddress Address, int PrefixLength);

public enum AdapterKind { Ethernet, Wireless, Tunnel, Ppp, Loopback, Other }

/// <summary>
/// Who may use the local link: phones on the same home network only, never over a VPN. Remote observatories on
/// metered links must not have previews pulled over Tailscale, ZeroTier, WireGuard or the like (docs/LOCAL-LINK.md).
/// The server listens only on the addresses <see cref="Listenable"/> allows, and answers only what
/// <see cref="Allows"/> accepts.
/// </summary>
public static class AddressPolicy {
    /// <summary>Adapters that are really VPNs or virtual links, whatever kind Windows says they are.</summary>
    private static readonly string[] VpnWords = ["tailscale", "zerotier", "wireguard", "openvpn", "tap-windows", "wintun", "nordlynx", "vpn", "wan miniport", "hamachi", "netbird", "twingate", "cloudflare warp"];

    /// <summary>The addresses to listen on: private addresses of physical Ethernet and Wi-Fi adapters, no VPNs.</summary>
    public static IReadOnlyList<LocalAddress> Listenable(IEnumerable<LocalAddress> addresses) =>
        addresses.Where(a => a.Kind is AdapterKind.Ethernet or AdapterKind.Wireless && !LooksLikeVpn(a) && IsPrivate(a.Address)).ToList();

    public static bool LooksLikeVpn(LocalAddress a) {
        var text = $"{a.InterfaceName} {a.Description}".ToLowerInvariant();
        return VpnWords.Any(text.Contains) || IsCgnat(a.Address);
    }

    /// <summary>
    /// A request from <paramref name="remote"/> that arrived on <paramref name="local"/> (one of the listenable
    /// addresses): allowed only from a private address in the same subnet, and never from Tailscale's range.
    /// </summary>
    public static bool Allows(IPAddress remote, LocalAddress local) {
        remote = Normalise(remote);
        if (IsCgnat(remote) || !IsPrivate(remote) || IPAddress.IsLoopback(remote)) return false;
        if (!Listenable([local]).Any()) return false;
        return SameSubnet(remote, Normalise(local.Address), local.PrefixLength);
    }

    /// <summary>RFC 1918 IPv4, IPv6 unique-local (fc00::/7) and link-local (fe80::/10).</summary>
    public static bool IsPrivate(IPAddress address) {
        address = Normalise(address);
        if (address.AddressFamily == AddressFamily.InterNetwork) {
            var b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6) {
            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
        }
        return false;
    }

    /// <summary>100.64.0.0/10: carrier-grade NAT, which is where Tailscale puts every device.</summary>
    public static bool IsCgnat(IPAddress address) {
        address = Normalise(address);
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    public static bool SameSubnet(IPAddress a, IPAddress b, int prefixLength) {
        if (a.AddressFamily != b.AddressFamily) return false;
        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        var bits = Math.Clamp(prefixLength, 0, x.Length * 8);
        for (var i = 0; i < x.Length && bits > 0; i++, bits -= 8) {
            var mask = bits >= 8 ? 0xFF : (byte)(0xFF << (8 - bits));
            if ((x[i] & mask) != (y[i] & mask)) return false;
        }
        return true;
    }

    private static IPAddress Normalise(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}
