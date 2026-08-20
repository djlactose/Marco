namespace Marco.Core.Baseline;

/// <summary>One known device: identity evidence (serial + burned-in MACs, with randomized MACs kept apart as
/// weak evidence) plus bookkeeping for the operator ("who trusted this, when, from which scan").</summary>
public sealed record BaselineEntry(
    string Id,
    string? Serial,
    IReadOnlyList<string> Macs,
    IReadOnlyList<string> RandomizedMacs,
    string? Name,
    string? LastAddress,
    IReadOnlyList<string> Blocks,
    DateTime AddedUtc,
    string? AddedBy,
    string Source, // "Blessed" | "Trusted"
    string? Note = null);

/// <summary>The known-device set a network's scans are compared against (baseline.json).</summary>
public sealed record AssetBaseline(
    int SchemaVersion,
    DateTime Updated,
    string? UpdatedBy,
    string? SourceScanId,
    IReadOnlyList<BaselineEntry> Entries)
{
    public const int CurrentSchemaVersion = 1;
}
