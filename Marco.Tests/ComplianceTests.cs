using Marco.Core.Compliance;
using Marco.Core.Model;
using Marco.Export;
using Xunit;

namespace Marco.Tests;

public class ComplianceTests
{
    private static Machine Inventoried(Action<Machine>? mutate = null)
    {
        var m = new Machine("10.0.0.20") { Name = "PC-A", DeviceType = DeviceType.Windows, Status = MachineStatus.Done };
        m.SetCollector("System", CollectorStatus.Ok);
        m.SetCollector("Security", CollectorStatus.Ok);
        m.SetCollector("Users", CollectorStatus.Ok);
        mutate?.Invoke(m);
        return m;
    }

    private static RuleDefinition Rule(string check, RuleSeverity sev = RuleSeverity.Medium,
        Dictionary<string, int>? p = null, RuleAppliesTo? applies = null)
        => new(check, check, "", sev, check, p, applies);

    private static RuleStatus Run(string check, Machine m, Dictionary<string, int>? p = null)
        => RuleCheckCatalog.Checks[check](m, p ?? new Dictionary<string, int>()).Status;

    // --- Check semantics: null never fails ---

    [Fact]
    public void NullInputs_AreUnknown_NeverFail()
    {
        var bare = Inventoried(); // Security collected but every field null
        foreach (var check in new[] { "smb1-disabled", "secure-boot", "uac-enabled", "firewall-all-profiles",
            "rdp-requires-nla", "tpm-2-enabled", "av-enabled", "bitlocker-os-volume", "patched-recently",
            "no-pending-reboot", "laps-managed", "smb-signing-required", "auto-update-enabled", "no-auto-logon" })
        {
            Assert.Equal(RuleStatus.Unknown, Run(check, bare));
        }
    }

    [Theory]
    [InlineData(false, RuleStatus.Pass)]
    [InlineData(true, RuleStatus.Fail)]
    public void Smb1_TriState(bool enabled, RuleStatus expected)
        => Assert.Equal(expected, Run("smb1-disabled", Inventoried(m => m.Security.Smb1Enabled = enabled)));

    [Fact]
    public void RdpDisabled_PassesTheNlaRule()
    {
        Assert.Equal(RuleStatus.Pass, Run("rdp-requires-nla", Inventoried(m => m.Security.RdpEnabled = false)));
        Assert.Equal(RuleStatus.Fail, Run("rdp-requires-nla", Inventoried(m =>
            { m.Security.RdpEnabled = true; m.Security.RdpNlaRequired = false; })));
        Assert.Equal(RuleStatus.Unknown, Run("rdp-requires-nla", Inventoried(m => m.Security.RdpEnabled = true)));
    }

    [Fact]
    public void Firewall_AnyOffFails_AnyNullUnknown_AllOnPasses()
    {
        Assert.Equal(RuleStatus.Fail, Run("firewall-all-profiles", Inventoried(m =>
            { m.Security.FirewallDomain = true; m.Security.FirewallPublic = false; })));
        Assert.Equal(RuleStatus.Unknown, Run("firewall-all-profiles", Inventoried(m =>
            { m.Security.FirewallDomain = true; m.Security.FirewallPrivate = true; })));
        Assert.Equal(RuleStatus.Pass, Run("firewall-all-profiles", Inventoried(m =>
            { m.Security.FirewallDomain = true; m.Security.FirewallPrivate = true; m.Security.FirewallPublic = true; })));
    }

    [Fact]
    public void BitLocker_OsVolumeDecides()
    {
        Assert.Equal(RuleStatus.Pass, Run("bitlocker-os-volume", Inventoried(m =>
            m.Security.BitLockerVolumes.Add(new BitLockerVolumeEntry { Letter = "C:", Protection = "On", VolumeType = "OS" }))));
        Assert.Equal(RuleStatus.Fail, Run("bitlocker-os-volume", Inventoried(m =>
            m.Security.BitLockerVolumes.Add(new BitLockerVolumeEntry { Letter = "C:", Protection = "Off", VolumeType = "OS" }))));
    }

