using System.Security;
using Marco.Core.Inventory;
using Marco.Core.Ipp;
using Marco.Core.Model;
using Marco.Core.Snmp;
using Marco.Core.Wmi;
using Marco.Inventory.Ipp;
using Marco.Inventory.Snmp;
using Xunit;

namespace Marco.Tests;

internal sealed class FakeIppClient : IIppClient
{
    public byte[]? PrinterResponse { get; set; }
    public byte[]? JobsResponse { get; set; }
    public IppException? Throws { get; set; }
    public List<string> Calls { get; } = new();

    public static FakeIppClient HpLike() => new()
    {
        PrinterResponse = IppTestBytes.Response(IppOperation.StatusOk, 1, IppTestBytes.Operation(),
            (IppTag.PrinterAttributes, new (byte, string, object)[]
            {
                (IppTag.Enum, "printer-state", 3),
                (IppTag.Keyword, "printer-state-reasons", new object[] { "marker-supply-low-warning" }),
                (IppTag.Integer, "queued-job-count", 2),
                (IppTag.TextWithoutLanguage, "printer-make-and-model", "HP Color LaserJet MFP M479fdw"),
                (IppTag.TextWithoutLanguage, "printer-firmware-string-version", "002_2303A-IPP"),
                (IppTag.Uri, "printer-uri-supported", "ipp://10.0.0.9:631/ipp/print"),
                (IppTag.NameWithoutLanguage, "marker-names", new object[] { "Black Toner", "Cyan Toner" }),
                (IppTag.Keyword, "marker-types", new object[] { "toner", "toner" }),
                (IppTag.NameWithoutLanguage, "marker-colors", new object[] { "#000000", "#00FFFF" }),
                (IppTag.Integer, "marker-levels", new object[] { 9, 81 }),
            })),
        JobsResponse = IppTestBytes.Response(IppOperation.StatusOk, 2, IppTestBytes.Operation(),
            (IppTag.JobAttributes, new (byte, string, object)[] { (IppTag.Integer, "job-id", 41), (IppTag.Enum, "job-state", 5), (IppTag.NameWithoutLanguage, "job-name", "Q3 report.pdf"), (IppTag.NameWithoutLanguage, "job-originating-user-name", "alice"), (IppTag.Integer, "job-impressions", 12) }),
            (IppTag.JobAttributes, new (byte, string, object)[] { (IppTag.Integer, "job-id", 42), (IppTag.Enum, "job-state", 3), (IppTag.NameWithoutLanguage, "job-name", "labels.docx") })),
    };

    public static FakeIppClient Unreachable() => new() { Throws = new IppException(IppFailureKind.Unreachable, "connection refused") };

    public Task<IppResponse> GetPrinterAttributesAsync(string host, IReadOnlyList<string> requestedAttributes, CancellationToken ct)
    {
        Calls.Add("Get-Printer-Attributes");
        if (Throws is not null || PrinterResponse is null) throw Throws ?? new IppException(IppFailureKind.Unreachable, "no response");
        return Task.FromResult(IppCodec.Parse(PrinterResponse));
    }

    public Task<IppResponse> GetJobsAsync(string host, string whichJobs, int limit, IReadOnlyList<string> requestedAttributes, CancellationToken ct)
    {
        Calls.Add("Get-Jobs");
        if (Throws is not null || JobsResponse is null) throw Throws ?? new IppException(IppFailureKind.HttpError, "no jobs");
        return Task.FromResult(IppCodec.Parse(JobsResponse));
    }
}

public class SnmpDeviceInventoryRunnerTests
{
    private static CredentialCandidate Community(string community, string? label = null, SnmpVersion? version = null)
    {
        var s = new SecureString();
        foreach (var c in community) s.AppendChar(c);
        return new CredentialCandidate(label ?? community, new WmiCredential { Password = s }, CredentialKind.Snmp, SnmpVersion: version);
    }

    private static CredentialCandidate WindowsAny(string user)
    {
        var s = new SecureString(); s.AppendChar('x');
        return new CredentialCandidate(user, new WmiCredential { Username = user, Password = s });
    }

