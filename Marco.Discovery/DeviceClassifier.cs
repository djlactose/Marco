using Marco.Core.Model;
using Marco.Core.Scanning;

namespace Marco.Discovery;

/// <summary>
/// Pure device classification from discovery signals. No I/O, so it is fully unit-testable. Precedence is
/// fixed and documented inline; the ICMP-TTL hint only breaks ties among otherwise-Unknown hosts and never
/// overrides a port/OUI signal. Hypervisor OUI sets <c>IsVirtual</c> orthogonally rather than choosing a type.
/// </summary>
public sealed class DeviceClassifier : IDeviceClassifier
{
    // Well-known ports the probe may report (must be a subset of ScanSettings.AllProbePorts()).
    private const int Rpc = 135, Smb = 445, Rdp = 3389, Ssh = 22;
    private const int Telnet = 23, Snmp = 161;
    private static readonly int[] PrinterPorts = { 9100, 515, 631 };

    public ClassificationResult Classify(
        IReadOnlyCollection<int> openPorts,
        OuiCategory ouiCategory,
        bool nbnsResponded,
        int? icmpTtl)
    {
        bool isVirtual = ouiCategory == OuiCategory.Hypervisor;
        var type = Determine(openPorts, ouiCategory, nbnsResponded, icmpTtl);
        return new ClassificationResult(type, isVirtual);
    }

    private static DeviceType Determine(
        IReadOnlyCollection<int> ports, OuiCategory oui, bool nbns, int? ttl)
    {
        bool Has(int p) => ports.Contains(p);
        bool HasAny(int[] ps) => ps.Any(ports.Contains);

        // 1. Printer — OUI vendor or a print-service port.
        if (oui == OuiCategory.Printer || HasAny(PrinterPorts))
            return DeviceType.Printer;

        // 2. Network gear — OUI vendor, or a management port with no SMB.
        if (oui == OuiCategory.Network || ((Has(Telnet) || Has(Snmp)) && !Has(Smb)))
            return DeviceType.NetworkDevice;

        // 3. Windows — SMB+RPC, or NBNS answered, or RDP. (PC vs Server resolved later by inventory.)
        if ((Has(Smb) && Has(Rpc)) || nbns || Has(Rdp))
            return DeviceType.Windows;

        // 4. Unix/Linux — SSH without SMB.
        if (Has(Ssh) && !Has(Smb))
            return DeviceType.UnixLinux;

        // 5. Weak TTL tiebreaker for otherwise-unknown hosts (spoofable — a hint, not a verdict). Only the
        //    64/128 buckets are trustworthy: TTL 255 is used by many embedded IoT stacks, not just infrastructure,
        //    so it stays Unknown here — real network gear is caught by OUI and the telnet/SNMP rule above.
        if (ttl is { } t and > 0 and <= 128)
            return t <= 64 ? DeviceType.UnixLinux : DeviceType.Windows;

        // 6. Nothing conclusive.
        return DeviceType.Unknown;
    }
}