    [Fact]
    public void DefenderRealtime_NotApplicable_WithThirdPartyAv()
    {
        var m = Inventoried(mm =>
        {
            mm.Security.DefenderEnabled = false;
            mm.Antivirus = new List<AntivirusEntry> { new() { Product = "ESET", Kind = "Antivirus", Enabled = true } };
        });
        Assert.Equal(RuleStatus.NotApplicable, Run("defender-realtime", m));
        Assert.Equal(RuleStatus.Pass, Run("av-enabled", m)); // the third-party product satisfies AV presence
    }

    [Fact]
    public void PasswordlessAccounts_RequireUsersCollectorEvidence()
    {
        var noUsers = new Machine("10.0.0.21") { DeviceType = DeviceType.Windows };
        noUsers.SetCollector("Security", CollectorStatus.Ok);
        Assert.Equal(RuleStatus.Unknown, Run("no-passwordless-accounts", noUsers)); // Users never ran

        Assert.Equal(RuleStatus.Fail, Run("no-passwordless-accounts", Inventoried(m =>
            m.LocalAccounts = new List<LocalAccountEntry> { new() { Name = "kiosk", PasswordRequired = false } })));
        Assert.Equal(RuleStatus.Pass, Run("no-passwordless-accounts", Inventoried(m =>
            m.LocalAccounts = new List<LocalAccountEntry> { new() { Name = "admin", PasswordRequired = true } })));
    }

    [Fact]
    public void AdminsLimited_UsesThreshold()
    {
        var m = Inventoried(mm => mm.LocalAdministrators = new List<string> { "a", "b", "c" });
        Assert.Equal(RuleStatus.Pass, Run("admins-limited", m));
        Assert.Equal(RuleStatus.Fail, Run("admins-limited", m, new() { ["maxAdmins"] = 2 }));
    }

    // --- Applies-to scoping ---

    [Fact]
    public void AppliesTo_MissesAreNotApplicable()
    {
        var server = Inventoried(m => { m.DeviceType = DeviceType.WindowsServer; m.Security.Smb1Enabled = true; });
        var rules = new[]
        {
            Rule("smb1-disabled", RuleSeverity.Critical),
            Rule("bitlocker-os-volume", RuleSeverity.Critical, applies: new RuleAppliesTo("windows", "client")),
            Rule("laps-managed", applies: new RuleAppliesTo("windows", DomainJoinedOnly: true)),
        };

        var result = ComplianceEvaluator.Evaluate(server, rules)!;

        Assert.Equal(RuleStatus.Fail, result.Results.Single(r => r.RuleId == "smb1-disabled").Status);
        Assert.Equal(RuleStatus.NotApplicable, result.Results.Single(r => r.RuleId == "bitlocker-os-volume").Status); // server, rule is client-only
        Assert.Equal(RuleStatus.NotApplicable, result.Results.Single(r => r.RuleId == "laps-managed").Status);        // not domain-joined
    }

    // --- Evaluation & scoring ---

    [Fact]
    public void UninventoriedMachine_GetsNoResult()
    {
        var pending = new Machine("10.0.0.9") { DeviceType = DeviceType.Windows };
        Assert.Null(ComplianceEvaluator.Evaluate(pending, new[] { Rule("smb1-disabled") }));
    }

    [Fact]
    public void Score_IsSeverityWeighted_UnknownsExcluded()
    {
        // Critical pass (8) + Low fail (1) → 8/9 ≈ 89. The Unknown rule must not drag the score down.
        var m = Inventoried(mm => { mm.Security.Smb1Enabled = false; mm.Updates.PendingReboot = true; });
        var rules = new[]
        {
            Rule("smb1-disabled", RuleSeverity.Critical),
            Rule("no-pending-reboot", RuleSeverity.Low),
            Rule("secure-boot", RuleSeverity.High), // null → Unknown
        };

        var result = ComplianceEvaluator.Evaluate(m, rules)!;

        Assert.Equal(89, result.Score);
        Assert.Equal(1, result.UnknownCount);
        Assert.Equal(1, result.FailCount);
    }

    [Fact]
    public void AllUnknown_MeansNullScore()
    {
        var result = ComplianceEvaluator.Evaluate(Inventoried(), new[] { Rule("secure-boot") })!;
        Assert.Null(result.Score);
    }

