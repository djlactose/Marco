using Marco.Core.Inventory;
using Marco.Core.Wmi;

namespace Marco.Inventory.Registry;

/// <summary>
/// Remote registry reader over WMI's <c>StdRegProv</c> provider — the fallback path. Unlike the SMB/OpenRemoteBaseKey
/// path it needs neither the Remote Registry service nor SMB (445): it rides the same DCOM/WMI channel that the rest
/// of inventory uses, so it works wherever WMI works (e.g. across a VPN, or when Remote Registry is disabled). It is
/// slower (one WMI call per value) and can't report key last-write times.
/// </summary>
public sealed class StdRegProvRegistry : IRemoteRegistry
{
    private const string Ns = "root\\default";
    private const uint HKLM = 0x80000002, HKU = 0x80000003;

    private readonly IWmiSession _wmi;

    public bool SupportsLastWriteTime => false;

    public StdRegProvRegistry(IWmiSession wmi) => _wmi = wmi;

    private static uint Hive(RegistryRoot root) => root == RegistryRoot.Users ? HKU : HKLM;

    public IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string path)
    {
        var res = Invoke("EnumKey", new Dictionary<string, object?>
        {
            ["hDefKey"] = Hive(root),
            ["sSubKeyName"] = path,
        });
        if (res is null || res.GetInt("ReturnValue") is not 0) return Array.Empty<string>();
        return res.GetStringArray("sNames") ?? Array.Empty<string>();
    }

    public IReadOnlyList<RegistryKeyData> EnumerateSubkeys(RegistryRoot root, string path, IReadOnlyCollection<string> valueNames)
    {
        var subs = GetSubKeyNames(root, path);
        var list = new List<RegistryKeyData>(subs.Count);
        foreach (var sub in subs)
        {
            var full = path.Length == 0 ? sub : $"{path}\\{sub}";
            var values = ReadValues(root, full, valueNames);
            list.Add(new RegistryKeyData(sub, values, null)); // no last-write time via StdRegProv
        }
        return list;
    }

    public IReadOnlyDictionary<string, object?> GetValues(RegistryRoot root, string path, IReadOnlyCollection<string> valueNames)
        => ReadValues(root, path, valueNames);

    private Dictionary<string, object?> ReadValues(RegistryRoot root, string path, IReadOnlyCollection<string> valueNames)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        uint hive = Hive(root);
        foreach (var vn in valueNames)
        {
            // Most values are strings; try that first (one call), then DWORD for the numeric flags.
            var str = Invoke("GetStringValue", new Dictionary<string, object?>
            {
                ["hDefKey"] = hive, ["sSubKeyName"] = path, ["sValueName"] = vn,
            });
            if (str is not null && str.GetInt("ReturnValue") is 0 && str.GetString("sValue") is { } sv)
            {
                dict[vn] = sv;
                continue;
            }

            var dword = Invoke("GetDWORDValue", new Dictionary<string, object?>
            {
                ["hDefKey"] = hive, ["sSubKeyName"] = path, ["sValueName"] = vn,
            });
            if (dword is not null && dword.GetInt("ReturnValue") is 0 && dword.GetLong("uValue") is { } dv)
                dict[vn] = dv;
        }
        return dict;
    }

    private WmiObject? Invoke(string method, IReadOnlyDictionary<string, object?> args)
    {
        // Synchronous block is fine: the whole inventory runs on a worker thread.
        return _wmi.InvokeAsync(Ns, "StdRegProv", method, args, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose() { }
}
