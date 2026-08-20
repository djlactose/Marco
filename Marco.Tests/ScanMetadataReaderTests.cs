using Marco.Core.Model;
using Marco.Export;
using Marco.Export.History;
using Xunit;

namespace Marco.Tests;

public class ScanMetadataReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "marco-meta-" + Guid.NewGuid().ToString("N")[..8]);

    public ScanMetadataReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string Write(string name, bool compress, int machineCount = 1)
    {
        var machines = Enumerable.Range(0, machineCount).Select(i =>
        {
            var m = new Machine($"10.0.0.{i + 1}") { Name = $"PC-{i}", Status = MachineStatus.Done };
            // Bulk the document out so the metadata head is a tiny fraction of the file.
            for (int s = 0; s < 50; s++)
                m.Software.Add(new SoftwareEntry { DisplayName = $"App {s}", Version = "1.0", Publisher = "Acme" });
            return m;
        });
        var meta = new ScanMetadata(new DateTime(2026, 3, 4, 5, 6, 7), "tester",
            new[] { "10.0.0.0/24", "192.168.1.0/24" }, machineCount, machineCount);
        var path = Path.Combine(_dir, name);
        new JsonExporter().Export(ScanDocument.From(meta, machines), path, compress);
        return path;
    }

    [Fact]
    public void Reads_MetadataOnly_FromPlainJson()
    {
        var meta = ScanMetadataReader.TryRead(Write("scan.json", compress: false, machineCount: 100));

        Assert.NotNull(meta);
        Assert.Equal(new DateTime(2026, 3, 4, 5, 6, 7), meta!.Timestamp);
        Assert.Equal("tester", meta.Operator);
        Assert.Equal(2, meta.RangesScanned.Count);
        Assert.Equal(100, meta.TotalTargets);
    }

    [Fact]
    public void Reads_MetadataOnly_FromGzip()
    {
        var meta = ScanMetadataReader.TryRead(Write("scan.json.gz", compress: true, machineCount: 100));

        Assert.NotNull(meta);
        Assert.Equal("tester", meta!.Operator);
    }

    [Fact]
    public void MalformedFile_ReturnsNull_NeverThrows()
    {
        var garbage = Path.Combine(_dir, "garbage.json");
        File.WriteAllText(garbage, "this is { not json");
        Assert.Null(ScanMetadataReader.TryRead(garbage));

        var wrongShape = Path.Combine(_dir, "wrong.json");
        File.WriteAllText(wrongShape, "{\"SomethingElse\": {\"a\": 1}}");
        Assert.Null(ScanMetadataReader.TryRead(wrongShape));

        Assert.Null(ScanMetadataReader.TryRead(Path.Combine(_dir, "missing.json")));
    }

    [Fact]
    public void GzipLoad_SniffsMagicBytes_RegardlessOfExtension()
    {
        // A gzipped document renamed to plain .json still opens (Load sniffs, never trusts the name).
        var gz = Write("mislabeled.json", compress: true);
        var doc = new JsonExporter().Load(gz);
        Assert.Equal("tester", doc.Metadata.Operator);
    }
}
