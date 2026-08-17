namespace Marco.Core.Inventory;

/// <summary>
/// Coercions for raw registry values as the two access paths hand them back: OpenRemoteBaseKey returns the
/// native CLR types (int for DWORD, long for QWORD, string[] for MULTI_SZ, byte[] for BINARY) while the
/// StdRegProv fallback returns strings and DWORDs as long. Collectors go through these so a flag reads the same
/// either way.
/// </summary>
public static class RegistryValues
{
    public static int? AsInt(object? v) => v switch
    {
        null => null,
        int i => i,
        uint u => unchecked((int)u),
        long l => l is >= int.MinValue and <= int.MaxValue ? (int)l : null,
        ulong ul => ul <= int.MaxValue ? (int)ul : null,
        short s => s,
        ushort us => us,
        byte b => b,
        string s when int.TryParse(s.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
        byte[] { Length: >= 4 } bytes => BitConverter.ToInt32(bytes, 0),
        _ => null,
    };

    public static long? AsLong(object? v) => v switch
    {
        null => null,
        long l => l,
        ulong ul => ul <= long.MaxValue ? (long)ul : null,
        int i => i,
        uint u => u,
        string s when long.TryParse(s.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
        byte[] { Length: >= 8 } bytes => BitConverter.ToInt64(bytes, 0),
        byte[] { Length: >= 4 } bytes => BitConverter.ToInt32(bytes, 0),
        _ => null,
    };

    /// <summary>DWORD flag → bool; null when absent/unreadable.</summary>
    public static bool? AsBool(object? v) => AsInt(v) is { } i ? i != 0 : null;

    /// <summary>String value; MULTI_SZ joined with ", "; empty/whitespace → null.</summary>
    public static string? AsString(object? v)
    {
        var s = v switch
        {
            null => null,
            string str => str,
            string[] arr => string.Join(", ", arr.Where(a => !string.IsNullOrWhiteSpace(a))),
            _ => v.ToString(),
        };
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>MULTI_SZ as a list (a plain string counts as one entry).</summary>
    public static IReadOnlyList<string> AsStrings(object? v) => v switch
    {
        string[] arr => arr.Where(a => !string.IsNullOrWhiteSpace(a)).ToList(),
        string s when !string.IsNullOrWhiteSpace(s) => new[] { s },
        _ => Array.Empty<string>(),
    };

    public static object? Get(IReadOnlyDictionary<string, object?> values, string name)
        => values.TryGetValue(name, out var v) ? v : null;
}
