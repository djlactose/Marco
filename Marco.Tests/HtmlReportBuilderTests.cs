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
        Assert.Contains("No findings requiring attention", html);
    }

    // --- Hardware: RAM type / max RAM / disk types / expansion estimate ---

    private static Machine HardwareMachine()
    {
        var m = Machine();
        m.System.ChassisType = "Mini Tower";
        m.System.ExpansionSlotsTotal = 4;
        m.System.ExpansionSlotsFree = 2;
        m.System.ExpansionSlotsFreeList = "PCIEX16_2, M.2_2";
        m.TotalMemoryBytes = 16L * 1024 * 1024 * 1024;
        m.MemorySlotsUsed = 2;
        m.MemorySlotsTotal = 4;
        m.MaxMemoryBytes = 64L * 1024 * 1024 * 1024;
        m.MemoryModules = new List<MemoryModule>
        {
            new() { CapacityBytes = 8L << 30, SpeedMhz = 3200, MemoryTypeName = "DDR4", FormFactor = "DIMM", SlotLabel = "DIMM_A1" },
            new() { CapacityBytes = 8L << 30, SpeedMhz = 3200, MemoryTypeName = "DDR4", FormFactor = "DIMM", SlotLabel = "DIMM_B1" },
        };
        m.Disks = new List<DiskInfo>
        {
            new() { Model = "Samsung 970", SizeBytes = 500L * 1000 * 1000 * 1000, MediaType = "SSD", BusType = "NVMe" },
            new() { Model = "WDC Blue", SizeBytes = 1000L * 1000 * 1000 * 1000, MediaType = "HDD", BusType = "SATA" },
        };
        m.RefreshCounts();
        return m;
    }

    [Fact]
    public void HardwareTable_ShowsMemoryTypeMaxAndDiskKinds()
    {
        var html = new HtmlReportBuilder().Build(Input(Doc(HardwareMachine())));

        Assert.Contains("<h3>Hardware</h3>", html);
        Assert.Contains("16 GB, 2 of 4 slots used, max 64 GB", html);
        Assert.Contains("2× 8 GB DDR4-3200 DIMM", html);
        Assert.Contains("Samsung 970 466 GB (SSD · NVMe)", html);
        Assert.Contains("WDC Blue 931 GB (HDD · SATA)", html);
        Assert.Contains("Estimate: tower/desktop chassis, 2 internal disks, 2 of 4 expansion slots free (PCIEX16_2, M.2_2) — likely room for additional drives.", html);
    }

    [Fact]
    public void HardwareTable_SurvivesDtoRoundTrip()
    {
        // The report reads MachineDto, so every new field must persist through JSON.
        var json = new JsonExporter().Serialize(Doc(HardwareMachine()));
        var reloaded = new JsonExporter().Deserialize(json);
        var html = new HtmlReportBuilder().Build(Input(reloaded));

        Assert.Contains("max 64 GB", html);
        Assert.Contains("DDR4-3200", html);
        Assert.Contains("2 of 4 expansion slots free (PCIEX16_2, M.2_2)", html);
    }

    [Fact]
    public void MechanicalInternalDisk_IsAMediumFinding()
    {
        var html = new HtmlReportBuilder().Build(Input(Doc(HardwareMachine())));
        Assert.Contains("sev-medium", html);
        Assert.Contains("mechanical hard drive WDC Blue — SSD upgrade candidate", html);
    }

    [Fact]
    public void UsbOrLegacyTextDisks_AreNotUpgradeFindings()
    {
        var m = Machine();
        m.Disks = new List<DiskInfo>
        {
            new() { Model = "Backup USB", MediaType = "HDD", BusType = "USB" },
            new() { Model = "Old host", MediaType = "Fixed hard disk media" }, // pre-Win8 Win32_DiskDrive text
        };
        var html = new HtmlReportBuilder().Build(Input(Doc(m)));
        Assert.DoesNotContain("SSD upgrade candidate", html);
    }

    [Fact]
    public void RamAtPlatformMax_IsAMediumFinding_AndSuppressesSlotRule()
    {
        var m = HardwareMachine();
        m.TotalMemoryBytes = 64L << 30;
        m.MemorySlotsUsed = 4;
        var html = new HtmlReportBuilder().Build(Input(Doc(m)));

        Assert.Contains("RAM at the platform maximum (64 GB) — no upgrade headroom", html);
        Assert.DoesNotContain("memory slots populated", html);
    }

    [Fact]
    public void AllSlotsFull_WithoutKnownMax_IsAMediumFinding()
    {
        var m = HardwareMachine();
        m.MaxMemoryBytes = null;
        m.MemorySlotsUsed = 4;
        var html = new HtmlReportBuilder().Build(Input(Doc(m)));
        Assert.Contains("all 4 memory slots populated", html);
    }

    [Fact]
    public void MediumFindings_SortAfterCriticalAndHigh()
    {
        var m = HardwareMachine();
        m.Lifecycle = new LifecycleInfo("Windows 10 22H2", new DateTime(2025, 10, 14), null, OsSupportStatus.EndOfLife, 4.0);
        var html = new HtmlReportBuilder().Build(Input(Doc(m)));
        Assert.True(html.IndexOf("<li class=\"sev-critical\"", StringComparison.Ordinal) < html.IndexOf("<li class=\"sev-medium\"", StringComparison.Ordinal));
    }

    [Fact]
    public void HardwareTable_EncodesFirmwareText()
    {
        var m = HardwareMachine();
        m.System.ExpansionSlotsFreeList = "<img src=x onerror=alert(1)>";
        m.MemoryModules = new List<MemoryModule> { new() { CapacityBytes = 8L << 30, MemoryTypeName = "<b>DDR4</b>" } };
        var html = new HtmlReportBuilder().Build(Input(Doc(m)));

        Assert.DoesNotContain("<img src=x", html);
        Assert.DoesNotContain("<b>DDR4</b>", html);
        Assert.Contains("&lt;b&gt;DDR4&lt;/b&gt;", html);
    }

    [Fact]
    public void HardwareTable_EmptyForMachineWithNoData()
    {
        var html = new HtmlReportBuilder().Build(Input(Doc(Machine())));
        Assert.Contains("<h3>Hardware</h3>", html);
        Assert.DoesNotContain("0 GB", html);
        Assert.DoesNotContain("Estimate:", html);
    }

    [Fact]
    public void VirtualMachine_GetsNoExpansionEstimate()
    {
        var m = HardwareMachine();
        m.IsVirtual = true;
        var html = new HtmlReportBuilder().Build(Input(Doc(m)));
        Assert.DoesNotContain("Estimate:", html);
    }

    [Fact]
    public void DescribeModules_GroupsIdenticalAndFallsBackWithoutType()
    {
        Assert.Equal("2× 8 GB DDR4-3200 SODIMM", HtmlReportBuilder.DescribeModules(new[]
        {
            new MemoryModule { CapacityBytes = 8L << 30, SpeedMhz = 3200, MemoryTypeName = "DDR4", FormFactor = "SODIMM" },
            new MemoryModule { CapacityBytes = 8L << 30, SpeedMhz = 3200, MemoryTypeName = "DDR4", FormFactor = "SODIMM" },
        }));
        Assert.Equal("1× 4 GB 1333 MHz", HtmlReportBuilder.DescribeModules(new[]
        {
            new MemoryModule { CapacityBytes = 4L << 30, SpeedMhz = 1333 }, // old BIOS: no type
        }));
        Assert.Null(HtmlReportBuilder.DescribeModules(null));
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
