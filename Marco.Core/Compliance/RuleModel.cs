using System.Text.Json.Serialization;

namespace Marco.Core.Compliance;

/// <summary>Outcome of one rule against one machine. Null inputs always yield <see cref="Unknown"/> — never
/// Fail — matching the tri-state philosophy of SecurityInfo (null = undetermined, false = determined off).</summary>
public enum RuleStatus { Pass, Fail, Unknown, NotApplicable }

public enum RuleSeverity { Info, Low, Medium, High, Critical }

/// <summary>Scoping filter: which machines a rule applies to at all (misses are NotApplicable, not Unknown).
/// <paramref name="Os"/>: "windows" / "linux" / "any"; <paramref name="InstallationType"/>: "client" /
/// "server" / "any" (from the Updates collector's InstallationType).</summary>
public sealed record RuleAppliesTo(string? Os = null, string? InstallationType = null, bool DomainJoinedOnly = false);

/// <summary>One rule in a pack: metadata plus a reference to a coded check (<see cref="RuleCheckCatalog"/>) with
/// its parameters. Rules reference checks rather than carrying expressions — tri-state semantics and cross-list
/// logic make a declarative expression language a trap; a catalog of small tested functions is not.</summary>
public sealed record RuleDefinition(
    string Id,
    string Name,
    string Description,
    RuleSeverity Severity,
    string Check,
    Dictionary<string, int>? Params = null,
    RuleAppliesTo? AppliesTo = null,
    bool Enabled = true);

public sealed record RulePack(
    int SchemaVersion,
    string Id,
    string Name,
    string? Description,
    IReadOnlyList<RuleDefinition> Rules);

public sealed record RuleResult(string RuleId, string Name, RuleSeverity Severity, RuleStatus Status, string? Detail);

/// <summary>A machine's evaluation: every rule result plus the weighted score (null when nothing was decidable).
/// Recomputed, not authoritative — reopening a scan re-evaluates against the current packs.</summary>
public sealed record ComplianceResult(IReadOnlyList<RuleResult> Results, int? Score, DateTime EvaluatedAt)
{
    [JsonIgnore] public int FailCount => Results.Count(r => r.Status == RuleStatus.Fail);
    [JsonIgnore] public int PassCount => Results.Count(r => r.Status == RuleStatus.Pass);
    [JsonIgnore] public int UnknownCount => Results.Count(r => r.Status == RuleStatus.Unknown);
    [JsonIgnore] public IEnumerable<RuleResult> Failures => Results.Where(r => r.Status == RuleStatus.Fail)
        .OrderByDescending(r => r.Severity);
}

/// <summary>One rule's failure footprint across the fleet.</summary>
public sealed record FleetIssue(string RuleId, string Name, RuleSeverity Severity, int MachineCount);

/// <summary>The fleet rollup: mean score over evaluated machines plus the top issues, severity-then-count.</summary>
public sealed record FleetSummary(int EvaluatedMachines, int? AverageScore, int CriticalFailures, int HighFailures,
    IReadOnlyList<FleetIssue> TopIssues);
