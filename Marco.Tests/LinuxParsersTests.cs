using Marco.Core.Model;
using Marco.Inventory.Linux;
using Xunit;

namespace Marco.Tests;

public class LinuxParsersTests
{
    [Fact]
    public void OsRelease_ParsesQuotedValues()
    {
        var text = "NAME=\"Ubuntu\"\nVERSION_ID=\"22.04\"\nPRETTY_NAME=\"Ubuntu 22.04.3 LTS\"\nID=ubuntu\n";
        var os = LinuxParsers.ParseOsRelease(text);
        Assert.Equal("Ubuntu 22.04.3 LTS", os["PRETTY_NAME"]);
        Assert.Equal("22.04", os["VERSION_ID"]);
        Assert.Equal("ubuntu", os["ID"]);
    }

    [Fact]
    public void MemTotal_ConvertsKbToBytes()
    {
        var text = "MemTotal:       16333764 kB\nMemFree:        1000000 kB\n";
        Assert.Equal(16333764L * 1024, LinuxParsers.ParseMemTotalBytes(text));
    }

    [Fact]
    public void Lscpu_ParsesCoresAndModel()
    {
        var text = string.Join('\n',
            "Architecture:            x86_64",
            "CPU(s):                  8",
            "Core(s) per socket:      4",
            "Socket(s):               1",
            "Model name:              Intel(R) Core(TM) i7-9700",
            "CPU max MHz:             4700.0000");
        var cpu = LinuxParsers.ParseLscpu(text);
        Assert.Equal("Intel(R) Core(TM) i7-9700", cpu.Name);
        Assert.Equal(8, cpu.LogicalProcessors);
        Assert.Equal(4, cpu.Cores);       // cores/socket * sockets
        Assert.Equal(4700, cpu.ClockMhz);
    }

    [Fact]
    public void Lsblk_ParsesDisksOnly_WithSpacesInModel()
    {
        var text = string.Join('\n',
            "NAME=\"sda\" SIZE=\"512110190592\" TYPE=\"disk\" MODEL=\"Samsung SSD 860\" SERIAL=\"S3Z9\"",
            "NAME=\"sda1\" SIZE=\"500107862016\" TYPE=\"part\" MODEL=\"\" SERIAL=\"\"");
        var disks = LinuxParsers.ParseLsblk(text);
        Assert.Single(disks);
        Assert.Equal("Samsung SSD 860", disks[0].Model);
        Assert.Equal(512110190592L, disks[0].SizeBytes);
        Assert.Equal("S3Z9", disks[0].Serial);
        Assert.Equal("Disk", disks[0].MediaType); // classic columns: no ROTA → generic
        Assert.Null(disks[0].BusType);
    }

    [Fact]
    public void Lsblk_RotaAndTran_GiveMediaAndBusType()
    {
        var text = string.Join('\n',
            "NAME=\"nvme0n1\" SIZE=\"512110190592\" TYPE=\"disk\" MODEL=\"Samsung 980\" SERIAL=\"S1\" ROTA=\"0\" TRAN=\"nvme\"",
            "NAME=\"sda\" SIZE=\"2000398934016\" TYPE=\"disk\" MODEL=\"WDC WD20\" SERIAL=\"S2\" ROTA=\"1\" TRAN=\"sata\"",
            "NAME=\"sdb\" SIZE=\"64000000000\" TYPE=\"disk\" MODEL=\"Flash\" SERIAL=\"\" ROTA=\"0\" TRAN=\"usb\"",
            "NAME=\"vda\" SIZE=\"64000000000\" TYPE=\"disk\" MODEL=\"\" SERIAL=\"\" ROTA=\"1\" TRAN=\"\"");
        var disks = LinuxParsers.ParseLsblk(text);

        Assert.Equal(4, disks.Count);
        Assert.Equal(("SSD", "NVMe"), (disks[0].MediaType, disks[0].BusType));
        Assert.Equal(("HDD", "SATA"), (disks[1].MediaType, disks[1].BusType));
        Assert.Equal(("SSD", "USB"), (disks[2].MediaType, disks[2].BusType));
        Assert.Equal("HDD", disks[3].MediaType);
        Assert.Null(disks[3].BusType); // empty TRAN (virtio disks) → unknown bus
        Assert.Equal("SSD · NVMe", disks[0].KindDisplay);
    }

