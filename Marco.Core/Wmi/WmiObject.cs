using System.Globalization;

namespace Marco.Core.Wmi;

/// <summary>
/// A read-only property bag representing one WMI/CIM instance, with typed accessors that tolerate the messy
/// reality of WMI values (nulls, boxed numerics of varying width, CIM_DATETIME strings). Collectors consume
/// this rather than <c>System.Management</c> types, so they can be unit-tested against canned objects.
/// </summary>
public sealed class WmiObject
{
    private readonly IReadOnlyDictionary<string, object?> _props;

    public WmiObject(IReadOnlyDictionary<string, object?> props) => _props = props;

    public object? this[string name] => _props.TryGetValue(name, out var v) ? v : null;

    public bool Has(string name) => _props.ContainsKey(name) && _props[name] is not null;

    public string? GetString(string name)
    {
        var v = this[name];
        if (v is null) return null;
        if (v is string s) return s.Length == 0 ? null : s;
        if (v is string[] arr) return arr.Length > 0 ? string.Join(", ", arr) : null;
        return v.ToString();
    }

    public int? GetInt(string name) => TryConvert(this[name], out long l) ? (int)l : null;
    public long? GetLong(string name) => TryConvert(this[name], out long l) ? l : null;

    public ulong? GetULong(string name)
    {
        var v = this[name];
        if (v is null) return null;
        try
        {
            if (v is string s) return ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;
            return Convert.ToUInt64(v, CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    public bool? GetBool(string name)
    {
        var v = this[name];
        if (v is null) return null;
        if (v is bool b) return b;
        if (v is string s) return bool.TryParse(s, out var p) ? p : null;
        return TryConvert(v, out long l) ? l != 0 : null;
    }

    public string[]? GetStringArray(string name)
    {
        var v = this[name];
        return v switch
        {
            string[] arr => arr,
            string s => new[] { s },
            null => null,
            _ => new[] { v.ToString()! },
        };
    }

    /// <summary>Parse a CIM_DATETIME (<c>yyyyMMddHHmmss.ffffff±UUU</c>) or a plain date string.</summary>
    public DateTime? GetDateTime(string name)
    {
        var v = this[name];
        if (v is DateTime dt) return dt;
        var s = v as string;
        if (string.IsNullOrWhiteSpace(s)) return null;
        return ParseCimDateTime(s);
    }

    public static DateTime? ParseCimDateTime(string s)
    {
        // CIM_DATETIME: 20240115143005.000000+000  — first 14 chars are yyyyMMddHHmmss.
        if (s.Length >= 14 && long.TryParse(s.AsSpan(0, 14), out _))
        {
            if (DateTime.TryParseExact(s.AsSpan(0, 14), "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var cim))
                return cim;
        }
        // Fallback: some sources give yyyyMMdd (registry InstallDate).
        if (s.Length == 8 && DateTime.TryParseExact(s, "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var ymd))
            return ymd;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var any))
            return any;
        return null;
    }

    private static bool TryConvert(object? v, out long result)
    {
        result = 0;
        if (v is null) return false;
        try
        {
            if (v is string s)
                return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
            result = Convert.ToInt64(v, CultureInfo.InvariantCulture);
            return true;
        }
        catch { return false; }
    }
}
