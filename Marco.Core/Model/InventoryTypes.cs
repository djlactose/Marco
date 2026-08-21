namespace Marco.Core.Model;

/// <summary>System/enclosure/BIOS identity (Win32_ComputerSystem, Win32_SystemEnclosure, Win32_BIOS).</summary>
public sealed class SystemInfo : ObservableBase
{
    private string? _manufacturer;
    private string? _model;
    private string? _productVersion;
    private string? _serialNumber;
    private string? _assetTag;
    private string? _chassisType;
    private string? _domain;
    private bool _partOfDomain;
    private string? _loggedOnUser;
    private string? _lastLoggedOnUser;
    private string? _biosVersion;
    private DateTime? _biosDate;
    private string? _motherboardManufacturer;
    private string? _motherboardModel;
    private bool _isDomainController;
    private int? _expansionSlotsTotal;
    private int? _expansionSlotsFree;
    private string? _expansionSlotsFreeList;

    public string? Manufacturer { get => _manufacturer; set => Set(ref _manufacturer, value); }
    public string? Model { get => _model; set => Set(ref _model, value); }
    /// <summary>Win32_ComputerSystemProduct.Version / DMI product_version — the friendly product name on Lenovo
    /// ("ThinkCentre M70q Gen 2"), where Model is the machine-type code. Null when the firmware left it blank.</summary>
    public string? ProductVersion { get => _productVersion; set => Set(ref _productVersion, value); }
    public string? SerialNumber { get => _serialNumber; set => Set(ref _serialNumber, value); }
    public string? AssetTag { get => _assetTag; set => Set(ref _assetTag, value); }
    public string? ChassisType { get => _chassisType; set => Set(ref _chassisType, value); }
    public string? Domain { get => _domain; set => Set(ref _domain, value); }
    public bool PartOfDomain { get => _partOfDomain; set => Set(ref _partOfDomain, value); }
    public string? LoggedOnUser { get => _loggedOnUser; set { if (Set(ref _loggedOnUser, value)) Raise(nameof(CurrentOrLastUser)); } }

    /// <summary>The last account to sign in (from LogonUI), used when no one is currently logged on.</summary>
    public string? LastLoggedOnUser { get => _lastLoggedOnUser; set { if (Set(ref _lastLoggedOnUser, value)) Raise(nameof(CurrentOrLastUser)); } }

    /// <summary>Current console user if signed in; otherwise the last person to sign in, suffixed "(last)".</summary>
    public string? CurrentOrLastUser =>
        !string.IsNullOrWhiteSpace(_loggedOnUser) ? _loggedOnUser
        : !string.IsNullOrWhiteSpace(_lastLoggedOnUser) ? $"{_lastLoggedOnUser} (last)"
        : null;

    public string? BiosVersion { get => _biosVersion; set => Set(ref _biosVersion, value); }
    public DateTime? BiosDate { get => _biosDate; set => Set(ref _biosDate, value); }
    public string? MotherboardManufacturer { get => _motherboardManufacturer; set => Set(ref _motherboardManufacturer, value); }
    public string? MotherboardModel { get => _motherboardModel; set => Set(ref _motherboardModel, value); }

    /// <summary>True when the OS reports itself as a domain controller (Win32_OperatingSystem.ProductType = 2).
    /// Collectors use it to skip account enumerations that would walk the whole domain.</summary>
    public bool IsDomainController { get => _isDomainController; set => Set(ref _isDomainController, value); }

    /// <summary>Board expansion slots (Win32_SystemSlot) — PCIe and, on boards that report them, M.2. Null when
    /// the firmware exposes none (typical for VMs). Feeds the best-effort drive-expansion estimate.</summary>
    public int? ExpansionSlotsTotal { get => _expansionSlotsTotal; set => Set(ref _expansionSlotsTotal, value); }
    /// <summary>Slots whose CurrentUsage is Available.</summary>
    public int? ExpansionSlotsFree { get => _expansionSlotsFree; set => Set(ref _expansionSlotsFree, value); }
    /// <summary>Designations of the free slots, comma-joined ("PCIEX16_1, M.2_2"); null when none are free.</summary>
    public string? ExpansionSlotsFreeList { get => _expansionSlotsFreeList; set => Set(ref _expansionSlotsFreeList, value); }
}

