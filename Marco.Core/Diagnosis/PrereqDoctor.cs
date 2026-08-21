using Marco.Core.Model;

namespace Marco.Core.Diagnosis;

/// <summary>Named root cause for a host inventory failure. Order matters nowhere; the doctor's rule order does.</summary>
public enum PrereqCause
{
    None = 0,
    NoCredentials,
    FirewallRpcBlocked,
    TokenFilteringLocalAdmin,
    BadCredentials,
    RemoteRegistryUnavailable,
    WmiTimeout,
    SshAuthFailed,
    SshUnreachable,
    NotInventoryable,
    SnmpDisabled,
    SnmpNoResponse,
    Unknown,
}

/// <summary>One host's diagnosis: what blocked inventory, in words, with the copy-paste fix when one exists.
/// <paramref name="Confident"/> is false when the evidence is circumstantial (e.g. ports were never probed),
/// so the UI can say "likely" instead of stating it as fact.</summary>
public sealed record PrereqDiagnosis(PrereqCause Cause, string Title, string Explanation, string? FixScript, bool Confident);

/// <summary>Hosts sharing one diagnosed cause, for the fleet rollup ("38 hosts: firewall blocks WMI").</summary>
public sealed record PrereqCauseGroup(PrereqCause Cause, string Title, string? FixScript, IReadOnlyList<Machine> Machines);

/// <summary>
/// Turns the structured failure evidence the runners record (<see cref="Machine.ConnectFailure"/>, per-collector
/// statuses, observed-open ports) into a named cause with a copy-paste fix. First matching rule wins. Emits text
/// and scripts ONLY — nothing here executes anything against a target.
/// Port evidence is observed-OPEN only: a port absent from <see cref="Machine.OpenPorts"/> may be closed or may
/// simply never have been probed (ICMP-only discovery), which is what the Confident flag communicates.
/// </summary>
public static class PrereqDoctor
{
    public static PrereqDiagnosis Diagnose(Machine m)
    {
        bool snmpDevice = m.DeviceType is DeviceType.Printer or DeviceType.NetworkDevice;

        // Printers/network devices that were never attempted (e.g. a scan from a version without SNMP support).
        if (snmpDevice && m.Collectors.Count == 0 && m.ConnectFailure == ConnectFailure.None)
            return new(PrereqCause.NotInventoryable, "Not inventoried yet",
                "Printers and network devices are read over SNMP (and printers over IPP); run inventory to collect them.",
                null, Confident: true);

        switch (m.ConnectFailure)
        {
            case ConnectFailure.NoCredentials:
                return new(PrereqCause.NoCredentials, "No matching credentials",
                    snmpDevice
                        ? "No SNMP community string applies to this device; add an SNMP credential in the Credentials panel."
                        : m.DeviceType == DeviceType.UnixLinux
                            ? "No Linux/SSH credential set is configured; add one in the Credentials panel."
                            : "No Windows credential set applies to this host; add one in the Credentials panel.",
                    null, Confident: true);

            case ConnectFailure.SnmpDisabled:
                return new(PrereqCause.SnmpDisabled, "SNMP is switched off on the device",
                    "The device answered UDP 161 with 'port unreachable', so no SNMP agent is listening. Enable SNMP "
                    + "v1/v2c (read-only) in the device's web admin page — usually under Network → SNMP — and set a "
                    + "read community. Newer HP FutureSmart firmware ships with v1/v2c disabled by default.",
                    PrereqFixes.SnmpEnable, Confident: true);

            case ConnectFailure.SnmpNoResponse:
                return new(PrereqCause.SnmpNoResponse, "No SNMP response",
                    "Nothing answered over SNMP v1/v2c. A wrong community string gets no reply at all, so this is "
                    + "either the community (add the site's read community as an SNMP credential), SNMP disabled on the "
                    + "device, or UDP 161 filtered between here and the device"
                    + (m.DeviceType == DeviceType.Printer ? " — and IPP on port 631 did not answer either." : "."),
                    PrereqFixes.SnmpEnable, Confident: false);

            case ConnectFailure.Timeout:
                // 135 answered but the session timed out: WAN/VPN latency or an overloaded target, not a block.
                if (m.OpenPorts.Contains(135))
                    return new(PrereqCause.WmiTimeout, "WMI connection timed out",
                        "RPC port 135 is reachable but the DCOM handshake timed out — typical over VPN/WAN links or "
                        + "against an overloaded host. Lower the Concurrency setting and retry; a persistent timeout "
                        + "usually means dynamic RPC ports (49152+) are filtered between here and the target.",
                        PrereqFixes.FirewallWmi, Confident: false);
                goto case ConnectFailure.Unreachable;

            case ConnectFailure.Unreachable:
                if (m.IsAlive)
                {
                    bool portsProbed = m.OpenPorts.Count > 0;
                    return new(PrereqCause.FirewallRpcBlocked,
                        portsProbed && !m.OpenPorts.Contains(135) ? "Firewall blocks WMI (RPC 135)" : "Firewall likely blocks WMI",
                        portsProbed && !m.OpenPorts.Contains(135)
                            ? "The host answers pings but port 135 was observed closed — the Windows firewall (or a "
                              + "network ACL) is blocking the WMI rule group."
                            : "The host is alive but the WMI connection could not be established. The usual cause is "
                              + "the firewall's WMI rule group being disabled; the fix below enables it.",
                        PrereqFixes.FirewallWmi, Confident: portsProbed && !m.OpenPorts.Contains(135));
                }
                return new(PrereqCause.Unknown, "Host unreachable",
                    "The host did not answer when inventory connected — it may have gone offline since discovery.",
                    null, Confident: false);

            case ConnectFailure.AuthFailed:
            case ConnectFailure.AccessDenied:
                if (m.ConnectFailureLocalAccount)
                    return new(PrereqCause.TokenFilteringLocalAdmin, "Local admin filtered by UAC",
                        "The credential is a local account, and UAC remote token filtering strips its admin rights "
                        + "over the network — the classic 'right password, still access denied'. Setting "
                        + "LocalAccountTokenFilterPolicy on the target fixes it (domain accounts are unaffected).",
                        PrereqFixes.TokenFiltering, Confident: true);
                return new(PrereqCause.BadCredentials, "Credentials rejected",
                    "Every applicable credential set was rejected. Verify the password with 'Test on host' in the "
                    + "credential dialog, and check the account actually has admin rights on this machine.",
                    null, Confident: true);

            case ConnectFailure.SshAuthFailed:
                return new(PrereqCause.SshAuthFailed, "SSH authentication failed",
                    "The SSH server rejected every credential set. Note that Marco uses password authentication — "
                    + "hosts with 'PasswordAuthentication no' in sshd_config will reject even a correct password.",
                    null, Confident: true);

            case ConnectFailure.SshUnreachable:
                return new(PrereqCause.SshUnreachable, "SSH unreachable",
                    m.OpenPorts.Contains(22)
                        ? "Port 22 was open during discovery but the SSH connection now fails — the service may "
                          + "have stopped, or a firewall rate-limits new sessions."
                        : "No SSH service was reachable on this host; check that sshd is running and port 22 (or "
                          + "the credential's custom port) is allowed through the firewall.",
                    null, Confident: true);
        }

        // Session opened fine — look for the registry-dependent collectors failing on their own.
        if (RegistryCollectorsFailed(m, out var failedNames))
            return new(PrereqCause.RemoteRegistryUnavailable, "Remote Registry unavailable",
                $"WMI works, but registry-based collectors ({failedNames}) failed. Marco already tried the "
                + "StdRegProv fallback, so the Remote Registry service is likely disabled AND the WMI registry "
                + "provider is restricted; starting the service (and allowing SMB 445) restores them.",
                PrereqFixes.RemoteRegistry, Confident: true);

        if (m.Status is MachineStatus.Error)
            return new(PrereqCause.Unknown, "Inventory failed",
                m.StatusDetail ?? "Every collector failed; see the per-collector list for details.",
                null, Confident: false);

        return new(PrereqCause.None, "", "", null, true);
    }

