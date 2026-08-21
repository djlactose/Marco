using Marco.Core.Model;
using Marco.Export;
using Xunit;

namespace Marco.Tests;

public class ExportTests
{
    private static Machine SampleMachine()
    {
        var m = new Machine("10.0.0.10")
        {
            Name = "PC-A",
            Fqdn = "pc-a.corp.local",
            DeviceType = DeviceType.Windows,
            Status = MachineStatus.Done,
        };
        m.System.Manufacturer = "Dell, Inc."; // comma → must be quoted in CSV
        m.System.Model = "OptiPlex";
        m.Os.Caption = "Windows 11 Pro";
        m.TotalMemoryBytes = 17_179_869_184;
        m.Cpus.Add(new CpuInfo { Name = "Intel i7", Cores = 8, LogicalProcessors = 16 });
        m.Disks.Add(new DiskInfo { Model = "SSD", SizeBytes = 512_000_000_000 });
        m.Software.Add(new SoftwareEntry { DisplayName = "App, Deluxe", Version = "1.0", Publisher = "Acme" });
        m.Software.Add(new SoftwareEntry { DisplayName = "Line\"Quote", Version = "2.0" });
        m.Adapters.Add(new AdapterInfo { Name = "NIC", Mac = "00:11:22:33:44:55", SpeedBps = 1_000_000_000 });
        m.RefreshCounts();
        return m;
    }

    private static ScanDocument Doc(params Machine[] machines)
    {
        var meta = new ScanMetadata(new DateTime(2026, 1, 1, 10, 0, 0), "tester",
            new[] { "10.0.0.0/24" }, machines.Length, machines.Length);
        return ScanDocument.From(meta, machines);
    }

    [Fact]
    public void Json_RoundTrips()
    {
        var json = new JsonExporter().Serialize(Doc(SampleMachine()));
        var doc = new JsonExporter().Deserialize(json);

        Assert.Single(doc.Machines);
        var m = doc.Machines[0];
        Assert.Equal("PC-A", m.Name);
        Assert.Equal(DeviceType.Windows, m.DeviceType);
        Assert.Equal(17_179_869_184, m.TotalMemoryBytes);
        Assert.Equal(2, m.Software.Count);
        Assert.Single(m.Cpus);

        // And rehydrate to a live Machine.
        var machine = doc.ToMachines()[0];
        Assert.Equal("PC-A", machine.Name);
        Assert.Equal(2, machine.Software.Count);
        Assert.Equal("Intel i7", machine.Cpus[0].Name);
    }

    [Fact]
    public void Json_UsesEnumNames_NotNumbers()
    {
        var json = new JsonExporter().Serialize(Doc(SampleMachine()));
        Assert.Contains("\"Windows\"", json);
        Assert.DoesNotContain("\"DeviceType\": 1", json);
    }

    [Fact]
    public void Csv_QuotesCommasAndEscapesQuotes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "marco-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var files = new CsvExporter().Export(Doc(SampleMachine()), dir);
            var machines = File.ReadAllText(Path.Combine(dir, "machines.csv"));
            var software = File.ReadAllText(Path.Combine(dir, "software.csv"));

