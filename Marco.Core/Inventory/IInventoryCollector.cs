using Marco.Core.Model;
using Marco.Core.Wmi;

namespace Marco.Core.Inventory;

/// <summary>Per-host services handed to every collector: the WMI session and a remote-registry reader over the
/// same credentials. Constructed once per host by the inventory runner.</summary>
public sealed class InventoryContext
{
    public IWmiSession Wmi { get; }
    public IRemoteRegistry Registry { get; }
    public string Host => Wmi.Host;

    public InventoryContext(IWmiSession wmi, IRemoteRegistry registry)
    {
        Wmi = wmi;
        Registry = registry;
    }
}

/// <summary>
/// One inventory collector against one host. Implementations are failure-isolated by the runner: a thrown
/// exception (missing class on an old OS, access denied) becomes a per-collector status on the machine and
/// never aborts the other collectors. Collectors mutate the passed <see cref="Machine"/> in place.
/// </summary>
public interface IInventoryCollector
{
    /// <summary>Stable name shown in the per-collector status list and the pre-scan checklist.</summary>
    string Name { get; }

    Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct);
}
