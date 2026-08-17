using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;

namespace Marco.Inventory.Collectors;

/// <summary>
/// Windows Update / servicing state: installed hotfixes (Win32_QuickFixEngineering), the feature release and
/// full build from the registry (Win32_OperatingSystem.Version stops at the build number — the DisplayVersion
/// and UBR are what "is it patched" needs), the pending-reboot flags the servicing stack leaves behind, and the
/// WSUS / Automatic Updates policy. Each source is independent: a Remote Registry that is off costs the registry
/// facts, not the hotfix list.
/// </summary>
public sealed class UpdatesCollector : IInventoryCollector
{
    public string Name => "Updates";

    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string CbsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing";
    private const string AutoUpdateKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update";
    private const string SessionManagerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager";
    private const string ActiveComputerNameKey = @"SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName";
    private const string ComputerNameKey = @"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName";
    private const string NetlogonKey = @"SYSTEM\CurrentControlSet\Services\Netlogon";
    private const string WuPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string AuPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";

    public async Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct)
    {
        var steps = new CollectorSteps();
        var u = machine.Updates;

        await steps.RunAsync("Hotfixes", async () =>
        {
            var rows = await context.Wmi.QueryAsync(WmiQueryHelpers.CimV2,
                "SELECT HotFixID, Description, InstalledOn, InstalledBy FROM Win32_QuickFixEngineering", ct);
            machine.Hotfixes.Clear();
            foreach (var r in rows)
            {
                var id = r.GetString("HotFixID")?.Trim();
                if (string.IsNullOrEmpty(id)) continue;
                machine.Hotfixes.Add(new HotfixEntry
                {
                    Id = id,
                    Description = r.GetString("Description")?.Trim(),
                    InstalledOn = ParseInstalledOn(r["InstalledOn"]),
                    InstalledBy = r.GetString("InstalledBy")?.Trim(),
                });
            }
            machine.Hotfixes.Sort((a, b) => Nullable.Compare(b.InstalledOn, a.InstalledOn));
            u.HotfixCount = machine.Hotfixes.Count;
            u.LastHotfixDate = machine.Hotfixes.Max(h => h.InstalledOn);
        });

        ct.ThrowIfCancellationRequested();
        steps.Run("Version (registry)", () =>
        {
            var v = context.Registry.GetValues(RegistryRoot.LocalMachine, CurrentVersionKey,
                new[] { "DisplayVersion", "ReleaseId", "UBR", "EditionID", "InstallationType", "ProductName", "CurrentBuild", "CurrentBuildNumber" });
            if (v.Count == 0) throw new InvalidOperationException("CurrentVersion key not readable.");
            u.DisplayVersion = RegistryValues.AsString(RegistryValues.Get(v, "DisplayVersion"))
                            ?? RegistryValues.AsString(RegistryValues.Get(v, "ReleaseId"));
            u.Ubr = RegistryValues.AsInt(RegistryValues.Get(v, "UBR"));
            u.EditionId = RegistryValues.AsString(RegistryValues.Get(v, "EditionID"));
            u.InstallationType = RegistryValues.AsString(RegistryValues.Get(v, "InstallationType"));
            u.ProductName = RegistryValues.AsString(RegistryValues.Get(v, "ProductName"));

            var build = RegistryValues.AsString(RegistryValues.Get(v, "CurrentBuild"))
                     ?? RegistryValues.AsString(RegistryValues.Get(v, "CurrentBuildNumber"));
            u.FullBuild = ComposeFullBuild(machine.Os.Version, build, u.Ubr);
        });

        ct.ThrowIfCancellationRequested();
        steps.Run("Pending reboot (registry)", () =>
        {
            var (pending, reasons) = ProbePendingReboot(context.Registry);
            u.PendingReboot = pending;
            u.PendingRebootReasons = reasons.Count == 0 ? null : string.Join(", ", reasons);
            if (pending is null) throw new InvalidOperationException("Registry not readable.");
        });

        ct.ThrowIfCancellationRequested();
        steps.Run("Update policy (registry)", () =>
        {
            var wu = context.Registry.GetValues(RegistryRoot.LocalMachine, WuPolicyKey,
                new[] { "WUServer", "WUStatusServer", "TargetGroup", "TargetGroupEnabled" });
            var au = context.Registry.GetValues(RegistryRoot.LocalMachine, AuPolicyKey,
                new[] { "UseWUServer", "NoAutoUpdate", "AUOptions" });
            u.WsusServer = RegistryValues.AsString(RegistryValues.Get(wu, "WUServer"));
            var group = RegistryValues.AsString(RegistryValues.Get(wu, "TargetGroup"));
            var groupEnabled = RegistryValues.AsBool(RegistryValues.Get(wu, "TargetGroupEnabled"));
            u.WsusTargetGroup = groupEnabled == false ? null : group;
            u.UseWsus = RegistryValues.AsBool(RegistryValues.Get(au, "UseWUServer"));
            u.NoAutoUpdate = RegistryValues.AsBool(RegistryValues.Get(au, "NoAutoUpdate"));
            u.AutoUpdateOption = RegistryValues.AsInt(RegistryValues.Get(au, "AUOptions")) is { } o ? DescribeAuOption(o) : null;
        });

        u.Notes = steps.Notes;
        machine.RefreshCounts();
        steps.ThrowIfNothingSucceeded();
    }

    /// <summary>InstalledOn comes back as "9/12/2024" (US short date, whatever the target's locale) on modern
    /// systems, as a CIM datetime on some, and as a hex FILETIME string on a few old ones.</summary>
    public static DateTime? ParseInstalledOn(object? raw)
    {
        if (raw is DateTime dt) return dt.Date;
        var s = (raw as string)?.Trim();
        if (string.IsNullOrEmpty(s)) return null;

        if (DateTime.TryParseExact(s, new[] { "M/d/yyyy", "MM/dd/yyyy", "yyyyMMdd", "yyyy-MM-dd", "dd/MM/yyyy" },
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var exact))
            return exact.Date;

        // Hex FILETIME (seen on some Windows 7-era images): 16 hex digits.
        if (s.Length == 16 && long.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var ft) && ft > 0)
        {
            try { return DateTime.FromFileTimeUtc(ft).ToLocalTime().Date; } catch { /* out of range */ }
        }

        return WmiObject.ParseCimDateTime(s)?.Date;
    }

    /// <summary>"10.0.22631" + UBR 4037 → "10.0.22631.4037". Falls back to the registry build when the OS
    /// collector didn't run.</summary>
    public static string? ComposeFullBuild(string? osVersion, string? registryBuild, int? ubr)
    {
        var version = osVersion;
        if (string.IsNullOrWhiteSpace(version) && !string.IsNullOrWhiteSpace(registryBuild))
            version = registryBuild.Contains('.') ? registryBuild : $"10.0.{registryBuild}";
        if (string.IsNullOrWhiteSpace(version)) return null;
        return ubr is { } n ? $"{version}.{n}" : version;
    }

    /// <summary>The servicing-stack breadcrumbs that mean "a restart is owed". Returns null when not one of the
    /// checks could be evaluated (registry unavailable), so the caller can distinguish "no" from "don't know".</summary>
    public static (bool? Pending, List<string> Reasons) ProbePendingReboot(IRemoteRegistry reg)
    {
        var reasons = new List<string>();
        int evaluated = 0;

        void Check(string label, Func<bool> probe)
        {
            try
            {
                if (probe()) reasons.Add(label);
                evaluated++;
            }
            catch { /* this check unavailable — the others still count */ }
        }

        Check("Component Based Servicing", () => HasSubKey(reg, CbsKey, "RebootPending"));
        Check("Windows Update", () => HasSubKey(reg, AutoUpdateKey, "RebootRequired"));
        Check("Pending file renames", () =>
            RegistryValues.AsStrings(RegistryValues.Get(
                reg.GetValues(RegistryRoot.LocalMachine, SessionManagerKey, new[] { "PendingFileRenameOperations" }),
                "PendingFileRenameOperations")).Count > 0);
        Check("Computer rename", () =>
        {
            var active = RegistryValues.AsString(RegistryValues.Get(
                reg.GetValues(RegistryRoot.LocalMachine, ActiveComputerNameKey, new[] { "ComputerName" }), "ComputerName"));
            var configured = RegistryValues.AsString(RegistryValues.Get(
                reg.GetValues(RegistryRoot.LocalMachine, ComputerNameKey, new[] { "ComputerName" }), "ComputerName"));
            return active is not null && configured is not null
                && !string.Equals(active, configured, StringComparison.OrdinalIgnoreCase);
        });
        Check("Domain join", () =>
        {
            var v = reg.GetValues(RegistryRoot.LocalMachine, NetlogonKey, new[] { "JoinDomain", "AvoidSpnSet" });
            return v.ContainsKey("JoinDomain") || v.ContainsKey("AvoidSpnSet");
        });

        if (evaluated == 0) return (null, reasons);
        return (reasons.Count > 0, reasons);
    }

    private static bool HasSubKey(IRemoteRegistry reg, string path, string subKey)
        => reg.GetSubKeyNames(RegistryRoot.LocalMachine, path)
              .Any(n => string.Equals(n, subKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>Automatic Updates policy value (AUOptions) → operator text.</summary>
    public static string DescribeAuOption(int option) => option switch
    {
        1 => "Never check for updates",
        2 => "Notify before download",
        3 => "Auto download, notify to install",
        4 => "Auto download and schedule install",
        5 => "Local admin chooses",
        7 => "Auto download, notify to install and restart",
        _ => $"Option {option}",
    };
}
