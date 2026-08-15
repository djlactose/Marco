using System.Reflection;
using Marco.Core.Update;

namespace Marco.Core;

/// <summary>
/// The running application's version, read from the entry assembly's AssemblyInformationalVersion (set by
/// &lt;Version&gt; in Directory.Build.props, or -p:Version for CI beta builds). Custom attributes are read from
/// bundle metadata and work under single-file publish — unlike Assembly.Location, which is empty there.
/// </summary>
public static class AppVersion
{
    /// <summary>Display string, e.g. "1.1.0" or "1.1.0-beta.4".</summary>
    public static string Display { get; }

    /// <summary>Parsed for update comparisons.</summary>
    public static MarcoVersion Current { get; }

    /// <summary>True when this build came from the develop branch (a "-beta.N" version).</summary>
    public static bool IsBeta => Current.IsBeta;

    static AppVersion()
    {
        var info = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // Defensively strip build metadata ("+sha") in case source-revision stamping is ever re-enabled.
        if (info is not null)
        {
            var plus = info.IndexOf('+');
            if (plus >= 0) info = info[..plus];
        }

        if (info is not null && MarcoVersion.TryParse(info, out var parsed))
        {
            Current = parsed;
            Display = info;
        }
        else
        {
            var asm = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);
            Current = new MarcoVersion(asm, null);
            Display = Current.ToString();
        }
    }
}
