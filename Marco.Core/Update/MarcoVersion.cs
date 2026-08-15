namespace Marco.Core.Update;

/// <summary>
/// Marco's comparable version: a stable core (e.g. 1.2.0) with an optional beta number (1.2.0-beta.4, produced by
/// CI builds of the develop branch). Ordering: cores compare first; with equal cores a stable outranks any beta;
/// betas compare by number. Only the "-beta.N" suffix is recognized — anything else (rc, alpha, arbitrary text)
/// is rejected so the updater can never install a build it doesn't understand.
/// </summary>
public sealed record MarcoVersion(Version Core, int? Beta) : IComparable<MarcoVersion>
{
    public bool IsBeta => Beta is not null;

    /// <summary>Parses "1.2.3", "v1.2.3", "1.2.3-beta.4" (leading v optional, case-insensitive).</summary>
    public static bool TryParse(string? text, out MarcoVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];

        int? beta = null;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            var suffix = s[(dash + 1)..];
            s = s[..dash];
            const string prefix = "beta.";
            if (!suffix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            if (!int.TryParse(suffix[prefix.Length..], out var n) || n < 0) return false;
            beta = n;
        }

        if (!Version.TryParse(s, out var core)) return false;
        version = new MarcoVersion(Normalize(core), beta);
        return true;
    }

    /// <summary>Missing minor/build/revision count as 0 so "1.2" == "1.2.0" == "1.2.0.0".</summary>
    private static Version Normalize(Version v)
        => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    public int CompareTo(MarcoVersion? other)
    {
        if (other is null) return 1;
        var core = Core.CompareTo(other.Core);
        if (core != 0) return core;
        if (Beta is null && other.Beta is null) return 0;
        if (Beta is null) return 1;   // same core: stable outranks beta
        if (other.Beta is null) return -1;
        return Beta.Value.CompareTo(other.Beta.Value);
    }

    public static bool operator >(MarcoVersion a, MarcoVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(MarcoVersion a, MarcoVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(MarcoVersion a, MarcoVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(MarcoVersion a, MarcoVersion b) => a.CompareTo(b) <= 0;

    public override string ToString()
        => $"{Core.Major}.{Core.Minor}.{Core.Build}" + (Beta is { } b ? $"-beta.{b}" : "");
}
