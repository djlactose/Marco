using Marco.Core.Model;

namespace Marco.Export.Diff;

/// <summary>
/// Compares two scan documents machine-by-machine (paired via <see cref="MachineIdentity"/>) into a
/// <see cref="ScanDiffResult"/>. Two rules keep the output signal instead of noise:
/// - A category is only compared when its collector succeeded in BOTH documents — otherwise every failed
///   collector would read as "everything vanished".
/// - Transient values (uptime, logon sessions, temperatures, free space, currently-plugged USB devices) are
///   never compared; they differ between any two scans of a healthy machine.
/// Security fields have a direction: worsening (BitLocker on→off, SMB1 off→on) is a Regression, improving is
/// plain Info, so the red in the diff window always means "look at this".
/// </summary>
public static class ScanDiffEngine
{
    public static ScanDiffResult Compute(ScanDocument older, ScanDocument newer)
    {
        var match = MachineIdentity.Match(older.Machines, newer.Machines);

        var changed = new List<MachineDiff>();
        foreach (var pair in match.Matched)
        {
            var changes = CompareMachine(pair.Older, pair.Newer);
            if (changes.Count > 0)
                changed.Add(new MachineDiff(MachineIdentity.ToRef(pair.Newer, pair.MatchedBy), changes));
        }

        return new ScanDiffResult(
            new DiffMetadata(older.Metadata.Timestamp, newer.Metadata.Timestamp,
                older.Metadata.RangesScanned, newer.Metadata.RangesScanned,
                older.Machines.Count, newer.Machines.Count),
            match.Added.Select(m => MachineIdentity.ToRef(m)).ToList(),
            match.Removed.Select(m => MachineIdentity.ToRef(m)).ToList(),
            changed);
    }

    private static List<FieldChange> CompareMachine(MachineDto o, MachineDto n)
    {
        var list = new List<FieldChange>();

        CompareDiscovery(o, n, list);
        if (BothOk(o, n, "System")) CompareSystem(o, n, list);
        if (BothOk(o, n, "OperatingSystem")) CompareOs(o, n, list);
        if (BothOk(o, n, "Memory")) CompareMemory(o, n, list);
        if (BothOk(o, n, "Cpu")) CompareCpus(o, n, list);
        if (BothOk(o, n, "Storage")) CompareStorage(o, n, list);
        if (BothOk(o, n, "Network")) CompareAdapters(o, n, list);
        if (BothOk(o, n, "InstalledSoftware")) CompareSoftware(o, n, list);
        if (BothOk(o, n, "Updates")) CompareUpdates(o, n, list);
        if (BothOk(o, n, "Security")) CompareSecurity(o, n, list);
        if (BothOk(o, n, "Users")) CompareAccounts(o, n, list);
        if (BothOk(o, n, "Services")) CompareServices(o, n, list);
        if (BothOk(o, n, "ScheduledTasks")) CompareScheduledTasks(o, n, list);
        if (BothOk(o, n, "Peripherals")) ComparePeripherals(o, n, list);
        if (BothOk(o, n, "Supplies") || BothOk(o, n, "PrinterStatus")) ComparePrinter(o, n, list);

        return list;
    }

