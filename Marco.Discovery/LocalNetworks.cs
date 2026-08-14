using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Marco.Discovery;

/// <summary>A local NIC's IPv4 network, offered as a scan suggestion.</summary>
public sealed record LocalSubnet(
    string AdapterName, string IpAddress, int PrefixLength, string Cidr, bool HasGateway, bool IsVpn = false)
{
    /// <summary>Usable host count of the subnet, for a hint in the UI.</summary>
    public long HostCount => PrefixLength >= 31 ? (1L << (32 - PrefixLength)) : (1L << (32 - PrefixLength)) - 2;

    /// <summary>A /31 or /32 adapter address — a point-to-point link, so its "subnet" is not a scannable LAN.</summary>
    public bool IsPointToPoint => PrefixLength >= 31;

    /// <summary>Whether "Scan subnet" makes sense (false for point-to-point links).</summary>
    public bool IsScannableSubnet => !IsPointToPoint;

    /// <summary>Short hint shown under the adapter name, e.g. "~254 hosts · VPN".</summary>
    public string Hint
    {
        get
        {
            var count = IsPointToPoint ? "point-to-point" : $"~{HostCount:N0} hosts";
            return IsVpn ? $"{count} · VPN" : count;
        }
    }

    public string Display => $"{Cidr}  ·  {AdapterName}";
}

/// <summary>Enumerates the machine's own IPv4 networks so the operator can add a subnet to the scan with one click
/// instead of typing a CIDR. Real LANs (interfaces with a default gateway) are listed first.</summary>
public static class LocalNetworks
{
    public static IReadOnlyList<LocalSubnet> Enumerate()
    {
        var result = new List<LocalSubnet>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            // Only Loopback is excluded. Tunnel/PPP are kept so VPN adapters (OpenVPN, WireGuard, AnyConnect,
            // Windows IKEv2) are suggested; the IPv4-only filter below already drops IPv6 transition pseudo-tunnels
            // (Teredo/ISATAP/6to4), which is what the old Tunnel exclusion was really for.
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var props = ni.GetIPProperties();
            bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                              && !g.Address.Equals(IPAddress.Any));
            bool isVpn = IsVpnLike(ni);

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue; // IPv4 only
                if (IPAddress.IsLoopback(ua.Address)) continue;

                var ip = ua.Address.ToString();
                if (ip.StartsWith("169.254", StringComparison.Ordinal)) continue; // APIPA

                int prefix = ua.PrefixLength;
                if (prefix is <= 0 or > 32) prefix = 24; // sane default if the OS didn't report one

                var cidr = ToCidr(ua.Address, prefix);
                if (cidr is null) continue;

                if (!result.Any(r => r.Cidr == cidr))
                    result.Add(new LocalSubnet(ni.Name, ip, prefix, cidr, hasGateway, isVpn));
            }
        }

        return result
            .OrderByDescending(r => r.HasGateway)
            .ThenBy(r => r.AdapterName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static readonly string[] VpnMarkers =
        { "vpn", "tap", "tun", "wireguard", "wintun", "anyconnect", "openvpn", "globalprotect",
          "pangp", "zerotier", "tailscale", "nordlynx", "wan miniport", "ppp", "forticlient" };

    private static bool IsVpnLike(NetworkInterface ni)
    {
        if (ni.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel) return true;
        var text = (ni.Name + " " + ni.Description).ToLowerInvariant();
        return VpnMarkers.Any(m => text.Contains(m));
    }

    /// <summary>Compute the network CIDR for an IPv4 address and prefix (exposed for testing).</summary>
    public static string? ToCidr(IPAddress ip, int prefix)
    {
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return null;
        uint addr = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
        uint mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        uint network = addr & mask;
        return $"{(network >> 24) & 0xFF}.{(network >> 16) & 0xFF}.{(network >> 8) & 0xFF}.{network & 0xFF}/{prefix}";
    }
}
