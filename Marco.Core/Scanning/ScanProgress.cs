namespace Marco.Core.Scanning;

public enum ScanPhase { Idle, Discovery, Inventory, Complete, Cancelled }

/// <summary>Immutable snapshot of scan progress, reported to the UI. <paramref name="InFlight"/> is the number of
/// hosts currently being worked on — what still has to drain after a pause or cancel before the counts settle.</summary>
public sealed record ScanProgress(
    ScanPhase Phase,
    int Completed,
    int? Total,
    int Alive,
    int Unreachable,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining,
    int InFlight = 0)
{
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Completed / Total.Value, 0, 1) : null;
}
