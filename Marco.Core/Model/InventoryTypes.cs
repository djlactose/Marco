namespace Marco.Core.Model;

/// <summary>System/enclosure/BIOS identity (Win32_ComputerSystem, Win32_SystemEnclosure, Win32_BIOS).</summary>
public sealed class SystemInfo : ObservableBase
{
    private string? _manufacturer;
    private string? _model;
    private string? _serialNumber;
    private string? _assetTag;
    private string? _chassisType;
    private string? _domain;
    private bool _partOfDomain;
    private string? _loggedOnUser;
    private string? _biosVersion;
    private DateTime? _biosDate;
    private string? _motherboardManufacturer;
    private string? _motherboardModel;

    public string? Manufacturer { get => _manufacturer; set => Set(ref _manufacturer, value); }
    public string? Model { get => _model; set => Set(ref _model, value); }
    public string? SerialNumber { get => _serialNumber; set => Set(ref _serialNumber, value); }
    public string? AssetTag { get => _assetTag; set => Set(ref _assetTag, value); }
    public string? ChassisType { get => _chassisType; set => Set(ref _chassisType, value); }
    public string? Domain { get => _domain; set => Set(ref _domain, value); }
    public bool PartOfDomain { get => _partOfDomain; set => Set(ref _partOfDomain, value); }
    public string? LoggedOnUser { get => _loggedOnUser; set => Set(ref _loggedOnUser, value); }
    public string? BiosVersion { get => _biosVersion; set => Set(ref _biosVersion, value); }
    public DateTime? BiosDate { get => _biosDate; set => Set(ref _biosDate, value); }
    public string? MotherboardManufacturer { get => _motherboardManufacturer; set => Set(ref _motherboardManufacturer, value); }
    public string? MotherboardModel { get => _motherboardModel; set => Set(ref _motherboardModel, value); }
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
    public int SpeedMhz { get; set; }
    public string? Manufacturer { get; set; }
    public string? PartNumber { get; set; }
    public string? SlotLabel { get; set; }
}

public sealed class DiskInfo
{
    public string? Model { get; set; }
    public long SizeBytes { get; set; }
    public string? MediaType { get; set; }
    public string? Serial { get; set; }
    public int? TempC { get; set; }
    public string? SmartStatus { get; set; }
}

public sealed class VolumeInfo
{
    public string? Letter { get; set; }
    public string? FileSystem { get; set; }
    public long CapacityBytes { get; set; }
    public long FreeBytes { get; set; }
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
}

public sealed class AntivirusEntry
{
    public string? Product { get; set; }
    public string? State { get; set; }
    public bool? Enabled { get; set; }
    public bool? UpToDate { get; set; }
}

public sealed class PrinterEntry
{
    public string? Name { get; set; }
    public bool IsDefault { get; set; }
    public string? PortName { get; set; }
}

public sealed class UsbDeviceEntry
{
    public string? Name { get; set; }
    public string? Version { get; set; }
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