    [Fact]
    public void Df_SkipsPseudoFilesystems()
    {
        var text = string.Join('\n',
            "Filesystem     Type   1B-blocks       Used   Available Use% Mounted on",
            "/dev/sda1      ext4  500000000000 200000000000 300000000000  40% /",
            "tmpfs          tmpfs   8000000000            0   8000000000   0% /run",
            "/dev/sda2      ext4  100000000000  10000000000  90000000000  10% /home");
        var vols = LinuxParsers.ParseDf(text);
        Assert.Equal(2, vols.Count);
        Assert.Contains(vols, v => v.Letter == "/" && v.FileSystem == "ext4" && v.CapacityBytes == 500000000000L);
        Assert.DoesNotContain(vols, v => v.FileSystem == "tmpfs");
    }

    [Fact]
    public void Ip_JoinsMacAndIpByInterface()
    {
        var link = string.Join('\n',
            "1: lo: <LOOPBACK,UP> mtu 65536 ... link/loopback 00:00:00:00:00:00 brd 00:00:00:00:00:00",
            "2: eth0: <BROADCAST,MULTICAST,UP> mtu 1500 ... link/ether 00:11:22:33:44:55 brd ff:ff:ff:ff:ff:ff");
        var addr = string.Join('\n',
            "1: lo    inet 127.0.0.1/8 scope host lo",
            "2: eth0    inet 192.168.1.20/24 brd 192.168.1.255 scope global eth0");
        var adapters = LinuxParsers.ParseIp(link, addr);
        Assert.Single(adapters);
        Assert.Equal("eth0", adapters[0].Name);
        Assert.Equal("00:11:22:33:44:55", adapters[0].Mac);
        Assert.Contains("192.168.1.20", adapters[0].IpAddresses);
    }

    [Fact]
    public void Dpkg_ParsesTabbedPackages()
    {
        var text = "bash\t5.1-6ubuntu1\tUbuntu Developers\nopenssh-server\t8.9p1-3\tUbuntu Developers\n";
        var pkgs = LinuxParsers.ParseDpkg(text);
        Assert.Equal(2, pkgs.Count);
        var bash = pkgs.Single(p => p.DisplayName == "bash");
        Assert.Equal("5.1-6ubuntu1", bash.Version);
        Assert.Equal("Ubuntu Developers", bash.Publisher);
        Assert.Equal(SoftwareSource.Package, bash.Source);
    }

    [Fact]
    public void Rpm_ParsesTabbedPackages()
    {
        var text = "kernel\t5.14.0-70.el9\tRed Hat, Inc.\nhttpd\t2.4.53-7.el9\tRed Hat, Inc.\n";
        var pkgs = LinuxParsers.ParseRpm(text);
        Assert.Equal(2, pkgs.Count);
        Assert.Contains(pkgs, p => p.DisplayName == "httpd" && p.Version == "2.4.53-7.el9");
    }

    [Fact]
    public void Apk_SplitsNameAndVersion()
    {
        var text = "musl-1.2.4-r2\nbusybox-1.36.1-r5\n";
        var pkgs = LinuxParsers.ParseApk(text);
        Assert.Contains(pkgs, p => p.DisplayName == "musl" && p.Version == "1.2.4-r2");
        Assert.Contains(pkgs, p => p.DisplayName == "busybox" && p.Version == "1.36.1-r5");
    }

    [Fact]
    public void Who_ReturnsFirstUser_LastReturnsUser()
    {
        Assert.Equal("nick", LinuxParsers.ParseWhoCurrentUser("nick     pts/0        2024-01-15 10:00 (10.0.0.5)\n"));
        Assert.Equal("admin", LinuxParsers.ParseLastUser("admin    pts/0    10.0.0.9  Mon Jan 15 09:00   still logged in\n\nwtmp begins ...\n"));
        Assert.Null(LinuxParsers.ParseWhoCurrentUser(""));
    }

    [Fact]
    public void BootTime_Parses()
        => Assert.Equal(new DateTime(2024, 1, 10, 8, 30, 0), LinuxParsers.ParseBootTime("2024-01-10 08:30:00\n"));
}
