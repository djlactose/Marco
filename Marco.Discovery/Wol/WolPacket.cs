using System.Net;

namespace Marco.Discovery.Wol;

/// <summary>Builds the Wake-on-LAN "magic packet" and computes directed-broadcast addresses. Pure and testable —
/// the transmit lives in <see cref="WolSender"/>.</summary>
public static class WolPacket
{
    /// <summary>Parse a MAC in any common form (AA:BB:CC:DD:EE:FF, AA-BB-…, or bare hex) into 6 bytes; null when
    /// it is not a 48-bit address.</summary>
    public static byte[]? TryParseMac(string? mac)
    {
        if (mac is null) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return null;
        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    /// <summary>The 102-byte magic packet: 6 bytes of 0xFF followed by the target MAC repeated 16 times.</summary>
    public static byte[] Build(byte[] mac)
    {
        if (mac.Length != 6) throw new ArgumentException("MAC must be 6 bytes.", nameof(mac));
        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int rep = 0; rep < 16; rep++)
            Array.Copy(mac, 0, packet, 6 + rep * 6, 6);
        return packet;
    }

    /// <summary>The directed-broadcast address of the CIDR block a host belongs to (e.g. 10.1.2.0/24 → 10.1.2.255),
    /// so a magic packet can reach a host on a routed subnet where routers forward directed broadcasts. Null when
    /// the block is not a usable IPv4 CIDR or is a /31 or /32 (no broadcast address).</summary>
    public static IPAddress? DirectedBroadcastFor(string? cidr)
    {
        if (cidr is null) return null;
        var slash = cidr.IndexOf('/');
        if (slash < 0) return null;
        if (!IPAddress.TryParse(cidr[..slash], out var network)
            || network.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        if (!int.TryParse(cidr[(slash + 1)..], out var prefix) || prefix is < 0 or > 30) return null;

        var addr = network.GetAddressBytes();
        uint value = (uint)((addr[0] << 24) | (addr[1] << 16) | (addr[2] << 8) | addr[3]);
        uint mask = prefix == 0 ? 0 : 0xFFFFFFFF << (32 - prefix);
        uint broadcast = (value & mask) | ~mask;
        return new IPAddress(new[]
        {
            (byte)(broadcast >> 24), (byte)(broadcast >> 16), (byte)(broadcast >> 8), (byte)broadcast,
        });
    }
}
