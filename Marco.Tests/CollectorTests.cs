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

    private const string LogonUiKey =
        "LocalMachine:SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Authentication\\LogonUI";

    [Fact]
    public async Task SystemCollector_ShowsLastLoggedOnUser_WhenNobodyCurrentlyLoggedOn()
    {
        var wmi = new FakeWmiSession()
            .With("Win32_ComputerSystem", Obj(("Name", "PC2"), ("UserName", null))); // nobody at console
        var reg = new FakeRemoteRegistry();
        reg.KeyValues[LogonUiKey] = new() { ["LastLoggedOnUser"] = "CORP\\jdoe" };

        var m = new Machine("10.0.0.9");
        await new SystemCollector().CollectAsync(Ctx(wmi, reg), m, default);

        Assert.Null(m.System.LoggedOnUser);
        Assert.Equal("CORP\\jdoe", m.System.LastLoggedOnUser);
        Assert.Equal("CORP\\jdoe (last)", m.System.CurrentOrLastUser);
    }

    [Fact]
    public async Task SystemCollector_PrefersCurrentUser_OverLast()
    {
        var wmi = new FakeWmiSession()
            .With("Win32_ComputerSystem", Obj(("Name", "PC3"), ("UserName", "CORP\\alice")));
        var reg = new FakeRemoteRegistry();
        reg.KeyValues[LogonUiKey] = new() { ["LastLoggedOnUser"] = "CORP\\bob" };

        var m = new Machine("10.0.0.10");
        await new SystemCollector().CollectAsync(Ctx(wmi, reg), m, default);

        Assert.Equal("CORP\\alice", m.System.CurrentOrLastUser); // current wins, no "(last)"
    }

    [Fact]
    public async Task SystemCollector_RegistryUnavailable_DoesNotFailCollector()
    {
        var wmi = new FakeWmiSession()
            .With("Win32_ComputerSystem", Obj(("Name", "PC4"), ("Manufacturer", "Dell")));
        var reg = new FakeRemoteRegistry { ThrowOnAccess = true };

        var m = new Machine("10.0.0.11");
        await new SystemCollector().CollectAsync(Ctx(wmi, reg), m, default); // must not throw

        Assert.Equal("Dell", m.System.Manufacturer);
        Assert.Null(m.System.LastLoggedOnUser);
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
        Assert.Null(m.MaxMemoryBytes); // no MaxCapacity reported → unknown, not 0
        Assert.Null(m.MemoryModules[0].MemoryTypeName);
    }

    [Fact]
    public async Task MemoryCollector_DecodesTypeFormFactorConfiguredSpeed_AndPlatformMax()
    {
        var wmi = new FakeWmiSession()
            .With("Win32_PhysicalMemory",
                Obj(("Capacity", (ulong)8_589_934_592), ("Speed", 3200), ("ConfiguredClockSpeed", 2933),
                    ("SMBIOSMemoryType", 26), ("MemoryType", 0), ("FormFactor", 12), ("DeviceLocator", "DIMM A")))
            .With("Win32_PhysicalMemoryArray", Obj(("MemoryDevices", 2), ("MaxCapacity", (uint)0x80000000), ("MaxCapacityEx", (ulong)67_108_864)));

        var m = new Machine("10.0.0.4");
        await new MemoryCollector().CollectAsync(Ctx(wmi), m, default);

        var mod = Assert.Single(m.MemoryModules);
        Assert.Equal("DDR4", mod.MemoryTypeName);
        Assert.Equal("SODIMM", mod.FormFactor);
        Assert.Equal(2933, mod.ConfiguredSpeedMhz);
        Assert.Equal("DDR4 · 3200 MHz (running 2933) · SODIMM", mod.TypeDisplay);
        Assert.Equal(64L * 1024 * 1024 * 1024, m.MaxMemoryBytes); // 67108864 KB
        Assert.Equal("8 GB, 1 of 2 slots used, max 64 GB", m.MemorySummary);
    }

    [Fact]
    public async Task MemoryCollector_FallsBackToMemoryType_WhenSmbiosTypeUnknown()
    {
        var wmi = new FakeWmiSession()
            .With("Win32_PhysicalMemory", Obj(("Capacity", (ulong)4_294_967_296), ("SMBIOSMemoryType", 0), ("MemoryType", 24)))
            .With("Win32_PhysicalMemoryArray", Obj(("MemoryDevices", 2), ("MaxCapacity", (uint)16_777_216)));

        var m = new Machine("10.0.0.4");
        await new MemoryCollector().CollectAsync(Ctx(wmi), m, default);

        Assert.Equal("DDR3", m.MemoryModules[0].MemoryTypeName);
        Assert.Null(m.MemoryModules[0].FormFactor);
        Assert.Equal(16L * 1024 * 1024 * 1024, m.MaxMemoryBytes); // MaxCapacity (KB) used when Ex is absent
    }

    [Fact]
    public async Task MemoryCollector_ReportsMaxAsUnknown_WhenPlaceholderOrBelowInstalled()
    {
        // Only the 0x80000000 "see extended" sentinel, and no MaxCapacityEx.
        var placeholder = new FakeWmiSession()
            .With("Win32_PhysicalMemory", Obj(("Capacity", (ulong)8_589_934_592)))
            .With("Win32_PhysicalMemoryArray", Obj(("MemoryDevices", 2), ("MaxCapacity", (uint)0x80000000)));
        var m1 = new Machine("10.0.0.4");
        await new MemoryCollector().CollectAsync(Ctx(placeholder), m1, default);
        Assert.Null(m1.MaxMemoryBytes);

        // A stale SMBIOS table claiming less than what is installed.
        var stale = new FakeWmiSession()
            .With("Win32_PhysicalMemory", Obj(("Capacity", (ulong)34_359_738_368)))
            .With("Win32_PhysicalMemoryArray", Obj(("MemoryDevices", 2), ("MaxCapacity", (uint)16_777_216)));
        var m2 = new Machine("10.0.0.4");
        await new MemoryCollector().CollectAsync(Ctx(stale), m2, default);
        Assert.Null(m2.MaxMemoryBytes);
        Assert.Equal("32 GB, 1 of 2 slots used", m2.MemorySummary);
    }

    [Fact]
    public async Task MemoryCollector_RetriesWithClassicColumns_OnOlderHosts()
    {
        // Pre-Win10: the class has no SMBIOSMemoryType, and WMI rejects the whole SELECT.
        var wmi = new FakeWmiSession()
            .ThrowsWhenWqlContains("SMBIOSMemoryType")
            .ThrowsWhenWqlContains("MaxCapacityEx")
            .With("Win32_PhysicalMemory", Obj(("Capacity", (ulong)4_294_967_296), ("Speed", 1333), ("DeviceLocator", "DIMM0")))
            .With("Win32_PhysicalMemoryArray", Obj(("MemoryDevices", 4), ("MaxCapacity", (uint)33_554_432)));

        var m = new Machine("10.0.0.4");
        await new MemoryCollector().CollectAsync(Ctx(wmi), m, default);

        Assert.Equal(4_294_967_296, m.TotalMemoryBytes);
        Assert.Equal(1333, m.MemoryModules[0].SpeedMhz);
        Assert.Equal(4, m.MemorySlotsTotal);
        Assert.Equal(32L * 1024 * 1024 * 1024, m.MaxMemoryBytes);
        Assert.Equal(4, wmi.Queries.Count); // extended → classic, for both classes
    }

    [Fact]
    public async Task MemoryCollector_StillFails_WhenClassicQueryFailsToo()
    {
        var wmi = new FakeWmiSession()
            .Throws("Win32_PhysicalMemory", new WmiException(WmiFailureKind.AccessDenied, "denied"));
        var ex = await Assert.ThrowsAsync<WmiException>(() => new MemoryCollector().CollectAsync(Ctx(wmi), new Machine("10.0.0.4"), default));
        Assert.Equal(WmiFailureKind.AccessDenied, ex.Kind);
    }

    [Theory]
    [InlineData(20, "DDR")]
    [InlineData(24, "DDR3")]
    [InlineData(26, "DDR4")]
    [InlineData(34, "DDR5")]
    [InlineData(30, "LPDDR4")]
    [InlineData(35, "LPDDR5")]
    [InlineData(0, null)]
    [InlineData(2, null)]
    [InlineData(null, null)]
    public void MemoryCollector_DescribeMemoryType(int? code, string? expected)
        => Assert.Equal(expected, MemoryCollector.DescribeMemoryType(code));

    [Fact]
    public void MemoryCollector_DescribeFormFactor()
    {
        Assert.Equal("DIMM", MemoryCollector.DescribeFormFactor(8));
        Assert.Equal("SODIMM", MemoryCollector.DescribeFormFactor(12));
        Assert.Null(MemoryCollector.DescribeFormFactor(0));
        Assert.Null(MemoryCollector.DescribeFormFactor(null));
    }

    [Fact]
    public async Task SystemCollector_CountsExpansionSlots_AndListsFreeOnes()
    {
        var wmi = new FakeWmiSession()
            .With("Win32_ComputerSystem", Obj(("Name", "PC1"), ("Manufacturer", "Dell Inc."), ("Model", "OptiPlex")))
            .With("Win32_SystemEnclosure", Obj(("ChassisTypes", new ushort[] { 6 })))
            .With("Win32_SystemSlot",
                Obj(("SlotDesignation", "PCIEX16_1"), ("CurrentUsage", 4)),
                Obj(("SlotDesignation", "PCIEX1_1"), ("CurrentUsage", 3)),
                Obj(("SlotDesignation", "M.2_2"), ("CurrentUsage", 3)),
                Obj(("SlotDesignation", "J7"), ("CurrentUsage", 2))); // Unknown usage: counted, not free

        var m = new Machine("10.0.0.1");
        await new SystemCollector().CollectAsync(Ctx(wmi), m, default);

        Assert.Equal("Mini Tower", m.System.ChassisType);
        Assert.Equal(4, m.System.ExpansionSlotsTotal);
        Assert.Equal(2, m.System.ExpansionSlotsFree);
        Assert.Equal("PCIEX1_1, M.2_2", m.System.ExpansionSlotsFreeList);
        Assert.Equal("Estimate: tower/desktop chassis, 0 internal disks, 2 of 4 expansion slots free (PCIEX1_1, M.2_2) — likely room for additional drives.", m.ExpansionOutlook);
    }

    [Fact]
    public async Task SystemCollector_LeavesSlotsNull_WhenClassEmptyOrFails()
    {
        var empty = new FakeWmiSession()
            .With("Win32_ComputerSystem", Obj(("Name", "VM1"), ("Manufacturer", "VMware, Inc.")));
        var m1 = new Machine("10.0.0.1");
        await new SystemCollector().CollectAsync(Ctx(empty), m1, default);
        Assert.Null(m1.System.ExpansionSlotsTotal);
        Assert.Null(m1.System.ExpansionSlotsFree);
        Assert.Null(m1.ExpansionOutlook);

        var denied = new FakeWmiSession()
            .With("Win32_ComputerSystem", Obj(("Name", "PC1")))
            .Throws("Win32_SystemSlot", new WmiException(WmiFailureKind.AccessDenied, "denied"));
        var m2 = new Machine("10.0.0.2");
        await new SystemCollector().CollectAsync(Ctx(denied), m2, default); // must not throw
        Assert.Null(m2.System.ExpansionSlotsTotal);
        Assert.Equal("PC1", m2.Name);
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