    /// <summary>Printer devices: only threshold crossings and status transitions — a toner level creeping from
    /// 41 % to 38 % is not a change worth a row, crossing into "low" or "empty" is, and recovering (a replaced
    /// cartridge) is worth an Info line. Page counters are transient and never compared.</summary>
    private static void ComparePrinter(MachineDto o, MachineDto n, List<FieldChange> list)
    {
        if (o.Printer is not { } op || n.Printer is not { } np) return;

        static string Grade(Marco.Core.Model.PrinterSupply s) => s.IsEmpty ? "empty" : s.IsLow ? "low" : "ok";
        Keyed(list, DiffCategory.Printer, "Supply", op.Supplies, np.Supplies,
            s => $"{s.Type}|{s.Colorant}|{s.Name}", s => $"{s.Name} ({s.LevelDisplay})", DiffSeverity.Info,
            compareMatched: (a, b) =>
            {
                var inner = new List<FieldChange>();
                var ga = Grade(a); var gb = Grade(b);
                if (ga == gb) return inner;
                bool worse = gb == "empty" || (gb == "low" && ga == "ok");
                inner.Add(new FieldChange(DiffCategory.Printer, $"Supply: {b.Name}", DiffChangeKind.Changed,
                    a.LevelDisplay, b.LevelDisplay, worse ? DiffSeverity.Regression : DiffSeverity.Info));
                return inner;
            });

        // Status: only the flip between "printing normally" and "needs a person" (jam, door, out of toner, down).
        if (op.HasErrorCondition != np.HasErrorCondition)
        {
            static string Describe(Marco.Core.Model.PrinterDevice p) => string.Join(", ",
                new[] { p.Status }.Concat(p.ErrorStates)
                    .Concat(string.Equals(p.DeviceStatus, "Down", StringComparison.OrdinalIgnoreCase) ? new[] { "Down" } : Array.Empty<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            Add(list, DiffCategory.Printer, "Printer status", Describe(op), Describe(np),
                np.HasErrorCondition ? DiffSeverity.Regression : DiffSeverity.Info);
        }
    }

    private static bool BothOk(MachineDto o, MachineDto n, string collector)
        => CollectorOk(o, collector) && CollectorOk(n, collector);

    private static bool CollectorOk(MachineDto m, string collector)
        => m.Collectors.Any(c => string.Equals(c.Name, collector, StringComparison.OrdinalIgnoreCase)
                                 && c.Status == CollectorStatus.Ok);

    // --- Discovery-level (no collector gate) ---

    private static void CompareDiscovery(MachineDto o, MachineDto n, List<FieldChange> list)
    {
        bool wasAlive = IsAlive(o.Status), isAlive = IsAlive(n.Status);
        if (wasAlive != isAlive)
            Add(list, DiffCategory.Identity, "Reachability", wasAlive ? "alive" : "down", isAlive ? "alive" : "down",
                isAlive ? DiffSeverity.Info : DiffSeverity.Notable);

        // Only meaningful when identity came from something stronger than the address itself.
        if (!string.Equals(o.Address, n.Address, StringComparison.OrdinalIgnoreCase))
            Add(list, DiffCategory.Network, "IP address", o.Address, n.Address, DiffSeverity.Info);

        if (!SameText(o.Name, n.Name))
            Add(list, DiffCategory.Identity, "Hostname", o.Name, n.Name, DiffSeverity.Notable);

        if (o.DeviceType != n.DeviceType && o.DeviceType != DeviceType.Unknown && n.DeviceType != DeviceType.Unknown)
            Add(list, DiffCategory.Identity, "Device type", o.DeviceType.ToString(), n.DeviceType.ToString(), DiffSeverity.Info);
    }

    private static bool IsAlive(MachineStatus s)
        => s is not (MachineStatus.Pending or MachineStatus.Scanning or MachineStatus.Unreachable or MachineStatus.Cancelled);

    // --- Hardware ---

    private static void CompareSystem(MachineDto o, MachineDto n, List<FieldChange> list)
    {
        // A serial change under a MAC/address match usually means the board or the whole box was swapped.
        Scalar(list, DiffCategory.Hardware, "Serial number",
            MachineIdentity.NormalizeSerial(o.System.SerialNumber), MachineIdentity.NormalizeSerial(n.System.SerialNumber),
            DiffSeverity.Notable);
        Scalar(list, DiffCategory.Hardware, "Model", o.System.Model, n.System.Model, DiffSeverity.Notable);
        Scalar(list, DiffCategory.Hardware, "Manufacturer", o.System.Manufacturer, n.System.Manufacturer, DiffSeverity.Notable);
        Scalar(list, DiffCategory.Hardware, "Motherboard", o.System.MotherboardModel, n.System.MotherboardModel, DiffSeverity.Notable);
        Scalar(list, DiffCategory.Hardware, "BIOS version", o.System.BiosVersion, n.System.BiosVersion, DiffSeverity.Info);
        Scalar(list, DiffCategory.Identity, "Domain", o.System.Domain, n.System.Domain, DiffSeverity.Notable);
    }

    private static void CompareOs(MachineDto o, MachineDto n, List<FieldChange> list)
    {
        Scalar(list, DiffCategory.Os, "OS", o.Os.Caption, n.Os.Caption, DiffSeverity.Notable);
        Scalar(list, DiffCategory.Os, "OS build", o.Os.Build, n.Os.Build, DiffSeverity.Info);
        Scalar(list, DiffCategory.Os, "Architecture", o.Os.Architecture, n.Os.Architecture, DiffSeverity.Info);
        if (o.Os.InstallDate is { } oi && n.Os.InstallDate is { } ni && oi.Date != ni.Date)
            Add(list, DiffCategory.Os, "OS install date", $"{oi:yyyy-MM-dd}", $"{ni:yyyy-MM-dd}", DiffSeverity.Notable); // reinstall
    }

    private static void CompareMemory(MachineDto o, MachineDto n, List<FieldChange> list)
    {
        if (o.TotalMemoryBytes != n.TotalMemoryBytes && o.TotalMemoryBytes > 0 && n.TotalMemoryBytes > 0)
            Add(list, DiffCategory.Hardware, "Total RAM", Gb(o.TotalMemoryBytes), Gb(n.TotalMemoryBytes), DiffSeverity.Notable);

        Keyed(list, DiffCategory.Hardware, "RAM module",
            o.MemoryModules, n.MemoryModules,
            m => m.SlotLabel ?? $"{m.PartNumber}/{m.CapacityBytes}",
            DescribeModule,
            DiffSeverity.Notable,
            (om, nm) =>
            {
                // Same slot, different stick (an upgrade or a swap) — keyed by slot, so report it as a change.
                // The type only takes part when both scans recorded it, so a scan saved before Marco collected
                // memory types does not read as "every module changed".
                var inner = new List<FieldChange>();
                bool withType = om.MemoryTypeName is not null && nm.MemoryTypeName is not null;
                if (!SameText(DescribeModule(om, withType), DescribeModule(nm, withType)))
                    Add(inner, DiffCategory.Hardware, $"RAM module {nm.SlotLabel ?? "?"}",
                        DescribeModule(om), DescribeModule(nm), DiffSeverity.Notable);
                return inner;
            });
    }

    private static string DescribeModule(MemoryModule m) => DescribeModule(m, withType: true);

    private static string DescribeModule(MemoryModule m, bool withType)
        => string.Join(" ", new[] { Gb(m.CapacityBytes), withType ? m.MemoryTypeName : null, m.Manufacturer, m.PartNumber }
            .Where(p => !string.IsNullOrWhiteSpace(p)));

    private static void CompareCpus(MachineDto o, MachineDto n, List<FieldChange> list)
    {
        var oldCpus = string.Join(" + ", o.Cpus.Select(c => c.Name).Where(x => x is not null));
        var newCpus = string.Join(" + ", n.Cpus.Select(c => c.Name).Where(x => x is not null));
        if (o.Cpus.Count > 0 && n.Cpus.Count > 0)
            Scalar(list, DiffCategory.Hardware, "CPU", oldCpus, newCpus, DiffSeverity.Notable);
    }

    private static void CompareStorage(MachineDto o, MachineDto n, List<FieldChange> list)
    {
        Keyed(list, DiffCategory.Storage, "Disk",
            o.Disks, n.Disks,
            d => MachineIdentity.NormalizeSerial(d.Serial) ?? $"{d.Model}/{d.SizeBytes}",
            d => $"{d.Model} {Gb(d.SizeBytes)}".Trim(),
            DiffSeverity.Notable,
            (od, nd) =>
            {
                var inner = new List<FieldChange>();
                if (od.SmartPredictFailure is not true && nd.SmartPredictFailure is true)
                    Add(inner, DiffCategory.Storage, $"Disk {nd.Model} SMART", "ok", "PREDICTING FAILURE", DiffSeverity.Regression);
                if (od.HealthStatus is not null && nd.HealthStatus is not null
                    && !SameText(od.HealthStatus, nd.HealthStatus))
                    Add(inner, DiffCategory.Storage, $"Disk {nd.Model} health", od.HealthStatus, nd.HealthStatus,
                        SameText(nd.HealthStatus, "Healthy") ? DiffSeverity.Info : DiffSeverity.Regression);
                return inner;
            });

        // Volumes: letters appearing/vanishing only — capacity and free space drift on every scan.
        Keyed(list, DiffCategory.Storage, "Volume",
            o.Volumes.Where(v => v.Letter is not null).ToList(), n.Volumes.Where(v => v.Letter is not null).ToList(),
            v => v.Letter!,
            v => $"{v.Letter} ({v.FileSystem}, {Gb(v.CapacityBytes)})",
            DiffSeverity.Info);
    }

    private static void CompareAdapters(MachineDto o, MachineDto n, List<FieldChange> list)
        => Keyed(list, DiffCategory.Network, "Adapter",
            o.Adapters.Where(a => a.Mac is not null).ToList(), n.Adapters.Where(a => a.Mac is not null).ToList(),
            a => MachineIdentity.NormalizeMac(a.Mac!),
            a => a.Name ?? a.Mac!,
            DiffSeverity.Info);

    // --- Software & updates ---

    private static void CompareSoftware(MachineDto o, MachineDto n, List<FieldChange> list)
        => Keyed(list, DiffCategory.Software, "Software",
            o.Software, n.Software,
            s => s.DisplayName,
            s => s.Version is null ? s.DisplayName : $"{s.DisplayName} {s.Version}",
            DiffSeverity.Info,
            (os, ns) =>
            {
                var inner = new List<FieldChange>();
                if (!SameText(os.Version, ns.Version))
                    Add(inner, DiffCategory.Software, os.DisplayName, os.Version ?? "?", ns.Version ?? "?", DiffSeverity.Info);
                return inner;
            });

    private static void CompareUpdates(MachineDto o, MachineDto n, List<FieldChange> list)
    {
        Keyed(list, DiffCategory.Hotfixes, "Hotfix",
            o.Hotfixes ?? Array.Empty<HotfixEntry>(), n.Hotfixes ?? Array.Empty<HotfixEntry>(),
            h => h.Id, h => h.Id, DiffSeverity.Info);

        if (o.Updates is { } ou && n.Updates is { } nu)
        {
            Scalar(list, DiffCategory.Os, "Feature release", ou.DisplayVersion, nu.DisplayVersion, DiffSeverity.Notable);
            Scalar(list, DiffCategory.Hotfixes, "Full build", ou.FullBuild, nu.FullBuild, DiffSeverity.Info);
            Bool(list, DiffCategory.Hotfixes, "Pending reboot", ou.PendingReboot, nu.PendingReboot, badWhenTrue: null);
            Bool(list, DiffCategory.Security, "Automatic updates disabled", ou.NoAutoUpdate, nu.NoAutoUpdate, badWhenTrue: true);
        }
    }

    // --- Security posture ---

    private static void CompareSecurity(MachineDto o, MachineDto n, List<FieldChange> list)
    {
        if (o.Security is not { } os || n.Security is not { } ns) return;

        Bool(list, DiffCategory.Security, "Antivirus (Defender)", os.DefenderEnabled, ns.DefenderEnabled, badWhenTrue: false);
        Bool(list, DiffCategory.Security, "Real-time protection", os.DefenderRealTime, ns.DefenderRealTime, badWhenTrue: false);
        Bool(list, DiffCategory.Security, "Tamper protection", os.DefenderTamperProtected, ns.DefenderTamperProtected, badWhenTrue: false);
        Bool(list, DiffCategory.Security, "Firewall (domain)", os.FirewallDomain, ns.FirewallDomain, badWhenTrue: false);
        Bool(list, DiffCategory.Security, "Firewall (private)", os.FirewallPrivate, ns.FirewallPrivate, badWhenTrue: false);
        Bool(list, DiffCategory.Security, "Firewall (public)", os.FirewallPublic, ns.FirewallPublic, badWhenTrue: false);
        Bool(list, DiffCategory.Security, "Secure Boot", os.SecureBoot, ns.SecureBoot, badWhenTrue: false);
        Bool(list, DiffCategory.Security, "SMB1", os.Smb1Enabled, ns.Smb1Enabled, badWhenTrue: true);
        Bool(list, DiffCategory.Security, "SMB signing required", os.SmbSigningRequired, ns.SmbSigningRequired, badWhenTrue: false, worsenedSeverity: DiffSeverity.Notable);
        Bool(list, DiffCategory.Security, "UAC", os.UacEnabled, ns.UacEnabled, badWhenTrue: false);
        Bool(list, DiffCategory.Security, "Remote token filtering off", os.LocalAccountTokenFilterPolicy, ns.LocalAccountTokenFilterPolicy, badWhenTrue: true, worsenedSeverity: DiffSeverity.Notable);
        Bool(list, DiffCategory.Security, "RDP", os.RdpEnabled, ns.RdpEnabled, badWhenTrue: true, worsenedSeverity: DiffSeverity.Notable);
        Bool(list, DiffCategory.Security, "RDP requires NLA", os.RdpNlaRequired, ns.RdpNlaRequired, badWhenTrue: false);
        Bool(list, DiffCategory.Security, "TPM present", os.TpmPresent, ns.TpmPresent, badWhenTrue: false, worsenedSeverity: DiffSeverity.Notable);
        Bool(list, DiffCategory.Security, "TPM enabled", os.TpmEnabled, ns.TpmEnabled, badWhenTrue: false, worsenedSeverity: DiffSeverity.Notable);
        Bool(list, DiffCategory.Security, "Credential Guard", os.CredentialGuardRunning, ns.CredentialGuardRunning, badWhenTrue: false, worsenedSeverity: DiffSeverity.Notable);
        Bool(list, DiffCategory.Security, "HVCI", os.HvciRunning, ns.HvciRunning, badWhenTrue: false, worsenedSeverity: DiffSeverity.Notable);
        Bool(list, DiffCategory.Security, "LAPS managed", os.LapsManaged, ns.LapsManaged, badWhenTrue: false);

        // BitLocker per volume; losing protection on any volume is the classic regression.
        var oldVols = os.BitLockerVolumes.Where(v => v.Letter is not null).ToDictionary(v => v.Letter!, StringComparer.OrdinalIgnoreCase);
        foreach (var nv in ns.BitLockerVolumes.Where(v => v.Letter is not null))
        {
            if (!oldVols.TryGetValue(nv.Letter!, out var ov)) continue;
            if (SameText(ov.Protection, nv.Protection)) continue;
            bool worsened = SameText(ov.Protection, "On") && !SameText(nv.Protection, "On");
            Add(list, DiffCategory.Security, $"BitLocker {nv.Letter}", ov.Protection, nv.Protection,
                worsened ? DiffSeverity.Regression : DiffSeverity.Info);
        }
    }

    // --- Users, services, tasks, peripherals ---

    private static void CompareAccounts(MachineDto o, MachineDto n, List<FieldChange> list)
    {
        var oldAdmins = new HashSet<string>(o.LocalAdministrators ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var newAdmins = new HashSet<string>(n.LocalAdministrators ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var added in newAdmins.Except(oldAdmins))
            Add(list, DiffCategory.Accounts, "Local admin", null, added, DiffSeverity.Notable);
        foreach (var removed in oldAdmins.Except(newAdmins))
            Add(list, DiffCategory.Accounts, "Local admin", removed, null, DiffSeverity.Info);

        Keyed(list, DiffCategory.Accounts, "Local account",
            o.LocalAccounts ?? Array.Empty<LocalAccountEntry>(), n.LocalAccounts ?? Array.Empty<LocalAccountEntry>(),
            a => a.Name, a => a.Name, DiffSeverity.Info,
            (oa, na) =>
            {
                var inner = new List<FieldChange>();
                if (oa.Disabled != na.Disabled)
                    Add(inner, DiffCategory.Accounts, $"Account {na.Name}", oa.Disabled ? "disabled" : "enabled",
                        na.Disabled ? "disabled" : "enabled", DiffSeverity.Info);
                if (!oa.IsAdmin && na.IsAdmin)
                    Add(inner, DiffCategory.Accounts, $"Account {na.Name}", "standard", "admin", DiffSeverity.Notable);
                if (oa.PasswordRequired && !na.PasswordRequired)
                    Add(inner, DiffCategory.Accounts, $"Account {na.Name} password required", "yes", "no", DiffSeverity.Notable);
                return inner;
            });
    }

    private static void CompareServices(MachineDto o, MachineDto n, List<FieldChange> list)
    {
        Keyed(list, DiffCategory.Services, "Service",
            o.Services ?? Array.Empty<ServiceEntry>(), n.Services ?? Array.Empty<ServiceEntry>(),
            s => s.Name, s => s.DisplayName ?? s.Name, DiffSeverity.Info,
            (osvc, nsvc) =>
            {
                var inner = new List<FieldChange>();
                // State (running/stopped) is transient; StartMode and the run-as account are configuration.
                if (!SameText(osvc.StartMode, nsvc.StartMode))
                    Add(inner, DiffCategory.Services, $"Service {nsvc.DisplayName ?? nsvc.Name} start mode",
                        osvc.StartMode, nsvc.StartMode, DiffSeverity.Info);
                if (!SameText(osvc.Account, nsvc.Account))
                    Add(inner, DiffCategory.Services, $"Service {nsvc.DisplayName ?? nsvc.Name} account",
                        osvc.Account, nsvc.Account, DiffSeverity.Notable);
                return inner;
            });

        // New startup entries are the persistence trick to notice.
        Keyed(list, DiffCategory.Services, "Startup item",
            o.StartupItems ?? Array.Empty<StartupEntry>(), n.StartupItems ?? Array.Empty<StartupEntry>(),
            s => $"{s.Name}|{s.Location}", s => s.Name ?? s.Command ?? "?",
            DiffSeverity.Info, addedSeverity: DiffSeverity.Notable);
    }

    private static void CompareScheduledTasks(MachineDto o, MachineDto n, List<FieldChange> list)
        => Keyed(list, DiffCategory.Services, "Scheduled task",
            o.ScheduledTasks ?? Array.Empty<ScheduledTaskEntry>(), n.ScheduledTasks ?? Array.Empty<ScheduledTaskEntry>(),
            t => t.Path ?? t.Name ?? "?", t => t.Name ?? t.Path ?? "?",
            DiffSeverity.Info, addedSeverity: DiffSeverity.Notable);

    private static void ComparePeripherals(MachineDto o, MachineDto n, List<FieldChange> list)
    {
        // Monitors carry serials — a vanished monitor is the classic "where did it go" question.
        Keyed(list, DiffCategory.Peripherals, "Monitor",
            (o.Monitors ?? Array.Empty<MonitorEntry>()).Where(m => m.Serial is not null).ToList(),
            (n.Monitors ?? Array.Empty<MonitorEntry>()).Where(m => m.Serial is not null).ToList(),
            m => m.Serial!,
            m => $"{m.Manufacturer} {m.Model} ({m.Serial})".Trim(),
            DiffSeverity.Notable);

        Keyed(list, DiffCategory.Peripherals, "GPU",
            o.Gpus ?? Array.Empty<GpuInfo>(), n.Gpus ?? Array.Empty<GpuInfo>(),
            g => g.Name ?? "?", g => g.Name ?? "?", DiffSeverity.Info,
            (og, ng) =>
            {
                var inner = new List<FieldChange>();
                if (!SameText(og.DriverVersion, ng.DriverVersion))
                    Add(inner, DiffCategory.Peripherals, $"GPU {ng.Name} driver", og.DriverVersion, ng.DriverVersion, DiffSeverity.Info);
                return inner;
            });

        Keyed(list, DiffCategory.Peripherals, "Printer",
            o.Printers ?? Array.Empty<PrinterEntry>(), n.Printers ?? Array.Empty<PrinterEntry>(),
            p => p.Name ?? "?", p => p.Name ?? "?", DiffSeverity.Info);
        // UsbDevices deliberately skipped: what's plugged in at scan time is transient by definition.
    }

    // --- helpers ---

    private static void Add(List<FieldChange> list, DiffCategory cat, string item, string? oldValue, string? newValue, DiffSeverity sev)
        => list.Add(new FieldChange(cat, item,
            oldValue is null ? DiffChangeKind.Added : newValue is null ? DiffChangeKind.Removed : DiffChangeKind.Changed,
            oldValue, newValue, sev));

    private static void Scalar(List<FieldChange> list, DiffCategory cat, string item, string? oldValue, string? newValue, DiffSeverity sev)
    {
        // Null on either side means "not collected then" — never a change.
        if (oldValue is null || newValue is null || SameText(oldValue, newValue)) return;
        Add(list, cat, item, oldValue, newValue, sev);
    }

    /// <summary>Tri-state posture compare: null never participates; the bad direction gets
    /// <paramref name="worsenedSeverity"/> (Regression by default), the good direction plain Info.
    /// <paramref name="badWhenTrue"/> null = no direction (both ways Info).</summary>
    private static void Bool(List<FieldChange> list, DiffCategory cat, string item, bool? oldValue, bool? newValue,
        bool? badWhenTrue, DiffSeverity worsenedSeverity = DiffSeverity.Regression)
    {
        if (oldValue is not { } ov || newValue is not { } nv || ov == nv) return;
        bool worsened = badWhenTrue is { } bad && nv == bad;
        Add(list, cat, item, ov ? "on" : "off", nv ? "on" : "off",
            worsened ? worsenedSeverity : DiffSeverity.Info);
    }

    /// <summary>Set comparison by key: entries only in the newer document report Added, only in the older report
    /// Removed, and pairs run <paramref name="compareMatched"/> for inner field changes.</summary>
    private static void Keyed<T>(List<FieldChange> list, DiffCategory cat, string kind,
        IReadOnlyList<T> older, IReadOnlyList<T> newer,
        Func<T, string> key, Func<T, string> display, DiffSeverity severity,
        Func<T, T, List<FieldChange>>? compareMatched = null, DiffSeverity? addedSeverity = null)
    {
        var oldByKey = ToMap(older, key);
        var newByKey = ToMap(newer, key);

        foreach (var (k, item) in newByKey)
        {
            if (oldByKey.TryGetValue(k, out var old))
            {
                if (compareMatched is not null) list.AddRange(compareMatched(old, item));
            }
            else
            {
                list.Add(new FieldChange(cat, kind, DiffChangeKind.Added, null, display(item), addedSeverity ?? severity));
            }
        }
        foreach (var (k, item) in oldByKey)
            if (!newByKey.ContainsKey(k))
                list.Add(new FieldChange(cat, kind, DiffChangeKind.Removed, display(item), null, severity));
    }

    private static Dictionary<string, T> ToMap<T>(IReadOnlyList<T> items, Func<T, string> key)
    {
        // Duplicate keys (two installs of the same DisplayName) keep the first; good enough for set membership.
        var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
            map.TryAdd(key(item), item);
        return map;
    }

    private static bool SameText(string? a, string? b)
        => string.Equals(a?.Trim() ?? "", b?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    private static string Gb(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";
}