    private static SnmpOidTable SwitchTable()
    {
        var t = new SnmpOidTable()
            .Str("1.3.6.1.2.1.1.1.0", "Cisco IOS Software, C2960X Software (C2960X-UNIVERSALK9-M), Version 15.2(7)E4")
            .Oid("1.3.6.1.2.1.1.2.0", "1.3.6.1.4.1.9.1.1208")
            .Ticks("1.3.6.1.2.1.1.3.0", 987654321)
            .Str("1.3.6.1.2.1.1.4.0", "netops@corp.local")
            .Str("1.3.6.1.2.1.1.5.0", "sw-core-01")
            .Str("1.3.6.1.2.1.1.6.0", "Rack 3, MDF")
            .Str("1.3.6.1.2.1.47.1.1.1.1.10.1001", "15.2(7)E4")
            .Str("1.3.6.1.2.1.47.1.1.1.1.11.1001", "FOC1234X5YZ")
            .Str("1.3.6.1.2.1.47.1.1.1.1.12.1001", "Cisco Systems, Inc.")
            .Str("1.3.6.1.2.1.47.1.1.1.1.13.1001", "WS-C2960X-48FPD-L")
            .Str("1.3.6.1.2.1.2.2.1.2.49", "Vlan1").Int("1.3.6.1.2.1.2.2.1.3.49", 53).Hex("1.3.6.1.2.1.2.2.1.6.49", "001a2b3c4d00").Int("1.3.6.1.2.1.2.2.1.8.49", 1)
            .Int("1.3.6.1.2.1.4.20.1.2.10.0.0.2", 49);
        for (int i = 1; i <= 48; i++)
        {
            t.Str($"1.3.6.1.2.1.2.2.1.2.{i}", $"GigabitEthernet1/0/{i}").Int($"1.3.6.1.2.1.2.2.1.3.{i}", 6)
             .Gauge($"1.3.6.1.2.1.2.2.1.5.{i}", 1_000_000_000).Hex($"1.3.6.1.2.1.2.2.1.6.{i}", $"001a2b3c4d{i:x2}")
             .Int($"1.3.6.1.2.1.2.2.1.8.{i}", i % 3 == 0 ? 2 : 1)
             .Str($"1.3.6.1.2.1.31.1.1.1.1.{i}", $"Gi1/0/{i}").Gauge($"1.3.6.1.2.1.31.1.1.1.15.{i}", 1000);
        }
        return t;
    }

    [Fact]
    public async Task HpPrinter_PopulatesEverySection()
    {
        var factory = new FakeSnmpSessionFactory(SnmpOidTable.FromFixture("snmp-hp-m479.txt"));
        var ipp = FakeIppClient.HpLike();
        var runner = new SnmpDeviceInventoryRunner(factory, ipp);
        var m = new Machine("10.0.0.9") { DeviceType = DeviceType.Printer, Vendor = "Hewlett Packard" };

        var outcome = await runner.InventoryAsync(m, new[] { Community("public") }, null, null, default);

        Assert.True(outcome.Authenticated);
        Assert.Equal("public", outcome.CredentialLabel);
        Assert.Equal(MachineStatus.Done, m.Status);
        Assert.Null(m.CurrentActivity);
        Assert.Equal(DeviceType.Printer, m.DeviceType);

        Assert.Equal("HP", m.System.Manufacturer);
        Assert.Equal("Color LaserJet MFP M479fdw", m.System.Model);   // vendor prefix stripped from hrDeviceDescr
        Assert.Equal("VNC1K23456", m.System.SerialNumber);
        Assert.Equal("NPI1A2B3C", m.Name);                            // sysName fills an empty name
        Assert.Contains("3C:D9:2B:11:22:33", m.MacAddresses);
        Assert.Single(m.Adapters);

        var p = m.Printer!;
        Assert.Equal("Idle", p.Status);
        Assert.Equal("Running", p.DeviceStatus);
        Assert.Equal(new[] { PrinterErrorStates.LowToner }, p.ErrorStates);
        Assert.Equal(6, p.Supplies.Count);                           // SNMP supplies win over IPP markers
        Assert.Equal(8, p.Supplies[0].Percent);
        Assert.Equal(45210, p.TotalPages);
        Assert.Equal("impressions", p.PageCountUnit);
        Assert.Equal(2, p.Trays.Count);
        Assert.Single(p.Alerts);
        Assert.Equal(new[] { "Front Door: closed" }, p.Covers);
        Assert.Equal(new[] { "Ready" }, p.DisplayText);
        Assert.Equal("2nd floor copy room", p.Location);
        Assert.Equal("IT Helpdesk", p.Contact);
        Assert.Equal(TimeSpan.FromDays(10), p.Uptime);
        Assert.Equal("002_2303A", p.Firmware);                        // Entity-MIB beats IPP firmware
        // IPP
        Assert.Equal("idle", p.IppState);
        Assert.Equal(new[] { "marker-supply-low-warning" }, p.IppStateReasons);
        Assert.Equal(2, p.QueuedJobs);
        Assert.Equal(2, p.Jobs.Count);
        Assert.Equal("processing", p.Jobs[0].State);
        Assert.Equal("alice", p.Jobs[0].User);
        Assert.Equal(new[] { "SNMP v2c", "IPP" }, p.Sources);
        Assert.Equal(new[] { "Get-Printer-Attributes", "Get-Jobs" }, ipp.Calls);

        Assert.Equal(1, p.LowSupplyCount);
        Assert.Contains("K 8%", p.SuppliesSummary);
        Assert.Contains("45,210 pages", m.PrinterSummary);
        Assert.Contains("2 queued", m.PrinterSummary);
        Assert.All(SnmpDeviceInventoryRunner.PrinterCollectorNames, n => Assert.Equal(CollectorStatus.Ok, m.Collectors.Single(c => c.Name == n).Status));
    }

