namespace Marco.Core.Scanning;

public enum ScanPhase { Idle, Discovery, Inventory, Complete, Cancelled }

/// <summary>Immutable snapshot of scan progress, reported to the UI.</summary>
public sealed record ScanProgress(
    ScanPhase Phase,
    int Completed,
    int? Total,
    int Alive,
    int Unreachable,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining)
{
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Completed / Total.Value, 0, 1) : null;
}
