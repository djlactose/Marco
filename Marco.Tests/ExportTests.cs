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
}