    [Fact]
    public void FleetSummary_AveragesAndRanksIssues()
    {
        var machines = new[]
        {
            Inventoried(m => { m.Security.Smb1Enabled = true; m.Security.UacEnabled = true; m.Compliance = null; }),
            Inventoried(m => { m.Security.Smb1Enabled = true; m.Security.UacEnabled = false; }),
            Inventoried(m => { m.Security.Smb1Enabled = false; m.Security.UacEnabled = true; }),
        };
        var rules = new[] { Rule("smb1-disabled", RuleSeverity.Critical), Rule("uac-enabled", RuleSeverity.Medium) };
        foreach (var m in machines) m.Compliance = ComplianceEvaluator.Evaluate(m, rules);

        var fleet = ComplianceEvaluator.Summarize(machines);

        Assert.Equal(3, fleet.EvaluatedMachines);
        Assert.Equal(2, fleet.CriticalFailures);
        Assert.Equal("smb1-disabled", fleet.TopIssues[0].RuleId); // severity ranks above raw count
        Assert.Equal(2, fleet.TopIssues[0].MachineCount);
    }

    // --- Pack loading ---

    [Fact]
    public void DefaultPack_LoadsAndEveryCheckExists()
    {
        var pack = RulePackLoader.LoadDefaultPack();
        Assert.True(pack.Rules.Count >= 20);
        Assert.All(pack.Rules, r => Assert.True(RuleCheckCatalog.Checks.ContainsKey(r.Check), $"missing check {r.Check}"));
        Assert.Equal(pack.Rules.Count, pack.Rules.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void UserPack_OverridesById_AndMalformedIsSkipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "marco-rules-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a-custom.json"), """
                { "SchemaVersion": 1, "Id": "custom", "Name": "Custom", "Rules": [
                  { "Id": "patched-recently", "Name": "Patched (strict)", "Description": "", "Severity": "Critical",
                    "Check": "patched-recently", "Params": { "maxDays": 14 } },
                  { "Id": "future-rule", "Name": "From a newer Marco", "Description": "", "Severity": "Low",
                    "Check": "check-that-does-not-exist" } ] }
                """);
            File.WriteAllText(Path.Combine(dir, "b-broken.json"), "{ not json");

            var warnings = new List<string>();
            var rules = RulePackLoader.LoadEffectiveRules(dir, null, warnings.Add);

            var patched = rules.Single(r => r.Id == "patched-recently");
            Assert.Equal(RuleSeverity.Critical, patched.Severity);              // user pack replaced the default
            Assert.Equal(14, patched.Params!["maxDays"]);
            Assert.DoesNotContain(rules, r => r.Id == "future-rule");           // unknown check dropped
            Assert.Contains(warnings, w => w.Contains("future-rule"));
            Assert.Contains(warnings, w => w.Contains("b-broken.json"));        // malformed pack skipped, not fatal
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Overrides_DisableARule_AsDeltasOnly()
    {
        var rules = RulePackLoader.LoadEffectiveRules(null, new Dictionary<string, bool> { ["smb1-disabled"] = false });
        Assert.False(rules.Single(r => r.Id == "smb1-disabled").Enabled);
        Assert.True(rules.Single(r => r.Id == "uac-enabled").Enabled);

        var deltas = RulePackLoader.OverridesFor(RulePackLoader.LoadDefaultPack().Rules,
            rules.ToDictionary(r => r.Id, r => r.Enabled));
        Assert.Single(deltas!);
        Assert.False(deltas!["smb1-disabled"]);
    }

    // --- Serialization ---

    [Fact]
    public void ComplianceResult_RoundTripsThroughScanDocument()
    {
        var m = Inventoried(mm => mm.Security.Smb1Enabled = true);
        m.Compliance = ComplianceEvaluator.Evaluate(m, new[] { Rule("smb1-disabled", RuleSeverity.Critical) });

        var meta = new ScanMetadata(new DateTime(2026, 1, 1), "tester", new[] { "10.0.0.0/24" }, 1, 1);
        var json = new JsonExporter().Serialize(ScanDocument.From(meta, new[] { m }));
        var back = new JsonExporter().Deserialize(json).ToMachines()[0];

        Assert.NotNull(back.Compliance);
        Assert.Equal(0, back.Compliance!.Score);
        Assert.Equal(RuleStatus.Fail, back.Compliance.Results.Single().Status);
        Assert.Contains("\"Fail\"", json); // enum names, not numbers
    }
}
