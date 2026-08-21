using Marco.Core.Model;
using Xunit;

namespace Marco.Tests;

public class ExpansionEstimatorTests
{
    [Fact]
    public void VirtualMachine_IsNull_EvenWithSlotData()
        => Assert.Null(ExpansionEstimator.Describe(true, "Desktop", 2, 4, "PCIEX1_1, M.2_2", 1));

    [Theory]
    [InlineData("Laptop")]
    [InlineData("Notebook")]
    [InlineData("Convertible")]
    [InlineData("tablet")] // case-insensitive
    public void Portable_SaysExpansionUnlikely(string chassis)
        => Assert.Equal("Estimate: laptop/portable chassis, 1 internal disk — internal drive expansion unlikely.",
            ExpansionEstimator.Describe(false, chassis, null, null, null, 1));

    [Fact]
    public void Tower_WithFreeSlots_NamesThem()
        => Assert.Equal("Estimate: tower/desktop chassis, 2 internal disks, 2 of 4 expansion slots free (PCIEX1_1, M.2_2) — likely room for additional drives.",
            ExpansionEstimator.Describe(false, "Mini Tower", 2, 4, "PCIEX1_1, M.2_2", 2));

    [Fact]
    public void Tower_WithoutSlotData_StillEstimatesFromChassis()
        => Assert.Equal("Estimate: tower/desktop chassis, 1 internal disk — likely room for additional drives.",
            ExpansionEstimator.Describe(false, "Desktop", null, null, null, 1));

    [Fact]
    public void Tower_AllSlotsUsed_OmitsNames()
        => Assert.Equal("Estimate: tower/desktop chassis, 3 internal disks, 0 of 3 expansion slots free — likely room for additional drives.",
            ExpansionEstimator.Describe(false, "Tower", 0, 3, null, 3));

    [Fact]
    public void Compact_SaysLimited()
        => Assert.StartsWith("Estimate: compact chassis, 1 internal disk, 1 of 1 expansion slot free (M.2_1) — limited room",
            ExpansionEstimator.Describe(false, "All in One", 1, 1, "M.2_1", 1));

    [Fact]
    public void Server_DefersToVendor()
    {
        var s = ExpansionEstimator.Describe(false, "Rack Mount Chassis", 1, 3, "PCIe3", 8);
        Assert.StartsWith("Estimate: server chassis, 8 internal disks, 1 of 3 expansion slots free (PCIe3) — drive bays are not reported by WMI", s);
    }

    [Fact]
    public void UnknownChassis_WithSlots_ReportsSlotsOnly()
        => Assert.Equal("Estimate: 1 internal disk, 1 of 2 expansion slots free (PCIEX16_1).",
            ExpansionEstimator.Describe(false, "Other", 1, 2, "PCIEX16_1", 1));

    [Theory]
    [InlineData(null)]
    [InlineData("Unknown")]
    [InlineData("Docking Station")]
    public void UnknownChassis_WithoutSlots_IsNull(string? chassis)
        => Assert.Null(ExpansionEstimator.Describe(false, chassis, null, null, null, 1));

    [Fact]
    public void CountInternal_ExcludesUsbAttachedDevices()
    {
        // A laptop with one NVMe and two empty USB card-reader slots has one internal disk, not three.
        var disks = new[]
        {
            new DiskInfo { Model = "WD_BLACK SN770", BusType = "NVMe" },
            new DiskInfo { Model = "NORELSYS 1081CS0 USB Device", BusType = "USB" },
            new DiskInfo { Model = "NORELSYS 1081CS1 USB Device", BusType = "usb" },
            new DiskInfo { Model = "Old host, bus unknown" },
        };
        Assert.Equal(2, ExpansionEstimator.CountInternal(disks));
    }

    [Fact]
    public void EveryNonNullResult_IsLabelledAnEstimate()
    {
        foreach (var chassis in new[] { "Laptop", "Desktop", "Tower", "All in One", "Blade", "Other", "Type 99" })
        {
            var s = ExpansionEstimator.Describe(false, chassis, 1, 2, "X", 1);
            if (s is not null) Assert.StartsWith("Estimate:", s);
        }
    }
}
