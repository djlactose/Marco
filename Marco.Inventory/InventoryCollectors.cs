using Marco.Core.Inventory;
using Marco.Inventory.Collectors;

namespace Marco.Inventory;

/// <summary>The Phase 2 collector set, in a sensible run order (identity first, software last as it is the
/// slowest). The pre-scan checklist enables/disables by collector Name.</summary>
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
        new SoftwareCollector(),
    };
}
