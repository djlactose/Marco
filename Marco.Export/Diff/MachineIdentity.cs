namespace Marco.Export.Diff;

/// <summary>
/// Pairs machines between two scan documents by durable identity, so DHCP churn shows up as an address CHANGE
/// on one machine instead of a bogus remove+add pair. Confidence descends serial → hardware MAC → randomized
/// MAC → address+name:
/// - Serials win, but OEM placeholder values are rejected and a serial that appears on several machines in the
///   same document (cloned VMs) is demoted — those machines fall through to MAC matching.
/// - MAC overlap matches on any shared hardware address; an overlap that exists only among locally-administered
///   addresses (the randomized kind modern Wi-Fi stacks rotate) is tagged <see cref="MatchKind.WeakMac"/> so the
///   UI can show reduced confidence.
/// - Address+name is the last resort for discovery-only rows that carry neither serial nor MAC.
/// </summary>
public static class MachineIdentity
{
    private static readonly HashSet<string> BogusSerials = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "0", "1", "none", "invalid", "n/a", "na", "unknown", "default string",
        "to be filled by o.e.m.", "to be filled by oem", "system serial number",
        "chassis serial number", "not specified", "not available", "no serial", "empty",
        "0123456789", "1234567890", "123456789",
    };

    public sealed record IdentityMatch(Marco.Export.MachineDto Older, Marco.Export.MachineDto Newer, MatchKind MatchedBy);

    public sealed record MatchResult(
        IReadOnlyList<IdentityMatch> Matched,
        IReadOnlyList<Marco.Export.MachineDto> Added,
        IReadOnlyList<Marco.Export.MachineDto> Removed);

    public static MatchResult Match(IReadOnlyList<Marco.Export.MachineDto> older, IReadOnlyList<Marco.Export.MachineDto> newer)
    {
        var matched = new List<IdentityMatch>();
        var oldLeft = new List<Marco.Export.MachineDto>(older);
        var newLeft = new List<Marco.Export.MachineDto>(newer);

        // 1) Serial — only serials that are real AND unique within their own document.
        var oldBySerial = UniqueSerialMap(oldLeft);
        var newBySerial = UniqueSerialMap(newLeft);
        foreach (var (serial, oldMachine) in oldBySerial)
        {
            if (!newBySerial.TryGetValue(serial, out var newMachine)) continue;
            matched.Add(new IdentityMatch(oldMachine, newMachine, MatchKind.Serial));
            oldLeft.Remove(oldMachine);
            newLeft.Remove(newMachine);
        }

        // 2) MAC overlap — hardware addresses first, then randomized-only overlaps at reduced confidence.
        PairByMac(oldLeft, newLeft, matched, randomized: false);
        PairByMac(oldLeft, newLeft, matched, randomized: true);

        // 3) Address + name (null names count as equal — discovery-only rows often have no name at all).
        foreach (var oldMachine in oldLeft.ToList())
        {
            var newMachine = newLeft.FirstOrDefault(n =>
                string.Equals(n.Address, oldMachine.Address, StringComparison.OrdinalIgnoreCase)
                && string.Equals(n.Name ?? "", oldMachine.Name ?? "", StringComparison.OrdinalIgnoreCase));
            if (newMachine is null) continue;
            matched.Add(new IdentityMatch(oldMachine, newMachine, MatchKind.AddressName));
            oldLeft.Remove(oldMachine);
            newLeft.Remove(newMachine);
        }

        return new MatchResult(matched, Added: newLeft, Removed: oldLeft);
    }

    public static MachineRef ToRef(Marco.Export.MachineDto m, MatchKind? matchedBy = null) => new(
        m.Address, m.Name, NormalizeSerial(m.System.SerialNumber),
        m.MacAddresses.FirstOrDefault(), matchedBy);

    /// <summary>Null when the value is an OEM placeholder rather than a real serial.</summary>
    public static string? NormalizeSerial(string? serial)
    {
        var s = serial?.Trim();
        return s is null || BogusSerials.Contains(s) ? null : s;
    }

    /// <summary>Second nibble 2/6/A/E — a locally-administered address, i.e. randomized/virtual, not burned in.</summary>
    public static bool IsLocallyAdministered(string mac)
    {
        var n = NormalizeMac(mac);
        return n.Length >= 2 && n[1] is '2' or '6' or 'A' or 'E';
    }

    public static string NormalizeMac(string mac)
        => new(mac.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    private static Dictionary<string, Marco.Export.MachineDto> UniqueSerialMap(IEnumerable<Marco.Export.MachineDto> machines)
    {
        var groups = machines
            .Select(m => (Machine: m, Serial: NormalizeSerial(m.System.SerialNumber)))
            .Where(x => x.Serial is not null)
            .GroupBy(x => x.Serial!, StringComparer.OrdinalIgnoreCase);
        // A serial shared by several machines identifies none of them (VM clones, lazy OEMs) — demote all.
        return groups.Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Machine, StringComparer.OrdinalIgnoreCase);
    }

    private static void PairByMac(List<Marco.Export.MachineDto> oldLeft, List<Marco.Export.MachineDto> newLeft,
        List<IdentityMatch> matched, bool randomized)
    {
        foreach (var oldMachine in oldLeft.ToList())
        {
            var oldMacs = MacSet(oldMachine, randomized);
            if (oldMacs.Count == 0) continue;
            var newMachine = newLeft.FirstOrDefault(n => MacSet(n, randomized).Overlaps(oldMacs));
            if (newMachine is null) continue;
            matched.Add(new IdentityMatch(oldMachine, newMachine, randomized ? MatchKind.WeakMac : MatchKind.Mac));
            oldLeft.Remove(oldMachine);
            newLeft.Remove(newMachine);
        }
    }

    private static HashSet<string> MacSet(Marco.Export.MachineDto m, bool randomized)
        => m.MacAddresses
            .Where(mac => IsLocallyAdministered(mac) == randomized)
            .Select(NormalizeMac)
            .Where(mac => mac.Length == 12)
            .ToHashSet(StringComparer.Ordinal);
}
