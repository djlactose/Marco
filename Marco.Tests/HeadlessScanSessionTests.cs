using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Scanning;
using Marco.Core.Targets;
using Marco.Discovery;
using Xunit;

namespace Marco.Tests;

public class HeadlessScanSessionTests
{
    /// <summary>Records which runner saw which host, so routing can be asserted.</summary>
    private sealed class RecordingRunner : IInventoryRunner
    {
        public List<string> Seen { get; } = new();
        private readonly bool _authenticate;
        public RecordingRunner(bool authenticate = true) => _authenticate = authenticate;

        public Task<InventoryOutcome> InventoryAsync(Machine machine, IReadOnlyList<CredentialCandidate> candidates,
            IReadOnlyDictionary<string, CredentialCandidate>? overrides, ISet<string>? enabled, CancellationToken ct)
        {
            lock (Seen) Seen.Add(machine.Address);
            machine.Status = _authenticate ? MachineStatus.Done : MachineStatus.AuthFailed;
            return Task.FromResult(new InventoryOutcome(_authenticate, candidates.FirstOrDefault()?.Label,
                machine.Status, null));
        }
    }

    private static ScanController Controller(FakeLivenessProbe liveness)
        => new(liveness, new FakeNameResolver(), new FakeMacResolver(), new FakeOuiLookup(), new DeviceClassifier());

    private static IReadOnlyList<ScanTarget> Targets(params string[] a) => a.Select(x => new ScanTarget(x, false)).ToList();

    private static IReadOnlyList<CredentialCandidate> Creds(params (string label, CredentialKind kind)[] c)
        => c.Select(x => new CredentialCandidate(x.label, new Marco.Core.Wmi.WmiCredential { Username = x.label }, x.kind)).ToList();

    private static ScanSettings Settings() => new() { DiscoveryConcurrency = 8, InventoryConcurrency = 4, ClassificationEnabled = false };

    [Fact]
    public async Task RoutesByDeviceType_AndFiltersCredentialsByKind()
    {
        var liveness = new FakeLivenessProbe();
        liveness.Alive.Add("10.0.0.1");
        liveness.Alive.Add("10.0.0.2");

        var windows = new RecordingRunner();
        var linux = new RecordingRunner();
        var session = new HeadlessScanSession(Controller(liveness), windows, linux);

        // Classify off, so set the device types after discovery via a wrapper: instead, seed types through the
        // machine list the controller emits — we mark them by address here and route on that.
        var creds = Creds(("win", CredentialKind.Windows), ("ssh", CredentialKind.Linux));

        // The controller emits Machines with DeviceType Unknown (classification off) → treated as Windows.
        var result = await session.RunAsync(Targets("10.0.0.1", "10.0.0.2"), Settings(), creds, null,
            inventory: true, includeUnreachable: false, default);

        Assert.Equal(2, result.Alive);
        Assert.Equal(2, windows.Seen.Count); // both non-Linux hosts went to the Windows runner
        Assert.Empty(linux.Seen);
        Assert.Equal(2, result.Authenticated);
    }

    [Fact]
    public async Task LinuxHost_GoesToLinuxRunner()
    {
        var liveness = new FakeLivenessProbe();
        liveness.Alive.Add("10.0.0.5");
        liveness.OpenPorts["10.0.0.5"] = new[] { 22 }; // SSH-only → classified UnixLinux

        var windows = new RecordingRunner();
        var linux = new RecordingRunner();
        var session = new HeadlessScanSession(Controller(liveness), windows, linux);

        var result = await session.RunAsync(Targets("10.0.0.5"),
            new ScanSettings { DiscoveryConcurrency = 4, InventoryConcurrency = 2, ClassificationEnabled = true },
            Creds(("ssh", CredentialKind.Linux)), null, inventory: true, includeUnreachable: false, default);

        Assert.Equal("10.0.0.5", Assert.Single(linux.Seen));
        Assert.Empty(windows.Seen);
    }

    [Fact]
    public async Task Printer_GoesToSnmpRunner_WithOnlySnmpCredentials()
    {
        var liveness = new FakeLivenessProbe();
        liveness.Alive.Add("10.0.0.9");
        liveness.OpenPorts["10.0.0.9"] = new[] { 9100 }; // JetDirect → classified Printer
        var windows = new RecordingRunner();
        var linux = new RecordingRunner();
        var snmp = new RecordingRunner();
        var session = new HeadlessScanSession(Controller(liveness), windows, linux, snmp);

        var creds = Creds(("win", CredentialKind.Windows), ("any", CredentialKind.Any), ("community", CredentialKind.Snmp));
        var result = await session.RunAsync(Targets("10.0.0.9"),
            new ScanSettings { DiscoveryConcurrency = 4, InventoryConcurrency = 2, ClassificationEnabled = true },
            creds, null, inventory: true, includeUnreachable: false, default);

        Assert.Equal("10.0.0.9", Assert.Single(snmp.Seen));
        Assert.Empty(windows.Seen);
        Assert.Empty(linux.Seen);
        Assert.Equal(1, result.Authenticated);
        Assert.Equal(0, result.Skipped);
        // AppliesTo: neither the Windows nor the "Any" password may be tried as a community string.
        Assert.False(creds[0].AppliesTo(CredentialKind.Snmp));
        Assert.False(creds[1].AppliesTo(CredentialKind.Snmp));
        Assert.True(creds[2].AppliesTo(CredentialKind.Snmp));
        Assert.False(creds[2].AppliesTo(CredentialKind.Windows));
    }

    [Fact]
    public async Task Printer_WithoutSnmpRunner_IsSkippedAsBefore()
    {
        var liveness = new FakeLivenessProbe();
        liveness.Alive.Add("10.0.0.9");
        liveness.OpenPorts["10.0.0.9"] = new[] { 9100 };
        var windows = new RecordingRunner();
        var session = new HeadlessScanSession(Controller(liveness), windows, new RecordingRunner());

        var result = await session.RunAsync(Targets("10.0.0.9"),
            new ScanSettings { DiscoveryConcurrency = 4, InventoryConcurrency = 2, ClassificationEnabled = true },
            Creds(("win", CredentialKind.Windows)), null, inventory: true, includeUnreachable: false, default);

        Assert.Empty(windows.Seen);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task NoInventory_RunsDiscoveryOnly()
    {
        var liveness = new FakeLivenessProbe();
        liveness.Alive.Add("10.0.0.1");
        var windows = new RecordingRunner();
        var session = new HeadlessScanSession(Controller(liveness), windows, new RecordingRunner());

        var result = await session.RunAsync(Targets("10.0.0.1"), Settings(), Creds(("win", CredentialKind.Windows)),
            null, inventory: false, includeUnreachable: false, default);

        Assert.Equal(1, result.Alive);
        Assert.Empty(windows.Seen); // discovery only
        Assert.Equal(0, result.Authenticated);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var liveness = new FakeLivenessProbe { DelayMs = 500 };
        liveness.Alive.Add("10.0.0.1");
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);
        var session = new HeadlessScanSession(Controller(liveness), new RecordingRunner(), new RecordingRunner());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.RunAsync(Targets("10.0.0.1"), Settings(), Creds(("win", CredentialKind.Windows)),
                null, inventory: true, includeUnreachable: false, cts.Token));
    }
}