    [Fact]
    public async Task Brother_SerialFromVendorOid_LevelsSomeRemaining()
    {
        var factory = new FakeSnmpSessionFactory(SnmpOidTable.FromFixture("snmp-brother-l3770.txt"));
        var runner = new SnmpDeviceInventoryRunner(factory, FakeIppClient.Unreachable());
        var m = new Machine("10.0.0.12") { DeviceType = DeviceType.Printer };

        await runner.InventoryAsync(m, new[] { Community("public") }, null, null, default);

        Assert.Equal("Brother", m.System.Manufacturer);               // from sysObjectID enterprise arc
        Assert.Equal("MFC-L3770CDW series", m.System.Model);          // hrDeviceDescr, not the NIC's sysDescr
        Assert.Equal("E78123K4N567890", m.System.SerialNumber);       // prtGeneralSerialNumber empty → vendor OID
        Assert.Equal(MachineStatus.Partial, m.Status);                // IPP unreachable = NotSupported, not a failure
        Assert.Equal(CollectorStatus.NotSupported, m.Collectors.Single(c => c.Name == "PrintQueue").Status);
        Assert.All(m.Printer!.Supplies.Where(s => s.Type == "toner"), s => Assert.True(s.SomeRemaining));
        Assert.Equal(1823, m.Printer.TotalPages);
        Assert.Equal(new[] { "SNMP v2c" }, m.Printer.Sources);
        Assert.Contains("IPP (port 631) not available", m.StatusDetail);
    }

    [Fact]
    public async Task Kyocera_SleepingStatus_EntitySerial()
    {
        var factory = new FakeSnmpSessionFactory(SnmpOidTable.FromFixture("snmp-kyocera-m2640.txt"));
        var runner = new SnmpDeviceInventoryRunner(factory, null);
        var m = new Machine("10.0.0.13") { DeviceType = DeviceType.Printer };
        await runner.InventoryAsync(m, new[] { Community("public") }, null, null, default);

        Assert.Equal("Idle (sleep)", m.Printer!.Status);
        Assert.Equal("VCF1234567", m.System.SerialNumber);
        Assert.Equal("KYOCERA Document Solutions", m.System.Manufacturer);
        Assert.Equal("ECOSYS M2640idw", m.System.Model);
        Assert.Equal(120553, m.Printer.TotalPages);
        Assert.Empty(m.Printer.ErrorStates);
        Assert.False(m.Printer.HasErrorCondition);
    }

    [Fact]
    public async Task CommunityOrder_TriesUntilOneAnswers_ThenRemembers()
    {
        var factory = new FakeSnmpSessionFactory(SnmpOidTable.FromFixture("snmp-hp-m479.txt")) { Community = "s3cret" };
        var runner = new SnmpDeviceInventoryRunner(factory, null);
        var m = new Machine("10.0.0.9") { DeviceType = DeviceType.Printer };
        var creds = new[] { Community("public", "SNMP default"), Community("s3cret", "Site community") };

        var outcome = await runner.InventoryAsync(m, creds, null, null, default);
        Assert.True(outcome.Authenticated);
        Assert.Equal("Site community", outcome.CredentialLabel);
        Assert.Equal("public", factory.Attempts[0].Community);          // tried first, both versions, silently
        Assert.Contains(factory.Attempts, a => a.Community == "s3cret");

        factory.Attempts.Clear();
        await runner.InventoryAsync(new Machine("10.0.0.9") { DeviceType = DeviceType.Printer }, creds, null, null, default);
        Assert.Equal("s3cret", factory.Attempts[0].Community);          // remembered winner goes first
    }