/// <summary>Operating system (Win32_OperatingSystem).</summary>
public sealed class OsInfo : ObservableBase
{
    private string? _caption;
    private string? _version;
    private string? _build;
    private string? _architecture;
    private DateTime? _installDate;
    private DateTime? _lastBoot;
    private string? _registeredOwner;
    private string? _registeredOrg;

    public string? Caption { get => _caption; set => Set(ref _caption, value); }
    public string? Version { get => _version; set => Set(ref _version, value); }
    public string? Build { get => _build; set => Set(ref _build, value); }
    public string? Architecture { get => _architecture; set => Set(ref _architecture, value); }
    public DateTime? InstallDate { get => _installDate; set => Set(ref _installDate, value); }
    public DateTime? LastBoot { get => _lastBoot; set { if (Set(ref _lastBoot, value)) Raise(nameof(UptimeDisplay)); } }
    public string? RegisteredOwner { get => _registeredOwner; set => Set(ref _registeredOwner, value); }
    public string? RegisteredOrganization { get => _registeredOrg; set => Set(ref _registeredOrg, value); }

    public string? UptimeDisplay
    {
        get
        {
            if (_lastBoot is not { } boot) return null;
            var span = DateTime.Now - boot;
            if (span < TimeSpan.Zero) return null;
            return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
        }
    }
}

public sealed class CpuInfo
{
    public string? Name { get; set; }
    public int Cores { get; set; }
    public int LogicalProcessors { get; set; }
    public int ClockMhz { get; set; }
    public string? Socket { get; set; }
}

public sealed class MemoryModule
{
    public long CapacityBytes { get; set; }
    /// <summary>Rated speed (Win32_PhysicalMemory.Speed).</summary>
    public int SpeedMhz { get; set; }
    public string? Manufacturer { get; set; }
    public string? PartNumber { get; set; }
    public string? SlotLabel { get; set; }
    /// <summary>"DDR4" / "DDR5" / "LPDDR5" … decoded from SMBIOSMemoryType (fallback MemoryType); null when the
    /// firmware reports Unknown, as many pre-2015 BIOSes do.</summary>
    public string? MemoryTypeName { get; set; }
    /// <summary>Speed the module is actually running at (ConfiguredClockSpeed); null when not reported.</summary>
    public int? ConfiguredSpeedMhz { get; set; }
    /// <summary>"DIMM" / "SODIMM"; null when unknown or soldered.</summary>
    public string? FormFactor { get; set; }

    /// <summary>"DDR4 · 3200 MHz (running 2933) · SODIMM" — the type facts, for the detail line.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? TypeDisplay
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(MemoryTypeName)) parts.Add(MemoryTypeName);
            if (SpeedMhz > 0)
            {
                var speed = $"{SpeedMhz} MHz";
                if (ConfiguredSpeedMhz is { } c && c > 0 && c != SpeedMhz) speed += $" (running {c})";
                parts.Add(speed);
            }
            else if (ConfiguredSpeedMhz is { } c && c > 0) parts.Add($"{c} MHz");
            if (!string.IsNullOrWhiteSpace(FormFactor)) parts.Add(FormFactor);
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }
}

