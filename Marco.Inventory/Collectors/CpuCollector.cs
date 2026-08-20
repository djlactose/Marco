using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;

namespace Marco.Inventory.Collectors;

public sealed class CpuCollector : IInventoryCollector
{
    public string Name => "Cpu";

    public async Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct)
    {
        var session = context.Wmi;
        var cpus = await session.QueryAsync(WmiQueryHelpers.CimV2,
            "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, SocketDesignation FROM Win32_Processor",
            ct);

        var list = new List<CpuInfo>();
        foreach (var c in cpus)
        {
            list.Add(new CpuInfo
            {
                Name = c.GetString("Name")?.Trim(),
                Cores = c.GetInt("NumberOfCores") ?? 0,
                LogicalProcessors = c.GetInt("NumberOfLogicalProcessors") ?? 0,
                ClockMhz = c.GetInt("MaxClockSpeed") ?? 0,
                Socket = c.GetString("SocketDesignation"),
            });
        }
        machine.Cpus = list;
        machine.RefreshCounts();
    }
}
