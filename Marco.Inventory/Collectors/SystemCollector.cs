using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;

namespace Marco.Inventory.Collectors;

/// <summary>System identity: computer system, enclosure/chassis, BIOS, and motherboard.</summary>
public sealed class SystemCollector : IInventoryCollector
{
    public string Name => "System";

    /// <summary>Win32_SystemSlot.CurrentUsage: 3 = Available, 4 = In Use (1/2 = Other/Unknown are not counted free).</summary>
    private const int SlotAvailable = 3;

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

        // Lenovo puts the machine-type code in Model and the friendly name ("ThinkPad T14 Gen 3") in
        // ComputerSystemProduct.Version; the hardware spec lookup matches on either. Optional enrichment.
        var product = await session.QueryOptionalAsync(
            "SELECT Version FROM Win32_ComputerSystemProduct", ct);
        if (product.Count > 0 && product[0].GetString("Version") is { } pv)
        {
            pv = pv.Trim();
            machine.System.ProductVersion = pv.Length == 0 || IsPlaceholder(pv) ? null : pv;
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

        ct.ThrowIfCancellationRequested();

        // Expansion slots feed the best-effort drive-expansion estimate. Optional: VMs and many older hosts
        // report none, and that must leave the fields null rather than touch the collector's status.
        var slots = await session.QueryOptionalAsync(
            "SELECT SlotDesignation, CurrentUsage, Status FROM Win32_SystemSlot", ct);
        if (slots.Count > 0)
        {
            var free = new List<string>();
            foreach (var s in slots)
                if (s.GetInt("CurrentUsage") == SlotAvailable)
                    free.Add(s.GetString("SlotDesignation") is { Length: > 0 } d ? d : "?");
            machine.System.ExpansionSlotsTotal = slots.Count;
            machine.System.ExpansionSlotsFree = free.Count;
            machine.System.ExpansionSlotsFreeList = free.Count == 0 ? null : string.Join(", ", free);
        }

        // Last person to sign in (shown when nobody is currently logged on). From LogonUI in the registry —
        // best-effort, since it needs the Remote Registry service; a failure must not fail the System collector.
        try
        {
            var logonUi = context.Registry.GetValues(RegistryRoot.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI",
                new[] { "LastLoggedOnUser", "LastLoggedOnDisplayName" });
            var last = (logonUi.TryGetValue("LastLoggedOnUser", out var u) ? u as string : null)?.Trim();
            if (string.IsNullOrWhiteSpace(last))
                last = (logonUi.TryGetValue("LastLoggedOnDisplayName", out var d) ? d as string : null)?.Trim();
            if (!string.IsNullOrWhiteSpace(last))
                machine.System.LastLoggedOnUser = last;
        }
        catch { /* Remote Registry unavailable — current-user (WMI) still populated above */ }
    }

    /// <summary>Firmware filler values that mean "not set".</summary>
    private static bool IsPlaceholder(string v) => v.Equals("None", StringComparison.OrdinalIgnoreCase)
        || v.Equals("Not Specified", StringComparison.OrdinalIgnoreCase)
        || v.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase)
        || v.Equals("System Version", StringComparison.OrdinalIgnoreCase)
        || v.Equals("Default string", StringComparison.OrdinalIgnoreCase)
        || v.Equals("x.x", StringComparison.OrdinalIgnoreCase);

    private static string? DescribeChassis(object? chassisTypes)
    {
        int? code = chassisTypes switch
        {
            ushort[] u when u.Length > 0 => u[0],
            int[] a when a.Length > 0 => a[0],
            string[] s when s.Length > 0 && int.TryParse(s[0], out var p) => p,
            _ => null,
        };
        return ChassisTypes.Describe(code);
    }
}