public sealed class DiskInfo
{
    public string? Model { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>"SSD" / "HDD" / "SCM" from MSFT_PhysicalDisk when available; otherwise the Win32_DiskDrive text
    /// ("Fixed hard disk media") on older systems.</summary>
    public string? MediaType { get; set; }
    public string? Serial { get; set; }
    /// <summary>Drive temperature (storage reliability counters, or SMART attribute 194/190).</summary>
    public int? TempC { get; set; }
    /// <summary>Win32_DiskDrive.Status ("OK", "Pred Fail"), overridden by a SMART predict-failure flag.</summary>
    public string? SmartStatus { get; set; }
    /// <summary>NVMe / SATA / USB / SAS / RAID … (MSFT_PhysicalDisk.BusType), else Win32_DiskDrive.InterfaceType.</summary>
    public string? BusType { get; set; }
    /// <summary>Healthy / Warning / Unhealthy (MSFT_PhysicalDisk.HealthStatus).</summary>
    public string? HealthStatus { get; set; }
    public string? Firmware { get; set; }
    /// <summary>GPT or MBR, from the disk's partitions.</summary>
    public string? PartitionStyle { get; set; }
    public int? Partitions { get; set; }
    /// <summary>SMART "predict failure" flag when the driver exposes it (root\WMI); null when unknown.</summary>
    public bool? SmartPredictFailure { get; set; }
    /// <summary>SSD wear (percent used) when reported.</summary>
    public int? WearPercent { get; set; }
    public long? PowerOnHours { get; set; }

    /// <summary>"SSD · NVMe · GPT" — the type facts, for the detail line.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? KindDisplay
    {
        get
        {
            var parts = new[] { MediaType, BusType, PartitionStyle }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    /// <summary>"OK · Healthy · 34 °C · wear 3% · 12,345 h" — the health facts, for the detail line.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? HealthDisplay
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(SmartStatus)) parts.Add(SmartStatus);
            if (!string.IsNullOrWhiteSpace(HealthStatus) && !string.Equals(HealthStatus, SmartStatus, StringComparison.OrdinalIgnoreCase)) parts.Add(HealthStatus);
            if (TempC is { } t) parts.Add($"{t} °C");
            if (WearPercent is { } w) parts.Add($"wear {w}%");
            if (PowerOnHours is { } h) parts.Add($"{h:N0} h");
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }
}

public sealed class VolumeInfo
{
    public string? Letter { get; set; }
    public string? FileSystem { get; set; }
    public long CapacityBytes { get; set; }
    public long FreeBytes { get; set; }
    public string? Label { get; set; }
}

public sealed class AdapterInfo
{
    public string? Name { get; set; }
    public string? Mac { get; set; }
    public long SpeedBps { get; set; }
    public List<string> IpAddresses { get; } = new();
    public string? SubnetMask { get; set; }
    public string? Gateway { get; set; }
    public List<string> DnsServers { get; } = new();
    public bool DhcpEnabled { get; set; }
}

public sealed class SoftwareEntry
{
    public string DisplayName { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public DateTime? InstallDate { get; set; }
    public SoftwareSource Source { get; set; }
}

public sealed class HotfixEntry
{
    public string Id { get; set; } = string.Empty;
    public DateTime? InstalledOn { get; set; }
    public string? Description { get; set; }
    public string? InstalledBy { get; set; }
}

/// <summary>A Security Center product (client SKUs) or Defender itself (servers). <see cref="Kind"/> is
/// "Antivirus", "Antispyware" or "Firewall".</summary>
public sealed class AntivirusEntry
{
    public string? Product { get; set; }
    public string Kind { get; set; } = "Antivirus";
    /// <summary>Raw productState (hex) for troubleshooting.</summary>
    public string? State { get; set; }
    public bool? Enabled { get; set; }
    public bool? UpToDate { get; set; }
}

public sealed class PrinterEntry
{
    public string? Name { get; set; }
    public bool IsDefault { get; set; }
    public string? PortName { get; set; }
    public string? DriverName { get; set; }
    public bool Shared { get; set; }
    public string? ShareName { get; set; }
    public bool IsNetwork { get; set; }
    public string? Location { get; set; }
    /// <summary>IP/host of a TCP/IP printer port, so the entry can be matched to a discovered printer.</summary>
    public string? HostAddress { get; set; }
    public string? Status { get; set; }
    /// <summary>Jobs currently in this queue on the host (Win32_PrintJob); null when the class wasn't readable.</summary>
    public int? QueuedJobs { get; set; }
    /// <summary>Decoded Win32_Printer.PrinterState flags ("Paused", "Error", "Paper jam", …); null when none.</summary>
    public string? PrinterState { get; set; }
}

public sealed class UsbDeviceEntry
{
    public string? Name { get; set; }
    public string? Manufacturer { get; set; }
    public string? PnpClass { get; set; }
    public string? DeviceId { get; set; }
    public string? Status { get; set; }
}

/// <summary>A USB storage device that has ever been connected (HKLM\SYSTEM\...\Enum\USBSTOR).</summary>
public sealed class UsbStorageHistoryEntry
{
    public string? FriendlyName { get; set; }
    public string? Vendor { get; set; }
    public string? Product { get; set; }
    public string? Serial { get; set; }
}

// --- Updates & servicing ---

/// <summary>Windows Update / servicing state (Updates collector). Scalars only — the hotfix list is on the
/// machine. Plain settable properties: bound through the machine's full re-raise (see
/// <see cref="Machine.NotifyInventoryUpdated"/>) and serialised as-is; the computed members are JsonIgnore'd.</summary>
public sealed class UpdateInfo : ObservableBase
{
    /// <summary>Feature release, e.g. "22H2" / "24H2" (registry DisplayVersion, else ReleaseId).</summary>
    public string? DisplayVersion { get; set; }
    /// <summary>Update Build Revision — the ".NNNN" that Win32_OperatingSystem.Version omits.</summary>
    public int? Ubr { get; set; }
    /// <summary>Full build "10.0.22631.4037" (OS version + UBR).</summary>
    public string? FullBuild { get; set; }
    public string? EditionId { get; set; }
    /// <summary>Client / Server / Server Core.</summary>
    public string? InstallationType { get; set; }
    public string? ProductName { get; set; }
    public int? HotfixCount { get; set; }
    public DateTime? LastHotfixDate { get; set; }
    /// <summary>Null when the registry could not be read at all.</summary>
    public bool? PendingReboot { get; set; }
    public string? PendingRebootReasons { get; set; }
    public string? WsusServer { get; set; }
    public string? WsusTargetGroup { get; set; }
    public bool? UseWsus { get; set; }
    public bool? NoAutoUpdate { get; set; }
    public string? AutoUpdateOption { get; set; }
    /// <summary>Sub-probes that were unavailable (access denied / not present), for the detail pane.</summary>
    public string? Notes { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? PendingRebootDisplay => PendingReboot switch { true => "Yes", false => "No", _ => null };

    [System.Text.Json.Serialization.JsonIgnore]
    public int? DaysSinceLastHotfix => LastHotfixDate is { } d ? (int)(DateTime.Now - d).TotalDays : null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? WsusDisplay => WsusServer is null ? null
        : WsusServer + (UseWsus == false ? " (not in use)" : "") + (WsusTargetGroup is { } g ? $" [{g}]" : "");

    /// <summary>"Release 24H2 · build 10.0.26100.4351". The OS caption is shown by the OS section; the registry
    /// ProductName is deliberately not used here — it still says "Windows 10 Pro" on Windows 11.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? BuildSummary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(DisplayVersion)) parts.Add("Release " + DisplayVersion);
            if (FullBuild is { } b) parts.Add("build " + b);
            if (!string.IsNullOrWhiteSpace(InstallationType) && !InstallationType.Equals("Client", StringComparison.OrdinalIgnoreCase)) parts.Add(InstallationType);
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    /// <summary>"42 hotfixes, last 2026-08-01 (16 days ago)".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? HotfixSummary => HotfixCount switch
    {
        null => null,
        0 => "No hotfixes reported",
        var n => $"{n} hotfix{(n == 1 ? "" : "es")}"
                 + (LastHotfixDate is { } d ? $", last {d:yyyy-MM-dd}" + (DaysSinceLastHotfix is { } days ? $" ({days} days ago)" : "") : ""),
    };

    /// <summary>True once the collector has produced anything worth a section in the detail pane.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasData => FullBuild is not null || HotfixCount is not null || PendingReboot is not null || WsusServer is not null || Notes is not null;
}

// --- Security posture ---

public sealed class BitLockerVolumeEntry
{
    public string? Letter { get; set; }
    /// <summary>On / Off / Unknown (ProtectionStatus).</summary>
    public string? Protection { get; set; }
    /// <summary>Fully encrypted / Encryption in progress / … (ConversionStatus).</summary>
    public string? Status { get; set; }
    public string? Method { get; set; }
    /// <summary>OS / Fixed data / Removable.</summary>
    public string? VolumeType { get; set; }
}

/// <summary>Security posture (Security collector). Every field is nullable: null = not determined (namespace
/// missing on this SKU, access denied, or registry unavailable), as opposed to false = determined off.</summary>
public sealed class SecurityInfo : ObservableBase
{
    // Defender (root\Microsoft\Windows\Defender)
    public bool? DefenderEnabled { get; set; }
    public bool? DefenderRealTime { get; set; }
    public string? DefenderSignatureVersion { get; set; }
    public int? DefenderSignatureAgeDays { get; set; }
    public DateTime? DefenderSignatureUpdated { get; set; }
    public DateTime? DefenderLastQuickScan { get; set; }
    public bool? DefenderTamperProtected { get; set; }
    public string? DefenderEngineVersion { get; set; }
    public string? DefenderRunningMode { get; set; }