    [Fact]
    public async Task PinnedVersion_OnlyThatVersionIsSent()
    {
        var factory = new FakeSnmpSessionFactory(SnmpOidTable.FromFixture("snmp-hp-m479.txt"));
        var runner = new SnmpDeviceInventoryRunner(factory, null);
        var m = new Machine("10.0.0.9") { DeviceType = DeviceType.Printer };
        await runner.InventoryAsync(m, new[] { Community("public", version: SnmpVersion.V1) }, null, null, default);
        Assert.All(factory.Attempts, a => Assert.Equal(SnmpVersion.V1, a.Version));
        Assert.Equal(new[] { "SNMP v1" }, m.Printer!.Sources);
    }

    [Fact]
    public async Task SnmpSilent_IppAnswers_IsAuthenticatedViaIpp()
    {
        var factory = new FakeSnmpSessionFactory(SnmpOidTable.FromFixture("snmp-hp-m479.txt")) { Community = "nobody-knows" };
        var ipp = FakeIppClient.HpLike();
        var runner = new SnmpDeviceInventoryRunner(factory, ipp);
        var m = new Machine("10.0.0.9") { DeviceType = DeviceType.Printer, Vendor = "HP" };

        var outcome = await runner.InventoryAsync(m, new[] { Community("public") }, null, null, default);

        Assert.True(outcome.Authenticated);
        Assert.Equal(SnmpDeviceInventoryRunner.IppCredentialLabel, outcome.CredentialLabel);
        Assert.Equal(MachineStatus.Partial, m.Status);
        Assert.Equal(CollectorStatus.NotSupported, m.Collectors.Single(c => c.Name == "Supplies").Status);
        Assert.Equal(CollectorStatus.Ok, m.Collectors.Single(c => c.Name == "PrintQueue").Status);
        var p = m.Printer!;
        Assert.Equal(2, p.Supplies.Count);                            // IPP markers fill in
        Assert.Equal(9, p.Supplies[0].Percent);
        Assert.Equal("Idle", p.Status);                               // from IPP printer-state
        Assert.Equal("002_2303A-IPP", p.Firmware);
        Assert.Equal("Color LaserJet MFP M479fdw", m.System.Model);
        Assert.Equal(new[] { "IPP" }, p.Sources);
        Assert.Contains("IPP only", m.StatusDetail);
    }

    [Fact]
    public async Task NothingAnswers_IsUnreachable_NotRed()
    {
        var factory = new FakeSnmpSessionFactory(new SnmpOidTable()) { Community = "nobody-knows" };
        var runner = new SnmpDeviceInventoryRunner(factory, FakeIppClient.Unreachable());
        var m = new Machine("10.0.0.9") { DeviceType = DeviceType.Printer };

        var outcome = await runner.InventoryAsync(m, new[] { Community("public") }, null, null, default);

        Assert.False(outcome.Authenticated);
        Assert.Equal(MachineStatus.Unreachable, m.Status);
        Assert.Equal(ConnectFailure.SnmpNoResponse, m.ConnectFailure);
        Assert.Contains("No SNMP v1/v2c response", m.StatusDetail);
        Assert.Contains("no IPP on port 631", m.StatusDetail);
        Assert.Null(m.Printer);
    }

    [Fact]
    public async Task PortUnreachable_StopsTryingCommunities_IsSnmpDisabled()
    {
        var factory = new FakeSnmpSessionFactory(new SnmpOidTable()) { PortUnreachable = true };
        var runner = new SnmpDeviceInventoryRunner(factory, null);
        var m = new Machine("10.0.0.9") { DeviceType = DeviceType.Printer };

        await runner.InventoryAsync(m, new[] { Community("a"), Community("b"), Community("c") }, null, null, default);

        Assert.Equal(ConnectFailure.SnmpDisabled, m.ConnectFailure);
        Assert.Equal(MachineStatus.Unreachable, m.Status);
        Assert.Single(factory.Attempts.Select(a => a.Community).Distinct()); // gave up after the first community
    }

