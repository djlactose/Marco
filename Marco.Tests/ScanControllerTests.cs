using System.Collections.Concurrent;
using Marco.Core.Model;
using Marco.Core.Scanning;
using Marco.Core.Targets;
using Marco.Discovery;
using Xunit;

namespace Marco.Tests;

public class ScanControllerTests
{
    private static ScanController Build(
        FakeLivenessProbe liveness,
        FakeNameResolver? names = null,
        FakeMacResolver? macs = null,
        FakeOuiLookup? oui = null)
        => new(liveness, names ?? new FakeNameResolver(), macs ?? new FakeMacResolver(),
               oui ?? new FakeOuiLookup(), new DeviceClassifier());

    private static IReadOnlyList<ScanTarget> Targets(params string[] addrs)
        => addrs.Select(a => new ScanTarget(a, false)).ToList();

    private static ScanSettings FastSettings() => new()
    {
        DiscoveryConcurrency = 8,
        ClassificationEnabled = true,
        ResolveNames = true,
        ResolveMac = true,
    };

    [Fact]
    public async Task EmitsOnlyAliveHosts_ByDefault()
    {
        var liveness = new FakeLivenessProbe();
        liveness.Alive.Add("10.0.0.1");
        liveness.Alive.Add("10.0.0.3");

        var emitted = new ConcurrentBag<Machine>();
        var controller = Build(liveness);

        await controller.RunDiscoveryAsync(
            Targets("10.0.0.1", "10.0.0.2", "10.0.0.3"),
            FastSettings(), totalHint: 3, includeUnreachable: false,
            onMachine: m => emitted.Add(m), progress: null, pause: null, ct: default);

        var addrs = emitted.Select(m => m.Address).OrderBy(a => a).ToArray();
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.3" }, addrs);
        Assert.All(emitted, m => Assert.Equal(MachineStatus.Alive, m.Status));
    }

    [Fact]
    public async Task IncludeUnreachable_EmitsDeadHostsAsUnreachable()
    {
        var liveness = new FakeLivenessProbe();
        liveness.Alive.Add("10.0.0.1");

        var emitted = new ConcurrentBag<Machine>();
        await Build(liveness).RunDiscoveryAsync(
            Targets("10.0.0.1", "10.0.0.2"),
            FastSettings(), 2, includeUnreachable: true,
            m => emitted.Add(m), null, null, default);

        Assert.Equal(2, emitted.Count);
        var dead = emitted.Single(m => m.Address == "10.0.0.2");
        Assert.Equal(MachineStatus.Unreachable, dead.Status);
        Assert.False(dead.IsAlive);
    }

    [Fact]
    public async Task PerHostException_IsIsolated_AndSurfacedAsError()
    {
        var liveness = new FakeLivenessProbe();
        liveness.Alive.Add("10.0.0.1");
        liveness.Alive.Add("10.0.0.3");
        liveness.ThrowFor.Add("10.0.0.2");

        var emitted = new ConcurrentBag<Machine>();
        await Build(liveness).RunDiscoveryAsync(
            Targets("10.0.0.1", "10.0.0.2", "10.0.0.3"),
            FastSettings(), 3, includeUnreachable: false,
            m => emitted.Add(m), null, null, default);

        // The two good hosts still come through.
        Assert.Contains(emitted, m => m.Address == "10.0.0.1" && m.Status == MachineStatus.Alive);
        Assert.Contains(emitted, m => m.Address == "10.0.0.3" && m.Status == MachineStatus.Alive);
        // The throwing host is surfaced as an errored row, not lost, and never crashes the loop.
        var errored = emitted.Single(m => m.Address == "10.0.0.2");
        Assert.Equal(MachineStatus.Error, errored.Status);
        Assert.Contains("boom", errored.StatusDetail);
    }

    [Fact]
    public async Task Classification_AppliesNameMacAndDeviceType()
    {
        var liveness = new FakeLivenessProbe();
        liveness.Alive.Add("10.0.0.5");
        liveness.OpenPorts["10.0.0.5"] = new[] { 445, 135 };

        var names = new FakeNameResolver();
        names.Names["10.0.0.5"] = new NameResult("WKS01", "wks01.corp.local", "CORP", null);

        var macs = new FakeMacResolver();
        macs.Macs["10.0.0.5"] = "00:15:5D:11:22:33";

        var oui = new FakeOuiLookup();
        oui.ByMac["00:15:5D:11:22:33"] = new OuiEntry("Microsoft Hyper-V", OuiCategory.Hypervisor);

        Machine? m = null;
        await Build(liveness, names, macs, oui).RunDiscoveryAsync(
            Targets("10.0.0.5"), FastSettings(), 1, false,
            x => m = x, null, null, default);

        Assert.NotNull(m);
        Assert.Equal("WKS01", m!.Name);
        Assert.Equal("wks01.corp.local", m.Fqdn);
        Assert.Equal("CORP", m.System.Domain);
        Assert.Contains("00:15:5D:11:22:33", m.MacAddresses);
        Assert.Equal(DeviceType.Windows, m.DeviceType);
        Assert.True(m.IsVirtual); // hypervisor OUI
    }

