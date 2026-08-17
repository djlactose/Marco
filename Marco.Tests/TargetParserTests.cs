using Marco.Core.Targets;
using Xunit;

namespace Marco.Tests;

public class TargetParserTests
{
    private static List<string> Addrs(string input, TargetExpansionOptions? opts = null)
        => TargetParser.Parse(new[] { input }, opts).Select(t => t.Address).ToList();

    [Fact]
    public void Cidr24_ExcludesNetworkAndBroadcast_ByDefault()
    {
        var result = Addrs("10.20.30.0/24");
        Assert.Equal(254, result.Count);
        Assert.Equal("10.20.30.1", result[0]);
        Assert.Equal("10.20.30.254", result[^1]);
        Assert.DoesNotContain("10.20.30.0", result);
        Assert.DoesNotContain("10.20.30.255", result);
    }

    [Fact]
    public void Cidr24_IncludesNetworkAndBroadcast_WhenRequested()
    {
        var result = Addrs("10.20.30.0/24", new TargetExpansionOptions { IncludeNetworkAndBroadcast = true });
        Assert.Equal(256, result.Count);
        Assert.Equal("10.20.30.0", result[0]);
        Assert.Equal("10.20.30.255", result[^1]);
    }

    [Fact]
    public void Cidr32_IsSingleHost()
    {
        var result = Addrs("192.168.1.5/32");
        Assert.Single(result);
        Assert.Equal("192.168.1.5", result[0]);
    }

    [Fact]
    public void Cidr31_YieldsBothHosts_NoStripping()
    {
        var result = Addrs("192.168.1.4/31");
        Assert.Equal(new[] { "192.168.1.4", "192.168.1.5" }, result);
    }

    [Fact]
    public void Cidr30_ExcludesNetworkAndBroadcast()
    {
        var result = Addrs("192.168.1.4/30");
        Assert.Equal(new[] { "192.168.1.5", "192.168.1.6" }, result);
    }

    [Fact]
    public void Cidr_NormalizesNonNetworkBaseAddress()
    {
        // Host bits set on the base should normalize to the network.
        var result = Addrs("10.0.0.37/24", new TargetExpansionOptions { IncludeNetworkAndBroadcast = true });
        Assert.Equal("10.0.0.0", result[0]);
        Assert.Equal("10.0.0.255", result[^1]);
    }

    [Fact]
    public void DashedRange_Full_IsInclusive()
    {
        var result = Addrs("10.20.30.1-10.20.30.5");
        Assert.Equal(new[] { "10.20.30.1", "10.20.30.2", "10.20.30.3", "10.20.30.4", "10.20.30.5" }, result);
    }

    [Fact]
    public void DashedRange_Shorthand_LastOctet()
    {
        var result = Addrs("10.20.30.10-12");
        Assert.Equal(new[] { "10.20.30.10", "10.20.30.11", "10.20.30.12" }, result);
    }

    [Fact]
    public void DashedRange_CrossesOctetBoundary()
    {
        var result = Addrs("10.0.0.254-10.0.1.1");
        Assert.Equal(new[] { "10.0.0.254", "10.0.0.255", "10.0.1.0", "10.0.1.1" }, result);
    }

    [Fact]
    public void DashedRange_EndBeforeStart_Throws()
    {
        Assert.Throws<TargetParseException>(() => Addrs("10.0.0.10-10.0.0.5"));
    }

    [Fact]
    public void SingleIp_PassesThrough()
    {
        var result = TargetParser.Parse(new[] { "172.16.0.9" }).Single();
        Assert.Equal("172.16.0.9", result.Address);
        Assert.False(result.IsHostname);
    }

    [Fact]
    public void Hostname_IsFlaggedAsHostname_EvenWithHyphen()
    {
        var result = TargetParser.Parse(new[] { "web-server-01" }).Single();
        Assert.Equal("web-server-01", result.Address);
        Assert.True(result.IsHostname);
    }

