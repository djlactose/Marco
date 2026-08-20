using Marco.Core.Model;
using Marco.Export;
using Marco.Export.Diff;
using Xunit;

namespace Marco.Tests;

public class MachineIdentityTests
{
    private static MachineDto Dto(string address, string? name = null, string? serial = null, params string[] macs)
    {
        var m = new Machine(address) { Name = name, Status = MachineStatus.Done };
        m.System.SerialNumber = serial;
        foreach (var mac in macs) m.MacAddresses.Add(mac);
        return MachineDto.From(m);
    }

    [Fact]
    public void SerialMatch_SurvivesAddressChange()
    {
        var result = MachineIdentity.Match(
            new[] { Dto("10.0.0.20", "PC-A", "5CD1234XYZ") },
            new[] { Dto("10.0.0.99", "PC-A", "5CD1234XYZ") }); // DHCP moved it

        var match = Assert.Single(result.Matched);
        Assert.Equal(MatchKind.Serial, match.MatchedBy);
        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
    }

    [Theory]
    [InlineData("To be filled by O.E.M.")]
    [InlineData("Default string")]
    [InlineData("System Serial Number")]
    [InlineData("0")]
    [InlineData("  ")]
    [InlineData("None")]
    public void BogusSerials_AreRejected(string bogus)
    {
        Assert.Null(MachineIdentity.NormalizeSerial(bogus));

        // Two DIFFERENT machines with the same placeholder must not pair by serial.
        var result = MachineIdentity.Match(
            new[] { Dto("10.0.0.20", "PC-A", bogus) },
            new[] { Dto("10.0.0.99", "PC-B", bogus) });
        Assert.Empty(result.Matched);
    }

    [Fact]
    public void DuplicateSerials_AreDemotedToMacMatching()
    {
        // Two cloned VMs share a serial; only their MACs tell them apart.
        var result = MachineIdentity.Match(
            new[]
            {
                Dto("10.0.0.20", "VM-A", "VMware-42", "00:50:56:AA:00:01"),
                Dto("10.0.0.21", "VM-B", "VMware-42", "00:50:56:AA:00:02"),
            },
            new[]
            {
                Dto("10.0.0.31", "VM-B", "VMware-42", "00:50:56:AA:00:02"),
                Dto("10.0.0.30", "VM-A", "VMware-42", "00:50:56:AA:00:01"),
            });

        Assert.Equal(2, result.Matched.Count);
        Assert.All(result.Matched, m => Assert.Equal(MatchKind.Mac, m.MatchedBy));
        Assert.Equal("VM-A", result.Matched.Single(m => m.Older.Name == "VM-A").Newer.Name);
    }

    [Fact]
    public void MacMatch_NormalizesFormats()
    {
        var result = MachineIdentity.Match(
            new[] { Dto("10.0.0.20", "PC-A", null, "3c:52:82:aa:bb:cc") },
            new[] { Dto("10.0.0.99", "PC-A2", null, "3C-52-82-AA-BB-CC") });

        var match = Assert.Single(result.Matched);
        Assert.Equal(MatchKind.Mac, match.MatchedBy);
    }

    [Fact]
    public void RandomizedMacOverlap_IsWeak()
    {
        // Second nibble 2/6/A/E = locally administered (randomized) — still a match, but flagged.
        Assert.True(MachineIdentity.IsLocallyAdministered("D2:11:22:33:44:55"));
        Assert.False(MachineIdentity.IsLocallyAdministered("D0:11:22:33:44:55"));

        var result = MachineIdentity.Match(
            new[] { Dto("10.0.0.20", null, null, "D2:11:22:33:44:55") },
            new[] { Dto("10.0.0.20", null, null, "D2:11:22:33:44:55") });

        // Same address+MAC: the MAC pass runs first but tags the pairing weak.
        var match = Assert.Single(result.Matched);
        Assert.Equal(MatchKind.WeakMac, match.MatchedBy);
    }

    [Fact]
    public void AddressNameFallback_WhenNoSerialOrMac()
    {
        var result = MachineIdentity.Match(
            new[] { Dto("10.0.0.20", "printer-2f") },
            new[] { Dto("10.0.0.20", "printer-2f") });

        var match = Assert.Single(result.Matched);
        Assert.Equal(MatchKind.AddressName, match.MatchedBy);
    }

    [Fact]
    public void DifferentDevices_DoNotFalselyMatch()
    {
        // Same address, different name, no serial/MAC: a different device took over the lease.
        var result = MachineIdentity.Match(
            new[] { Dto("10.0.0.20", "OLD-PC") },
            new[] { Dto("10.0.0.20", "NEW-PC") });

        Assert.Empty(result.Matched);
        Assert.Single(result.Added);
        Assert.Single(result.Removed);
    }
}
