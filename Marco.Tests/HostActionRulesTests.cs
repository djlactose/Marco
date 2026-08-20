using Marco.Core.Actions;
using Marco.Core.Model;
using Xunit;

namespace Marco.Tests;

public class HostActionRulesTests
{
    private static Machine Host(DeviceType type, bool alive = true, string? name = "PC", string? mac = null,
        string? serial = null, params int[] ports)
    {
        var m = new Machine("10.0.0.20") { DeviceType = type, IsAlive = alive, Name = name };
        m.System.SerialNumber = serial;
        if (mac is not null) m.MacAddresses.Add(mac);
        foreach (var p in ports) m.OpenPorts.Add(p);
        return m;
    }

    [Fact]
    public void Rdp_OfferedOnPortEvidence_OrRdpFlag()
    {
        Assert.Contains(HostActionKind.Rdp, HostActionRules.AvailableFor(Host(DeviceType.Windows, ports: 3389)));

        var byFlag = Host(DeviceType.Windows);
        byFlag.Security.RdpEnabled = true;
        Assert.Contains(HostActionKind.Rdp, HostActionRules.AvailableFor(byFlag));

        Assert.DoesNotContain(HostActionKind.Rdp, HostActionRules.AvailableFor(Host(DeviceType.Windows)));
    }

    [Fact]
    public void Ssh_ForLinux_OrPort22()
    {
        Assert.Contains(HostActionKind.Ssh, HostActionRules.AvailableFor(Host(DeviceType.UnixLinux)));
        Assert.Contains(HostActionKind.Ssh, HostActionRules.AvailableFor(Host(DeviceType.Windows, ports: 22)));
        Assert.DoesNotContain(HostActionKind.Ssh, HostActionRules.AvailableFor(Host(DeviceType.Windows)));
    }

    [Fact]
    public void WebAdmin_ForNetworkGearWithHttpPort()
    {
        var printer = HostActionRules.AvailableFor(Host(DeviceType.Printer, ports: 80));
        Assert.Contains(HostActionKind.WebAdminHttp, printer);

        var https = HostActionRules.AvailableFor(Host(DeviceType.NetworkDevice, ports: 443));
        Assert.Contains(HostActionKind.WebAdminHttps, https);
    }

    [Fact]
    public void SharesAndComputerManagement_ForWindows()
    {
        var win = HostActionRules.AvailableFor(Host(DeviceType.Windows));
        Assert.Contains(HostActionKind.AdminShare, win);
        Assert.Contains(HostActionKind.ComputerManagement, win);

        Assert.DoesNotContain(HostActionKind.AdminShare, HostActionRules.AvailableFor(Host(DeviceType.UnixLinux)));
    }

    [Fact]
    public void CopyActions_GatedOnData()
    {
        var bare = HostActionRules.AvailableFor(Host(DeviceType.Unknown, name: null));
        Assert.Contains(HostActionKind.CopyIp, bare);                 // always
        Assert.DoesNotContain(HostActionKind.CopyHostname, bare);     // no name
        Assert.DoesNotContain(HostActionKind.CopyMac, bare);          // no mac
        Assert.DoesNotContain(HostActionKind.CopySerial, bare);       // no serial

        var full = HostActionRules.AvailableFor(Host(DeviceType.Windows, name: "PC-A", mac: "3C:52:82:AA:BB:CC", serial: "SER-1"));
        Assert.Contains(HostActionKind.CopyHostname, full);
        Assert.Contains(HostActionKind.CopyMac, full);
        Assert.Contains(HostActionKind.CopySerial, full);
    }

    [Fact]
    public void Wake_OnlyForAsleepHostWithMac()
    {
        Assert.Contains(HostActionKind.Wake,
            HostActionRules.AvailableFor(Host(DeviceType.Windows, alive: false, mac: "3C:52:82:AA:BB:CC")));
        Assert.DoesNotContain(HostActionKind.Wake,
            HostActionRules.AvailableFor(Host(DeviceType.Windows, alive: true, mac: "3C:52:82:AA:BB:CC"))); // alive
        Assert.DoesNotContain(HostActionKind.Wake,
            HostActionRules.AvailableFor(Host(DeviceType.Windows, alive: false))); // no MAC
    }
}
