using Marco.Core.Compliance;
using Marco.Core.Diagnosis;
using Marco.Core.Model;
using Marco.Core.Printing;
using Marco.Credentials;
using Marco.Export;
using Marco.Export.Diff;
using Marco.Inventory.Collectors;
using Marco.Report;
using Xunit;

namespace Marco.Tests;

/// <summary>Printer data through every surface: JSON/CSV export, the HTML report, compare, compliance, the
/// prerequisite doctor, the print-server queue linker and the credential store.</summary>
public class PrinterSurfacesTests
{
    private static Machine PrinterMachine(string address = "10.0.0.9", int blackPercent = 8, bool jammed = false)
    {
        var m = new Machine(address) { Name = "NPI1A2B3C", DeviceType = DeviceType.Printer, Status = MachineStatus.Done, Vendor = "HP" };
        m.System.Manufacturer = "HP"; m.System.Model = "Color LaserJet MFP M479fdw"; m.System.SerialNumber = "VNC1K23456";
        m.MacAddresses.Add("3C:D9:2B:11:22:33");
        m.Printer = new PrinterDevice
        {
            Status = "Idle", DeviceStatus = "Running", TotalPages = 45210, PageCountUnit = "impressions", QueuedJobs = 2,
            IppState = "idle", Firmware = "002_2303A", Location = "2nd floor",
            ErrorStates = jammed ? new List<string> { PrinterErrorStates.Jammed } : new List<string>(),
            Supplies = new List<PrinterSupply>
            {
                new() { Name = "Black Cartridge HP 414A", Type = "toner", Colorant = "black", Percent = blackPercent, Level = blackPercent, MaxCapacity = 100, Unit = "percent" },
                new() { Name = "Cyan Cartridge HP 414A", Type = "toner", Colorant = "cyan", Percent = 80 },
                new() { Name = "Toner Collection Unit", Type = "wasteToner", IsReceptacle = true, Percent = 30 },
            },
            Trays = new List<PrinterTray> { new() { Name = "Tray 2", Percent = 72, Status = "OK" } },
            Sources = new List<string> { "SNMP v2c", "IPP" },
        };
        foreach (var c in Marco.Inventory.Snmp.SnmpDeviceInventoryRunner.PrinterCollectorNames) m.SetCollector(c, CollectorStatus.Ok);
        return m;
    }

    private static Machine PrintServer(string printerAddress = "10.0.0.9")
    {
        var s = new Machine("10.0.0.5") { Name = "PRINTSRV", DeviceType = DeviceType.WindowsServer, Status = MachineStatus.Done };
        s.Printers = new List<PrinterEntry>
        {
            new() { Name = "Sales-MFP", PortName = "IP_10.0.0.9", HostAddress = printerAddress, Shared = true, ShareName = "SalesMFP", QueuedJobs = 3, Status = "Idle" },
            new() { Name = "Lobby", PortName = "IP_10.0.0.77", HostAddress = "10.0.0.77", QueuedJobs = 0 },
        };
        s.SetCollector("Peripherals", CollectorStatus.Ok);
        return s;
    }

    private static ScanDocument Doc(params Machine[] machines)
        => ScanDocument.From(new ScanMetadata(new DateTime(2026, 8, 21), "tester", new[] { "10.0.0.0/24" }, machines.Length, machines.Length), machines);

    // --- JSON / CSV ---------------------------------------------------------------------------------------

    [Fact]
    public void Json_RoundTrips_PrinterDevice_AndSkipsComputedProperties()
    {
        var json = new JsonExporter().Serialize(Doc(PrinterMachine()));
        Assert.DoesNotContain("\"LevelDisplay\"", json);
        Assert.DoesNotContain("\"SuppliesSummary\"", json);
        Assert.DoesNotContain("\"PrintServerQueues\"", json);

        var back = new JsonExporter().Deserialize(json).ToMachines()[0];
        var p = back.Printer!;
        Assert.Equal("Idle", p.Status);
        Assert.Equal(3, p.Supplies.Count);
        Assert.Equal(8, p.Supplies[0].Percent);
        Assert.True(p.Supplies[0].IsLow);
        Assert.Equal(45210, p.TotalPages);
        Assert.Equal(2, p.QueuedJobs);
        Assert.Contains("K 8%", back.PrinterSummary);
        Assert.Equal(1, back.PrinterLowSupplyCount);
    }

