using Marco.Core.Model;
using Marco.Export;
using Marco.Export.History;
using Xunit;

namespace Marco.Tests;

public class ScanHistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "marco-hist-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static ScanDocument Doc(DateTime timestamp, bool inventoried = false, string range = "10.0.0.0/24")
    {
        var m = new Machine("10.0.0.10") { Name = "PC-A", Status = MachineStatus.Done };
        m.LastScanned = timestamp; // discovery sets this on every host — it must NOT count as inventoried
        if (inventoried) m.SetCollector("System", CollectorStatus.Ok, null);
        var meta = new ScanMetadata(timestamp, "tester", new[] { range }, 1, 1);
        return ScanDocument.From(meta, new[] { m });
    }

    [Fact]
    public void Save_WritesGzipDocument_AndIndexEntry()
    {
        var store = new ScanHistoryStore(_dir);
        var when = new DateTime(2026, 1, 1, 10, 0, 0);

        var entry = store.Save(Doc(when), ScanHistoryStore.NewRunId(when), ScanHistoryPhase.DiscoveryOnly);

        var path = Path.Combine(_dir, entry.File);
        Assert.True(File.Exists(path));
        var head = File.ReadAllBytes(path).Take(2).ToArray();
        Assert.Equal(new byte[] { 0x1F, 0x8B }, head); // actually gzipped

        var listed = store.List();
        Assert.Single(listed);
        Assert.Equal(entry.Id, listed[0].Id);
        Assert.Equal(ScanHistoryPhase.DiscoveryOnly, listed[0].Phase);
        Assert.Equal(new[] { "10.0.0.0/24" }, listed[0].Ranges);
        Assert.Equal("tester", listed[0].Operator);
        Assert.Equal(0, listed[0].InventoriedCount); // LastScanned alone (set by discovery) is not "inventoried"
        Assert.True(listed[0].SizeBytes > 0);
    }

    [Fact]
    public void Save_SameRunId_UpgradesInPlace()
    {
        var store = new ScanHistoryStore(_dir);
        var when = new DateTime(2026, 1, 1, 10, 0, 0);
        var id = ScanHistoryStore.NewRunId(when);

        store.Save(Doc(when), id, ScanHistoryPhase.DiscoveryOnly);
        store.Save(Doc(when.AddMinutes(5), inventoried: true), id, ScanHistoryPhase.Inventoried);

        var listed = store.List();
        Assert.Single(listed); // one run = one entry = one file
        Assert.Equal(ScanHistoryPhase.Inventoried, listed[0].Phase);
        Assert.Equal(1, listed[0].InventoriedCount);
        Assert.Single(Directory.GetFiles(_dir, "*.json.gz"));
    }

    [Fact]
    public void Save_PrunesOldestBeyondLimit()
    {
        var store = new ScanHistoryStore(_dir);
        var start = new DateTime(2026, 1, 1, 8, 0, 0);
        for (int i = 0; i < 5; i++)
            store.Save(Doc(start.AddHours(i)), ScanHistoryStore.NewRunId(start.AddHours(i)), ScanHistoryPhase.DiscoveryOnly, limit: 3);

        var listed = store.List();
        Assert.Equal(3, listed.Count);
        Assert.Equal(start.AddHours(4), listed[0].Timestamp); // newest kept, oldest gone
        Assert.Equal(start.AddHours(2), listed[^1].Timestamp);
        Assert.Equal(3, Directory.GetFiles(_dir, "*.json.gz").Length); // pruned files really deleted
    }

    [Fact]
    public void List_DropsEntriesWhoseFileVanished()
    {
        var store = new ScanHistoryStore(_dir);
        var when = new DateTime(2026, 1, 1, 10, 0, 0);
        var entry = store.Save(Doc(when), ScanHistoryStore.NewRunId(when), ScanHistoryPhase.DiscoveryOnly);

        File.Delete(Path.Combine(_dir, entry.File)); // sync client / operator removed it behind our back

        Assert.Empty(store.List());
    }

    [Fact]
    public void List_CorruptIndex_RebuildsFromDocuments()
    {
        var store = new ScanHistoryStore(_dir);
        var when = new DateTime(2026, 1, 1, 10, 0, 0);
        var entry = store.Save(Doc(when), ScanHistoryStore.NewRunId(when), ScanHistoryPhase.DiscoveryOnly);

        File.WriteAllText(Path.Combine(_dir, "index.json"), "{ not json at all");

        var listed = store.List();
        Assert.Single(listed);
        Assert.Equal(entry.Id, listed[0].Id);
        Assert.Equal(ScanHistoryPhase.Unknown, listed[0].Phase); // phase is honest: the index record was lost
        Assert.Equal(when, listed[0].Timestamp);                 // metadata head still readable
    }

    [Fact]
    public void List_AdoptsPlainJsonDroppedIntoTheFolder()
    {
        Directory.CreateDirectory(_dir);
        new JsonExporter().Export(Doc(new DateTime(2026, 2, 2, 9, 0, 0)), Path.Combine(_dir, "manual-export.json"));

        var listed = new ScanHistoryStore(_dir).List();

        Assert.Single(listed);
        Assert.Equal("manual-export", listed[0].Id);
        Assert.Equal(ScanHistoryPhase.Unknown, listed[0].Phase);
    }

    [Fact]
    public void List_IgnoresForeignFiles()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "notes.json"), "{\"hello\":1}");
        File.WriteAllText(Path.Combine(_dir, "readme.txt"), "not a scan");

        Assert.Empty(new ScanHistoryStore(_dir).List());
    }

    [Fact]
    public void TwoStores_ConcurrentSaves_BothSurvive()
    {
        // Two windows saving at once: distinct ids → distinct files; mutexed index merges both entries.
        var a = new ScanHistoryStore(_dir);
        var b = new ScanHistoryStore(_dir);
        var when = new DateTime(2026, 1, 1, 10, 0, 0);
        var idA = ScanHistoryStore.NewRunId(when);
        var idB = ScanHistoryStore.NewRunId(when);

        Parallel.Invoke(
            () => a.Save(Doc(when), idA, ScanHistoryPhase.DiscoveryOnly),
            () => b.Save(Doc(when.AddSeconds(1)), idB, ScanHistoryPhase.Inventoried));

        var listed = a.List();
        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, e => e.Id == idA);
        Assert.Contains(listed, e => e.Id == idB);
    }

    [Fact]
    public void Delete_RemovesEntryAndFile_TolerantWhenAlreadyGone()
    {
        var store = new ScanHistoryStore(_dir);
        var when = new DateTime(2026, 1, 1, 10, 0, 0);
        var entry = store.Save(Doc(when), ScanHistoryStore.NewRunId(when), ScanHistoryPhase.DiscoveryOnly);

        store.Delete(entry);
        store.Delete(entry); // second delete is a no-op, not an error

        Assert.Empty(store.List());
        Assert.Empty(Directory.GetFiles(_dir, "*.json.gz"));
    }

    [Fact]
    public void NewRunId_IsSortableAndDistinct()
    {
        var when = new DateTime(2026, 8, 20, 13, 45, 2);
        var a = ScanHistoryStore.NewRunId(when);
        var b = ScanHistoryStore.NewRunId(when);

        Assert.StartsWith("20260820-134502-", a);
        Assert.NotEqual(a, b); // random suffix keeps same-second ids from two windows apart
    }

    [Fact]
    public void LoadDocument_RoundTrips()
    {
        var store = new ScanHistoryStore(_dir);
        var when = new DateTime(2026, 1, 1, 10, 0, 0);
        var entry = store.Save(Doc(when, inventoried: true), ScanHistoryStore.NewRunId(when), ScanHistoryPhase.Inventoried);

        var doc = store.LoadDocument(entry);

        Assert.Single(doc.Machines);
        Assert.Equal("PC-A", doc.Machines[0].Name);
        Assert.Equal(when, doc.Metadata.Timestamp);
    }
}
