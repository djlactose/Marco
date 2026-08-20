using System.Text.Json;
using Marco.Export.Diff;
using Xunit;

namespace Marco.Tests;

public class DiffExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "marco-diffx-" + Guid.NewGuid().ToString("N")[..8]);

    public DiffExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static ScanDiffResult SampleDiff() => new(
        new DiffMetadata(new DateTime(2026, 1, 1), new DateTime(2026, 2, 1),
            new[] { "10.0.0.0/24" }, new[] { "10.0.0.0/24" }, 2, 2),
        Added: new[] { new MachineRef("10.0.0.40", "NEW-PC", "SER-C", null, null) },
        Removed: new[] { new MachineRef("10.0.0.30", "OLD-PC", "SER-B", null, null) },
        Changed: new[]
        {
            new MachineDiff(new MachineRef("10.0.0.20", "PC-A", "SER-A", "3C:52:82:AA:BB:CC", MatchKind.Serial),
                new[]
                {
                    new FieldChange(DiffCategory.Software, "Software", DiffChangeKind.Added, null, "AnyDesk, remote 8.0", DiffSeverity.Info),
                    new FieldChange(DiffCategory.Security, "BitLocker C:", DiffChangeKind.Changed, "On", "Off", DiffSeverity.Regression),
                }),
        });

    [Fact]
    public void Json_RoundTrips()
    {
        var path = Path.Combine(_dir, "diff.json");
        DiffExporters.ExportJson(SampleDiff(), path);

        var loaded = JsonSerializer.Deserialize<ScanDiffResult>(File.ReadAllText(path),
            new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Added);
        Assert.Equal(2, Assert.Single(loaded.Changed).Changes.Count);
        Assert.Equal(DiffSeverity.Regression, loaded.Changed[0].Changes[1].Severity);
        Assert.Contains("\"Regression\"", File.ReadAllText(path)); // enum names, not numbers
    }

    [Fact]
    public void Csv_HasHeaderAndQuotedFields()
    {
        var path = Path.Combine(_dir, "diff.csv");
        DiffExporters.ExportCsv(SampleDiff(), path);

        var lines = File.ReadAllLines(path);
        Assert.StartsWith("Machine,Address,MatchedBy,Category,Item,Change,Old,New,Severity", lines[0]);
        Assert.Equal(5, lines.Count(l => l.Length > 0)); // header + added + removed + 2 changes
        Assert.Contains(lines, l => l.Contains("\"AnyDesk, remote 8.0\"")); // comma field quoted
        Assert.Contains(lines, l => l.Contains("Regression"));
    }
}
