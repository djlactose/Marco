using System.Reflection;
using System.Text.Json;

namespace Marco.Core.Lifecycle;

/// <summary>Windows entry: matched by build number plus kind (client vs server — build numbers collide across
/// the two, e.g. 17763 is both Win10 1809 and Server 2019). Client entries carry separate Home/Pro and
/// Enterprise/Education dates because they differ per release.</summary>
public sealed record WindowsEol(
    string Product,
    string? Release,
    string Build,
    string Kind, // "client" | "server"
    DateTime? EosHomePro = null,
    DateTime? EosEnterprise = null,
    DateTime? EosMainstream = null,
    DateTime? EosExtended = null,
    DateTime? EsuEnd = null);

/// <summary>Linux entry: matched by distro name + version fragment against the OS caption/version.</summary>
public sealed record LinuxEol(string Distro, string VersionMatch, DateTime? Eos, DateTime? EosExtended = null);

public sealed record EolTableData(int SchemaVersion, string Updated,
    IReadOnlyList<WindowsEol> Windows, IReadOnlyList<LinuxEol> Linux);

/// <summary>Loads the bundled OS end-of-support table (regenerate with build\refresh-eol.ps1 — the file header
/// carries its snapshot date, surfaced in the UI so a stale table is visible, not silent).</summary>
public static class OsEolTable
{
    private const string Resource = "Marco.Core.Lifecycle.Resources.os-eol.json";
    private static readonly Lazy<EolTableData> Instance = new(Load);

    public static EolTableData Data => Instance.Value;

    private static EolTableData Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"Embedded resource {Resource} missing.");
        return JsonSerializer.Deserialize<EolTableData>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("EOL table is empty.");
    }
}
