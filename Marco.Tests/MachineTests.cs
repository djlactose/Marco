using Marco.Core.Model;
using Xunit;

namespace Marco.Tests;

public class MachineTests
{
    [Fact]
    public void MemorySummary_ComposesInstalledSlotsAndMax()
    {
        var m = new Machine("10.0.0.1");
        Assert.Null(m.MemorySummary); // nothing collected yet

        m.TotalMemoryBytes = 16L << 30;
        Assert.Equal("16 GB", m.MemorySummary);

        m.MemorySlotsUsed = 2; m.MemorySlotsTotal = 4;
        Assert.Equal("16 GB, 2 of 4 slots used", m.MemorySummary);

        m.MaxMemoryBytes = 64L << 30;
        Assert.Equal("16 GB, 2 of 4 slots used, max 64 GB", m.MemorySummary);

        m.MemorySlotsTotal = 1; m.MemorySlotsUsed = 1;
        Assert.Equal("16 GB, 1 of 1 slot used, max 64 GB", m.MemorySummary);
    }

    [Fact]
    public void MemorySummary_RaisesWhenInputsChange()
    {
        var m = new Machine("10.0.0.1");
        var raised = new List<string?>();
        m.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        m.MaxMemoryBytes = 32L << 30;
        Assert.Contains(nameof(Machine.MemorySummary), raised);
    }

    [Fact]
    public void MemoryModule_TypeDisplay_OnlyShowsRunningSpeedWhenItDiffers()
    {
        Assert.Equal("DDR4 · 3200 MHz · SODIMM",
            new MemoryModule { MemoryTypeName = "DDR4", SpeedMhz = 3200, ConfiguredSpeedMhz = 3200, FormFactor = "SODIMM" }.TypeDisplay);
        Assert.Equal("DDR4 · 3200 MHz (running 2933) · SODIMM",
            new MemoryModule { MemoryTypeName = "DDR4", SpeedMhz = 3200, ConfiguredSpeedMhz = 2933, FormFactor = "SODIMM" }.TypeDisplay);
        Assert.Equal("2133 MHz", new MemoryModule { ConfiguredSpeedMhz = 2133 }.TypeDisplay); // rated speed unreported
        Assert.Equal("DDR3", new MemoryModule { MemoryTypeName = "DDR3" }.TypeDisplay);
        Assert.Null(new MemoryModule().TypeDisplay);
    }

    [Fact]
    public void ExpansionOutlook_UsesSystemSlotsAndDiskCount()
    {
        var m = new Machine("10.0.0.1");
        Assert.Null(m.ExpansionOutlook);

        m.System.ChassisType = "Laptop";
        m.Disks = new List<DiskInfo> { new() { Model = "NVMe" } };
        Assert.Equal("Estimate: laptop/portable chassis, 1 internal disk — internal drive expansion unlikely.", m.ExpansionOutlook);

        m.IsVirtual = true;
        Assert.Null(m.ExpansionOutlook);
    }

    [Fact]
    public void AddressSortKey_OrdersNumerically_NotAlphabetically()
    {
        var addresses = new[] { "10.0.0.10", "10.0.0.2", "10.0.0.1", "192.168.1.1", "9.255.255.255", "10.0.1.0" };
        var sorted = addresses.Select(a => new Machine(a)).OrderBy(m => m.AddressSortKey).Select(m => m.Address).ToArray();
        Assert.Equal(new[] { "9.255.255.255", "10.0.0.1", "10.0.0.2", "10.0.0.10", "10.0.1.0", "192.168.1.1" }, sorted);
    }

    [Fact]
    public void AddressSortKey_NonIpv4_SortsLast()
    {
        var host = new Machine("server01");
        var v6 = new Machine("fe80::1");
        var ip = new Machine("255.255.255.254");
        Assert.Equal(long.MaxValue, host.AddressSortKey);
        Assert.Equal(long.MaxValue, v6.AddressSortKey);
        Assert.True(ip.AddressSortKey < host.AddressSortKey);
    }
}
