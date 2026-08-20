using Marco.Core.Inventory;
using Marco.Core.Model;

namespace Marco.Inventory.Collectors;

/// <summary>
/// Installed software from the registry uninstall keys — NEVER Win32_Product, which triggers an MSI consistency
/// check that can reconfigure every product on the target. Reads the 64-bit and 32-bit HKLM hives plus each
/// currently-loaded user hive under HKU, then hands the raw keys to <see cref="SoftwareParser"/> for the
/// Programs-and-Features filtering, dedup, and install-date resolution.
/// </summary>
public sealed class SoftwareCollector : IInventoryCollector
{
    public string Name => "InstalledSoftware";

    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallPathWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct)
    {
        var reg = context.Registry;
        var raw = new List<SoftwareParser.RawEntry>();

        foreach (var key in reg.EnumerateSubkeys(RegistryRoot.LocalMachine, UninstallPath, SoftwareParser.ValueNames))
            raw.Add(new SoftwareParser.RawEntry(key, SoftwareSource.Native64));

        ct.ThrowIfCancellationRequested();
        foreach (var key in reg.EnumerateSubkeys(RegistryRoot.LocalMachine, UninstallPathWow, SoftwareParser.ValueNames))
            raw.Add(new SoftwareParser.RawEntry(key, SoftwareSource.Wow6432));

        // Per-user hives: only currently-logged-in users are mounted under HKU.
        ct.ThrowIfCancellationRequested();
        foreach (var sid in reg.GetSubKeyNames(RegistryRoot.Users, ""))
        {
            if (!sid.StartsWith("S-1-5-21", StringComparison.OrdinalIgnoreCase)) continue; // real users only
            if (sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)) continue;

            var userPath = $"{sid}\\{UninstallPath}";
            foreach (var key in reg.EnumerateSubkeys(RegistryRoot.Users, userPath, SoftwareParser.ValueNames))
                raw.Add(new SoftwareParser.RawEntry(key, SoftwareSource.PerUser));
        }

        machine.Software = SoftwareParser.Parse(raw, reg.SupportsLastWriteTime).ToList();
        machine.RefreshCounts();
        return Task.CompletedTask;
    }
}
