namespace Marco.Core.Storage;

/// <summary>
/// Resolves where Marco keeps its settings, logs, run log, credential profiles, and saved scans. Portable-first:
/// a <c>Marco.Data</c> folder beside the exe when that location is writable, else <c>%LOCALAPPDATA%\Marco</c>.
/// Every component reads paths from here; nothing hardcodes a location.
/// </summary>
public sealed class AppPaths
{
    public const string DataFolderName = "Marco.Data";
    public const string AppFolderName = "Marco";

    /// <summary>Resolved data root.</summary>
    public string Root { get; }

    /// <summary>True when running from the portable location beside the exe.</summary>
    public bool IsPortable { get; }

    /// <summary>Human-readable explanation of the chosen location, surfaced in the About/status UI.</summary>
    public string Reason { get; }

    private readonly IStorageProbe _probe;

    private AppPaths(IStorageProbe probe, string root, bool isPortable, string reason)
    {
        _probe = probe;
        Root = root;
        IsPortable = isPortable;
        Reason = reason;
    }

    public static AppPaths Resolve(IStorageProbe? probe = null)
    {
        probe ??= new DefaultStorageProbe();

        var beside = Path.Combine(probe.ExeDirectory, DataFolderName);
        if (probe.CanCreateAndWrite(beside))
        {
            probe.EnsureDirectory(beside);
            return new AppPaths(probe, beside, true,
                $"Portable mode — writing beside the executable at {beside}.");
        }

        var appData = Path.Combine(probe.LocalAppDataDirectory, AppFolderName);
        probe.EnsureDirectory(appData);
        return new AppPaths(probe, appData, false,
            $"The executable folder is not writable; using {appData} instead.");
    }

    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string CredentialsFile => Path.Combine(Root, "credentials.dat");

    public string LogsDirectory => Sub("logs");
    public string ScansDirectory => Sub("scans");
    public string ExportsDirectory => Sub("exports");
    public string UpdatesDirectory => Sub("updates");
    /// <summary>User compliance rule packs (*.json), loaded on top of the embedded defaults.</summary>
    public string ComplianceDirectory => Sub("compliance");

    public string LogFile => Path.Combine(LogsDirectory, "marco.log");
    public string RunLogFile => Path.Combine(LogsDirectory, "runlog.jsonl");

    private string Sub(string name)
    {
        var path = Path.Combine(Root, name);
        _probe.EnsureDirectory(path);
        return path;
    }
}
