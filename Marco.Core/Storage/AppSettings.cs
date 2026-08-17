namespace Marco.Core.Storage;

/// <summary>
/// Operator preferences persisted to settings.json (<see cref="AppPaths.SettingsFile"/>) so options survive
/// restarts. Defaults mirror the UI's first-launch state. <see cref="IncludeBetaUpdates"/> is tri-state: null
/// means "auto" — a beta build follows the beta channel, a stable build follows stable — until the operator
/// explicitly toggles it.
/// </summary>
public sealed record AppSettings
{
    public bool? IncludeBetaUpdates { get; init; }

    public string TargetsText { get; init; } = "";
    public int Concurrency { get; init; } = 32;
    public bool IcmpEnabled { get; init; } = true;
    public bool TcpFallback { get; init; } = true;
    public bool Classification { get; init; } = true;
    public bool ResolveNames { get; init; } = true;
    public bool ResolveMac { get; init; } = true;
    public bool IncludeUnreachable { get; init; }
    public bool AutoInventory { get; init; }

    /// <summary>Group the results grid into one collapsible section per target block (CIDR/range).</summary>
    public bool GroupByBlock { get; init; } = true;
}
