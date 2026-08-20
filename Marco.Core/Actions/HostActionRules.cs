using Marco.Core.Model;

namespace Marco.Core.Actions;

/// <summary>The per-host actions Marco can launch. Availability is contextual — driven by what discovery and
/// inventory already learned about the host — so the menu never offers RDP to a box with RDP off.</summary>
public enum HostActionKind
{
    Rdp,
    Ssh,
    WebAdminHttp,
    WebAdminHttps,
    AdminShare,
    ComputerManagement,
    Ping,
    Traceroute,
    CopyIp,
    CopyHostname,
    CopyMac,
    CopySerial,
    Wake,
}

/// <summary>
/// Decides which actions apply to a given machine. Pure — the actual launching (Process.Start, clipboard) lives
/// in the app layer. Every rule keys off evidence already collected: observed-open ports, device type, the RDP
/// flag from the security collector, a known MAC, alive state.
/// </summary>
public static class HostActionRules
{
    public static IReadOnlyList<HostActionKind> AvailableFor(Machine m)
    {
        var actions = new List<HostActionKind>();
        bool windows = m.DeviceType is DeviceType.Windows or DeviceType.WindowsServer;
        bool linux = m.DeviceType == DeviceType.UnixLinux;
        bool netDevice = m.DeviceType is DeviceType.Printer or DeviceType.NetworkDevice;

        if (m.OpenPorts.Contains(3389) || m.Security.RdpEnabled == true) actions.Add(HostActionKind.Rdp);
        if (linux || m.OpenPorts.Contains(22)) actions.Add(HostActionKind.Ssh);

        // Web admin: printers/network gear (and anything else) with an HTTP(S) port answering.
        if (m.OpenPorts.Contains(443)) actions.Add(HostActionKind.WebAdminHttps);
        if (m.OpenPorts.Contains(80) && (netDevice || !m.OpenPorts.Contains(443))) actions.Add(HostActionKind.WebAdminHttp);

        if (windows || (m.DeviceType == DeviceType.Unknown && m.OpenPorts.Contains(445)))
        {
            actions.Add(HostActionKind.AdminShare);
            actions.Add(HostActionKind.ComputerManagement);
        }

        actions.Add(HostActionKind.Ping);
        actions.Add(HostActionKind.Traceroute);
        actions.Add(HostActionKind.CopyIp);
        if (!string.IsNullOrWhiteSpace(m.Name)) actions.Add(HostActionKind.CopyHostname);
        if (m.PrimaryMac is not null) actions.Add(HostActionKind.CopyMac);
        if (!string.IsNullOrWhiteSpace(m.System.SerialNumber)) actions.Add(HostActionKind.CopySerial);

        if (m.PrimaryMac is not null && !m.IsAlive) actions.Add(HostActionKind.Wake);

        return actions;
    }
}
