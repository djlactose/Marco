using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;
using Marco.Inventory.Collectors;
using Xunit;
using static Marco.Tests.WmiFakeBuilders;

namespace Marco.Tests;

public class CollectorTests
{
    private static InventoryContext Ctx(FakeWmiSession wmi, FakeRemoteRegistry? reg = null)
        => new(wmi, reg ?? new FakeRemoteRegistry());

    [Fact]
    public async Task SystemCollector_PopulatesIdentityAndPrefersBiosSerial()
    {
        var wmi = new FakeWmiSession()
            .With("Win32_ComputerSystem", Obj(("Name", "PC1"), ("Manufacturer", "Dell Inc."),
                ("Model", "OptiPlex"), ("Domain", "CORP"), ("PartOfDomain", true), ("UserName", "CORP\\jane")))
            .With("Win32_SystemEnclosure", Obj(("SerialNumber", "ENC123"), ("ChassisTypes", new ushort[] { 3 })))
            .With("Win32_BIOS", Obj(("SerialNumber", "BIOS999"), ("SMBIOSBIOSVersion", "1.2.3")))
            .With("Win32_BaseBoard", Obj(("Manufacturer", "Dell"), ("Product", "0ABC")));

        var m = new Machine("10.0.0.1");
        await new SystemCollector().CollectAsync(Ctx(wmi), m, default);

        Assert.Equal("Dell Inc.", m.System.Manufacturer);
        Assert.Equal("OptiPlex", m.System.Model);
        Assert.Equal("Desktop", m.System.ChassisType);
        Assert.Equal("BIOS999", m.System.SerialNumber); // BIOS wins over enclosure
        Assert.Equal("CORP\\jane", m.System.LoggedOnUser);
        Assert.True(m.System.PartOfDomain);
        Assert.Equal("0ABC", m.System.MotherboardModel);
    }

    [Fact]
    public async Task OsCollector_SetsServer_FromProductType()
    {
        var wmi = new FakeWmiSession().With("Win32_OperatingSystem",
            Obj(("Caption", "Windows Server 2022"), ("Version", "10.0.20348"), ("BuildNumber", "20348"),
                ("OSArchitecture", "64-bit"), ("ProductType", 3)));
        var m = new Machine("10.0.0.2") { DeviceType = DeviceType.Windows };
        await new OsCollector().CollectAsync(Ctx(wmi), m, default);

        Assert.Equal("Windows Server 2022", m.Os.Caption);
        Assert.Equal(DeviceType.WindowsServer, m.DeviceType);
    }

    [Fact]
    public async Task OsCollector_ThrowsNotSupported_WhenClassMissing()
    {
        var wmi = new FakeWmiSession(); // no Win32_OperatingSystem rows
        var m = new Machine("10.0.0.3");
        var ex = await Assert.ThrowsAsync<WmiException>(() => new OsCollector().CollectAsync(Ctx(wmi), m, default));
        Assert.Equal(WmiFailureKind.NotSupported, ex.Kind);
    }

    [Fact]
    public async Task MemoryCollector_SumsCapacityAndReadsSlots()
    {
        var wmi = new FakeWmiSession()
            .With("Win32_PhysicalMemory",
                Obj(("Capacity", (ulong)8_589_934_592), ("Speed", 3200), ("DeviceLocator", "DIMM0"), ("PartNumber", "ABC")),
                Obj(("Capacity", (ulong)8_589_934_592), ("Speed", 3200), ("DeviceLocator", "DIMM1"), ("PartNumber", "ABC")))
            .With("Win32_PhysicalMemoryArray", Obj(("MemoryDevices", 4)));

        var m = new Machine("10.0.0.4");
        await new MemoryCollector().CollectAsync(Ctx(wmi), m, default);

        Assert.Equal(17_179_869_184, m.TotalMemoryBytes);
        Assert.Equal(2, m.MemorySlotsUsed);
        Assert.Equal(4, m.MemorySlotsTotal);
    }

