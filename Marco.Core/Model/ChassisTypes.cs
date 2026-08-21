namespace Marco.Core.Model;

/// <summary>SMBIOS System Enclosure type codes (Win32_SystemEnclosure.ChassisTypes, /sys/class/dmi/id/chassis_type).</summary>
public static class ChassisTypes
{
    private static readonly string[] Names =
    {
        "Other", "Unknown", "Desktop", "Low Profile Desktop", "Pizza Box", "Mini Tower", "Tower", "Portable",
        "Laptop", "Notebook", "Handheld", "Docking Station", "All in One", "Sub Notebook", "Space-saving",
        "Lunch Box", "Main System Chassis", "Expansion Chassis", "Sub Chassis", "Bus Expansion Chassis",
        "Peripheral Chassis", "RAID Chassis", "Rack Mount Chassis", "Sealed-case PC", "Multi-system Chassis",
        "Compact PCI", "Advanced TCA", "Blade", "Blade Enclosure", "Tablet", "Convertible", "Detachable",
        "IoT Gateway", "Embedded PC", "Mini PC", "Stick PC",
    };

    /// <summary>"Desktop", "Notebook", "Mini PC" … or "Type 40" for a code the table does not know; null for 0/absent.</summary>
    public static string? Describe(int? code)
    {
        if (code is null or <= 0) return null;
        int idx = code.Value - 1;
        return idx < Names.Length ? Names[idx] : $"Type {code}";
    }
}
