using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;

namespace Marco.Inventory.Collectors;

/// <summary>Installed modules (type, speed, form factor), total RAM, slot usage and the platform maximum.</summary>
public sealed class MemoryCollector : IInventoryCollector
{
    public string Name => "Memory";

    /// <summary>SMBIOS "Maximum Capacity" sentinel (in KB) meaning "see Extended Maximum Capacity".</summary>
    private const ulong MaxCapacityPlaceholderKb = 0x8000_0000;

    public async Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct)
    {
        var session = context.Wmi;

        // SMBIOSMemoryType / ConfiguredClockSpeed arrived with Windows 10; a host whose class lacks them rejects
        // the whole SELECT, so fall back to the classic property list. The retry is the primary query: if it
        // fails too, the exception surfaces as the collector's status.
        IReadOnlyList<WmiObject> modules;
        try
        {
            modules = await session.QueryAsync(WmiQueryHelpers.CimV2,
                "SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DeviceLocator, " +
                "SMBIOSMemoryType, MemoryType, FormFactor FROM Win32_PhysicalMemory", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (WmiException)
        {
            modules = await session.QueryAsync(WmiQueryHelpers.CimV2,
                "SELECT Capacity, Speed, Manufacturer, PartNumber, DeviceLocator FROM Win32_PhysicalMemory", ct);
        }

        var list = new List<MemoryModule>();
        long total = 0;
        foreach (var m in modules)
        {
            var cap = (long)(m.GetULong("Capacity") ?? 0);
            total += cap;
            var configured = m.GetInt("ConfiguredClockSpeed");
            list.Add(new MemoryModule
            {
                CapacityBytes = cap,
                SpeedMhz = m.GetInt("Speed") ?? 0,
                ConfiguredSpeedMhz = configured is > 0 ? configured : null,
                Manufacturer = m.GetString("Manufacturer"),
                PartNumber = m.GetString("PartNumber")?.Trim(),
                SlotLabel = m.GetString("DeviceLocator"),
                MemoryTypeName = DescribeMemoryType(m.GetInt("SMBIOSMemoryType"))
                                 ?? DescribeMemoryType(m.GetInt("MemoryType")),
                FormFactor = DescribeFormFactor(m.GetInt("FormFactor")),
            });
        }
        machine.MemoryModules = list;
        machine.TotalMemoryBytes = total;
        machine.MemorySlotsUsed = list.Count;

        ct.ThrowIfCancellationRequested();

        // Total physical slots and the platform maximum from the memory array(s). MaxCapacityEx is Win10 1607+;
        // MaxCapacity and MemoryDevices have always existed, so retry without the newer property.
        var arrays = await session.QueryOptionalAsync(
            "SELECT MemoryDevices, MaxCapacity, MaxCapacityEx FROM Win32_PhysicalMemoryArray", ct);
        if (arrays.Count == 0)
            arrays = await session.QueryTolerantAsync(
                "SELECT MemoryDevices, MaxCapacity FROM Win32_PhysicalMemoryArray", ct);

        int slots = 0;
        ulong maxKb = 0;
        foreach (var a in arrays)
        {
            slots += a.GetInt("MemoryDevices") ?? 0;
            var kb = a.GetULong("MaxCapacityEx");
            if (kb is null or 0)
            {
                kb = a.GetULong("MaxCapacity");
                if (kb == MaxCapacityPlaceholderKb) kb = null;
            }
            maxKb += kb ?? 0;
        }
        machine.MemorySlotsTotal = slots > 0 ? slots : list.Count;
        machine.MaxMemoryBytes = SanitizeMaxMemory(maxKb, total);
    }

    /// <summary>Both WMI properties are in KB. A figure of zero, or one below what is actually installed (seen on
    /// boards whose SMBIOS table was never updated), is reported as unknown rather than clamped — a clamped number
    /// would read as authoritative.</summary>
    public static long? SanitizeMaxMemory(ulong maxKb, long installedBytes)
    {
        if (maxKb == 0 || maxKb > long.MaxValue / 1024) return null;
        var bytes = (long)maxKb * 1024;
        return bytes < installedBytes ? null : bytes;
    }

    /// <summary>SMBIOS type 17 "Memory Type" codes (shared by SMBIOSMemoryType and MemoryType). Unknown (0/2) and
    /// legacy codes stay null rather than guessing.</summary>
    public static string? DescribeMemoryType(int? code) => code switch
    {
        20 => "DDR",
        21 => "DDR2",
        22 => "DDR2 FB-DIMM",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => null,
    };

    /// <summary>SMBIOS type 17 "Form Factor" codes — only the two an operator can buy off the shelf are named.</summary>
    public static string? DescribeFormFactor(int? code) => code switch
    {
        8 => "DIMM",
        12 => "SODIMM",
        _ => null,
    };
}
