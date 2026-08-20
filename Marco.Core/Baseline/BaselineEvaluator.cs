using Marco.Core.Model;

namespace Marco.Core.Baseline;

public sealed record BaselineSummary(int Known, int Unknown, int UnknownWeak,
    IReadOnlyList<Machine> UnknownMachines, DateTime EvaluatedAt);

/// <summary>
/// Builds baselines from scanned machines and paints later scans against them. Identity descends serial →
/// burned-in MAC → name → address, mirroring the diff engine's confidence order:
/// - Serial or hardware-MAC hit → Known.
/// - A device carrying ONLY randomized (locally-administered) MACs and no serial → UnknownWeak: it may well be
///   a known laptop rotating its Wi-Fi MAC — inventorying it recovers the serial and settles the question.
/// - No serial/MAC at all (routed subnets have no ARP): the name decides; a bare address match alone is weak.
/// </summary>
public static class BaselineEvaluator
{
    public static AssetBaseline Build(IEnumerable<Machine> machines, string? @operator, string? sourceScanId, string source = "Blessed")
    {
        var entries = machines.Select(m => ToEntry(m, source)).ToList();
        return new AssetBaseline(AssetBaseline.CurrentSchemaVersion, DateTime.UtcNow, @operator, sourceScanId, entries);
    }

    public static BaselineEntry ToEntry(Machine m, string source)
    {
        var macs = m.MacAddresses.Select(HardwareIdentity.NormalizeMac).Where(x => x.Length == 12).Distinct().ToList();
        return new BaselineEntry(
            Guid.NewGuid().ToString("n"),
            HardwareIdentity.NormalizeSerial(m.System.SerialNumber),
            macs.Where(x => !HardwareIdentity.IsLocallyAdministered(x)).ToList(),
            macs.Where(HardwareIdentity.IsLocallyAdministered).ToList(),
            m.Name,
            m.Address,
            m.TargetBlock is { } b ? new[] { b } : Array.Empty<string>(),
            DateTime.UtcNow,
            source == "Blessed" ? null : Environment.UserName,
            source);
    }

    /// <summary>Set each machine's BaselineStatus and return the rollup. Pure over the inputs.</summary>
    public static BaselineSummary Evaluate(IReadOnlyList<Machine> machines, AssetBaseline baseline)
    {
        var serials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hardMacs = new HashSet<string>(StringComparer.Ordinal);
        var softMacs = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in baseline.Entries)
        {
            if (e.Serial is not null) serials.Add(e.Serial);
            foreach (var mac in e.Macs) hardMacs.Add(mac);
            foreach (var mac in e.RandomizedMacs) softMacs.Add(mac);
            if (!string.IsNullOrWhiteSpace(e.Name)) names.Add(e.Name!);
            if (e.LastAddress is not null) addresses.Add(e.LastAddress);
        }

        int known = 0, unknown = 0, weak = 0;
        var unknownMachines = new List<Machine>();
        foreach (var m in machines)
        {
            var status = Classify(m, serials, hardMacs, softMacs, names, addresses);
            m.BaselineStatus = status;
            switch (status)
            {
                case BaselineStatus.Known: known++; break;
                case BaselineStatus.UnknownWeak: weak++; unknownMachines.Add(m); break;
                case BaselineStatus.Unknown: unknown++; unknownMachines.Add(m); break;
            }
        }
        return new BaselineSummary(known, unknown, weak, unknownMachines, DateTime.Now);
    }

    private static BaselineStatus Classify(Machine m, HashSet<string> serials, HashSet<string> hardMacs,
        HashSet<string> softMacs, HashSet<string> names, HashSet<string> addresses)
    {
        if (HardwareIdentity.NormalizeSerial(m.System.SerialNumber) is { } serial)
            return serials.Contains(serial) ? BaselineStatus.Known : BaselineStatus.Unknown;

        var macs = m.MacAddresses.Select(HardwareIdentity.NormalizeMac).Where(x => x.Length == 12).ToList();
        var hard = macs.Where(x => !HardwareIdentity.IsLocallyAdministered(x)).ToList();
        var soft = macs.Where(HardwareIdentity.IsLocallyAdministered).ToList();

        if (hard.Count > 0)
            return hard.Any(hardMacs.Contains) ? BaselineStatus.Known : BaselineStatus.Unknown;
        if (soft.Count > 0)
            // Randomized-only evidence: a hit on a recorded randomized MAC is a real match; a miss proves
            // nothing (the whole point of randomization) — flag it weakly and let inventory settle it.
            return soft.Any(softMacs.Contains) ? BaselineStatus.Known : BaselineStatus.UnknownWeak;

        // No hardware identity at all (off-subnet discovery): the name is the best remaining evidence.
        if (!string.IsNullOrWhiteSpace(m.Name))
            return names.Contains(m.Name!) ? BaselineStatus.Known : BaselineStatus.Unknown;
        return addresses.Contains(m.Address) ? BaselineStatus.Known : BaselineStatus.UnknownWeak;
    }
}
