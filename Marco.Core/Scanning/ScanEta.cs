using System.Globalization;

namespace Marco.Core.Scanning;

/// <summary>
/// Linear-extrapolation ETA shared by discovery and inventory, plus its status-bar text. Pure functions so
/// both the math and the formatting are unit-testable.
/// </summary>
public static class ScanEta
{
    /// <summary>Per-completed-item extrapolation over active (non-paused) elapsed time. Null when nothing has
    /// completed yet or the total is unknown. Linear by design: early fast-failers skew it low at first and it
    /// converges as the run progresses.</summary>
    public static TimeSpan? Estimate(int completed, int? total, TimeSpan activeElapsed)
    {
        if (completed <= 0 || total is not > 0) return null;
        double perItem = Math.Max(0, activeElapsed.TotalSeconds) / completed;
        int remaining = Math.Max(0, total.Value - completed);
        return TimeSpan.FromSeconds(perItem * remaining);
    }

    /// <summary>"ETA 02:14 · ~3:42 PM". The clock projection is appended only when at least a minute remains —
    /// a "done by" time for a 20-second tail is noise. Uses the locale's short-time pattern.</summary>
    public static string Compose(TimeSpan remaining, DateTime now, IFormatProvider? provider = null)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var text = $"ETA {FormatDuration(remaining)}";
        if (remaining >= TimeSpan.FromMinutes(1))
            text += $" · ~{(now + remaining).ToString("t", provider ?? CultureInfo.CurrentCulture)}";
        return text;
    }

    /// <summary>h:mm:ss over an hour, mm:ss under (moved verbatim from MainViewModel so ElapsedText keeps
    /// its exact format, including runs past 24 hours).</summary>
    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
}