    [Fact]
    public async Task WindowsCredentials_AreNeverSentAsCommunities()
    {
        var factory = new FakeSnmpSessionFactory(SnmpOidTable.FromFixture("snmp-hp-m479.txt"));
        var runner = new SnmpDeviceInventoryRunner(factory, null);
        var m = new Machine("10.0.0.9") { DeviceType = DeviceType.Printer };

        var outcome = await runner.InventoryAsync(m, new[] { WindowsAny("admin"), new CredentialCandidate("token", WmiCredential.CurrentToken) }, null, null, default);

        Assert.False(outcome.Authenticated);
        Assert.Empty(factory.Attempts);
        Assert.Equal(ConnectFailure.NoCredentials, m.ConnectFailure);
        Assert.False(WindowsAny("admin").AppliesTo(CredentialKind.Snmp));
        Assert.True(Community("public").AppliesTo(CredentialKind.Snmp));
        Assert.False(Community("public").AppliesTo(CredentialKind.Windows));
    }

    [Fact]
    public async Task Switch_GetsSystemAndNetworkOnly()
    {
        var factory = new FakeSnmpSessionFactory(SwitchTable());
        var runner = new SnmpDeviceInventoryRunner(factory, FakeIppClient.HpLike());
        var m = new Machine("10.0.0.2") { DeviceType = DeviceType.NetworkDevice };

        var outcome = await runner.InventoryAsync(m, new[] { Community("public") }, null, null, default);

        Assert.True(outcome.Authenticated);
        Assert.Equal(MachineStatus.Done, m.Status);
        Assert.Equal(DeviceType.NetworkDevice, m.DeviceType);
        Assert.Null(m.Printer);
        var nd = m.NetworkDevice!;
        Assert.Equal("sw-core-01", nd.SysName);
        Assert.Equal("Rack 3, MDF", nd.Location);
        Assert.Equal(49, nd.InterfaceCount);
        Assert.Equal(33, nd.InterfacesUp);
        Assert.Equal("15.2(7)E4", nd.Firmware);
        Assert.Equal("Cisco Systems, Inc.", m.System.Manufacturer);
        Assert.Equal("WS-C2960X-48FPD-L", m.System.Model);
        Assert.Equal("FOC1234X5YZ", m.System.SerialNumber);
        Assert.Equal(49, m.Adapters.Count);
        Assert.Contains("10.0.0.2", m.Adapters.Single(a => a.Name == "Vlan1").IpAddresses);
        Assert.Equal(new[] { "System", "Network" }, m.Collectors.Select(c => c.Name));
    }

    [Fact]
    public async Task NetworkDevice_ThatIsReallyAPrinter_IsReTyped()
    {
        var factory = new FakeSnmpSessionFactory(SnmpOidTable.FromFixture("snmp-hp-m479.txt"));
        var runner = new SnmpDeviceInventoryRunner(factory, null);
        var m = new Machine("10.0.0.9") { DeviceType = DeviceType.NetworkDevice };

        await runner.InventoryAsync(m, new[] { Community("public") }, null, null, default);

        Assert.Equal(DeviceType.Printer, m.DeviceType);
        Assert.NotNull(m.Printer);
        Assert.Null(m.NetworkDevice);
        Assert.Equal(6, m.Printer!.Supplies.Count);
        Assert.Contains("Re-classified as printer", m.StatusDetail);
    }

    [Fact]
    public async Task DisabledCollectors_AreSkipped()
    {
        var factory = new FakeSnmpSessionFactory(SnmpOidTable.FromFixture("snmp-hp-m479.txt"));
        var runner = new SnmpDeviceInventoryRunner(factory, FakeIppClient.HpLike());
        var m = new Machine("10.0.0.9") { DeviceType = DeviceType.Printer };
        var enabled = new HashSet<string> { "System", "Supplies" };

        await runner.InventoryAsync(m, new[] { Community("public") }, null, enabled, default);

        Assert.Equal(new[] { "System", "Supplies" }, m.Collectors.Select(c => c.Name));
        Assert.Equal(6, m.Printer!.Supplies.Count);
        Assert.Null(m.Printer.TotalPages);
        Assert.Empty(m.Adapters);
    }

    [Fact]
    public async Task PerHostOverride_WinsOverOrder()
    {
        var factory = new FakeSnmpSessionFactory(SnmpOidTable.FromFixture("snmp-hp-m479.txt")) { Community = "site" };
        var runner = new SnmpDeviceInventoryRunner(factory, null);
        var m = new Machine("10.0.0.9") { DeviceType = DeviceType.Printer };
        var overrides = new Dictionary<string, CredentialCandidate> { ["10.0.0.9"] = Community("site") };

        var outcome = await runner.InventoryAsync(m, new[] { Community("public") }, overrides, null, default);
        Assert.True(outcome.Authenticated);
        Assert.Equal("site", outcome.CredentialLabel);
        Assert.All(factory.Attempts, a => Assert.Equal("site", a.Community));
    }
}
