using System.Net;
using Marco.Core.Targets;
using Marco.Discovery;
using Xunit;

namespace Marco.Tests;

public class LocalNetworksTests
{
    [Theory]
    [InlineData("192.168.86.56", 24, "192.168.86.0/24")]
    [InlineData("10.0.5.37", 16, "10.0.0.0/16")]
    [InlineData("172.21.112.1", 20, "172.21.112.0/20")]
    [InlineData("10.1.2.3", 8, "10.0.0.0/8")]
    [InlineData("192.168.1.1", 32, "192.168.1.1/32")]
    public void ToCidr_ComputesNetworkAddress(string ip, int prefix, string expected)
        => Assert.Equal(expected, LocalNetworks.ToCidr(IPAddress.Parse(ip), prefix));

    [Fact]
    public void ToCidr_OutputIsParseableByTargetParser()
    {
        var cidr = LocalNetworks.ToCidr(IPAddress.Parse("192.168.86.56"), 24)!;
        var hosts = TargetParser.Parse(new[] { cidr }).ToList();
        Assert.Equal(254, hosts.Count); // /24 minus network + broadcast
        Assert.Equal("192.168.86.1", hosts[0].Address);
    }

    [Fact]
    public void PointToPoint_IsNotScannable_AndHintsVpn()
    {
        var vpnP2P = new LocalSubnet("Corp VPN", "10.8.0.6", 32, "10.8.0.6/32", HasGateway: true, IsVpn: true);
        Assert.True(vpnP2P.IsPointToPoint);
        Assert.False(vpnP2P.IsScannableSubnet);
        Assert.Contains("point-to-point", vpnP2P.Hint);
        Assert.Contains("VPN", vpnP2P.Hint);

        var lan = new LocalSubnet("Ethernet", "192.168.86.56", 24, "192.168.86.0/24", HasGateway: true);
        Assert.True(lan.IsScannableSubnet);
        Assert.Contains("254", lan.Hint);
        Assert.DoesNotContain("VPN", lan.Hint);
    }

    [Fact]
    public void IsOnLocalSubnet_TrueOnlyForOnLinkTargets()
    {
        // Emulates the VPN case: local NICs are 10.69/172.25/172.30, scanning 192.168.0.x over the tunnel.
        var locals = new List<LocalSubnet>
        {
            new("Ethernet", "10.69.22.15", 24, "10.69.22.0/24", HasGateway: true),
            new("vEthernet", "172.25.32.1", 20, "172.25.32.0/20", HasGateway: false),
        };

        Assert.True(LocalNetworks.IsOnLocalSubnet(IPAddress.Parse("10.69.22.200"), locals));  // on-link
        Assert.True(LocalNetworks.IsOnLocalSubnet(IPAddress.Parse("172.25.40.5"), locals));   // on-link (/20)
        Assert.False(LocalNetworks.IsOnLocalSubnet(IPAddress.Parse("192.168.0.25"), locals)); // routed over VPN
        Assert.False(LocalNetworks.IsOnLocalSubnet(IPAddress.Parse("10.69.23.1"), locals));   // just outside the /24
    }

    [Fact]
    public void Enumerate_ReturnsWellFormedParseableSubnets()
    {
        // Runs against the real NICs of the test host; assert shape rather than specific values.
        foreach (var net in LocalNetworks.Enumerate())
        {
            Assert.Contains('/', net.Cidr);
            Assert.True(net.PrefixLength is > 0 and <= 32);
            Assert.True(net.HostCount > 0);
            // Every suggested CIDR must be a valid target.
            Assert.NotEmpty(TargetParser.Parse(new[] { net.Cidr },
                new TargetExpansionOptions { AllowLargeExpansion = true }).Take(1).ToList());
        }
    }
}