            Assert.Contains("\"Dell, Inc.\"", machines);          // comma field quoted
            Assert.Contains("\"App, Deluxe\"", software);         // comma field quoted
            Assert.Contains("\"Line\"\"Quote\"", software);       // embedded quote doubled
            Assert.True(files.Count >= 4);                        // machines + software + disks + adapters
            Assert.True(File.Exists(Path.Combine(dir, "scan-info.txt")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Csv_EmptyScan_WritesHeadersOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "marco-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            new CsvExporter().Export(Doc(), dir);
            var machines = File.ReadAllLines(Path.Combine(dir, "machines.csv"));
            Assert.Single(machines, l => l.Length > 0); // header row only
            Assert.StartsWith("Address,", machines[0]);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Json_OldFormatWithoutSchemaVersion_StillDeserializes()
    {
        // A pre-1.1.0 export: no SchemaVersion property, hardcoded "1.0" app version.
        const string oldJson = """
        {
          "Metadata": {
            "Timestamp": "2026-01-01T10:00:00",
            "Operator": "tester",
            "RangesScanned": ["10.0.0.0/24"],
            "TotalTargets": 1,
            "AliveCount": 1,
            "Tool": "Marco",
            "Version": "1.0"
          },
          "Machines": []
        }
        """;
        var doc = new JsonExporter().Deserialize(oldJson);

        Assert.Equal("1.0", doc.Metadata.Version);
        Assert.Equal("1", doc.Metadata.SchemaVersion); // record default fills the missing property
        Assert.Empty(doc.ToMachines());
    }

    [Fact]
    public void Metadata_AppVersion_LandsInScanInfo()
    {
        var meta = new ScanMetadata(new DateTime(2026, 1, 1, 10, 0, 0), "tester",
            new[] { "10.0.0.0/24" }, 1, 1, Version: "9.9.9-beta.7");
        var dir = Path.Combine(Path.GetTempPath(), "marco-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            new CsvExporter().Export(ScanDocument.From(meta, new[] { SampleMachine() }), dir);
            var info = File.ReadAllText(Path.Combine(dir, "scan-info.txt"));
            Assert.Contains("9.9.9-beta.7", info);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Csv_SoftwareCompanion_KeyedByMachine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "marco-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            new CsvExporter().Export(Doc(SampleMachine()), dir);
            var software = File.ReadAllLines(Path.Combine(dir, "software.csv"));
            Assert.StartsWith("MachineAddress,MachineName,DisplayName,", software[0]);
            Assert.Contains(software, l => l.StartsWith("10.0.0.10,PC-A,"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // --- Phase 3 additions: every new group and the open-port list round-trip through JSON and land in CSV ---

    private static Machine RichMachine()
    {
        var m = SampleMachine();
        m.OpenPorts.Add(445); m.OpenPorts.Add(135);
        m.Hotfixes.Add(new HotfixEntry { Id = "KB5040442", InstalledOn = new DateTime(2024, 7, 10), Description = "Security Update" });
        m.Updates.DisplayVersion = "23H2"; m.Updates.Ubr = 4037; m.Updates.FullBuild = "10.0.22631.4037";
        m.Updates.HotfixCount = 1; m.Updates.LastHotfixDate = new DateTime(2024, 7, 10);
        m.Updates.PendingReboot = true; m.Updates.PendingRebootReasons = "Windows Update";
        m.Antivirus.Add(new AntivirusEntry { Product = "Windows Defender", Enabled = true, UpToDate = true });
        m.Security.DefenderEnabled = true; m.Security.FirewallDomain = true; m.Security.FirewallPublic = false;
        m.Security.BitLockerVolumes.Add(new BitLockerVolumeEntry { Letter = "C:", Protection = "On", Method = "XTS-AES 256" });
        m.Security.TpmPresent = true; m.Security.TpmVersion = "2.0"; m.Security.SecureBoot = true; m.Security.FirmwareType = "UEFI";
        m.Security.RdpEnabled = true; m.Security.RdpNlaRequired = true; m.Security.Smb1Enabled = false;
        m.LocalAccounts.Add(new LocalAccountEntry { Name = "jdoe", IsAdmin = true, LastLogon = new DateTime(2024, 8, 1) });
        m.LocalAdministrators.Add("PC-A\\jdoe");
        m.UserProfiles.Add(new UserProfileEntry { User = "jdoe", LocalPath = "C:\\Users\\jdoe", LastUse = new DateTime(2024, 8, 1), Loaded = true });
        m.LogonSessions.Add(new LogonSessionEntry { Account = "CORP\\jdoe", LogonType = "Interactive" });
        m.Services.Add(new ServiceEntry { Name = "Spooler", DisplayName = "Print Spooler", State = "Running", StartMode = "Auto", Account = "LocalSystem" });
        m.Services.Add(new ServiceEntry { Name = "Acme", DisplayName = "Acme", State = "Stopped", StartMode = "Auto" });
        m.StartupItems.Add(new StartupEntry { Name = "OneDrive", Command = "OneDrive.exe", Location = "HKCU\\...\\Run", User = "jdoe" });
        m.ScheduledTasks.Add(new ScheduledTaskEntry { Name = "Backup", Path = "\\Acme\\", State = "Ready", RunAs = "svc" });
        m.Monitors.Add(new MonitorEntry { Manufacturer = "Dell", Model = "U2415", Serial = "ABC123", Year = 2019, DiagonalInches = 24.0, Active = true });
        m.Gpus.Add(new GpuInfo { Name = "NVIDIA RTX A2000", VramBytes = 6442450944, DriverVersion = "31.0", Resolution = "1920x1200 @ 60 Hz" });
        m.Printers.Add(new PrinterEntry { Name = "HP LaserJet", IsDefault = true, PortName = "IP_10.0.0.50", HostAddress = "10.0.0.50" });
        m.UsbDevices.Add(new UsbDeviceEntry { Name = "SanDisk Cruzer", Manufacturer = "SanDisk", PnpClass = "USB" });
        m.UsbStorageHistory.Add(new UsbStorageHistoryEntry { FriendlyName = "SanDisk Cruzer USB Device", Vendor = "SanDisk", Product = "Cruzer", Serial = "4C53" });
        m.Battery = new BatteryInfo { Name = "DELL 1VX1H", ChargePercent = 87, HealthPercent = 75, CycleCount = 210 };
        m.ThermalTempC = 40;
        m.Disks[0].MediaType = "SSD"; m.Disks[0].BusType = "NVMe"; m.Disks[0].HealthStatus = "Healthy"; m.Disks[0].TempC = 38;
        m.RefreshCounts();
        return m;
    }

    [Fact]
    public void Json_RoundTrips_Phase3Groups_AndOpenPorts()
    {
        var json = new JsonExporter().Serialize(Doc(RichMachine()));
        var back = new JsonExporter().Deserialize(json).ToMachines()[0];

        Assert.Equal(new[] { 135, 445 }, back.OpenPorts.OrderBy(p => p));
        Assert.Single(back.Hotfixes);
        Assert.Equal("10.0.22631.4037", back.Updates.FullBuild);
        Assert.True(back.Updates.PendingReboot);
        Assert.Equal("Yes", back.Updates.PendingRebootDisplay);
        Assert.Single(back.Antivirus);
        Assert.Contains("Windows Defender (on)", back.AntivirusSummary);
        Assert.True(back.Security.DefenderEnabled);
        Assert.Single(back.Security.BitLockerVolumes);
        Assert.Equal("C: On (XTS-AES 256)", back.Security.BitLockerSummary);
        Assert.Equal("2.0", back.Security.TpmVersion);
        Assert.Equal("Enabled, NLA required", back.Security.RdpSummary);
        Assert.Single(back.LocalAccounts);
        Assert.True(back.LocalAccounts[0].IsAdmin);
        Assert.Equal(new[] { "PC-A\\jdoe" }, back.LocalAdministrators);
        Assert.Single(back.UserProfiles);
        Assert.Single(back.LogonSessions);
        Assert.Equal(2, back.Services.Count);
        Assert.Equal(1, back.StoppedAutoServiceCount);
        Assert.Single(back.StartupItems);
        Assert.Single(back.ScheduledTasks);
        Assert.Single(back.Monitors);
        Assert.Equal("ABC123", back.Monitors[0].Serial);
        Assert.Single(back.Gpus);
        Assert.Equal(6442450944, back.Gpus[0].VramBytes);
        Assert.Single(back.Printers);
        Assert.Single(back.UsbDevices);
        Assert.Single(back.UsbStorageHistory);
        Assert.NotNull(back.Battery);
        Assert.Equal(75, back.Battery!.HealthPercent);
        Assert.Equal(40, back.ThermalTempC);
        Assert.Equal("SSD", back.Disks[0].MediaType);
        Assert.Equal(38, back.Disks[0].TempC);

        // Computed members are not persisted (they'd be noise, and can't be set back anyway).
        Assert.DoesNotContain("\"BitLockerSummary\"", json);
        Assert.DoesNotContain("\"HasData\"", json);
    }

    [Fact]
    public void Json_PreviousSchema_WithoutPhase3Fields_StillOpens()
    {
        // Serialise with the current writer, then strip the new fields to mimic an older file.
        var doc = Doc(SampleMachine());
        var json = new JsonExporter().Serialize(doc);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var machine = node["Machines"]![0]!.AsObject();
        foreach (var key in new[] { "OpenPorts", "Hotfixes", "Antivirus", "Updates", "Security", "LocalAccounts", "Services", "Monitors", "Battery" })
            machine.Remove(key);

        var back = new JsonExporter().Deserialize(node.ToJsonString()).ToMachines()[0];
        Assert.Equal("PC-A", back.Name);
        Assert.Empty(back.Hotfixes);
        Assert.NotNull(back.Updates);   // fresh empty group, never null
        Assert.NotNull(back.Security);
        Assert.Null(back.Battery);
        Assert.Empty(back.OpenPorts);
    }

    // --- Hardware detail: RAM type / platform max / expansion slots ---

    private static Machine HardwareMachine()
    {
        var m = SampleMachine();
        m.MaxMemoryBytes = 64L << 30;
        m.MemoryModules = new List<MemoryModule>
        {
            new() { CapacityBytes = 8L << 30, SpeedMhz = 3200, ConfiguredSpeedMhz = 2933, MemoryTypeName = "DDR4", FormFactor = "SODIMM", SlotLabel = "DIMM A" },
        };
        m.System.ChassisType = "Mini Tower";
        m.System.ExpansionSlotsTotal = 3;
        m.System.ExpansionSlotsFree = 1;
        m.System.ExpansionSlotsFreeList = "M.2_2";
        return m;
    }

    [Fact]
    public void Json_RoundTrips_HardwareDetail()
    {
        var json = new JsonExporter().Serialize(Doc(HardwareMachine()));
        var back = new JsonExporter().Deserialize(json).ToMachines()[0];

        Assert.Equal(64L << 30, back.MaxMemoryBytes);
        var mod = Assert.Single(back.MemoryModules);
        Assert.Equal("DDR4", mod.MemoryTypeName);
        Assert.Equal(2933, mod.ConfiguredSpeedMhz);
        Assert.Equal("SODIMM", mod.FormFactor);
        Assert.Equal(3, back.System.ExpansionSlotsTotal);
        Assert.Equal(1, back.System.ExpansionSlotsFree);
        Assert.Equal("M.2_2", back.System.ExpansionSlotsFreeList);
        Assert.NotNull(back.ExpansionOutlook);

        Assert.DoesNotContain("\"TypeDisplay\"", json);     // computed, not persisted
        Assert.DoesNotContain("\"MemorySummary\"", json);
        Assert.DoesNotContain("\"ExpansionOutlook\"", json);
    }

    [Fact]
    public void Json_PreviousSchema_WithoutHardwareDetail_StillOpens()
    {
        var json = new JsonExporter().Serialize(Doc(HardwareMachine()));
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var machine = node["Machines"]![0]!.AsObject();
        machine.Remove("MaxMemoryBytes");
        var system = machine["System"]!.AsObject();
        foreach (var key in new[] { "ExpansionSlotsTotal", "ExpansionSlotsFree", "ExpansionSlotsFreeList" }) system.Remove(key);
        var module = machine["MemoryModules"]![0]!.AsObject();
        foreach (var key in new[] { "MemoryTypeName", "ConfiguredSpeedMhz", "FormFactor" }) module.Remove(key);

        var back = new JsonExporter().Deserialize(node.ToJsonString()).ToMachines()[0];
        Assert.Null(back.MaxMemoryBytes);
        Assert.Null(back.System.ExpansionSlotsTotal);
        Assert.Null(back.MemoryModules[0].MemoryTypeName);
        Assert.Equal(3200, back.MemoryModules[0].SpeedMhz);
        Assert.Equal("Mini Tower", back.System.ChassisType);
    }

    [Fact]
    public void Csv_WritesCompanionFiles_AndHeadlineColumns()
    {
        var dir = Path.Combine(Path.GetTempPath(), "marco-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var files = new CsvExporter().Export(Doc(RichMachine()), dir);
            foreach (var f in new[] { "security.csv", "hotfixes.csv", "services.csv", "startup.csv", "accounts.csv", "profiles.csv", "monitors.csv", "gpus.csv", "printers.csv", "usb.csv" })
                Assert.Contains(files, p => p.EndsWith(f));

            var machines = File.ReadAllLines(Path.Combine(dir, "machines.csv"));
            Assert.Contains("PendingReboot", machines[0]);
            Assert.Contains("BitLocker", machines[0]);
            Assert.Contains(",23H2,10.0.22631.4037,", machines[1]);
            Assert.Contains("Windows Defender (on)", machines[1]);
            Assert.Contains("C: On (XTS-AES 256)", machines[1]);
            Assert.Contains("135 445", machines[1]);

            var security = File.ReadAllLines(Path.Combine(dir, "security.csv"));
            Assert.StartsWith("MachineAddress,MachineName,Antivirus,", security[0]);
            Assert.Contains(security, l => l.StartsWith("10.0.0.10,PC-A,Windows Defender,Yes,Yes,"));

            var accounts = File.ReadAllLines(Path.Combine(dir, "accounts.csv"));
            Assert.Contains(accounts, l => l.StartsWith("10.0.0.10,PC-A,jdoe,") && l.Contains(",Yes,2024-08-01"));

            var monitors = File.ReadAllLines(Path.Combine(dir, "monitors.csv"));
            Assert.Contains(monitors, l => l.Contains(",Dell,U2415,ABC123,"));

            var usb = File.ReadAllLines(Path.Combine(dir, "usb.csv"));
            Assert.Contains(usb, l => l.Contains(",StorageHistory,SanDisk Cruzer USB Device,"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
