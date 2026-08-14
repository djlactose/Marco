using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;

namespace Marco.Inventory.Collectors;

/// <summary>Network adapters: IP configuration from Win32_NetworkAdapterConfiguration, joined to
/// Win32_NetworkAdapter (by Index) for the link speed.</summary>
public sealed class NetworkCollector : IInventoryCollector
{
    public string Name => "Network";

    public async Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct)
    {
        var session = context.Wmi;
        // Link speed keyed by adapter Index.
        var adapters = await session.QueryAsync(WmiQueryHelpers.CimV2,
            "SELECT Index, Name, Speed, MACAddress FROM Win32_NetworkAdapter WHERE NetEnabled = TRUE", ct);
        var speedByIndex = new Dictionary<int, long>();
        var nameByIndex = new Dictionary<int, string?>();
        foreach (var a in adapters)
        {
            var idx = a.GetInt("Index");
            if (idx is null) continue;
            speedByIndex[idx.Value] = (long)(a.GetULong("Speed") ?? 0);
            nameByIndex[idx.Value] = a.GetString("Name");
        }

        var configs = await session.QueryAsync(WmiQueryHelpers.CimV2,
            "SELECT Index, Description, MACAddress, IPAddress, IPSubnet, DefaultIPGateway, " +
            "DNSServerSearchOrder, DHCPEnabled FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE", ct);

        machine.Adapters.Clear();
        foreach (var c in configs)
        {
            var idx = c.GetInt("Index");
            var adapter = new AdapterInfo
            {
                Name = (idx is not null && nameByIndex.TryGetValue(idx.Value, out var n) ? n : null)
                       ?? c.GetString("Description"),
                Mac = c.GetString("MACAddress"),
                SpeedBps = idx is not null && speedByIndex.TryGetValue(idx.Value, out var sp) ? sp : 0,
                SubnetMask = First(c.GetStringArray("IPSubnet")),
                Gateway = First(c.GetStringArray("DefaultIPGateway")),
                DhcpEnabled = c.GetBool("DHCPEnabled") ?? false,
            };
            foreach (var ip in c.GetStringArray("IPAddress") ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(ip)) adapter.IpAddresses.Add(ip);
            foreach (var dns in c.GetStringArray("DNSServerSearchOrder") ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(dns)) adapter.DnsServers.Add(dns);

            machine.Adapters.Add(adapter);

            if (!string.IsNullOrWhiteSpace(adapter.Mac) && !machine.MacAddresses.Contains(adapter.Mac))
                machine.MacAddresses.Add(adapter.Mac);
        }
        machine.RefreshCounts();
    }

    private static string? First(string[]? arr) => arr is { Length: > 0 } ? arr[0] : null;
}
