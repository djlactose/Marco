namespace Marco.Core.Inventory;

/// <summary>One entry in the operator-facing collector checklist. <see cref="Name"/> is the stable collector
/// name the runners key on (see <see cref="IInventoryCollector.Name"/> and the Linux runner's group names).</summary>
public sealed record CollectorInfo(
    string Name,
    string DisplayName,
    string Description,
    bool DefaultEnabled,
    bool Windows,
    bool Linux);

/// <summary>
/// The catalogue of collectors the UI offers, in display order. Lives in Core (BCL only) so the app, the runners
/// and the tests share one list; the concrete collector set in Marco.Inventory must use these names. Heavier or
/// more sensitive collectors default off — the operator opts in.
/// </summary>
public static class CollectorCatalog
{
    public static IReadOnlyList<CollectorInfo> All { get; } = new[]
    {
        new CollectorInfo("System", "System", "Make, model, serial, asset tag, chassis, BIOS, board, domain, signed-in user.", true, true, true),
        new CollectorInfo("OperatingSystem", "Operating system", "Edition, version, build, architecture, install date, uptime.", true, true, true),
        new CollectorInfo("Cpu", "Processors", "CPU model, cores, threads, clock.", true, true, true),
        new CollectorInfo("Memory", "Memory", "Total RAM and per-slot modules.", true, true, true),
        new CollectorInfo("Storage", "Storage", "Physical disks (type, bus, health, SMART, temperature) and volumes.", true, true, true),
        new CollectorInfo("Network", "Network", "Adapters, IPs, MACs, gateway, DNS, DHCP.", true, true, true),
        new CollectorInfo("Users", "Users", "Local accounts, local Administrators, user profiles, signed-in sessions (Windows); who/last (Linux).", true, true, true),
        new CollectorInfo("Updates", "Updates & servicing", "Installed hotfixes, feature release and full build, pending reboot, WSUS policy.", true, true, false),
        new CollectorInfo("Security", "Security posture", "Antivirus/Defender, firewall, BitLocker, TPM, Secure Boot, UAC, RDP/NLA, SMB1/signing, VBS, LAPS.", true, true, false),
        new CollectorInfo("Services", "Services & startup", "Windows services (state, start mode, account) and startup items.", true, true, false),
        new CollectorInfo("Peripherals", "Peripherals", "Monitors (serials), GPU, printers, USB devices, battery health, thermal zone.", true, true, false),
        new CollectorInfo("InstalledSoftware", "Installed software", "Programs and Features (Windows) or distro packages (Linux). The slowest collector.", true, true, true),
        new CollectorInfo("ScheduledTasks", "Scheduled tasks", "Non-Microsoft scheduled tasks and the account they run as. Slower; off by default.", false, true, false),
        new CollectorInfo("UsbHistory", "USB storage history", "USB storage devices that have ever been connected (registry). Off by default.", false, true, false),
    };

    public static ISet<string> DefaultEnabledNames()
        => new HashSet<string>(All.Where(c => c.DefaultEnabled).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>The effective enabled set: catalogue defaults overlaid with the operator's explicit overrides
    /// (name → enabled). Unknown override names are ignored; catalogue entries without an override keep their
    /// default, so a collector added in a later version shows up with its default state.</summary>
    public static HashSet<string> EnabledNames(IReadOnlyDictionary<string, bool>? overrides)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in All)
        {
            bool enabled = c.DefaultEnabled;
            if (overrides is not null && overrides.TryGetValue(c.Name, out var o)) enabled = o;
            if (enabled) set.Add(c.Name);
        }
        return set;
    }

    /// <summary>The overrides worth persisting: only entries whose state differs from the catalogue default.</summary>
    public static Dictionary<string, bool>? OverridesFor(IEnumerable<string> enabledNames)
    {
        var enabled = new HashSet<string>(enabledNames, StringComparer.OrdinalIgnoreCase);
        var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in All)
        {
            bool on = enabled.Contains(c.Name);
            if (on != c.DefaultEnabled) dict[c.Name] = on;
        }
        return dict.Count == 0 ? null : dict;
    }
}
