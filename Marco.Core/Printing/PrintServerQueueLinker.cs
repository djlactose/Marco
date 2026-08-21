using Marco.Core.Model;

namespace Marco.Core.Printing;

/// <summary>
/// Joins the two printer views a scan produces: the Windows print queues collected from servers/PCs
/// (<see cref="Machine.Printers"/>, each carrying the TCP/IP port's host address) and the printer devices
/// themselves. For every printer (or any host a queue points at) the queues on other machines that target
/// its address, any of its IPs, or its name are attached as <see cref="Machine.PrintServerQueues"/>. Pure and
/// idempotent — rerun after every inventory pass and after opening a saved scan; the result is derived, never
/// serialized.
/// </summary>
public static class PrintServerQueueLinker
{
    public static void Link(IReadOnlyList<Machine> machines)
    {
        // Index every queue by the address/host it points at.
        var byTarget = new Dictionary<string, List<PrintServerQueue>>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in machines)
        {
            foreach (var q in server.Printers)
            {
                if (string.IsNullOrWhiteSpace(q.HostAddress)) continue;
                var key = q.HostAddress.Trim();
                if (!byTarget.TryGetValue(key, out var list)) byTarget[key] = list = new List<PrintServerQueue>();
                list.Add(new PrintServerQueue
                {
                    ServerAddress = server.Address,
                    ServerName = server.Name,
                    QueueName = q.Name ?? "?",
                    ShareName = q.ShareName,
                    Shared = q.Shared,
                    Status = q.Status,
                    QueuedJobs = q.QueuedJobs,
                });
            }
        }

        foreach (var m in machines)
        {
            var matches = new List<PrintServerQueue>();
            foreach (var key in Keys(m))
                if (byTarget.TryGetValue(key, out var list))
                    foreach (var q in list)
                        if (!matches.Any(x => x.ServerAddress == q.ServerAddress && x.QueueName == q.QueueName)
                            && !string.Equals(q.ServerAddress, m.Address, StringComparison.OrdinalIgnoreCase))
                            matches.Add(q);
            // Assign only when something changed, so untouched rows don't raise notifications needlessly.
            if (matches.Count > 0 || m.PrintServerQueues.Count > 0)
                m.PrintServerQueues = matches.OrderBy(q => q.ServerName ?? q.ServerAddress, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(q => q.QueueName, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private static IEnumerable<string> Keys(Machine m)
    {
        yield return m.Address;
        foreach (var ip in m.IpAddresses) if (ip != m.Address) yield return ip;
        if (!string.IsNullOrWhiteSpace(m.Name)) yield return m.Name;
        if (!string.IsNullOrWhiteSpace(m.Fqdn)) yield return m.Fqdn;
    }
}
