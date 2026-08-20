using Marco.Core.Diagnosis;
using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;
using Xunit;

namespace Marco.Tests;

public class PrereqDoctorTests
{
    private static Machine Host(ConnectFailure failure = ConnectFailure.None, bool localAccount = false,
        bool alive = true, DeviceType type = DeviceType.Windows, params int[] openPorts)
    {
        var m = new Machine("10.0.0.20") { Name = "PC-A", DeviceType = type, IsAlive = alive };
        m.ConnectFailure = failure;
        m.ConnectFailureLocalAccount = localAccount;
        foreach (var p in openPorts) m.OpenPorts.Add(p);
        return m;
    }

    [Fact]
    public void NoCredentials_IsDiagnosed()
    {
        var d = PrereqDoctor.Diagnose(Host(ConnectFailure.NoCredentials));
        Assert.Equal(PrereqCause.NoCredentials, d.Cause);
        Assert.Null(d.FixScript); // fix is "add a credential", not a script
    }

    [Fact]
    public void AliveButUnreachable_With135ObservedClosed_IsConfidentFirewall()
    {
        var d = PrereqDoctor.Diagnose(Host(ConnectFailure.Unreachable, openPorts: new[] { 445, 3389 }));
        Assert.Equal(PrereqCause.FirewallRpcBlocked, d.Cause);
        Assert.True(d.Confident);
        Assert.Contains("windows management instrumentation", d.FixScript);
    }

