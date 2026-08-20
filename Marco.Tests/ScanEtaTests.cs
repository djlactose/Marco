using System.Globalization;
using Marco.Core.Scanning;
using Xunit;

namespace Marco.Tests;

public class ScanEtaTests
{
    // --- Estimate ---

    [Fact]
    public void Estimate_NothingCompleted_IsNull()
        => Assert.Null(ScanEta.Estimate(0, 100, TimeSpan.FromSeconds(10)));

    [Fact]
    public void Estimate_UnknownTotal_IsNull()
        => Assert.Null(ScanEta.Estimate(5, null, TimeSpan.FromSeconds(10)));

    [Fact]
    public void Estimate_ZeroTotal_IsNull()
        => Assert.Null(ScanEta.Estimate(5, 0, TimeSpan.FromSeconds(10)));

    [Fact]
    public void Estimate_LinearExtrapolation()
        // 2 done of 10 in 20s active → 10s per item → 8 remaining → 80s.
        => Assert.Equal(TimeSpan.FromSeconds(80), ScanEta.Estimate(2, 10, TimeSpan.FromSeconds(20)));

    [Fact]
    public void Estimate_AllDone_IsZero()
        => Assert.Equal(TimeSpan.Zero, ScanEta.Estimate(10, 10, TimeSpan.FromSeconds(20)));

    [Fact]
    public void Estimate_CompletedBeyondTotal_ClampsToZero()
        => Assert.Equal(TimeSpan.Zero, ScanEta.Estimate(12, 10, TimeSpan.FromSeconds(20)));

    [Fact]
    public void Estimate_NegativeActiveElapsed_TreatedAsZero()
        // Defensive: clock skew between stopwatch and paused-time bookkeeping must not yield a negative ETA.
        => Assert.Equal(TimeSpan.Zero, ScanEta.Estimate(2, 10, TimeSpan.FromSeconds(-5)));

    // --- Compose ---

    private static readonly CultureInfo EnUs = new("en-US");

    [Fact]
    public void Compose_MinuteOrMore_AppendsClockTime()
        => Assert.Equal("ETA 02:14 · ~3:42 PM",
            ScanEta.Compose(new TimeSpan(0, 2, 14), new DateTime(2026, 8, 20, 15, 40, 0), EnUs));

    [Fact]
    public void Compose_SubMinute_NoClockTime()
        => Assert.Equal("ETA 00:42",
            ScanEta.Compose(TimeSpan.FromSeconds(42), new DateTime(2026, 8, 20, 15, 40, 0), EnUs));

    [Fact]
    public void Compose_OverAnHour_UsesHourFormat()
        => Assert.Equal("ETA 1:05:00 · ~4:45 PM",
            ScanEta.Compose(new TimeSpan(1, 5, 0), new DateTime(2026, 8, 20, 15, 40, 0), EnUs));

    [Fact]
    public void Compose_Negative_ClampsToZero()
        => Assert.Equal("ETA 00:00",
            ScanEta.Compose(TimeSpan.FromSeconds(-3), new DateTime(2026, 8, 20, 15, 40, 0), EnUs));

    // --- FormatDuration ---

    [Theory]
    [InlineData(0, 0, 42, "00:42")]
    [InlineData(0, 12, 3, "12:03")]
    [InlineData(2, 3, 4, "2:03:04")]
    [InlineData(26, 0, 1, "26:00:01")] // >24h keeps counting hours (no day wrap)
    public void FormatDuration_Cases(int h, int m, int s, string expected)
        => Assert.Equal(expected, ScanEta.FormatDuration(new TimeSpan(0, h, m, s)));
}
