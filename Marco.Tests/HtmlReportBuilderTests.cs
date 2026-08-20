using Marco.Core.Compliance;
using Marco.Core.Lifecycle;
using Marco.Core.Model;
using Marco.Export;
using Marco.Report;
using Xunit;

namespace Marco.Tests;

public class HtmlReportBuilderTests
{
    private static ScanDocument Doc(params Machine[] machines)
        => ScanDocument.From(new ScanMetadata(new DateTime(2026, 1, 1), "tester",
            new[] { "10.0.0.0/24" }, machines.Length, machines.Length), machines);

    private static ReportInput Input(ScanDocument doc, ReportBranding? branding = null, FleetSummary? fleet = null)
        => new(doc, fleet, branding ?? ReportBranding.Neutral, "Acme", new DateTime(2026, 8, 20), "2026-08-20");

    private static Machine Machine(string address = "10.0.0.20")
    {
        var m = new Machine(address) { Name = "PC-A", DeviceType = DeviceType.Windows, Status = MachineStatus.Done };
        m.Os.Caption = "Windows 11 Pro";
        m.System.Model = "OptiPlex";
        m.SetCollector("System", CollectorStatus.Ok);
        return m;
    }

    [Fact]
    public void ContainsCoreSections()
    {
        var html = new HtmlReportBuilder().Build(Input(Doc(Machine())));
        Assert.Contains("<!doctype html>", html);
        Assert.Contains("Executive summary", html);
        Assert.Contains("Attention needed", html);
        Assert.Contains("Asset appendix", html);
        Assert.Contains("Windows 11 Pro", html);
        Assert.Contains("EOL data 2026-08-20", html);
    }

    [Fact]
    public void Hostname_IsHtmlEncoded()
    {
        var m = Machine();
        m.Name = "<script>alert('x')</script>"; // attacker-influenceable
        var html = new HtmlReportBuilder().Build(Input(Doc(m)));

        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void EndOfLifeOs_BecomesACriticalFinding()
    {
        var m = Machine();
        m.Lifecycle = new LifecycleInfo("Windows 10 22H2", new DateTime(2025, 10, 14), null,
            OsSupportStatus.EndOfLife, 4.0);
        var html = new HtmlReportBuilder().Build(Input(Doc(m)));

        Assert.Contains("sev-critical", html);
        Assert.Contains("support ENDED", html);
    }

    [Fact]
    public void CriticalComplianceFailure_IsListed()
    {
        var m = Machine();
        m.Compliance = new ComplianceResult(new[]
        {
            new RuleResult("smb1", "SMB1 disabled", RuleSeverity.Critical, RuleStatus.Fail, "SMB1 is enabled"),
            new RuleResult("uac", "UAC", RuleSeverity.Medium, RuleStatus.Fail, null), // medium → not in findings
        }, 60, DateTime.Now);

        var html = new HtmlReportBuilder().Build(Input(Doc(m)));
        Assert.Contains("SMB1 disabled", html);
        Assert.DoesNotContain(">UAC<", html); // medium failures aren't promoted to the findings list
    }

    [Fact]
    public void NoFindings_SaysSo()
    {
        var html = new HtmlReportBuilder().Build(Input(Doc(Machine())));
        Assert.Contains("No critical or high-severity findings", html);
    }

    [Fact]
    public void ZeroMachines_DoesNotThrow()
    {
        var html = new HtmlReportBuilder().Build(Input(Doc()));
        Assert.Contains("Executive summary", html);
        Assert.Contains("0", html);
    }

    [Fact]
    public void FleetScore_RendersDonut()
    {
        var fleet = new FleetSummary(3, 82, 1, 2, Array.Empty<FleetIssue>());
        var html = new HtmlReportBuilder().Build(Input(Doc(Machine()), fleet: fleet));
        Assert.Contains("82%", html);
        Assert.Contains("<svg", html);
    }

    [Fact]
    public void AccentColor_SanitizedIntoStyles()
    {
        var good = new HtmlReportBuilder().Build(Input(Doc(Machine()), new ReportBranding(AccentColor: "#AA1122")));
        Assert.Contains("#AA1122", good);

        // A non-hex accent must not reach the CSS (it is injected into a stylesheet).
        var bad = new HtmlReportBuilder().Build(Input(Doc(Machine()), new ReportBranding(AccentColor: "red;}body{display:none")));
        Assert.DoesNotContain("display:none", bad);
        Assert.Contains("#2A6FB0", bad); // fell back to the default
    }

    [Fact]
    public void AppendixToggle_Respected()
    {
        var doc = Doc(Machine());
        var without = new HtmlReportBuilder().Build(
            new ReportInput(doc, null, ReportBranding.Neutral, "Acme", DateTime.Now, "2026-08-20", IncludeAppendix: false));
        Assert.DoesNotContain("Asset appendix", without);
    }
}
