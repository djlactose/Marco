using System.Net;
using System.Net.Sockets;
using Marco.Core.Scanning;

namespace Marco.Discovery;

/// <summary>
/// Names a host by reverse DNS first (wrapped in an explicit timeout — GetHostEntryAsync ignores cancellation
/// on some paths and can block for seconds against a dead resolver), falling back to an NBNS node-status query
/// for older or non-DNS-registered hosts. NBNS also yields the workgroup/domain and, on some stacks, the MAC.
/// </summary>
public sealed class NameResolver : INameResolver
{
    private readonly int _dnsTimeoutMs;
    private readonly int _nbnsTimeoutMs;

    public NameResolver(int dnsTimeoutMs = 1500, int nbnsTimeoutMs = 1000)
    {
        _dnsTimeoutMs = dnsTimeoutMs;
        _nbnsTimeoutMs = nbnsTimeoutMs;
    }

    public async Task<NameResult> ResolveAsync(string address, CancellationToken ct)
    {
        string? name = null, fqdn = null, domain = null, mac = null;
        bool nbnsResponded = false;

        // 1. Reverse DNS (hard timeout). A DNS name is NOT a NetBIOS signal — many non-Windows devices have one.
        var host = await ReverseDnsAsync(address, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(host))
        {
            fqdn = host;
            name = host.Split('.')[0];
        }

        // 2. NBNS node status — fills the gaps DNS didn't (name, domain, MAC) and, crucially, tells the
        //    classifier a NetBIOS stack answered.
        if (IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            try
            {
                var nb = await NbnsNodeStatusAsync(ip, _nbnsTimeoutMs, ct).ConfigureAwait(false);
                if (nb is not null)
                {
                    nbnsResponded = true;
                    name ??= nb.Name;
                    domain ??= nb.Domain;
                    mac ??= nb.Mac;
                }
            }
            catch { /* NetBIOS disabled / filtered — expected on modern networks */ }
        }

        return new NameResult(name, fqdn, domain, mac, nbnsResponded);
    }

    private async Task<string?> ReverseDnsAsync(string address, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_dnsTimeoutMs);
            var lookup = Dns.GetHostEntryAsync(address);
            var completed = await Task.WhenAny(lookup, Task.Delay(_dnsTimeoutMs, cts.Token)).ConfigureAwait(false);
            if (completed == lookup && lookup.IsCompletedSuccessfully)
            {
                var name = lookup.Result.HostName;
                // A PTR that just echoes the IP is not a real name.
                if (!string.IsNullOrWhiteSpace(name) && !name.Equals(address, StringComparison.OrdinalIgnoreCase))
                    return name;
            }
        }
        catch { /* no PTR record, or resolver down */ }
        return null;
    }

    // --- NBNS node status (RFC 1002) ---

    private sealed record NbnsResult(string? Name, string? Domain, string? Mac);

    private static async Task<NbnsResult?> NbnsNodeStatusAsync(IPAddress ip, int timeoutMs, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var query = BuildNodeStatusQuery();
        await udp.SendAsync(query, query.Length, new IPEndPoint(ip, 137)).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var receiveTask = udp.ReceiveAsync(cts.Token).AsTask();
        try
        {
            var result = await receiveTask.ConfigureAwait(false);
            return ParseNodeStatus(result.Buffer);
        }
        catch (OperationCanceledException)
        {
            return null; // no response within timeout
        }
    }

    private static byte[] BuildNodeStatusQuery()
    {
        // Header (12) + encoded wildcard name (34) + QTYPE NBSTAT (2) + QCLASS IN (2).
        var packet = new List<byte>
        {
            0x00, 0x00,             // transaction id
            0x00, 0x00,             // flags
            0x00, 0x01,             // questions = 1
            0x00, 0x00,             // answer RRs
            0x00, 0x00,             // authority RRs
            0x00, 0x00,             // additional RRs
        };

        // Wildcard NetBIOS name "*" padded to 16 bytes, first-level encoded (each nibble + 'A').
        Span<byte> raw = stackalloc byte[16];
        raw.Clear();
        raw[0] = (byte)'*';
        packet.Add(0x20); // encoded length = 32
        foreach (var b in raw)
        {
            packet.Add((byte)('A' + (b >> 4)));
            packet.Add((byte)('A' + (b & 0x0F)));
        }
        packet.Add(0x00); // root label terminator

        packet.AddRange(new byte[] { 0x00, 0x21 }); // QTYPE = NBSTAT
        packet.AddRange(new byte[] { 0x00, 0x01 }); // QCLASS = IN
        return packet.ToArray();
    }

    private static NbnsResult? ParseNodeStatus(byte[] r)
    {
        // Header(12) + echoed name(34) + TYPE(2)+CLASS(2)+TTL(4)+RDLENGTH(2) = 56 bytes to RDATA.
        const int rdataStart = 56;
        if (r.Length <= rdataStart) return null;

        int numNames = r[rdataStart];
        int p = rdataStart + 1;
        string? machine = null, domain = null;

        for (int i = 0; i < numNames; i++)
        {
            if (p + 18 > r.Length) break;
            var nameStr = System.Text.Encoding.ASCII.GetString(r, p, 15).TrimEnd(' ', '\0');
            byte suffix = r[p + 15];
            int flags = (r[p + 16] << 8) | r[p + 17];
            bool group = (flags & 0x8000) != 0;
            p += 18;

            if (nameStr.Length == 0) continue;
            if (suffix == 0x00 && !group) machine ??= nameStr;          // workstation name
            else if (suffix == 0x00 && group) domain ??= nameStr;        // workgroup / domain
            else if (suffix == 0x1C) domain ??= nameStr;                 // domain (DC group)
        }

        // Trailing 6-byte adapter MAC (often all-zero on modern Windows).
        string? mac = null;
        if (p + 6 <= r.Length)
        {
            var m = new byte[6];
            Array.Copy(r, p, m, 0, 6);
            if (m.Any(b => b != 0))
                mac = string.Join(":", m.Select(b => b.ToString("X2")));
        }

        if (machine is null && domain is null && mac is null) return null;
        return new NbnsResult(machine, domain, mac);
    }
}
