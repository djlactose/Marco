namespace Marco.Core.Scanning;

/// <summary>Operator-tunable knobs for a scan run. Defaults are LAN-sensible; lower the concurrency for WAN.</summary>
public sealed class ScanSettings
{
    /// <summary>Concurrent hosts during discovery (cheap ping/TCP work). Clamped to
    /// [1, <see cref="MaxConcurrency"/>] at run time — see <see cref="EffectiveDiscoveryConcurrency"/>.</summary>
    public int DiscoveryConcurrency { get; set; } = 32;

    /// <summary>Concurrent hosts during inventory — deliberately lower than discovery, since each host is a
    /// DCOM auth plus dozens of WMI queries plus a bulk registry read and 32-wide can swamp a WAN link. Clamped
    /// to [1, <see cref="MaxConcurrency"/>] at run time — see <see cref="EffectiveInventoryConcurrency"/>.</summary>
    public int InventoryConcurrency { get; set; } = 16;

    /// <summary>Ceiling applied to both concurrency knobs when a run starts. Defaults to what this machine can
    /// safely sustain (<see cref="ConcurrencyLimits.Max"/>); tests lower it to make the clamp observable.</summary>
    public int MaxConcurrency { get; set; } = ConcurrencyLimits.Max;

    /// <summary>What discovery actually runs at: <see cref="DiscoveryConcurrency"/> clamped to [1, max].</summary>
    public int EffectiveDiscoveryConcurrency => Math.Clamp(DiscoveryConcurrency, 1, Math.Max(1, MaxConcurrency));

    /// <summary>What inventory actually runs at: <see cref="InventoryConcurrency"/> clamped to [1, max].</summary>
    public int EffectiveInventoryConcurrency => Math.Clamp(InventoryConcurrency, 1, Math.Max(1, MaxConcurrency));

    public bool IcmpEnabled { get; set; } = true;

    /// <summary>When true, fall back to TCP connect probes if ICMP fails. When false, the scan is ICMP-only
    /// for liveness (classification ports are still probed against already-alive hosts).</summary>
    public bool TcpFallbackEnabled { get; set; } = true;

    /// <summary>Probe classification ports and infer a device type. Off = faster, liveness only.</summary>
    public bool ClassificationEnabled { get; set; } = true;

    public bool ResolveNames { get; set; } = true;
    public bool ResolveMac { get; set; } = true;

    public int IcmpTimeoutMs { get; set; } = 1000;
    public int TcpTimeoutMs { get; set; } = 1000;

    /// <summary>General-purpose liveness ports (any open = host is up).</summary>
    public IReadOnlyList<int> LivenessPorts { get; set; } = new[] { 135, 445, 139, 3389, 22, 80 };

    /// <summary>Printer signal ports (RAW/JetDirect, LPD, IPP).</summary>
    public IReadOnlyList<int> PrinterPorts { get; set; } = new[] { 9100, 515, 631 };

    /// <summary>Network-gear signal ports (telnet, and a cheap TCP-161 SNMP-presence knock).</summary>
    public IReadOnlyList<int> NetworkPorts { get; set; } = new[] { 23, 161 };

    /// <summary>Every port the probe opens = liveness ∪ classification. The classifier may only key on these.</summary>
    public IEnumerable<int> AllProbePorts()
    {
        var set = new HashSet<int>(LivenessPorts);
        if (ClassificationEnabled)
        {
            foreach (var p in PrinterPorts) set.Add(p);
            foreach (var p in NetworkPorts) set.Add(p);
        }
        return set;
    }
}