    /// <summary>Group the diagnosable failures across a run, biggest group first. None and by-design skips are
    /// excluded — the rollup is a to-do list, not a census.</summary>
    public static IReadOnlyList<PrereqCauseGroup> Rollup(IEnumerable<Machine> machines)
        => machines
            .Select(m => (Machine: m, Diagnosis: Diagnose(m)))
            .Where(x => x.Diagnosis.Cause is not (PrereqCause.None or PrereqCause.NotInventoryable))
            .GroupBy(x => x.Diagnosis.Cause)
            .Select(g => new PrereqCauseGroup(g.Key, g.First().Diagnosis.Title, g.First().Diagnosis.FixScript,
                g.Select(x => x.Machine).ToList()))
            .OrderByDescending(g => g.Machines.Count)
            .ToList();

    private static readonly string[] RegistryCollectors = { "InstalledSoftware", "Updates", "UsbHistory" };

    private static bool RegistryCollectorsFailed(Machine m, out string failedNames)
    {
        var failed = m.Collectors
            .Where(c => RegistryCollectors.Contains(c.Name, StringComparer.OrdinalIgnoreCase)
                        && c.Status is CollectorStatus.AccessDenied or CollectorStatus.Failed)
            .Select(c => c.Name)
            .ToList();
        failedNames = string.Join(", ", failed);
        // Only meaningful when WMI-based collectors demonstrably worked in the same pass.
        bool wmiWorked = m.Collectors.Any(c => !RegistryCollectors.Contains(c.Name, StringComparer.OrdinalIgnoreCase)
                                               && c.Status == CollectorStatus.Ok);
        return failed.Count > 0 && wmiWorked;
    }
}
