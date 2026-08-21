using Marco.Core.Model;

namespace Marco.Core.Compliance;

/// <summary>
/// Runs a rule set against machines and rolls the fleet up. A machine that was never inventoried (no collector
/// results at all) gets NO result — grading a host on data nobody gathered would make every unreachable box
/// look non-compliant. Scoring is a severity-weighted pass ratio with Unknown and NotApplicable excluded from
/// the denominator: unknowns are surfaced as counts, never punished.
/// </summary>
public static class ComplianceEvaluator
{
    private static int Weight(RuleSeverity s) => s switch
    {
        RuleSeverity.Critical => 8,
        RuleSeverity.High => 4,
        RuleSeverity.Medium => 2,
        RuleSeverity.Low => 1,
        _ => 0,
    };

    public static ComplianceResult? Evaluate(Machine m, IReadOnlyList<RuleDefinition> rules)
    {
        if (m.Collectors.Count == 0) return null; // never inventoried — nothing to grade

        var empty = new Dictionary<string, int>();
        var results = new List<RuleResult>(rules.Count);
        foreach (var rule in rules.Where(r => r.Enabled))
        {
            RuleStatus status;
            string? detail;
            if (!Applies(m, rule.AppliesTo))
            {
                (status, detail) = (RuleStatus.NotApplicable, null);
            }
            else if (RuleCheckCatalog.Checks.TryGetValue(rule.Check, out var check))
            {
                try { (status, detail) = check(m, (IReadOnlyDictionary<string, int>?)rule.Params ?? empty); }
                catch (Exception ex) { (status, detail) = (RuleStatus.Unknown, ex.Message); } // a check must never break evaluation
            }
            else
            {
                (status, detail) = (RuleStatus.Unknown, $"unknown check '{rule.Check}'");
            }
            results.Add(new RuleResult(rule.Id, rule.Name, rule.Severity, status, detail));
        }

        int passWeight = results.Where(r => r.Status == RuleStatus.Pass).Sum(r => Weight(r.Severity));
        int failWeight = results.Where(r => r.Status == RuleStatus.Fail).Sum(r => Weight(r.Severity));
        int? score = passWeight + failWeight == 0 ? null
            : (int)Math.Round(100.0 * passWeight / (passWeight + failWeight));

        return new ComplianceResult(results, score, DateTime.Now);
    }

    public static FleetSummary Summarize(IEnumerable<Machine> machines)
    {
        var evaluated = machines.Where(m => m.Compliance is not null).ToList();
        var scores = evaluated.Where(m => m.Compliance!.Score is not null).Select(m => m.Compliance!.Score!.Value).ToList();

        var issues = evaluated
            .SelectMany(m => m.Compliance!.Results.Where(r => r.Status == RuleStatus.Fail))
            .GroupBy(r => r.RuleId)
            .Select(g => new FleetIssue(g.Key, g.First().Name, g.First().Severity, g.Count()))
            .OrderByDescending(i => i.Severity)
            .ThenByDescending(i => i.MachineCount)
            .ToList();

        return new FleetSummary(
            evaluated.Count,
            scores.Count == 0 ? null : (int)Math.Round(scores.Average()),
            issues.Where(i => i.Severity == RuleSeverity.Critical).Sum(i => i.MachineCount),
            issues.Where(i => i.Severity == RuleSeverity.High).Sum(i => i.MachineCount),
            issues);
    }

    private static bool Applies(Machine m, RuleAppliesTo? a)
    {
        bool isWindows = m.DeviceType is DeviceType.Windows or DeviceType.WindowsServer;
        bool isLinux = m.DeviceType == DeviceType.UnixLinux;
        bool isPrinter = m.DeviceType == DeviceType.Printer;
        bool isDevice = isPrinter || m.DeviceType == DeviceType.NetworkDevice;
        var os = a?.Os?.ToLowerInvariant();

        // Printers and network gear only take rules written for them ("printer"); every computer-oriented rule
        // (including unscoped ones) is NotApplicable there, otherwise they'd all read Unknown against an empty
        // security posture.
        if (isDevice) return os == "printer" && isPrinter;
        if (a is null) return true;

        switch (os)
        {
            case "windows" when !isWindows: return false;
            case "linux" when !isLinux: return false;
            case "printer": return false;
        }

        if (a.InstallationType is { } wanted && !wanted.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            // Prefer the collector's answer; fall back to the device classification.
            bool isServer = m.Updates.InstallationType is { } it
                ? it.Contains("Server", StringComparison.OrdinalIgnoreCase)
                : m.DeviceType == DeviceType.WindowsServer;
            if (wanted.Equals("client", StringComparison.OrdinalIgnoreCase) && isServer) return false;
            if (wanted.Equals("server", StringComparison.OrdinalIgnoreCase) && !isServer) return false;
        }

        if (a.DomainJoinedOnly && !m.System.PartOfDomain) return false;
        return true;
    }
}