    [Fact]
    public void Json_OlderFile_WithoutPrinterField_StillOpens()
    {
        var json = new JsonExporter().Serialize(Doc(PrinterMachine()));
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["Machines"]![0]!.AsObject().Remove("Printer");
        var back = new JsonExporter().Deserialize(node.ToJsonString()).ToMachines()[0];
        Assert.Null(back.Printer);
        Assert.Null(back.PrinterSummary);
    }

    [Fact]
    public void Csv_WritesPrinterDevicesAndSupplies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "marco-prn-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var files = new CsvExporter().Export(Doc(PrinterMachine(), PrintServer()), dir);
            var devices = File.ReadAllLines(Path.Combine(dir, "printer-devices.csv"));
            Assert.Equal(2, devices.Length);
            Assert.Contains("10.0.0.9", devices[1]);
            Assert.Contains("45210", devices[1]);
            Assert.Contains("Black Cartridge HP 414A: 8 %", devices[1]);
            var supplies = File.ReadAllLines(Path.Combine(dir, "printer-supplies.csv"));
            Assert.Equal(4, supplies.Length);
            Assert.Contains("wasteToner", supplies[3]);
            var queues = File.ReadAllLines(Path.Combine(dir, "printers.csv"));
            Assert.Contains("QueuedJobs", queues[0]);
            Assert.Contains(",3,", queues[1]);
            Assert.Contains(files, f => f.EndsWith("printer-devices.csv"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // --- Report ---------------------------------------------------------------------------------------------

    [Fact]
    public void Report_ListsLowTonerAndJam_AndPrinterTable()
    {
        var doc = Doc(PrinterMachine(jammed: true));
        var html = new HtmlReportBuilder().Build(new ReportInput(doc, null, ReportBranding.Neutral, "Acme", new DateTime(2026, 8, 21), "2026-08-20"));
        Assert.Contains("sev-high", html);
        Assert.Contains("paper jam", html);
        Assert.Contains("sev-medium", html);
        Assert.Contains("Black Cartridge HP 414A at 8 %", html);
        Assert.Contains("<h3>Printers</h3>", html);
        Assert.Contains("45,210", html);
        Assert.Contains("VNC1K23456", html);
    }

    [Fact]
    public void Report_NoPrinters_NoPrinterTable()
    {
        var pc = new Machine("10.0.0.20") { Name = "PC-A", DeviceType = DeviceType.Windows, Status = MachineStatus.Done };
        pc.SetCollector("System", CollectorStatus.Ok);
        var html = new HtmlReportBuilder().Build(new ReportInput(Doc(pc), null, ReportBranding.Neutral, "Acme", new DateTime(2026, 8, 21), "2026-08-20"));
        Assert.DoesNotContain("<h3>Printers</h3>", html);
    }

    // --- Compare ----------------------------------------------------------------------------------------------

    [Fact]
    public void Diff_ReportsThresholdCrossings_NotEveryPercent()
    {
        var older = PrinterMachine(blackPercent: 40);
        var drift = PrinterMachine(blackPercent: 37);
        Assert.True(ScanDiffEngine.Compute(Doc(older), Doc(drift)).IsEmpty);

        var low = PrinterMachine(blackPercent: 6);
        var diff = ScanDiffEngine.Compute(Doc(older), Doc(low));
        var change = Assert.Single(Assert.Single(diff.Changed).Changes);
        Assert.Equal(DiffCategory.Printer, change.Category);
        Assert.Equal(DiffSeverity.Regression, change.Severity);
        Assert.Equal("40 %", change.OldValue);
        Assert.Equal("6 %", change.NewValue);

        var replaced = ScanDiffEngine.Compute(Doc(low), Doc(PrinterMachine(blackPercent: 100)));
        Assert.Equal(DiffSeverity.Info, Assert.Single(Assert.Single(replaced.Changed).Changes).Severity);
    }

    [Fact]
    public void Diff_ReportsJam_AsStatusRegression()
    {
        var diff = ScanDiffEngine.Compute(Doc(PrinterMachine(blackPercent: 50)), Doc(PrinterMachine(blackPercent: 50, jammed: true)));
        var change = Assert.Single(Assert.Single(diff.Changed).Changes);
        Assert.Equal("Printer status", change.Item);
        Assert.Equal(DiffSeverity.Regression, change.Severity);
        Assert.Contains("Paper jam", change.NewValue);
    }

    // --- Compliance ---------------------------------------------------------------------------------------------

    [Fact]
    public void Compliance_PrinterRulesApplyOnlyToPrinters_AndComputerRulesNotToPrinters()
    {
        var rules = RulePackLoader.LoadDefaultPack().Rules;
        var printer = PrinterMachine(blackPercent: 8);
        var result = ComplianceEvaluator.Evaluate(printer, rules)!;

        var supplies = result.Results.Single(r => r.RuleId == "printer-supplies-not-low");
        Assert.Equal(RuleStatus.Fail, supplies.Status);
        Assert.Contains("Black Cartridge HP 414A", supplies.Detail);
        Assert.Equal(RuleStatus.Pass, result.Results.Single(r => r.RuleId == "printer-no-error-state").Status);
        Assert.All(result.Results.Where(r => !r.RuleId.StartsWith("printer-")), r => Assert.Equal(RuleStatus.NotApplicable, r.Status));
        Assert.NotNull(result.Score);

        var pc = new Machine("10.0.0.20") { DeviceType = DeviceType.Windows, Status = MachineStatus.Done };
        pc.SetCollector("System", CollectorStatus.Ok);
        var pcResult = ComplianceEvaluator.Evaluate(pc, rules)!;
        Assert.All(pcResult.Results.Where(r => r.RuleId.StartsWith("printer-")), r => Assert.Equal(RuleStatus.NotApplicable, r.Status));
    }

    [Fact]
    public void Compliance_PrinterErrorRule_FailsOnJam_UnknownWithoutStatus()
    {
        var rules = RulePackLoader.LoadDefaultPack().Rules;
        var jammed = ComplianceEvaluator.Evaluate(PrinterMachine(jammed: true), rules)!;
        Assert.Equal(RuleStatus.Fail, jammed.Results.Single(r => r.RuleId == "printer-no-error-state").Status);

        var blank = PrinterMachine();
        blank.Printer!.Status = null; blank.Printer.DeviceStatus = null; blank.Printer.IppState = null;
        var unknown = ComplianceEvaluator.Evaluate(blank, rules)!;
        Assert.Equal(RuleStatus.Unknown, unknown.Results.Single(r => r.RuleId == "printer-no-error-state").Status);
    }

    // --- Doctor -------------------------------------------------------------------------------------------------

    [Fact]
    public void Doctor_NamesSnmpCauses()
    {
        var off = new Machine("10.0.0.9") { DeviceType = DeviceType.Printer, IsAlive = true, ConnectFailure = ConnectFailure.SnmpDisabled, Status = MachineStatus.Unreachable };
        var d = PrereqDoctor.Diagnose(off);
        Assert.Equal(PrereqCause.SnmpDisabled, d.Cause);
        Assert.Contains("SNMP", d.FixScript);

        var silent = new Machine("10.0.0.10") { DeviceType = DeviceType.Printer, IsAlive = true, ConnectFailure = ConnectFailure.SnmpNoResponse, Status = MachineStatus.Unreachable };
        Assert.Equal(PrereqCause.SnmpNoResponse, PrereqDoctor.Diagnose(silent).Cause);
        Assert.Contains("IPP", PrereqDoctor.Diagnose(silent).Explanation);

        var fresh = new Machine("10.0.0.11") { DeviceType = DeviceType.NetworkDevice, IsAlive = true };
        Assert.Equal(PrereqCause.NotInventoryable, PrereqDoctor.Diagnose(fresh).Cause);

        var rollup = PrereqDoctor.Rollup(new[] { off, silent, fresh });
        Assert.Equal(2, rollup.Count); // NotInventoryable excluded, the two SNMP causes grouped
    }

    // --- Linker -------------------------------------------------------------------------------------------------

    [Fact]
    public void Linker_AttachesServerQueues_ByPortAddress()
    {
        var printer = PrinterMachine();
        var server = PrintServer();
        var other = new Machine("10.0.0.77") { Name = "lobby-printer", DeviceType = DeviceType.Printer };
        PrintServerQueueLinker.Link(new[] { printer, server, other });

        var q = Assert.Single(printer.PrintServerQueues);
        Assert.Equal("PRINTSRV", q.ServerName);
        Assert.Equal("Sales-MFP", q.QueueName);
        Assert.Equal(3, q.QueuedJobs);
        Assert.Contains("PRINTSRV\\Sales-MFP · 3 queued (shared as SalesMFP)", q.Display);
        Assert.Contains("5 queued", printer.PrinterSummary);        // 2 on the printer + 3 on the server
        Assert.Equal("Lobby", Assert.Single(other.PrintServerQueues).QueueName);
        Assert.Empty(server.PrintServerQueues);

        // Idempotent, and the server's own address never links to itself.
        PrintServerQueueLinker.Link(new[] { printer, server, other });
        Assert.Single(printer.PrintServerQueues);
    }

    [Fact]
    public void Linker_MatchesByNameAndSecondaryIp()
    {
        var printer = PrinterMachine();
        printer.IpAddresses.Add("192.168.5.9");
        var byIp = PrintServer("192.168.5.9");
        var byName = PrintServer("npi1a2b3c");
        byName.Printers[0].Name = "Floor2";
        PrintServerQueueLinker.Link(new[] { printer, byIp, byName });
        Assert.Equal(2, printer.PrintServerQueues.Count);
    }

    // --- Windows queue parsing ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Sales-MFP, 41", "Sales-MFP")]
    [InlineData("HP LaserJet 4000 Series PCL, 7", "HP LaserJet 4000 Series PCL")]
    [InlineData("Lobby, Copier, 12", "Lobby, Copier")]
    [InlineData("NoJobId", "NoJobId")]
    [InlineData("", null)]
    public void PrinterNameFromJob_SplitsOnLastComma(string job, string? expected)
        => Assert.Equal(expected, PeripheralsCollector.PrinterNameFromJob(job));

    [Fact]
    public void DescribePrinterState_DecodesFlags()
    {
        Assert.Null(PeripheralsCollector.DescribePrinterState(0));
        Assert.Null(PeripheralsCollector.DescribePrinterState(null));
        Assert.Equal("Paused, Paper jam", PeripheralsCollector.DescribePrinterState(0x1 | 0x8));
        Assert.Equal("Toner low", PeripheralsCollector.DescribePrinterState(0x20000));
    }

    // --- Credential store -----------------------------------------------------------------------------------------

    [Fact]
    public void CredentialStore_RoundTrips_SnmpCommunityAndVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), "marco-snmp-" + Guid.NewGuid().ToString("N")[..8] + ".dat");
        try
        {
            using (var store = new CredentialStore())
            {
                var snmp = new CredentialSet("Site community", null, null, null) { Kind = Marco.Core.Inventory.CredentialKind.Snmp, SnmpVersion = Marco.Core.Snmp.SnmpVersion.V1 };
                snmp.SetPassword("s1te-r0");
                store.Add(snmp);
                store.Add(CredentialSet.SnmpDefault());
                store.Save(path);
                Assert.DoesNotContain("s1te-r0", File.ReadAllText(path));
            }
            using (var loaded = new CredentialStore())
            {
                loaded.Load(path);
                Assert.Equal(2, loaded.Sets.Count);
                Assert.Equal(Marco.Core.Inventory.CredentialKind.Snmp, loaded.Sets[0].Kind);
                Assert.Equal(Marco.Core.Snmp.SnmpVersion.V1, loaded.Sets[0].SnmpVersion);
                Assert.Null(loaded.Sets[1].SnmpVersion);
                var c = loaded.Sets[0].ToCandidate();
                Assert.Equal(Marco.Core.Snmp.SnmpVersion.V1, c.SnmpVersion);
                Assert.Equal("s1te-r0", new System.Net.NetworkCredential("", c.Credential.Password).Password);
                Assert.Equal("public", new System.Net.NetworkCredential("", loaded.Sets[1].ToCandidate().Credential.Password).Password);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
