namespace Marco.Core.Model;

/// <summary>Inferred device class from discovery signals (ports, OUI, NBNS, TTL).</summary>
public enum DeviceType
{
    Unknown = 0,
    Windows,
    WindowsServer,
    Printer,
    NetworkDevice,
    UnixLinux,
}

/// <summary>Per-host lifecycle state, shown in the grid status column.</summary>
public enum MachineStatus
{
    Pending = 0,
    Scanning,
    Alive,
    Unreachable,
    AuthFailed,
    Partial,
    Done,
    Error,
    Cancelled,
}

/// <summary>How liveness was established.</summary>
public enum DiscoveryMethod
{
    None = 0,
    Icmp,
    Tcp,
    NameOnly,
    Manual,
}

/// <summary>Outcome of a single inventory collector against one host.</summary>
public enum CollectorStatus
{
    NotRun = 0,
    Ok,
    NotSupported,
    AccessDenied,
    Failed,
}

/// <summary>Why inventory could not open a session to the host — None once one succeeds. Structured evidence
/// for the prerequisite doctor (Marco.Core.Diagnosis), captured where the runners already catch the failure.</summary>
public enum ConnectFailure
{
    None = 0,
    NoCredentials,
    AuthFailed,
    AccessDenied,
    Unreachable,
    Timeout,
    SshAuthFailed,
    SshUnreachable,
    /// <summary>The printer/network device answered neither SNMP (any configured community) nor IPP.</summary>
    SnmpNoResponse,
    /// <summary>The host replied ICMP port-unreachable on UDP 161 — SNMP is switched off on the device.</summary>
    SnmpDisabled,
}

/// <summary>How a host relates to the blessed known-device baseline (see Marco.Core.Baseline). Transient —
/// evaluated per scan against the current baseline, never serialized.</summary>
public enum BaselineStatus
{
    NotEvaluated = 0,
    Known,
    Unknown,
    /// <summary>Only weak identity evidence (randomized MAC, or bare address) — possibly a known device whose
    /// Wi-Fi rotates its MAC; inventorying it recovers the serial and settles the question.</summary>
    UnknownWeak,
}

/// <summary>Where an installed-software entry came from.</summary>
public enum SoftwareSource
{
    Unknown = 0,
    Native64,
    Wow6432,
    PerUser,
    Package, // Linux distro package (dpkg/rpm/apk)
}
