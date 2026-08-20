using System.Net;
using Marco.Discovery.Wol;
using Xunit;

namespace Marco.Tests;

public class WolPacketTests
{
    [Theory]
    [InlineData("3C:52:82:AA:BB:CC")]
    [InlineData("3c-52-82-aa-bb-cc")]
    [InlineData("3C5282AABBCC")]
    [InlineData("3c:52:82:aa:bb:cc")]
    public void TryParseMac_AcceptsCommonFormats(string mac)
    {
        var bytes = WolPacket.TryParseMac(mac);
        Assert.NotNull(bytes);
        Assert.Equal(new byte[] { 0x3C, 0x52, 0x82, 0xAA, 0xBB, 0xCC }, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a mac")]
    [InlineData("3C:52:82:AA:BB")]      // 5 bytes
    [InlineData("3C:52:82:AA:BB:CC:DD")] // 7 bytes
    public void TryParseMac_RejectsInvalid(string? mac) => Assert.Null(WolPacket.TryParseMac(mac));

    [Fact]
    public void Build_HasCorrectLayout()
    {
        var mac = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        var packet = WolPacket.Build(mac);

        Assert.Equal(102, packet.Length);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, packet[..6]);
        for (int rep = 0; rep < 16; rep++)
            Assert.Equal(mac, packet[(6 + rep * 6)..(6 + rep * 6 + 6)]);
    }

    [Theory]
    [InlineData("10.1.2.0/24", "10.1.2.255")]
    [InlineData("192.168.0.0/16", "192.168.255.255")]
    [InlineData("10.0.0.0/8", "10.255.255.255")]
    [InlineData("10.1.2.128/25", "10.1.2.255")]
    public void DirectedBroadcast_Computed(string cidr, string expected)
        => Assert.Equal(IPAddress.Parse(expected), WolPacket.DirectedBroadcastFor(cidr));

    [Theory]
    [InlineData("10.1.2.5/31")]  // no broadcast address
    [InlineData("10.1.2.5/32")]
    [InlineData("Individual hosts")] // not a CIDR
    [InlineData("not-a-block")]
    [InlineData(null)]
    public void DirectedBroadcast_NullForUnusable(string? cidr)
        => Assert.Null(WolPacket.DirectedBroadcastFor(cidr));
}