    [Fact]
    public void AliveButUnreachable_NoPortsProbed_IsLikelyFirewall()
    {
        // ICMP-only discovery: no port evidence at all — same cause, hedged wording.
        var d = PrereqDoctor.Diagnose(Host(ConnectFailure.Unreachable));
        Assert.Equal(PrereqCause.FirewallRpcBlocked, d.Cause);
        Assert.False(d.Confident);
        Assert.Contains("likely", d.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Timeout_With135Open_IsWmiTimeout()
    {
        var d = PrereqDoctor.Diagnose(Host(ConnectFailure.Timeout, openPorts: new[] { 135, 445 }));
        Assert.Equal(PrereqCause.WmiTimeout, d.Cause);
        Assert.Contains("Concurrency", d.Explanation);
    }

    [Fact]
    public void AuthFailed_LocalAccount_IsTokenFiltering_WithRegAddFix()
    {
        var d = PrereqDoctor.Diagnose(Host(ConnectFailure.AuthFailed, localAccount: true));
        Assert.Equal(PrereqCause.TokenFilteringLocalAdmin, d.Cause);
        Assert.True(d.Confident);
        Assert.Contains("LocalAccountTokenFilterPolicy", d.FixScript);
    }

    [Fact]
    public void AuthFailed_DomainAccount_IsBadCredentials()
    {
        var d = PrereqDoctor.Diagnose(Host(ConnectFailure.AccessDenied));
        Assert.Equal(PrereqCause.BadCredentials, d.Cause);
        Assert.Null(d.FixScript);
    }

    [Fact]
    public void WmiOkButRegistryCollectorsDenied_IsRemoteRegistry()
    {
        var m = Host();
        m.SetCollector("System", CollectorStatus.Ok);
        m.SetCollector("Cpu", CollectorStatus.Ok);
        m.SetCollector("InstalledSoftware", CollectorStatus.AccessDenied, "registry denied");
        m.SetCollector("Updates", CollectorStatus.Failed, "registry unavailable");
        m.Status = MachineStatus.Partial;

        var d = PrereqDoctor.Diagnose(m);

        Assert.Equal(PrereqCause.RemoteRegistryUnavailable, d.Cause);
        Assert.Contains("InstalledSoftware", d.Explanation);
        Assert.Contains("RemoteRegistry", d.FixScript);
    }

    [Fact]
    public void RegistryCollectorsDenied_ButNothingElseWorked_IsNotRemoteRegistry()
    {
        // If WMI collectors ALSO failed, the evidence doesn't isolate Remote Registry.
        var m = Host();
        m.SetCollector("System", CollectorStatus.Failed, "x");
        m.SetCollector("InstalledSoftware", CollectorStatus.AccessDenied, "y");
        m.Status = MachineStatus.Error;

        Assert.NotEqual(PrereqCause.RemoteRegistryUnavailable, PrereqDoctor.Diagnose(m).Cause);
    }

    [Fact]
    public void SshFailures_AreDiagnosed()
    {
        Assert.Equal(PrereqCause.SshAuthFailed,
            PrereqDoctor.Diagnose(Host(ConnectFailure.SshAuthFailed, type: DeviceType.UnixLinux)).Cause);
        Assert.Equal(PrereqCause.SshUnreachable,
            PrereqDoctor.Diagnose(Host(ConnectFailure.SshUnreachable, type: DeviceType.UnixLinux)).Cause);
    }

    [Fact]
    public void PrinterWithoutInventory_IsByDesign()
    {
        var d = PrereqDoctor.Diagnose(Host(type: DeviceType.Printer));
        Assert.Equal(PrereqCause.NotInventoryable, d.Cause);
    }

    [Fact]
    public void HealthyMachine_HasNoDiagnosis()
    {
        var m = Host();
        m.SetCollector("System", CollectorStatus.Ok);
        m.Status = MachineStatus.Done;

        var d = PrereqDoctor.Diagnose(m);
        Assert.Equal(PrereqCause.None, d.Cause);
        Assert.Equal("", d.Title); // empty title collapses the detail-pane panel
    }

    [Fact]
    public void Rollup_GroupsByCause_LargestFirst_ExcludingNoneAndByDesign()
    {
        var machines = new[]
        {
            Host(ConnectFailure.AuthFailed, localAccount: true),
            Host(ConnectFailure.AuthFailed, localAccount: true),
            Host(ConnectFailure.AuthFailed, localAccount: true),
            Host(ConnectFailure.Unreachable, openPorts: new[] { 445 }),
            Host(type: DeviceType.Printer),          // by design — excluded
            Host(),                                   // healthy — excluded
        };

        var rollup = PrereqDoctor.Rollup(machines);

        Assert.Equal(2, rollup.Count);
        Assert.Equal(PrereqCause.TokenFilteringLocalAdmin, rollup[0].Cause);
        Assert.Equal(3, rollup[0].Machines.Count);
        Assert.Equal(PrereqCause.FirewallRpcBlocked, rollup[1].Cause);
    }

    // --- Evidence capture: the runner records what the doctor later reads ---

    private static InventoryRunner Runner(IWmiSessionFactory factory)
        => new(factory, new FakeRemoteRegistryFactory(new FakeRemoteRegistry()), Array.Empty<IInventoryCollector>());

    [Fact]
    public async Task Runner_RecordsLocalAccountAuthFailure()
    {
        var rejectAll = new FakeWmiSessionFactory((h, _) =>
            throw new WmiException(WmiFailureKind.AccessDenied, "denied"));
        var m = new Machine("10.0.0.30");

        // Local account: no domain on the credential.
        await Runner(rejectAll).InventoryAsync(m,
            new[] { new CredentialCandidate("local-admin", new WmiCredential { Username = "admin" }) }, null, null, default);

        Assert.Equal(ConnectFailure.AccessDenied, m.ConnectFailure);
        Assert.True(m.ConnectFailureLocalAccount);
        Assert.Equal(PrereqCause.TokenFilteringLocalAdmin, PrereqDoctor.Diagnose(m).Cause);
    }

    [Fact]
    public async Task Runner_RecordsDomainAuthFailure_AsNotLocal()
    {
        var rejectAll = new FakeWmiSessionFactory((h, _) =>
            throw new WmiException(WmiFailureKind.AuthFailed, "bad password"));
        var m = new Machine("10.0.0.31");

        await Runner(rejectAll).InventoryAsync(m,
            new[] { new CredentialCandidate("corp", new WmiCredential { Domain = "CORP", Username = "svc" }) }, null, null, default);

        Assert.Equal(ConnectFailure.AuthFailed, m.ConnectFailure);
        Assert.False(m.ConnectFailureLocalAccount);
        Assert.Equal(PrereqCause.BadCredentials, PrereqDoctor.Diagnose(m).Cause);
    }

    [Fact]
    public async Task Runner_RecordsUnreachable_AndClearsEvidenceOnSuccess()
    {
        var m = new Machine("10.0.0.32") { IsAlive = true };
        var unreachable = new FakeWmiSessionFactory((h, _) =>
            throw new WmiException(WmiFailureKind.Unreachable, "RPC unavailable"));
        await Runner(unreachable).InventoryAsync(m,
            new[] { new CredentialCandidate("a", new WmiCredential { Username = "a" }) }, null, null, default);
        Assert.Equal(ConnectFailure.Unreachable, m.ConnectFailure);

        // Fixed overnight: the next successful pass must clear the stale evidence.
        var acceptAll = new FakeWmiSessionFactory((h, _) => new FakeWmiSession(h));
        await Runner(acceptAll).InventoryAsync(m,
            new[] { new CredentialCandidate("a", new WmiCredential { Username = "a" }) }, null, null, default);

        Assert.Equal(ConnectFailure.None, m.ConnectFailure);
        Assert.Equal(PrereqCause.None, PrereqDoctor.Diagnose(m).Cause);
    }
}
