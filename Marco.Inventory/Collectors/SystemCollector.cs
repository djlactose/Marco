using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;

namespace Marco.Inventory.Collectors;

/// <summary>System identity: computer system, enclosure/chassis, BIOS, and motherboard.</summary>
public sealed class SystemCollector : IInventoryCollector
{
    public string Name => "System";

    private static readonly string[] ChassisTypeNames =
    {
        "Other", "Unknown", "Desktop", "Low Profile Desktop", "Pizza Box", "Mini Tower", "Tower", "Portable",
        "Laptop", "Notebook", "Handheld", "Docking Station", "All in One", "Sub Notebook", "Space-saving",
        "Lunch Box", "Main System Chassis", "Expansion Chassis", "Sub Chassis", "Bus Expansion Chassis",
        "Peripheral Chassis", "RAID Chassis", "Rack Mount Chassis", "Sealed-case PC", "Multi-system Chassis",
        "Compact PCI", "Advanced TCA", "Blade", "Blade Enclosure", "Tablet", "Convertible", "Detachable",
    };

    public async Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct)
    {
        var session = context.Wmi;
        var cs = await session.QueryFirstAsync(
            "SELECT Name, Manufacturer, Model, Domain, PartOfDomain, UserName FROM Win32_ComputerSystem", ct);
        if (cs is not null)
        {
            if (cs.GetString("Name") is { } n && string.IsNullOrWhiteSpace(machine.Name)) machine.Name = n;
            machine.System.Manufacturer = cs.GetString("Manufacturer");
            machine.System.Model = cs.GetString("Model");
            machine.System.Domain = cs.GetString("Domain") ?? machine.System.Domain;
            machine.System.PartOfDomain = cs.GetBool("PartOfDomain") ?? false;
            machine.System.LoggedOnUser = cs.GetString("UserName");
        }

        var enc = await session.QueryFirstAsync(
            "SELECT SerialNumber, SMBIOSAssetTag, ChassisTypes FROM Win32_SystemEnclosure", ct);
        if (enc is not null)
        {
            machine.System.AssetTag = enc.GetString("SMBIOSAssetTag");
            var chassis = enc["ChassisTypes"];
            machine.System.ChassisType = DescribeChassis(chassis);
            // Enclosure serial is a good fallback if BIOS serial is blank.
            if (machine.System.SerialNumber is null) machine.System.SerialNumber = enc.GetString("SerialNumber");
        }

        var bios = await session.QueryFirstAsync(
            "SELECT SerialNumber, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS", ct);
        if (bios is not null)
        {
            var serial = bios.GetString("SerialNumber");
            if (!string.IsNullOrWhiteSpace(serial)) machine.System.SerialNumber = serial;
            machine.System.BiosVersion = bios.GetString("SMBIOSBIOSVersion");
            machine.System.BiosDate = bios.GetDateTime("ReleaseDate");
        }

        var board = await session.QueryFirstAsync(
            "SELECT Manufacturer, Product FROM Win32_BaseBoard", ct);
        if (board is not null)
        {
            machine.System.MotherboardManufacturer = board.GetString("Manufacturer");
            machine.System.MotherboardModel = board.GetString("Product");
        }
    }

    private static string? DescribeChassis(object? chassisTypes)
    {
        int? code = chassisTypes switch
        {
            ushort[] u when u.Length > 0 => u[0],
            int[] a when a.Length > 0 => a[0],
            string[] s when s.Length > 0 && int.TryParse(s[0], out var p) => p,
            _ => null,
        };
        if (code is null or <= 0) return null;
        int idx = code.Value - 1;
        return idx >= 0 && idx < ChassisTypeNames.Length ? ChassisTypeNames[idx] : $"Type {code}";
    }
}
