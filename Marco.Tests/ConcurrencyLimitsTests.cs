using Marco.Core.Scanning;
using Xunit;

namespace Marco.Tests;

public class ConcurrencyLimitsTests
{
    [Fact]
    public void Compute_SmallMachine_IsThreadBound()
    {
        // 4 cores × 16 = 64, well under the 744 the default port range allows.
        Assert.Equal(64, ConcurrencyLimits.Compute(4));
        Assert.Equal(32, ConcurrencyLimits.Compute(2)); // never below the historical default on a 2-core box
    }

    [Fact]
    public void Compute_BigMachine_IsSocketBound()
    {
        // 64 cores × 16 = 1024 would exceed floor(16384 × 0.5 / 11) = 744.
        Assert.Equal(744, ConcurrencyLimits.Compute(64));
        Assert.Equal(744, ConcurrencyLimits.SocketCap(ConcurrencyLimits.DefaultEphemeralPortRange, ConcurrencyLimits.WorstCaseTcpProbesPerHost));
    }

    [Fact]
    public void Compute_NeverBelowOne()
    {
        Assert.True(ConcurrencyLimits.Compute(0) >= 1);
        Assert.Equal(1, ConcurrencyLimits.Compute(1, ephemeralPortRange: 1, tcpProbesPerHost: 100));
        Assert.Equal(1, ConcurrencyLimits.Compute(-5, ephemeralPortRange: -1, tcpProbesPerHost: 0));
    }

    [Fact]
    public void Compute_HonoursCeiling()
    {
        Assert.Equal(ConcurrencyLimits.AbsoluteCeiling, ConcurrencyLimits.Compute(1000, 65535, 1));
    }

    [Fact]
    public void Max_MatchesComputeForThisMachine_AndIsInRange()
    {
        Assert.Equal(ConcurrencyLimits.Compute(Environment.ProcessorCount), ConcurrencyLimits.Max);
        Assert.InRange(ConcurrencyLimits.Max, 1, ConcurrencyLimits.AbsoluteCeiling);
    }

    [Fact]
    public void WorstCaseTcpProbesPerHost_TracksDefaultProbePorts()
    {
        // The socket cap assumes every default port is opened at once; keep the constant honest if the lists change.
        Assert.Equal(ConcurrencyLimits.WorstCaseTcpProbesPerHost, new ScanSettings().AllProbePorts().Count());
    }

    [Fact]
    public void Explain_MentionsTheCap()
    {
        Assert.Contains("64", ConcurrencyLimits.Explain(4));
        Assert.Contains("744", ConcurrencyLimits.Explain(64));
        Assert.Contains(ConcurrencyLimits.Max.ToString("N0"), ConcurrencyLimits.Explanation);
    }

    [Theory]
    [InlineData(50, 4, 4)]
    [InlineData(3, 4, 3)]
    [InlineData(0, 4, 1)]
    [InlineData(-7, 4, 1)]
    [InlineData(8, 0, 1)]   // a nonsense max still yields a usable degree of parallelism
    public void ScanSettings_EffectiveConcurrency_ClampsToMax(int requested, int max, int expected)
    {
        var s = new ScanSettings { DiscoveryConcurrency = requested, InventoryConcurrency = requested, MaxConcurrency = max };
        Assert.Equal(expected, s.EffectiveDiscoveryConcurrency);
        Assert.Equal(expected, s.EffectiveInventoryConcurrency);
    }

    [Fact]
    public void ScanSettings_MaxConcurrency_DefaultsToThisMachine()
    {
        Assert.Equal(ConcurrencyLimits.Max, new ScanSettings().MaxConcurrency);
    }
}
