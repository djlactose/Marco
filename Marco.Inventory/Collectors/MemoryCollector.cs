using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;

namespace Marco.Inventory.Collectors;

public sealed class MemoryCollector : IInventoryCollector
{
    public string Name => "Memory";

    public async Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct)
    {
        var session = context.Wmi;
        var modules = await session.QueryAsync(WmiQueryHelpers.CimV2,
            "SELECT Capacity, Speed, Manufacturer, PartNumber, DeviceLocator FROM Win32_PhysicalMemory", ct);

        var list = new List<MemoryModule>();
        long total = 0;
        foreach (var m in modules)
        {
            var cap = (long)(m.GetULong("Capacity") ?? 0);
            total += cap;
            list.Add(new MemoryModule
            {
                CapacityBytes = cap,
                SpeedMhz = m.GetInt("Speed") ?? 0,
                Manufacturer = m.GetString("Manufacturer"),
                PartNumber = m.GetString("PartNumber")?.Trim(),
                SlotLabel = m.GetString("DeviceLocator"),
            });
        }
        machine.MemoryModules = list;
        machine.TotalMemoryBytes = total;
        machine.MemorySlotsUsed = list.Count;

        // Total physical slots from the memory array(s).
        var arrays = await session.QueryTolerantAsync(
            "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray", ct);
        int slots = 0;
        foreach (var a in arrays) slots += a.GetInt("MemoryDevices") ?? 0;
        machine.MemorySlotsTotal = slots > 0 ? slots : list.Count;
    }
}
