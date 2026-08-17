using Marco.Core.Scanning;
using Xunit;

namespace Marco.Tests;

public class ScanStatusTextTests
{
    private static ScanProgress P(ScanPhase phase, int completed, int? total, int alive = 0, int inFlight = 0)
        => new(phase, completed, total, alive, 0, TimeSpan.FromSeconds(1), null, inFlight);

    [Fact]
    public void Discovery_Scanning()
        => Assert.Equal("Scanning… 120/508  ·  40 alive",
            ScanStatusText.Compose(P(ScanPhase.Discovery, 120, 508, alive: 40, inFlight: 32), paused: false, cancelling: false));

    [Fact]
    public void Discovery_Paused_WithInFlight()
        => Assert.Equal("Paused at 120/508 · 40 alive (7 in flight finishing)",
            ScanStatusText.Compose(P(ScanPhase.Discovery, 120, 508, alive: 40, inFlight: 7), paused: true, cancelling: false));

    [Fact]
    public void Discovery_Paused_Settled()
        => Assert.Equal("Paused at 120/508 · 40 alive",
            ScanStatusText.Compose(P(ScanPhase.Discovery, 120, 508, alive: 40), paused: true, cancelling: false));

    [Fact]
    public void Discovery_Cancelling_WithInFlight_UsesPlural()
        => Assert.Equal("Cancelling… finishing 3 in-flight probes.",
            ScanStatusText.Compose(P(ScanPhase.Discovery, 120, 508, inFlight: 3), paused: false, cancelling: true));

    [Fact]
    public void Discovery_Cancelling_OneInFlight_IsSingular()
        => Assert.Equal("Cancelling… finishing 1 in-flight probe.",
            ScanStatusText.Compose(P(ScanPhase.Discovery, 120, 508, inFlight: 1), paused: false, cancelling: true));

    [Fact]
    public void Discovery_Cancelling_Settled()
        => Assert.Equal("Cancelling…",
            ScanStatusText.Compose(P(ScanPhase.Discovery, 120, 508), paused: false, cancelling: true));

    [Fact]
    public void Cancelling_WinsOverPaused()
        => Assert.StartsWith("Cancelling",
            ScanStatusText.Compose(P(ScanPhase.Discovery, 1, 2, inFlight: 1), paused: true, cancelling: true));

    [Fact]
    public void Inventory_Variants()
    {
        Assert.Equal("Inventorying… 3/10",
            ScanStatusText.Compose(P(ScanPhase.Inventory, 3, 10, inFlight: 4), false, false));
        Assert.Equal("Inventory paused at 3/10 (4 hosts finishing)",
            ScanStatusText.Compose(P(ScanPhase.Inventory, 3, 10, inFlight: 4), true, false));
        Assert.Equal("Inventory paused at 3/10",
            ScanStatusText.Compose(P(ScanPhase.Inventory, 3, 10), true, false));
        Assert.Equal("Cancelling inventory… finishing 1 host.",
            ScanStatusText.Compose(P(ScanPhase.Inventory, 3, 10, inFlight: 1), false, true));
        Assert.Equal("Cancelling inventory…",
            ScanStatusText.Compose(P(ScanPhase.Inventory, 3, 10), false, true));
    }

    [Fact]
    public void UnknownTotal_ShowsQuestionMark()
        => Assert.Equal("Scanning… 5/?  ·  2 alive",
            ScanStatusText.Compose(P(ScanPhase.Discovery, 5, null, alive: 2), false, false));

    [Theory]
    [InlineData(ScanPhase.Idle)]
    [InlineData(ScanPhase.Complete)]
    [InlineData(ScanPhase.Cancelled)]
    public void TerminalPhases_ProduceNoText(ScanPhase phase)
        => Assert.Equal("", ScanStatusText.Compose(P(phase, 5, 5), paused: true, cancelling: true));
}
