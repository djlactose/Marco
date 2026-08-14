using System.Collections.Frozen;
using System.IO.Compression;
using System.Reflection;
using Marco.Core.Scanning;

namespace Marco.Discovery;

/// <summary>
/// MAC OUI → vendor/category lookup, keyed by the 24-bit prefix. Two layered sources: a small CURATED table
/// (<c>oui.tsv</c>) that carries the device-type <see cref="OuiCategory"/> driving <see cref="DeviceClassifier"/>,
/// overlaid on the FULL IEEE registry (<c>oui-vendors.tsv.gz</c>, ~40k prefixes) that supplies vendor names for
/// everything else. Both load once into <see cref="FrozenDictionary{TKey,TValue}"/>. Refresh via build/refresh-oui.ps1.
/// </summary>
public sealed class OuiLookup : IOuiLookup
{
    private readonly FrozenDictionary<uint, OuiEntry> _curated;
    private readonly FrozenDictionary<uint, string> _vendors;

    public int Count => _curated.Count + _vendors.Count;

    private OuiLookup(FrozenDictionary<uint, OuiEntry> curated, FrozenDictionary<uint, string> vendors)
    {
        _curated = curated;
        _vendors = vendors;
    }

    public static OuiLookup LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        var curatedTsv = ReadEmbeddedText(asm, "oui.tsv")
            ?? throw new InvalidOperationException("Embedded oui.tsv resource not found.");
        var vendors = LoadVendors(asm);
        return LoadFrom(curatedTsv, vendors);
    }

    public static OuiLookup LoadFrom(string curatedTsv, IReadOnlyDictionary<uint, string>? vendors = null)
    {
        var curated = new Dictionary<uint, OuiEntry>();
        foreach (var line in curatedTsv.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var parts = trimmed.Split('\t');
            if (parts.Length < 3) continue;
            if (!TryParsePrefix(parts[0], out uint prefix)) continue;
            if (!Enum.TryParse<OuiCategory>(parts[2].Trim(), ignoreCase: true, out var category))
                category = OuiCategory.None;
            curated[prefix] = new OuiEntry(parts[1].Trim(), category);
        }

        var vendorFrozen = (vendors ?? new Dictionary<uint, string>())
            .ToFrozenDictionary(kv => kv.Key, kv => kv.Value);
        return new OuiLookup(curated.ToFrozenDictionary(), vendorFrozen);
    }

    private static Dictionary<uint, string> LoadVendors(Assembly asm)
    {
        var dict = new Dictionary<uint, string>();
        using var stream = FindResource(asm, "oui-vendors.tsv.gz");
        if (stream is null) return dict; // vendor table optional; curated still works
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            int tab = line.IndexOf('\t');
            if (tab != 6) continue;
            if (!TryParsePrefix(line[..6], out uint prefix)) continue;
            var vendor = line[(tab + 1)..].Trim();
            if (vendor.Length > 0) dict[prefix] = vendor;
        }
        return dict;
    }

    private static Stream? FindResource(Assembly asm, string suffix)
    {
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        return name is null ? null : asm.GetManifestResourceStream(name);
    }

    private static string? ReadEmbeddedText(Assembly asm, string suffix)
    {
        using var stream = FindResource(asm, suffix);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public OuiEntry? Lookup(string? mac)
    {
        if (!TryPrefixFromMac(mac, out uint prefix)) return null;
        // Curated wins (it carries the device category); otherwise fall back to the full vendor registry.
        if (_curated.TryGetValue(prefix, out var entry)) return entry;
        if (_vendors.TryGetValue(prefix, out var vendor)) return new OuiEntry(vendor, OuiCategory.None);
        return null;
    }

    /// <summary>Parse a 6-hex-digit OUI prefix (any separators tolerated) into a 24-bit uint.</summary>
    private static bool TryParsePrefix(string s, out uint prefix)
    {
        prefix = 0;
        Span<char> hex = stackalloc char[6];
        int n = 0;
        foreach (var c in s)
        {
            if (Uri.IsHexDigit(c))
            {
                if (n == 6) return false;
                hex[n++] = c;
            }
        }
        if (n != 6) return false;
        prefix = Convert.ToUInt32(new string(hex), 16);
        return true;
    }

    /// <summary>Extract the leading 24-bit OUI from a full MAC in any common format.</summary>
    private static bool TryPrefixFromMac(string? mac, out uint prefix)
    {
        prefix = 0;
        if (string.IsNullOrWhiteSpace(mac)) return false;
        Span<char> hex = stackalloc char[6];
        int n = 0;
        foreach (var c in mac)
        {
            if (Uri.IsHexDigit(c))
            {
                if (n < 6) hex[n++] = c;
                else break;
            }
        }
        if (n != 6) return false;
        prefix = Convert.ToUInt32(new string(hex), 16);
        return true;
    }
}
