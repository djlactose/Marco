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

        machine.MemoryModules.Clear();
        long total = 0;
        foreach (var m in modules)
        {
            var cap = (long)(m.GetULong("Capacity") ?? 0);
            total += cap;
            machine.MemoryModules.Add(new MemoryModule
            {
                CapacityBytes = cap,
                SpeedMhz = m.GetInt("Speed") ?? 0,
                Manufacturer = m.GetString("Manufacturer"),
                PartNumber = m.GetString("PartNumber")?.Trim(),
                SlotLabel = m.GetString("DeviceLocator"),
            });
        }
        machine.TotalMemoryBytes = total;
        machine.MemorySlotsUsed = machine.MemoryModules.Count;

        // Total physical slots from the memory array(s).
        var arrays = await session.QueryTolerantAsync(
            "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray", ct);
        int slots = 0;
        foreach (var a in arrays) slots += a.GetInt("MemoryDevices") ?? 0;
        machine.MemorySlotsTotal = slots > 0 ? slots : machine.MemoryModules.Count;
    }
}
