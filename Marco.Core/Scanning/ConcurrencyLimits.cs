using System.Globalization;

namespace Marco.Core.Scanning;

/// <summary>
/// The largest scan concurrency this machine can safely sustain, derived from local resources rather than
/// measured. Two limits are combined and the smaller wins:
/// <list type="bullet">
/// <item><b>Sockets (hard).</b> <c>LivenessProbe</c> opens every probe port for a host at once — 11 TCP
/// connects per host with classification on (the default: 6 liveness + 3 printer + 2 network) — so N hosts in
/// flight means up to 11·N ephemeral TCP ports held simultaneously. Windows' default dynamic range is 16,384
/// ports (49152–65535); at N=1000 that is ~11k half-open sockets and the sweep fails with WSAENOBUFS. Only
/// half the range is budgeted for in-flight probes; the rest is left for TIME_WAIT on successful connects,
/// inventory's DCOM/SSH sockets, other Marco windows, and everything else on the box.</item>
/// <item><b>Thread pool (soft).</b> Dead hosts cost no pool thread (ping and connect are overlapped I/O), but
/// an alive host blocks one during enrichment — reverse DNS runs <c>getnameinfo</c> on a pool thread and
/// <c>SendARP</c> runs in <c>Task.Run</c> — and every WMI/SSH inventory call blocks one outright. The pool
/// starts at <see cref="Environment.ProcessorCount"/> workers and injects only ~1–2/s when starved, so past a
/// point more concurrency starves the continuations and makes the scan slower. 16× per logical processor keeps
/// the worst case reachable within tens of seconds and never caps a 2-core box below the historical default
/// of 32. (<c>ThreadPool.GetMaxThreads</c> is 32,767 and not a useful bound.)</item>
/// </list>
/// The probe count is a constant worst case rather than read from live settings so the displayed cap doesn't
/// move when "Classify device type" is toggled. Inputs are explicit on <see cref="Compute"/> so tests can pin
/// the arithmetic; <see cref="Max"/> is the machine-filled value the app uses.
/// </summary>
public static class ConcurrencyLimits
{
    /// <summary>Windows default dynamic (ephemeral) TCP port range: 49152–65535.</summary>
    public const int DefaultEphemeralPortRange = 16384;

    /// <summary>TCP connects opened concurrently per host with the default port lists and classification on
    /// (see <see cref="ScanSettings.AllProbePorts"/>).</summary>
    public const int WorstCaseTcpProbesPerHost = 11;

    /// <summary>Fraction of the ephemeral range we allow in-flight probes to occupy.</summary>
    public const double EphemeralPortBudget = 0.5;

    /// <summary>Blocking-work allowance per logical processor.</summary>
    public const int ThreadsPerProcessor = 16;

    /// <summary>Sanity ceiling on MaxDegreeOfParallelism, whatever the inputs say.</summary>
    public const int AbsoluteCeiling = 1024;

    /// <summary>The cap for this machine.</summary>
    public static int Max { get; } = Compute(Environment.ProcessorCount);

    /// <summary>One or two sentences saying what <see cref="Max"/> is and why — for the UI tooltip.</summary>
    public static string Explanation { get; } = Explain(Environment.ProcessorCount);

    public static int SocketCap(int ephemeralPortRange, int tcpProbesPerHost)
        => (int)Math.Floor(Math.Max(0, ephemeralPortRange) * EphemeralPortBudget / Math.Max(1, tcpProbesPerHost));

    public static int ThreadCap(int processorCount)
        => Math.Max(1, processorCount) * ThreadsPerProcessor;

    public static int Compute(
        int processorCount,
        int ephemeralPortRange = DefaultEphemeralPortRange,
        int tcpProbesPerHost = WorstCaseTcpProbesPerHost)
        => Math.Clamp(Math.Min(SocketCap(ephemeralPortRange, tcpProbesPerHost), ThreadCap(processorCount)), 1, AbsoluteCeiling);

    public static string Explain(
        int processorCount,
        int ephemeralPortRange = DefaultEphemeralPortRange,
        int tcpProbesPerHost = WorstCaseTcpProbesPerHost)
    {
        int socketCap = SocketCap(ephemeralPortRange, tcpProbesPerHost);
        int threadCap = ThreadCap(processorCount);
        int max = Compute(processorCount, ephemeralPortRange, tcpProbesPerHost);
        var c = CultureInfo.CurrentCulture;

        if (threadCap <= socketCap)
        {
            return $"Capped at {max.ToString("N0", c)} on this PC: {Math.Max(1, processorCount).ToString("N0", c)} logical "
                 + $"processor(s) × {ThreadsPerProcessor} (name and MAC lookups each hold a thread-pool thread while a "
                 + $"host is enriched). The {ephemeralPortRange.ToString("N0", c)}-port ephemeral TCP range alone would "
                 + $"allow {socketCap.ToString("N0", c)} ({tcpProbesPerHost} probes per host).";
        }

        return $"Capped at {max.ToString("N0", c)} on this PC: {ephemeralPortRange.ToString("N0", c)} ephemeral TCP "
             + $"ports, spending at most half on the {tcpProbesPerHost} simultaneous probes each host opens.";
    }
}
