namespace Marco.Export.History;

/// <summary>How far the saved run got. <see cref="Unknown"/> marks entries rebuilt from a bare file whose index
/// record was lost — the document is fine, we just no longer know whether inventory ran.</summary>
public enum ScanHistoryPhase { DiscoveryOnly, Inventoried, Unknown }

/// <summary>One saved run in the scans folder, as recorded in <c>index.json</c>. <paramref name="File"/> is the
/// document's file name relative to the scans directory; <paramref name="Id"/> doubles as the run id the app
/// tracks so an inventory pass can upgrade its own discovery save in place.</summary>
public sealed record ScanHistoryEntry(
    string Id,
    string File,
    DateTime Timestamp,
    string? Operator,
    IReadOnlyList<string> Ranges,
    int TotalTargets,
    int AliveCount,
    int InventoriedCount,
    ScanHistoryPhase Phase,
    string AppVersion,
    string DocSchemaVersion,
    long SizeBytes);

/// <summary>Root of <c>index.json</c>. The index is a cache of the directory, not a source of truth — see
/// <see cref="ScanHistoryStore"/> for the reconciliation rules.</summary>
public sealed record ScanHistoryIndex(int SchemaVersion, IReadOnlyList<ScanHistoryEntry> Entries)
{
    public const int CurrentSchemaVersion = 1;
}
