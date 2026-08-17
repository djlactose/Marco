namespace Marco.Core.Scanning;

/// <summary>
/// Composes the one-line status shown during a run from the latest progress snapshot plus the operator's
/// pause/cancel intent. Progress reports keep arriving while in-flight hosts drain after a Pause or Cancel, so the
/// text has to be a function of state (not a message written once and then overwritten by the next report).
/// </summary>
public static class ScanStatusText
{
    /// <summary>Returns the status line, or an empty string for phases that have no in-run text (Idle,
    /// Complete, Cancelled — the caller writes its own summary for those).</summary>
    public static string Compose(ScanProgress p, bool paused, bool cancelling)
    {
        var total = p.Total?.ToString("N0") ?? "?";
        int n = Math.Max(0, p.InFlight);

        switch (p.Phase)
        {
            case ScanPhase.Discovery:
                if (cancelling)
                    return n > 0 ? $"Cancelling… finishing {n:N0} in-flight probe{Plural(n)}." : "Cancelling…";
                if (paused)
                    return $"Paused at {p.Completed:N0}/{total} · {p.Alive:N0} alive"
                        + (n > 0 ? $" ({n:N0} in flight finishing)" : "");
                return $"Scanning… {p.Completed:N0}/{total}  ·  {p.Alive:N0} alive";

            case ScanPhase.Inventory:
                if (cancelling)
                    return n > 0 ? $"Cancelling inventory… finishing {n:N0} host{Plural(n)}." : "Cancelling inventory…";
                if (paused)
                    return $"Inventory paused at {p.Completed:N0}/{total}"
                        + (n > 0 ? $" ({n:N0} host{Plural(n)} finishing)" : "");
                return $"Inventorying… {p.Completed:N0}/{total}";

            default:
                return "";
        }
    }

    private static string Plural(int n) => n == 1 ? "" : "s";
}