    // Firewall profiles (root\StandardCimv2)
    public bool? FirewallDomain { get; set; }
    public bool? FirewallPrivate { get; set; }
    public bool? FirewallPublic { get; set; }

    // BitLocker (settable so System.Text.Json can populate it on reopen)
    public List<BitLockerVolumeEntry> BitLockerVolumes { get; set; } = new();

    // TPM
    public bool? TpmPresent { get; set; }
    public bool? TpmEnabled { get; set; }
    public bool? TpmActivated { get; set; }
    public bool? TpmOwned { get; set; }
    /// <summary>"2.0" / "1.2".</summary>
    public string? TpmVersion { get; set; }
    public string? TpmManufacturer { get; set; }

    // Firmware
    public bool? SecureBoot { get; set; }
    /// <summary>UEFI or BIOS.</summary>
    public string? FirmwareType { get; set; }

    // UAC
    public bool? UacEnabled { get; set; }
    public string? UacAdminPrompt { get; set; }
    public bool? LocalAccountTokenFilterPolicy { get; set; }

    /// <summary>Winlogon AutoAdminLogon — true means a password sits in the registry and the box logs itself in.</summary>
    public bool? AutoAdminLogon { get; set; }

    // Remote Desktop
    public bool? RdpEnabled { get; set; }
    public bool? RdpNlaRequired { get; set; }
    public int? RdpPort { get; set; }

