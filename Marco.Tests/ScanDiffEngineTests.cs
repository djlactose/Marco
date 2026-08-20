using Marco.Core.Model;
using Marco.Export;
using Marco.Export.Diff;
using Xunit;

namespace Marco.Tests;

public class ScanDiffEngineTests
{
    private static readonly string[] AllCollectors =
        { "System", "OperatingSystem", "Cpu", "Memory", "Storage", "Network", "InstalledSoftware",
          "Updates", "Security", "Users", "Services", "ScheduledTasks", "Peripherals" };

    private static Machine Sample(string address = "10.0.0.20", string serial = "5CD1234XYZ",
        params string[] withoutCollectors)
    {
        var m = new Machine(address) { Name = "PC-A", Status = MachineStatus.Done };
        m.System.SerialNumber = serial;
        m.System.Model = "OptiPlex";
        // MAC derived from the serial so distinct machines in a fixture never share one (a shared MAC would
        // legitimately pair them — exactly what the identity matcher is for).
        m.MacAddresses.Add($"3C:52:82:{(byte)serial.GetHashCode():X2}:BB:CC");
        m.Os.Caption = "Windows 11 Pro";
        m.TotalMemoryBytes = 16L * 1024 * 1024 * 1024;
        m.Software = new List<SoftwareEntry>
        {
            new() { DisplayName = "7-Zip", Version = "23.01" },
            new() { DisplayName = "Chrome", Version = "127.0" },
        };
        m.Hotfixes = new List<HotfixEntry> { new() { Id = "KB5030219" } };
        m.Security = new SecurityInfo
        {
            SecureBoot = true,
            Smb1Enabled = false,
            FirewallPublic = true,
            BitLockerVolumes = { new BitLockerVolumeEntry { Letter = "C:", Protection = "On", VolumeType = "OS" } },
        };
        m.LocalAdministrators = new List<string> { "PC-A\\Administrator" };
        m.Monitors = new List<MonitorEntry> { new() { Manufacturer = "Dell", Model = "U2723QE", Serial = "MON111" } };
        foreach (var c in AllCollectors.Except(withoutCollectors))
            m.SetCollector(c, CollectorStatus.Ok, null);
        return m;
    }

    private static ScanDocument Doc(params Machine[] machines)
        => ScanDocument.From(new ScanMetadata(new DateTime(2026, 1, 1), "tester",
            new[] { "10.0.0.0/24" }, machines.Length, machines.Length), machines);

    [Fact]
    public void IdenticalDocuments_ProduceEmptyDiff()
    {
        var diff = ScanDiffEngine.Compute(Doc(Sample()), Doc(Sample()));
        Assert.True(diff.IsEmpty);
        Assert.Equal("No differences.", diff.Summary);
    }

    [Fact]
    public void AddedAndRemovedMachines_AreReported()
    {
        var diff = ScanDiffEngine.Compute(
            Doc(Sample(), Sample("10.0.0.30", "SER-B")),
            Doc(Sample(), Sample("10.0.0.40", "SER-C")));

        Assert.Equal("SER-C", Assert.Single(diff.Added).Serial);
        Assert.Equal("SER-B", Assert.Single(diff.Removed).Serial);
    }

    [Fact]
    public void DhcpAddressMove_IsANetworkChange_NotAddRemove()
    {
        var moved = Sample(address: "10.0.0.99");
        var diff = ScanDiffEngine.Compute(Doc(Sample()), Doc(moved));

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        var machine = Assert.Single(diff.Changed);
        var change = Assert.Single(machine.Changes);
        Assert.Equal(DiffCategory.Network, change.Category);
        Assert.Equal("IP address", change.Item);
        Assert.Equal("10.0.0.20", change.OldValue);
        Assert.Equal("10.0.0.99", change.NewValue);
    }

    [Fact]
    public void SoftwareInstallRemoveAndVersionChange()
    {
        var newer = Sample();
        newer.Software = new List<SoftwareEntry>
        {
            new() { DisplayName = "7-Zip", Version = "24.05" },     // upgraded
            new() { DisplayName = "AnyDesk", Version = "8.0" },     // appeared
        };                                                          // Chrome vanished

        var changes = Assert.Single(ScanDiffEngine.Compute(Doc(Sample()), Doc(newer)).Changed).Changes;

        Assert.Contains(changes, c => c is { Category: DiffCategory.Software, Kind: DiffChangeKind.Added } && c.NewValue!.Contains("AnyDesk"));
        Assert.Contains(changes, c => c is { Category: DiffCategory.Software, Kind: DiffChangeKind.Removed } && c.OldValue!.Contains("Chrome"));
        Assert.Contains(changes, c => c is { Item: "7-Zip", OldValue: "23.01", NewValue: "24.05" });
    }

