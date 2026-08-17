using Marco.Core.Inventory;
using Marco.Inventory.Collectors;

namespace Marco.Inventory;

/// <summary>The Windows collector set, in a sensible run order: identity first (later collectors read
/// System/Os facts such as the DC flag and OS version), the cheap WMI groups next, software last as it is the
/// slowest. Names must match <see cref="CollectorCatalog"/> — the operator's checklist enables/disables by
/// name, and heavier collectors default off there.</summary>
public static class InventoryCollectors
{
    public static IReadOnlyList<IInventoryCollector> Phase2() => new IInventoryCollector[]
    {
        new SystemCollector(),
        new OsCollector(),
        new CpuCollector(),
        new MemoryCollector(),
        new StorageCollector(),
        new NetworkCollector(),
        new UsersCollector(),
        new UpdatesCollector(),
        new SecurityCollector(),
        new ServicesCollector(),
        new PeripheralsCollector(),
        new ScheduledTasksCollector(),
        new UsbHistoryCollector(),
        new SoftwareCollector(),
    };
}
