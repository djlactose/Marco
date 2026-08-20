using Marco.Core.Baseline;
using Marco.Core.Model;
using Xunit;

namespace Marco.Tests;

public class BaselineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "marco-base-" + Guid.NewGuid().ToString("N")[..8]);
    private string BaselinePath => Path.Combine(_dir, "baseline.json");

    public BaselineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static Machine Device(string address, string? name = null, string? serial = null, params string[] macs)
    {
        var m = new Machine(address) { Name = name, Status = MachineStatus.Alive, IsAlive = true, TargetBlock = "10.0.0.0/24" };
        m.System.SerialNumber = serial;
        foreach (var mac in macs) m.MacAddresses.Add(mac);
        return m;
    }

    [Fact]
    public void Build_SkipsBogusSerials_AndSegregatesRandomizedMacs()
    {
        var baseline = BaselineEvaluator.Build(new[]
        {
            Device("10.0.0.20", "PC-A", "To be filled by O.E.M.", "3C:52:82:AA:BB:CC", "D2:11:22:33:44:55"),
        }, "op", null);

        var entry = Assert.Single(baseline.Entries);
        Assert.Null(entry.Serial);                                  // placeholder rejected
        Assert.Equal(new[] { "3C5282AABBCC" }, entry.Macs);         // burned-in
        Assert.Equal(new[] { "D21122334455" }, entry.RandomizedMacs);
    }

    [Fact]
    public void Evaluate_KnownBySerial_SurvivesMacAndAddressChange()
    {
        var baseline = BaselineEvaluator.Build(new[] { Device("10.0.0.20", "PC-A", "SER-1", "3C:52:82:AA:BB:CC") }, "op", null);
        var moved = Device("10.0.0.99", "PC-A-renamed", "SER-1", "00:11:22:33:44:55");

        var summary = BaselineEvaluator.Evaluate(new[] { moved }, baseline);

        Assert.Equal(BaselineStatus.Known, moved.BaselineStatus);
        Assert.Equal(1, summary.Known);
    }

    [Fact]
    public void Evaluate_UnknownDevice_IsFlagged()
    {
        var baseline = BaselineEvaluator.Build(new[] { Device("10.0.0.20", "PC-A", "SER-1", "3C:52:82:AA:BB:CC") }, "op", null);
        var rogue = Device("10.0.0.66", "raspberrypi", null, "B8:27:EB:11:22:33"); // Raspberry Pi OUI, hardware MAC

        var summary = BaselineEvaluator.Evaluate(new[] { rogue }, baseline);

        Assert.Equal(BaselineStatus.Unknown, rogue.BaselineStatus);
        Assert.Equal("NEW", rogue.BaselineTag);
        Assert.Same(rogue, Assert.Single(summary.UnknownMachines));
    }

    [Fact]
    public void Evaluate_RandomizedMacOnly_IsWeak_UntilSerialArrives()
    {
        var baseline = BaselineEvaluator.Build(new[] { Device("10.0.0.20", "laptop", "SER-1", "3C:52:82:AA:BB:CC") }, "op", null);
        var wifi = Device("10.0.0.30", null, null, "D2:99:88:77:66:55"); // randomized, unseen

        BaselineEvaluator.Evaluate(new[] { wifi }, baseline);
        Assert.Equal(BaselineStatus.UnknownWeak, wifi.BaselineStatus);
        Assert.Equal("NEW?", wifi.BaselineTag);

        // Inventory recovers the serial: the SAME device re-evaluates to Known.
        wifi.System.SerialNumber = "SER-1";
        BaselineEvaluator.Evaluate(new[] { wifi }, baseline);
        Assert.Equal(BaselineStatus.Known, wifi.BaselineStatus);
    }

    [Fact]
    public void Evaluate_NoHardwareIdentity_FallsBackToName()
    {
        // Off-subnet discovery: no ARP, no serial — the name decides.
        var baseline = BaselineEvaluator.Build(new[] { Device("192.168.5.10", "printer-2f") }, "op", null);

        var samePrinter = Device("192.168.5.44", "printer-2f");
        var stranger = Device("192.168.5.45", "evil-box");
        BaselineEvaluator.Evaluate(new[] { samePrinter, stranger }, baseline);

        Assert.Equal(BaselineStatus.Known, samePrinter.BaselineStatus);
        Assert.Equal(BaselineStatus.Unknown, stranger.BaselineStatus);
    }

    [Fact]
    public void Store_RoundTrips_AndCorruptFileReadsAsNoBaseline()
    {
        var store = new BaselineStore(BaselinePath);
        Assert.False(store.Exists);
        Assert.Null(store.Load());

        store.Replace(BaselineEvaluator.Build(new[] { Device("10.0.0.20", "PC-A", "SER-1") }, "op", "run-1"));
        var loaded = store.Load()!;
        Assert.Single(loaded.Entries);
        Assert.Equal("run-1", loaded.SourceScanId);

        File.WriteAllText(BaselinePath, "{ nope");
        Assert.Null(store.Load());
    }

    [Fact]
    public void AddEntries_MergesOnTopOfCurrentFile_LikeConcurrentWindows()
    {
        var a = new BaselineStore(BaselinePath);
        var b = new BaselineStore(BaselinePath);
        a.Replace(BaselineEvaluator.Build(new[] { Device("10.0.0.20", "PC-A", "SER-1") }, "op", null));

        // Two windows trust different devices "simultaneously" — both must survive.
        a.AddEntries(new[] { BaselineEvaluator.ToEntry(Device("10.0.0.30", "PC-B", "SER-2"), "Trusted") }, "op-a");
        b.AddEntries(new[] { BaselineEvaluator.ToEntry(Device("10.0.0.40", "PC-C", "SER-3"), "Trusted") }, "op-b");

        var final = a.Load()!;
        Assert.Equal(3, final.Entries.Count);
        Assert.Contains(final.Entries, e => e.Serial == "SER-2");
        Assert.Contains(final.Entries, e => e.Serial == "SER-3");
    }
}
