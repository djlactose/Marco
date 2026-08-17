using Marco.Core.Model;
using Xunit;

namespace Marco.Tests;

public class MachineTests
{
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
