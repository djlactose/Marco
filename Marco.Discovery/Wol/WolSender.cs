using System.Net;
using System.Net.Sockets;

namespace Marco.Discovery.Wol;

/// <summary>Sends Wake-on-LAN magic packets over UDP port 9. Broadcasts to the limited broadcast address on every
/// operational interface (reaches the local segment) and, when the host's target block is known, to that block's
/// directed broadcast (reaches a routed subnet if the routers forward it — many don't by default). Returns the
/// destinations it attempted so the caller can report/log them.</summary>
public sealed class WolSender
{
    public IReadOnlyList<string> Send(string mac, string? targetBlockCidr)
    {
        var macBytes = WolPacket.TryParseMac(mac) ?? throw new ArgumentException($"Not a MAC address: '{mac}'.", nameof(mac));
        var packet = WolPacket.Build(macBytes);
        var attempted = new List<string>();

        using (var socket = new UdpClient { EnableBroadcast = true })
        {
            var limited = new IPEndPoint(IPAddress.Broadcast, 9);
            try { socket.Send(packet, packet.Length, limited); attempted.Add("255.255.255.255:9"); }
            catch { /* interface may not permit it; keep trying the others */ }

            if (WolPacket.DirectedBroadcastFor(targetBlockCidr) is { } directed)
            {
                try
                {
                    socket.Send(packet, packet.Length, new IPEndPoint(directed, 9));
                    attempted.Add($"{directed}:9");
                }
                catch { }
            }
        }

        return attempted;
    }
}
