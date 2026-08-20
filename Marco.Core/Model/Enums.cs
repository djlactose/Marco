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
