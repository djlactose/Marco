namespace Marco.Core.Cli;

/// <summary>Process exit codes for the headless `scan` verb. 4 is dedicated because DPAPI-decrypt failure —
/// the scheduled task not running as the user who saved the credentials — is the single most common setup
/// mistake, and a distinct code lets a wrapper detect exactly that.</summary>
public enum CliExitCode
{
    Ok = 0,
    Usage = 1,
    ScanFailed = 2,
    WriteFailed = 3,
    CredentialDecrypt = 4,
    Changed = 10, // reserved for --exit-code-on-change (diff-aware)
}

/// <summary>Parsed `scan` options. Targets is the raw value (a file path or inline tokens); resolution happens
/// in the app-layer command where TargetParser lives.</summary>
public sealed record CliOptions(
    string TargetsValue,
    string? OutJsonPath,
    string? CsvDirectory,
    IReadOnlyList<string>? CollectorNames,
    int? Concurrency,
    bool NoInventory,
    string? CredentialLabel,
    string? ClientName,
    bool Quiet,
    string? LogPath,
    bool ExitCodeOnChange);

/// <summary>A parse failure, with a human-readable reason for stderr.</summary>
public sealed record CliParseError(string Message);