    [Fact]
    public async Task DnsNamedHost_WithoutNbnsOrSmb_IsNotClassifiedWindows()
    {
        // Regression: a resolved DNS name (common on IoT devices) must not imply a NetBIOS/Windows host.
        var liveness = new FakeLivenessProbe();
        liveness.Alive.Add("10.0.0.7");
        liveness.OpenPorts["10.0.0.7"] = new[] { 80 }; // web only, no SMB
        liveness.Ttl["10.0.0.7"] = 64;                 // Linux-like, as a real Chromecast reports

        var names = new FakeNameResolver();
        names.Names["10.0.0.7"] = new NameResult("chromecast", "chromecast.lan", null, null, NbnsResponded: false);

        Machine? m = null;
        await Build(liveness, names).RunDiscoveryAsync(
            Targets("10.0.0.7"), FastSettings(), 1, false, x => m = x, null, null, default);

        Assert.NotNull(m);
        Assert.Equal("chromecast", m!.Name);
        Assert.NotEqual(DeviceType.Windows, m.DeviceType);
    }

    [Fact]
    public async Task NbnsResponse_WithoutPorts_IsClassifiedWindows()
    {
        var liveness = new FakeLivenessProbe();
        liveness.Alive.Add("10.0.0.8");
        liveness.OpenPorts["10.0.0.8"] = Array.Empty<int>();

        var names = new FakeNameResolver();
        names.Names["10.0.0.8"] = new NameResult("OLDPC", null, "WORKGROUP", null, NbnsResponded: true);

        Machine? m = null;
        await Build(liveness, names).RunDiscoveryAsync(
            Targets("10.0.0.8"), FastSettings(), 1, false, x => m = x, null, null, default);

        Assert.Equal(DeviceType.Windows, m!.DeviceType);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var liveness = new FakeLivenessProbe { DelayMs = 200 };
        for (int i = 1; i <= 50; i++) liveness.Alive.Add($"10.0.0.{i}");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // already cancelled

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Build(liveness).RunDiscoveryAsync(
                Targets(Enumerable.Range(1, 50).Select(i => $"10.0.0.{i}").ToArray()),
                FastSettings(), 50, false,
                _ => { }, null, null, cts.Token));
    }

    [Fact]
    public async Task Progress_ReportsCompletionAndAliveCounts()
    {
        var liveness = new FakeLivenessProbe();
        liveness.Alive.Add("10.0.0.1");
        liveness.Alive.Add("10.0.0.2");

        ScanProgress? last = null;
        var progress = new Progress<ScanProgress>(p => last = p);

        await Build(liveness).RunDiscoveryAsync(
            Targets("10.0.0.1", "10.0.0.2", "10.0.0.3"),
            FastSettings(), 3, false,
            _ => { }, progress, null, default);

        // Give the Progress synchronization context a moment to flush the final callback.
        await Task.Delay(50);
        Assert.NotNull(last);
        Assert.Equal(ScanPhase.Complete, last!.Phase);
        Assert.Equal(3, last.Completed);
        Assert.Equal(2, last.Alive);
        Assert.Equal(1, last.Unreachable);
        Assert.Equal(0, last.InFlight);
    }

    [Fact]
    public async Task EmittedRows_CarryTheTargetBlock()
    {
        var liveness = new FakeLivenessProbe();
        liveness.Alive.Add("10.0.0.1");
        liveness.ThrowFor.Add("10.0.0.3");

        var emitted = new ConcurrentBag<Machine>();
        await Build(liveness).RunDiscoveryAsync(
            new[] { new ScanTarget("10.0.0.1", false, "10.0.0.0/24"),
                    new ScanTarget("10.0.0.2", false, "10.0.0.0/24"),
                    new ScanTarget("10.0.0.3", false, "10.0.0.0/24") },
            FastSettings(), 3, includeUnreachable: true, m => emitted.Add(m), null, null, default);

        Assert.Equal(3, emitted.Count);
        Assert.All(emitted, m => Assert.Equal("10.0.0.0/24", m.TargetBlock)); // alive, unreachable and errored rows alike
    }

    // --- In-flight tracking, pause semantics, cancel settling ------------------------------------------------

    /// <summary>A probe gate per address: hosts sit "in flight" until the test releases them.</summary>
    private sealed class ProbeGates
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
        public TaskCompletionSource For(string address)
            => _gates.GetOrAdd(address, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        public Task WaitAsync(string address, CancellationToken ct)
            => For(address).Task.WaitAsync(ct);
        public void Release(string address) => For(address).TrySetResult();
        public void ReleaseAll() { foreach (var g in _gates.Values) g.TrySetResult(); }
        public int Waiting => _gates.Values.Count(g => !g.Task.IsCompleted);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Progress_ReportsInFlight_WhileHostsAreProbing()
    {
        var gates = new ProbeGates();
        var liveness = new FakeLivenessProbe { BeforeProbe = gates.WaitAsync };
        foreach (var a in new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3" }) liveness.Alive.Add(a);

        var progress = new SyncProgress<ScanProgress>();
        var run = Build(liveness).RunDiscoveryAsync(
            Targets("10.0.0.1", "10.0.0.2", "10.0.0.3"),
            FastSettings(), 3, false, _ => { }, progress, null, default);

        await WaitUntilAsync(() => liveness.Calls.Count == 3); // all three parked inside the probe
        gates.Release("10.0.0.2");
        await WaitUntilAsync(() => progress.Reports.Count > 0);

        // The first report is the one host that finished; the other two are still in flight.
        var first = progress.Reports[0];
        Assert.Equal(1, first.Completed);
        Assert.Equal(2, first.InFlight);

        gates.ReleaseAll();
        await run;
        Assert.Equal(0, progress.Last!.InFlight);
        Assert.Equal(ScanPhase.Complete, progress.Last.Phase);
    }

    [Fact]
    public async Task Pause_ParksAliveHostsBeforeEnrichment_AndReportsZeroInFlight()
    {
        var gates = new ProbeGates();
        var liveness = new FakeLivenessProbe { BeforeProbe = gates.WaitAsync };
        var names = new FakeNameResolver();
        var addrs = new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4" };
        foreach (var a in addrs) liveness.Alive.Add(a);

        var emitted = new ConcurrentBag<Machine>();
        var progress = new SyncProgress<ScanProgress>();
        var pause = new PauseController();

        var run = Build(liveness, names).RunDiscoveryAsync(
            Targets(addrs), FastSettings(), 4, false, m => emitted.Add(m), progress, pause, default);

        await WaitUntilAsync(() => liveness.Calls.Count == 4); // all four in flight (parked in the probe)
        pause.Pause();
        gates.ReleaseAll();                                     // liveness finishes; enrichment must NOT start

        // Every host reports itself parked at the second gate: nothing completed, nothing in flight.
        await WaitUntilAsync(() => progress.Reports.Any(p => p.InFlight == 0 && p.Completed == 0));
        await Task.Delay(100);
        Assert.Empty(names.Calls);
        Assert.Empty(emitted);

        pause.Resume();
        await run;

        Assert.Equal(4, emitted.Count);
        Assert.Equal(4, names.Calls.Count);
        Assert.Equal(4, progress.Last!.Alive);
        Assert.Equal(0, progress.Last.InFlight);
    }

    [Fact]
    public async Task Cancel_WhilePaused_ReturnsPromptly_WithSettledCounts()
    {
        var gates = new ProbeGates();
        var liveness = new FakeLivenessProbe { BeforeProbe = gates.WaitAsync };
        var addrs = new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3" };
        foreach (var a in addrs) liveness.Alive.Add(a);

        var emitted = new ConcurrentBag<Machine>();
        var progress = new SyncProgress<ScanProgress>();
        var pause = new PauseController();
        using var cts = new CancellationTokenSource();

        var run = Build(liveness).RunDiscoveryAsync(
            Targets(addrs), FastSettings(), 3, false, m => emitted.Add(m), progress, pause, cts.Token);

        await WaitUntilAsync(() => liveness.Calls.Count == 3);
        pause.Pause();
        gates.ReleaseAll();
        await WaitUntilAsync(() => progress.Reports.Any(p => p.InFlight == 0 && p.Completed == 0));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"cancel took {sw.ElapsedMilliseconds} ms");

        var last = progress.Last!;
        Assert.Equal(ScanPhase.Cancelled, last.Phase);
        Assert.Equal(0, last.InFlight);
        Assert.Equal(emitted.Count, last.Alive);
    }

    [Fact]
    public async Task Cancel_MidSweep_FinalReportIsCancelledAndSettled()
    {
        var liveness = new FakeLivenessProbe { DelayMs = 50 };
        var addrs = Enumerable.Range(1, 20).Select(i => $"10.0.0.{i}").ToArray();
        foreach (var a in addrs) liveness.Alive.Add(a);

        var emitted = new ConcurrentBag<Machine>();
        var progress = new SyncProgress<ScanProgress>();
        using var cts = new CancellationTokenSource();
        progress.OnReport = p => { if (p.Completed >= 1) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Build(liveness).RunDiscoveryAsync(
                Targets(addrs), FastSettings(), 20, false, m => emitted.Add(m), progress, null, cts.Token));

        var last = progress.Last!;
        Assert.Equal(ScanPhase.Cancelled, last.Phase);
        Assert.Equal(0, last.InFlight);
        Assert.Equal(emitted.Count, last.Alive);
        Assert.True(last.Completed < 20);
    }

    [Fact]
    public async Task Eta_ExcludesPausedTime()
    {
        var gates = new ProbeGates();
        var liveness = new FakeLivenessProbe { BeforeProbe = gates.WaitAsync };
        var addrs = new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4" };
        foreach (var a in addrs) liveness.Alive.Add(a);

        var progress = new SyncProgress<ScanProgress>();
        var pause = new PauseController();
        var run = Build(liveness).RunDiscoveryAsync(
            Targets(addrs), FastSettings(), 4, false, _ => { }, progress, pause, default);

        await WaitUntilAsync(() => liveness.Calls.Count == 4);   // all four parked inside the probe
        gates.Release("10.0.0.1");
        await WaitUntilAsync(() => progress.Reports.Any(p => p.Completed == 1));

        pause.Pause();
        await Task.Delay(1500);                                   // paused time the rate math must NOT count
        pause.Resume();

        gates.Release("10.0.0.2");
        await WaitUntilAsync(() => progress.Reports.Any(p => p.Phase == ScanPhase.Discovery && p.Completed == 2));

        // 2 done, 2 remaining: pause-inclusive math would say >= the full 1.5s pause; active-time math says
        // only genuine work time, far under half of it even on a slow CI box.
        var report = progress.Reports.First(p => p.Phase == ScanPhase.Discovery && p.Completed == 2);
        Assert.NotNull(report.EstimatedRemaining);
        Assert.True(report.EstimatedRemaining < TimeSpan.FromMilliseconds(750),
            $"ETA {report.EstimatedRemaining} suggests paused time was counted as work time");

        gates.ReleaseAll();
        await run;
    }

    // --- Concurrency cap ---------------------------------------------------------------------------------------

    /// <summary>The requested degree of parallelism is clamped to ScanSettings.MaxConcurrency (and floored at 1):
    /// while every host is parked in the probe, exactly the effective count are in flight.</summary>
    [Theory]
    [InlineData(50, 4, 4)]  // over the cap → cap
    [InlineData(3, 4, 3)]   // under the cap → as requested
    [InlineData(0, 4, 1)]   // nonsense → still one host at a time
    public async Task Discovery_InFlight_IsClampedToMaxConcurrency(int requested, int max, int expected)
    {
        var gates = new ProbeGates();
        var liveness = new FakeLivenessProbe { BeforeProbe = gates.WaitAsync };
        var addrs = Enumerable.Range(1, 10).Select(i => $"10.0.0.{i}").ToArray();
        // Pre-create every gate so ReleaseAll also frees the hosts that haven't reached the probe yet.
        foreach (var a in addrs) gates.For(a);

        var settings = new ScanSettings { DiscoveryConcurrency = requested, MaxConcurrency = max };
        var run = Build(liveness).RunDiscoveryAsync(
            Targets(addrs), settings, addrs.Length, false, _ => { }, null, null, default);

        await WaitUntilAsync(() => liveness.Calls.Count >= expected);
        await Task.Delay(150); // give the loop every chance to (wrongly) start more
        Assert.Equal(expected, liveness.Calls.Count); // Calls is added before the gate await, so this is the in-flight count

        gates.ReleaseAll();
        await run;
        Assert.Equal(addrs.Length, liveness.Calls.Count);
    }
}
