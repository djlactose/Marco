using Marco.Core.Model;

namespace Marco.Core.Scanning;

/// <summary>MAC OUI category — the classification-relevant grouping of a vendor prefix.</summary>
public enum OuiCategory
{
    None = 0,
    Printer,
    Network,
    Pc,
    Nic,
    Hypervisor,
}

public sealed record OuiEntry(string Vendor, OuiCategory Category);

/// <summary>Result of a liveness probe against one address.</summary>
public sealed record LivenessResult(
    bool IsAlive,
    DiscoveryMethod Method,
    int? IcmpTtl,
    IReadOnlyCollection<int> OpenPorts)
{
    public static readonly LivenessResult Dead =
        new(false, DiscoveryMethod.None, null, Array.Empty<int>());
}

/// <summary>Result of name resolution — any field may be null when a source didn't answer. <see cref="NbnsResponded"/>
/// is true only when the NBNS node-status query actually answered; a DNS-derived name must not set it, since NBNS
/// is a Windows/NetBIOS signal the classifier keys on and DNS is not.</summary>
public sealed record NameResult(string? Name, string? Fqdn, string? Domain, string? Mac, bool NbnsResponded = false)
{
    public static readonly NameResult Empty = new(null, null, null, null);
}

/// <summary>ICMP + optional TCP-connect liveness, plus classification-port probing.</summary>
public interface ILivenessProbe
{
    Task<LivenessResult> ProbeAsync(string address, ScanSettings settings, CancellationToken ct);
}

/// <summary>Reverse DNS with NBNS fallback.</summary>
public interface INameResolver
{
    Task<NameResult> ResolveAsync(string address, CancellationToken ct);
}

/// <summary>ARP-based MAC lookup for on-subnet IPv4 hosts.</summary>
public interface IMacResolver
{
    Task<string?> GetMacAsync(string address, CancellationToken ct);
}

/// <summary>Bundled OUI prefix → vendor/category table.</summary>
public interface IOuiLookup
{
    OuiEntry? Lookup(string? mac);
}

/// <summary>Pure classification from discovery signals. No I/O — fully unit-testable.</summary>
public interface IDeviceClassifier
{
    ClassificationResult Classify(
        IReadOnlyCollection<int> openPorts,
        OuiCategory ouiCategory,
        bool nbnsResponded,
        int? icmpTtl);
}

public readonly record struct ClassificationResult(DeviceType DeviceType, bool IsVirtual);