    [Fact]
    public void SecurityWorsening_IsRegression_ImprovementIsInfo()
    {
        var newer = Sample();
        newer.Security!.SecureBoot = false;                                  // worsened
        newer.Security.Smb1Enabled = true;                                   // worsened
        newer.Security.FirewallPublic = true;                                // unchanged
        newer.Security.BitLockerVolumes[0].Protection = "Off";               // worsened

        var older = Sample();
        older.Security!.UacEnabled = false;
        newer.Security.UacEnabled = true;                                    // improved

        var changes = Assert.Single(ScanDiffEngine.Compute(Doc(older), Doc(newer)).Changed).Changes;

        Assert.Equal(DiffSeverity.Regression, changes.Single(c => c.Item == "Secure Boot").Severity);
        Assert.Equal(DiffSeverity.Regression, changes.Single(c => c.Item == "SMB1").Severity);
        Assert.Equal(DiffSeverity.Regression, changes.Single(c => c.Item == "BitLocker C:").Severity);
        Assert.Equal(DiffSeverity.Info, changes.Single(c => c.Item == "UAC").Severity);
        Assert.DoesNotContain(changes, c => c.Item.StartsWith("Firewall"));
    }

    [Fact]
    public void NewLocalAdmin_IsNotable()
    {
        var newer = Sample();
        newer.LocalAdministrators = new List<string> { "PC-A\\Administrator", "CORP\\helpdesk" };

        var changes = Assert.Single(ScanDiffEngine.Compute(Doc(Sample()), Doc(newer)).Changed).Changes;
        var admin = changes.Single(c => c.Item == "Local admin");
        Assert.Equal(DiffChangeKind.Added, admin.Kind);
        Assert.Equal("CORP\\helpdesk", admin.NewValue);
        Assert.Equal(DiffSeverity.Notable, admin.Severity);
    }

    [Fact]
    public void MonitorSwap_IsNotable()
    {
        var newer = Sample();
        newer.Monitors = new List<MonitorEntry> { new() { Manufacturer = "HP", Model = "E27", Serial = "MON999" } };

        var changes = Assert.Single(ScanDiffEngine.Compute(Doc(Sample()), Doc(newer)).Changed).Changes;
        Assert.Contains(changes, c => c is { Item: "Monitor", Kind: DiffChangeKind.Added, Severity: DiffSeverity.Notable });
        Assert.Contains(changes, c => c is { Item: "Monitor", Kind: DiffChangeKind.Removed } && c.OldValue!.Contains("MON111"));
    }

    [Fact]
    public void FailedCollector_SkipsItsCategory_InsteadOfReportingEverythingRemoved()
    {
        var newer = Sample(withoutCollectors: "InstalledSoftware");
        newer.Software = new List<SoftwareEntry>(); // collector failed → empty list
        newer.SetCollector("InstalledSoftware", CollectorStatus.AccessDenied, "denied");

        var diff = ScanDiffEngine.Compute(Doc(Sample()), Doc(newer));

        Assert.True(diff.IsEmpty); // NOT "7-Zip removed, Chrome removed"
    }

    [Fact]
    public void DiscoveryOnlyPair_ComparesOnlyDiscoveryFacts()
    {
        // Neither side ran collectors: still diffable on reachability etc., no inventory noise.
        var oldM = new Machine("10.0.0.20") { Name = "PC-A", Status = MachineStatus.Done };
        var newM = new Machine("10.0.0.20") { Name = "PC-A", Status = MachineStatus.Unreachable };

        var diff = ScanDiffEngine.Compute(Doc(oldM), Doc(newM));

        var change = Assert.Single(Assert.Single(diff.Changed).Changes);
        Assert.Equal("Reachability", change.Item);
        Assert.Equal(DiffSeverity.Notable, change.Severity); // went down
    }

    [Fact]
    public void PendingRebootFlip_IsInfo_EitherDirection()
    {
        var older = Sample(); older.Updates = new UpdateInfo { PendingReboot = false };
        var newer = Sample(); newer.Updates = new UpdateInfo { PendingReboot = true };

        var changes = Assert.Single(ScanDiffEngine.Compute(Doc(older), Doc(newer)).Changed).Changes;
        Assert.Equal(DiffSeverity.Info, changes.Single(c => c.Item == "Pending reboot").Severity);
    }

    [Fact]
    public void HotfixInstalled_IsReported()
    {
        var newer = Sample();
        newer.Hotfixes = new List<HotfixEntry> { new() { Id = "KB5030219" }, new() { Id = "KB5041585" } };

        var changes = Assert.Single(ScanDiffEngine.Compute(Doc(Sample()), Doc(newer)).Changed).Changes;
        Assert.Contains(changes, c => c is { Category: DiffCategory.Hotfixes, Kind: DiffChangeKind.Added, NewValue: "KB5041585" });
    }

    [Fact]
    public void RamChange_IsNotable()
    {
        var newer = Sample();
        newer.TotalMemoryBytes = 32L * 1024 * 1024 * 1024;

        var changes = Assert.Single(ScanDiffEngine.Compute(Doc(Sample()), Doc(newer)).Changed).Changes;
        var ram = changes.Single(c => c.Item == "Total RAM");
        Assert.Equal(("16 GB", "32 GB"), (ram.OldValue, ram.NewValue));
        Assert.Equal(DiffSeverity.Notable, ram.Severity);
    }

    [Fact]
    public void Summary_CountsRegressions()
    {
        var newer = Sample();
        newer.Security!.SecureBoot = false;
        var diff = ScanDiffEngine.Compute(Doc(Sample()), Doc(newer));

        Assert.Equal(1, diff.RegressionCount);
        Assert.Contains("1 regression", diff.Summary);
    }
}
