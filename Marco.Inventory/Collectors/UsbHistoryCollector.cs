using System.Text.RegularExpressions;
using Marco.Core.Inventory;
using Marco.Core.Model;

namespace Marco.Inventory.Collectors;

/// <summary>
/// USB storage devices that have ever been plugged in, from HKLM\SYSTEM\CurrentControlSet\Enum\USBSTOR: one key
/// per device model ("Disk&amp;Ven_SanDisk&amp;Prod_Cruzer&amp;Rev_1.00") with one subkey per unit (the serial).
/// Registry-only, read-only, off by default in the checklist — it is an audit view rather than an asset one.
/// </summary>
public sealed class UsbHistoryCollector : IInventoryCollector
{
    public string Name => "UsbHistory";

    private const string UsbStorKey = @"SYSTEM\CurrentControlSet\Enum\USBSTOR";
    private static readonly Regex DeviceKey = new(@"^(?<type>[^&]+)&Ven_(?<ven>[^&]*)&Prod_(?<prod>[^&]*)(&Rev_(?<rev>[^&]*))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct)
    {
        var reg = context.Registry;
        var list = new List<UsbStorageHistoryEntry>();
        foreach (var deviceKey in reg.GetSubKeyNames(RegistryRoot.LocalMachine, UsbStorKey))
        {
            ct.ThrowIfCancellationRequested();
            var (vendor, product) = ParseDeviceKey(deviceKey);
            foreach (var unit in reg.EnumerateSubkeys(RegistryRoot.LocalMachine, $"{UsbStorKey}\\{deviceKey}", new[] { "FriendlyName", "DeviceDesc" }))
            {
                list.Add(new UsbStorageHistoryEntry
                {
                    FriendlyName = RegistryValues.AsString(RegistryValues.Get(unit.Values, "FriendlyName"))
                                   ?? StripDeviceDesc(RegistryValues.AsString(RegistryValues.Get(unit.Values, "DeviceDesc")))
                                   ?? $"{vendor} {product}".Trim(),
                    Vendor = vendor,
                    Product = product,
                    Serial = ParseSerial(unit.SubKeyName),
                });
            }
        }
        list.Sort((a, b) => string.Compare(a.FriendlyName, b.FriendlyName, StringComparison.OrdinalIgnoreCase));
        machine.UsbStorageHistory = list;
        return Task.CompletedTask;
    }

    /// <summary>"Disk&amp;Ven_SanDisk&amp;Prod_Cruzer_Glide&amp;Rev_1.00" → ("SanDisk", "Cruzer Glide").</summary>
    public static (string? Vendor, string? Product) ParseDeviceKey(string key)
    {
        var m = DeviceKey.Match(key);
        if (!m.Success) return (null, null);
        static string? Clean(string s) { s = s.Replace('_', ' ').Trim(); return s.Length == 0 ? null : s; }
        return (Clean(m.Groups["ven"].Value), Clean(m.Groups["prod"].Value));
    }

    /// <summary>The unit subkey is "&lt;serial&gt;&amp;&lt;instance&gt;" for devices with a serial, or a generated
    /// "7&amp;1a2b3c&amp;0" for those without (no leading serial). Returns null for generated IDs.</summary>
    public static string? ParseSerial(string unitKey)
    {
        var serial = unitKey.Split('&')[0].Trim();
        if (serial.Length == 0) return null;
        // Generated instance IDs are a bare short number ("7") — real serials are longer.
        return serial.Length <= 2 && serial.All(char.IsDigit) ? null : serial;
    }

    /// <summary>"@disk.inf,%genmanufacturer%;Disk drive" → "Disk drive".</summary>
    public static string? StripDeviceDesc(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return null;
        var i = desc.LastIndexOf(';');
        var s = (i >= 0 ? desc[(i + 1)..] : desc).Trim();
        return s.Length == 0 ? null : s;
    }
}