    [Fact]
    public void HostFile_SkipsCommentsAndBlanks_AndInlineComments()
    {
        var input = "# header comment\n\n10.0.0.1\n10.0.0.2  # gateway\n   \n#10.0.0.99\n10.0.0.3";
        var result = Addrs(input);
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3" }, result);
    }

    [Fact]
    public void MultipleTokensPerLine_AreSplit()
    {
        var result = Addrs("10.0.0.1, 10.0.0.2 10.0.0.3;10.0.0.4");
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4" }, result);
    }

    [Fact]
    public void Deduplicates_AcrossSources()
    {
        var result = Addrs("10.0.0.1\n10.0.0.1\n10.0.0.0/30");
        // 10.0.0.1 once, plus .1 and .2 from the /30 (but .1 already seen) => .1, .2
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, result);
    }

    [Fact]
    public void OverThreshold_Throws_WithoutOverride()
    {
        var ex = Assert.Throws<TargetTooLargeException>(() => Addrs("10.0.0.0/8"));
        Assert.True(ex.RequestedCount > 65536);
    }

    [Fact]
    public void OverThreshold_Allowed_WithOverride()
    {
        var count = TargetParser.EstimateCount(new[] { "10.0.0.0/16" });
        Assert.Equal(65534, count); // /16 minus network+broadcast
        // Should not throw when override is set.
        var seq = TargetParser.Parse(new[] { "10.0.0.0/16" },
            new TargetExpansionOptions { AllowLargeExpansion = true });
        Assert.Equal("10.0.0.1", seq.First().Address);
    }

    [Fact]
    public void InvalidCidrPrefix_Throws()
    {
        Assert.Throws<TargetParseException>(() => Addrs("10.0.0.0/33"));
    }

    [Fact]
    public void InvalidOctet_Throws()
    {
        Assert.Throws<TargetParseException>(() => Addrs("10.0.0.300/24"));
    }

    [Fact]
    public void EstimateCount_MatchesActualExpansion_ForModestRanges()
    {
        var input = new[] { "10.0.0.0/24\n10.0.1.5-10.0.1.9\n192.168.0.1" };
        long est = TargetParser.EstimateCount(input);
        int actual = TargetParser.Parse(input).Count();
        Assert.Equal(254 + 5 + 1, est);
        Assert.Equal(est, actual);
    }

    [Fact]
    public void ExpandedTargets_CarryTheirBlock()
    {
        var targets = TargetParser.Parse(new[] { "192.168.1.0/30\n192.168.0.0/24\n10.0.0.5-6\n172.16.0.9\nserver01" }).ToList();

        Assert.All(targets.Where(t => t.Address.StartsWith("192.168.1.")), t => Assert.Equal("192.168.1.0/30", t.Block));
        Assert.All(targets.Where(t => t.Address.StartsWith("192.168.0.")), t => Assert.Equal("192.168.0.0/24", t.Block));
        Assert.All(targets.Where(t => t.Address.StartsWith("10.0.0.")), t => Assert.Equal("10.0.0.5-6", t.Block));
        Assert.Equal(TargetParser.IndividualHostsBlock, targets.Single(t => t.Address == "172.16.0.9").Block);
        Assert.Equal(TargetParser.IndividualHostsBlock, targets.Single(t => t.Address == "server01").Block);
    }

    [Theory]
    [InlineData("192.168.0.77", "192.168.0.0/24")]
    [InlineData("192.168.1.2", "192.168.1.0/30")]
    [InlineData("10.0.0.6", "10.0.0.5-6")]
    [InlineData("172.16.0.9", TargetParser.IndividualHostsBlock)]
    [InlineData("server01", TargetParser.IndividualHostsBlock)]
    [InlineData("8.8.8.8", null)]
    public void FindBlock_LocatesTheCoveringToken(string address, string? expected)
    {
        var tokens = new[] { "192.168.1.0/30", "192.168.0.0/24", "10.0.0.5-6", "172.16.0.9", "server01", "not/a/cidr" };
        Assert.Equal(expected, TargetParser.FindBlock(tokens, address));
    }
}