    // SMB server
    public bool? Smb1Enabled { get; set; }
    public bool? SmbSigningRequired { get; set; }
    public bool? SmbEncryptData { get; set; }

    // Virtualization-based security
    public string? VbsStatus { get; set; }
    public bool? CredentialGuardRunning { get; set; }
    public bool? HvciRunning { get; set; }

    // LAPS
    public bool? LapsManaged { get; set; }
    public string? LapsKind { get; set; }

    public string? Notes { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? FirewallSummary => Summarize(("Domain", FirewallDomain), ("Private", FirewallPrivate), ("Public", FirewallPublic));

    [System.Text.Json.Serialization.JsonIgnore]
    public string? TpmSummary => TpmPresent switch
    {
        false => "Not present",
        true => (TpmVersion is { } v ? "TPM " + v : "Present")
                + (TpmEnabled == false ? ", disabled" : "") + (TpmActivated == false ? ", not activated" : ""),
        _ => null,
    };

    /// <summary>"TPM 2.0 · Secure Boot on · UEFI" — the platform-security line.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? PlatformSummary
    {
        get
        {
            var parts = new List<string>();
            if (TpmSummary is { } t) parts.Add(t.StartsWith("TPM", StringComparison.Ordinal) ? t : "TPM " + t.ToLowerInvariant());
            if (SecureBoot is { } sb) parts.Add(sb ? "Secure Boot on" : "Secure Boot OFF");
            if (FirmwareType is { } f) parts.Add(f);
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? BitLockerSummary => BitLockerVolumes.Count == 0 ? null
        : string.Join(", ", BitLockerVolumes.Select(v => $"{v.Letter ?? "?"} {v.Protection ?? "?"}"
            + (v.Protection == "On" && v.Method is { } m ? $" ({m})" : "")));

    [System.Text.Json.Serialization.JsonIgnore]
    public string? RdpSummary => RdpEnabled switch
    {
        false => "Disabled",
        true => "Enabled" + (RdpNlaRequired switch { true => ", NLA required", false => ", NLA NOT required", _ => "" })
                          + (RdpPort is { } p && p != 3389 ? $", port {p}" : ""),
        _ => null,
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string? SmbSummary => Smb1Enabled is null && SmbSigningRequired is null ? null
        : $"SMB1 {(Smb1Enabled switch { true => "ENABLED", false => "off", _ => "?" })}, "
          + $"signing {(SmbSigningRequired switch { true => "required", false => "not required", _ => "?" })}";

    [System.Text.Json.Serialization.JsonIgnore]
    public string? DefenderSummary => DefenderEnabled is null ? null
        : (DefenderEnabled == true ? "On" : "Off")
          + (DefenderRealTime == false ? ", real-time protection off" : "")
          + (DefenderSignatureAgeDays is { } a ? $", signatures {a}d old" : "")
          + (DefenderTamperProtected == true ? ", tamper-protected" : "");

    [System.Text.Json.Serialization.JsonIgnore]
    public string? UacSummary => UacEnabled switch
    {
        false => "Disabled",
        true => "Enabled" + (UacAdminPrompt is { } p ? $" ({p})" : "")
                          + (LocalAccountTokenFilterPolicy == true ? ", remote token filtering off" : ""),
        _ => null,
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string? VbsSummary => VbsStatus is null ? null
        : VbsStatus + (CredentialGuardRunning == true ? ", Credential Guard" : "") + (HvciRunning == true ? ", HVCI" : "");

    [System.Text.Json.Serialization.JsonIgnore]
    public string? LapsSummary => LapsManaged switch { true => LapsKind ?? "Managed", false => "Not managed", _ => null };

    /// <summary>True once any probe produced a value (or a note), so the detail pane knows to show the section.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasData => DefenderSummary is not null || FirewallSummary is not null || BitLockerSummary is not null
        || TpmSummary is not null || SecureBoot is not null || FirmwareType is not null || RdpSummary is not null
        || SmbSummary is not null || UacSummary is not null || VbsSummary is not null || LapsSummary is not null || Notes is not null;

    private static string? Summarize(params (string Name, bool? On)[] items)
    {
        var known = items.Where(i => i.On is not null).ToList();
        if (known.Count == 0) return null;
        return string.Join(", ", known.Select(i => $"{i.Name} {(i.On == true ? "on" : "OFF")}"));
    }
}

// --- Users ---

public sealed class LocalAccountEntry
{
    public string Name { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? Sid { get; set; }
    public bool Disabled { get; set; }
    public bool Locked { get; set; }
    public bool PasswordRequired { get; set; }
    public bool PasswordExpires { get; set; }
    /// <summary>Member of the local Administrators group.</summary>
    public bool IsAdmin { get; set; }
    public DateTime? LastLogon { get; set; }
    public string? Description { get; set; }

    /// <summary>"admin · disabled · last logon 2026-08-01" for the detail pane.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Flags
    {
        get
        {
            var parts = new List<string>();
            if (IsAdmin) parts.Add("admin");
            if (Disabled) parts.Add("disabled");
            if (Locked) parts.Add("locked");
            if (!PasswordRequired) parts.Add("no password required");
            if (!PasswordExpires) parts.Add("password never expires");
            if (LastLogon is { } d) parts.Add($"last logon {d:yyyy-MM-dd}");
            return string.Join(" · ", parts);
        }
    }
}

public sealed class UserProfileEntry
{
    /// <summary>Leaf of the profile path (C:\Users\jdoe → jdoe).</summary>
    public string? User { get; set; }
    public string? LocalPath { get; set; }
    public string? Sid { get; set; }
    public DateTime? LastUse { get; set; }
    public bool Loaded { get; set; }
}

public sealed class LogonSessionEntry
{
    public string Account { get; set; } = string.Empty;
    /// <summary>Interactive / RemoteInteractive / CachedInteractive.</summary>
    public string? LogonType { get; set; }
    public DateTime? StartTime { get; set; }
}

// --- Services & startup ---

public sealed class ServiceEntry
{
    public string Name { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? State { get; set; }
    public string? StartMode { get; set; }
    /// <summary>The account the service runs as (StartName).</summary>
    public string? Account { get; set; }
    public string? Path { get; set; }
    public int? ProcessId { get; set; }
}

public sealed class StartupEntry
{
    public string? Name { get; set; }
    public string? Command { get; set; }
    public string? Location { get; set; }
    public string? User { get; set; }
}

public sealed class ScheduledTaskEntry
{
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? State { get; set; }
    public string? RunAs { get; set; }
    public string? Author { get; set; }
    public DateTime? Created { get; set; }
}

// --- Peripherals ---

public sealed class MonitorEntry
{
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? Serial { get; set; }
    public string? ProductCode { get; set; }
    public int? Year { get; set; }
    public int? Week { get; set; }
    public double? DiagonalInches { get; set; }
    public bool Active { get; set; }
}

public sealed class GpuInfo
{
    public string? Name { get; set; }
    public long VramBytes { get; set; }
    public string? DriverVersion { get; set; }
    public DateTime? DriverDate { get; set; }
    /// <summary>"1920x1080 @ 60 Hz" when the adapter drives a display.</summary>
    public string? Resolution { get; set; }
}

public sealed class BatteryInfo : ObservableBase
{
    public string? Name { get; set; }
    public int? ChargePercent { get; set; }
    public string? Status { get; set; }
    public int? DesignCapacityMwh { get; set; }
    public int? FullChargeCapacityMwh { get; set; }
    /// <summary>Full-charge capacity as a percentage of design capacity.</summary>
    public double? HealthPercent { get; set; }
    public int? CycleCount { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary =>
        (Name ?? "Battery")
        + (ChargePercent is { } c ? $" — {c}%" : "")
        + (Status is { } s ? $" ({s})" : "")
        + (HealthPercent is { } h ? $", health {h:0}%" : "")
        + (CycleCount is { } n ? $", {n} cycles" : "");
}

/// <summary>Per-collector outcome recorded on each machine for failure attribution.</summary>
public sealed class CollectorResult : ObservableBase
{
    private CollectorStatus _status;
    private string? _error;

    public string Name { get; }
    public CollectorStatus Status { get => _status; set => Set(ref _status, value); }
    public string? Error { get => _error; set => Set(ref _error, value); }

    public CollectorResult(string name) => Name = name;
}
