using System.Net;
using System.Runtime.InteropServices;
using Marco.Core.Scanning;

namespace Marco.Discovery;

/// <summary>
/// Resolves a MAC address for an on-subnet IPv4 host via the ARP cache (iphlpapi SendARP). Off-subnet hosts
/// return null — that is normal (the router's MAC would be returned by the OS otherwise, which we don't want),
/// not an error. Runs unelevated.
/// </summary>
public sealed partial class ArpResolver : IMacResolver
{
    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint macAddrLen);

    public Task<string?> GetMacAsync(string address, CancellationToken ct)
        => Task.Run(() => Resolve(address), ct);

    public static string? Resolve(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return null;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null; // IPv4 only

        var bytes = ip.GetAddressBytes();
        uint dest = BitConverter.ToUInt32(bytes, 0);

        var mac = new byte[6];
        uint len = (uint)mac.Length;
        int result = SendARP(dest, 0, mac, ref len);
        if (result != 0 || len < 6) return null;

        // All-zero means no entry.
        if (mac[0] == 0 && mac[1] == 0 && mac[2] == 0 && mac[3] == 0 && mac[4] == 0 && mac[5] == 0)
            return null;

        return string.Join(":", mac.Take(6).Select(b => b.ToString("X2")));
    }
}