    [Fact]
    public async Task CpuCollector_HandlesMissingPropertiesGracefully()
    {
        var wmi = new FakeWmiSession().With("Win32_Processor",
            Obj(("Name", "Intel i5"))); // no core counts
        var m = new Machine("10.0.0.5");
        await new CpuCollector().CollectAsync(Ctx(wmi), m, default);

        Assert.Single(m.Cpus);
        Assert.Equal("Intel i5", m.Cpus[0].Name);
        Assert.Equal(0, m.Cpus[0].Cores); // missing → 0, not a crash
        Assert.Equal(1, m.CpuCount);
    }

    [Fact]
    public async Task StorageCollector_ReadsDisksAndLocalVolumes()
    {
        var wmi = new FakeWmiSession()
            .With("Win32_DiskDrive", Obj(("Model", "Samsung SSD"), ("Size", (ulong)512_000_000_000),
                ("MediaType", "Fixed hard disk media"), ("Status", "OK")))
            .With("Win32_LogicalDisk", Obj(("DeviceID", "C:"), ("FileSystem", "NTFS"),
                ("Size", (ulong)500_000_000_000), ("FreeSpace", (ulong)200_000_000_000)));

        var m = new Machine("10.0.0.6");
        await new StorageCollector().CollectAsync(Ctx(wmi), m, default);

        Assert.Single(m.Disks);
        Assert.Equal("Samsung SSD", m.Disks[0].Model);
        Assert.Single(m.Volumes);
        Assert.Equal("C:", m.Volumes[0].Letter);
    }

    [Fact]
    public async Task NetworkCollector_JoinsAdapterSpeedToConfig()
    {
        var wmi = new FakeWmiSession()
            .With("Win32_NetworkAdapter", Obj(("Index", 1), ("Name", "Intel NIC"), ("Speed", (ulong)1_000_000_000)))
            .With("Win32_NetworkAdapterConfiguration",
                Obj(("Index", 1), ("Description", "Intel NIC"), ("MACAddress", "00:11:22:33:44:55"),
                    ("IPAddress", new[] { "10.0.0.6", "fe80::1" }), ("IPSubnet", new[] { "255.255.255.0" }),
                    ("DefaultIPGateway", new[] { "10.0.0.1" }), ("DHCPEnabled", true)));

        var m = new Machine("10.0.0.6");
        await new NetworkCollector().CollectAsync(Ctx(wmi), m, default);

        Assert.Single(m.Adapters);
        var a = m.Adapters[0];
        Assert.Equal(1_000_000_000, a.SpeedBps);
        Assert.Equal("00:11:22:33:44:55", a.Mac);
        Assert.Contains("10.0.0.6", a.IpAddresses);
        Assert.Contains("00:11:22:33:44:55", m.MacAddresses);
    }

    [Fact]
    public async Task SoftwareCollector_ReadsAllHivesAndTagsSources()
    {
        var reg = new FakeRemoteRegistry { SupportsLastWriteTime = true };
        reg.Subkeys["LocalMachine:SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall"] = new()
        {
            Key("app64", null, ("DisplayName", "App64"), ("DisplayVersion", "1.0")),
        };
        reg.Subkeys["LocalMachine:SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall"] = new()
        {
            Key("app32", null, ("DisplayName", "App32"), ("DisplayVersion", "2.0")),
        };
        reg.SubkeyNames["Users:"] = new() { "S-1-5-21-111-222-333-1001", "S-1-5-18" };
        reg.Subkeys["Users:S-1-5-21-111-222-333-1001\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall"] = new()
        {
            Key("appuser", null, ("DisplayName", "UserApp"), ("DisplayVersion", "3.0")),
        };

        var m = new Machine("10.0.0.7");
        await new SoftwareCollector().CollectAsync(new InventoryContext(new FakeWmiSession(), reg), m, default);

        Assert.Equal(3, m.Software.Count);
        Assert.Contains(m.Software, s => s.DisplayName == "App64" && s.Source == SoftwareSource.Native64);
        Assert.Contains(m.Software, s => s.DisplayName == "App32" && s.Source == SoftwareSource.Wow6432);
        Assert.Contains(m.Software, s => s.DisplayName == "UserApp" && s.Source == SoftwareSource.PerUser);
        Assert.Equal(3, m.SoftwareCount);
    }
}
