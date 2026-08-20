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

    /// <summary>Hosts in flight during discovery. Not validated here — the file round-trips as written — but the
    /// UI clamps it to [1, <see cref="Scanning.ConcurrencyLimits.Max"/>] when applied, so a value saved on a
    /// bigger machine snaps down on a smaller one.</summary>
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

    /// <summary>Inventory collectors the operator has switched away from their catalogue default (name → enabled).
    /// Only the differences are stored, so a collector added in a later version appears with its own default
    /// (see <see cref="Inventory.CollectorCatalog"/>). Null/empty = all defaults.</summary>
    public Dictionary<string, bool>? CollectorOverrides { get; init; }

    /// <summary>Automatically keep a copy of every completed run in the scans folder (see ScanHistoryStore).</summary>
    public bool AutoSaveScans { get; init; } = true;

    /// <summary>Auto-saved runs kept before the oldest are pruned. Not validated here; the store clamps to ≥ 1.</summary>
    public int ScanHistoryLimit { get; init; } = 30;

    /// <summary>Also auto-save runs where only discovery ran (no inventory). Off = inventoried runs only.</summary>
    public bool AutoSaveDiscoveryOnly { get; init; } = true;

    /// <summary>Compliance rules the operator toggled away from their pack default (rule id → enabled). Deltas
    /// only, same pattern as <see cref="CollectorOverrides"/>. Null/empty = pack defaults.</summary>
    public Dictionary<string, bool>? ComplianceRuleOverrides { get; init; }

    /// <summary>Active client profile id; null = no client selected.</summary>
    public string? ActiveClientId { get; init; }
}
