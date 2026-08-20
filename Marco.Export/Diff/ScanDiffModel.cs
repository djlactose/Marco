using System.Text.Json.Serialization;

namespace Marco.Export.Diff;

/// <summary>How a machine in the older document was paired with one in the newer (see MachineIdentity).
/// Confidence descends: a serial pairing survives any amount of DHCP churn; an address+name pairing is only as
/// stable as the network it came from.</summary>
public enum MatchKind { Serial, Mac, WeakMac, AddressName }

public enum DiffCategory { Identity, Os, Hardware, Storage, Network, Software, Hotfixes, Security, Accounts, Services, Peripherals }

public enum DiffChangeKind { Added, Removed, Changed }

/// <summary>Info = routine drift; Notable = worth a look (new local admin, disk swapped); Regression = security
/// posture got worse (BitLocker off, firewall down). The diff window colours by this.</summary>
public enum DiffSeverity { Info, Notable, Regression }

/// <summary>Enough identity to name a machine in diff output without carrying the whole DTO.</summary>
public sealed record MachineRef(string Address, string? Name, string? Serial, string? PrimaryMac, MatchKind? MatchedBy)
{
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} ({Address})";
}

/// <summary>One observed difference. <paramref name="Item"/> names the thing that changed within the category —
/// a software title, a KB id, a field name like "Secure Boot".</summary>
public sealed record FieldChange(
    DiffCategory Category,
    string Item,
    DiffChangeKind Kind,
    string? OldValue,
    string? NewValue,
    DiffSeverity Severity);

public sealed record MachineDiff(MachineRef Machine, IReadOnlyList<FieldChange> Changes);

public sealed record DiffMetadata(
    DateTime OlderTimestamp,
    DateTime NewerTimestamp,
    IReadOnlyList<string> OlderRanges,
    IReadOnlyList<string> NewerRanges,
    int OlderMachineCount,
    int NewerMachineCount);

/// <summary>The full comparison of two scan documents. Serializable as-is (diff JSON export).</summary>
public sealed record ScanDiffResult(
    DiffMetadata Meta,
    IReadOnlyList<MachineRef> Added,
    IReadOnlyList<MachineRef> Removed,
    IReadOnlyList<MachineDiff> Changed)
{
    [JsonIgnore] public int ChangeCount => Changed.Sum(c => c.Changes.Count);
    [JsonIgnore] public int RegressionCount => Changed.Sum(c => c.Changes.Count(x => x.Severity == DiffSeverity.Regression));
    [JsonIgnore] public int NotableCount => Changed.Sum(c => c.Changes.Count(x => x.Severity == DiffSeverity.Notable));

    [JsonIgnore]
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;

    /// <summary>"+2 machines, −1 gone, 12 changed on 5 machines · 2 regressions" for headers and status lines.</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            if (IsEmpty) return "No differences.";
            var parts = new List<string>();
            if (Added.Count > 0) parts.Add($"+{Added.Count} new machine{(Added.Count == 1 ? "" : "s")}");
            if (Removed.Count > 0) parts.Add($"−{Removed.Count} gone");
            if (Changed.Count > 0) parts.Add($"{ChangeCount} change{(ChangeCount == 1 ? "" : "s")} on {Changed.Count} machine{(Changed.Count == 1 ? "" : "s")}");
            var text = string.Join(", ", parts);
            if (RegressionCount > 0) text += $" · {RegressionCount} regression{(RegressionCount == 1 ? "" : "s")}";
            return text;
        }
    }
}
