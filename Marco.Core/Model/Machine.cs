using System.Collections.ObjectModel;

namespace Marco.Core.Model;

/// <summary>
/// The aggregate root: one machine = one row in the results grid. Identity is filled by discovery;
/// the System/OS/Cpu/... groups are filled by inventory collectors. Scalar and status fields raise
/// change notifications so the grid updates in place; list fields are populated off-thread during a
/// scan and read by the detail view once that machine's inventory is complete.
/// </summary>
public sealed class Machine : ObservableBase
{
    // --- Identity ---
    private string? _name;
    private string? _fqdn;
    private DeviceType _deviceType;
    private bool _isVirtual;

    /// <summary>Primary address; stable key for a discovered host. Set once at creation.</summary>
    public string Address { get; }

    /// <summary>Numeric ordering key for <see cref="Address"/>: the IPv4 value, so the grid sorts 10.0.0.2 before
    /// 10.0.0.10 (a string sort would not); anything that isn't a dotted quad (hostnames, IPv6) sorts last.</summary>
    public long AddressSortKey { get; }

    private string? _targetBlock;
    /// <summary>The operator's target token this host came from (e.g. "192.168.0.0/24") — the results grid groups
    /// rows by it. Null for rows from a scan file that predates block information.</summary>
    public string? TargetBlock { get => _targetBlock; set => Set(ref _targetBlock, value); }

    public string? Name { get => _name; set { if (Set(ref _name, value)) Raise(nameof(DisplayName)); } }
    public string? Fqdn { get => _fqdn; set => Set(ref _fqdn, value); }
    public DeviceType DeviceType { get => _deviceType; set => Set(ref _deviceType, value); }
    public bool IsVirtual { get => _isVirtual; set => Set(ref _isVirtual, value); }

    // Plain lists (not ObservableCollection): these are mutated on worker threads during discovery/inventory,
    // and a plain list has no CollectionChanged to marshal, so cross-thread mutation is safe. The UI re-reads
    // them when NotifyInventoryUpdated() raises the corresponding property, or when a row is (re)selected.
    public List<string> IpAddresses { get; } = new();
    public List<string> MacAddresses { get; } = new();

    private string? _vendor;
    /// <summary>MAC OUI vendor, when resolvable.</summary>
    public string? Vendor { get => _vendor; set => Set(ref _vendor, value); }

    public string DisplayName => !string.IsNullOrWhiteSpace(_name) ? _name! : Address;

    // --- Reachability / status ---
    private bool _isAlive;
    private DiscoveryMethod _discoveryMethod;
    private DateTime? _lastScanned;
    private MachineStatus _status = MachineStatus.Pending;
    private string? _statusDetail;
    private int? _icmpTtl;

    public bool IsAlive { get => _isAlive; set => Set(ref _isAlive, value); }
    public DiscoveryMethod DiscoveryMethod { get => _discoveryMethod; set => Set(ref _discoveryMethod, value); }
    public DateTime? LastScanned { get => _lastScanned; set => Set(ref _lastScanned, value); }
    public MachineStatus Status { get => _status; set => Set(ref _status, value); }
    public string? StatusDetail { get => _statusDetail; set => Set(ref _statusDetail, value); }
    public int? IcmpTtl { get => _icmpTtl; set => Set(ref _icmpTtl, value); }

    private string? _currentActivity;
    /// <summary>What inventory is doing to this host right now ("Connecting (lab-admin, 2/3)…",
    /// "Collecting Software…"); null when idle. Set from worker threads — WPF marshals scalar INPC the same
    /// way it already does for Status/StatusDetail. Transient: never serialized (no MachineDto field).</summary>
    public string? CurrentActivity { get => _currentActivity; set => Set(ref _currentActivity, value); }

    /// <summary>Open TCP ports observed during discovery (feeds classification; not shown directly).</summary>
    public HashSet<int> OpenPorts { get; } = new();

    // --- Inventory groups (non-null so nested bindings always resolve) ---
    // The lists below are settable: collectors build a local list and ASSIGN it on completion (never
    // Clear()+Add on the shared instance). The reference change is what makes bound ItemsControls
    // regenerate on NotifyInventoryUpdated — a re-read of the same reference is a no-op to WPF — and it
    // removes the mutate-while-the-UI-enumerates race.
    public SystemInfo System { get; } = new();
    public OsInfo Os { get; } = new();

    private List<CpuInfo> _cpus = new();
    public List<CpuInfo> Cpus { get => _cpus; set => _cpus = value ?? new(); }
    private List<MemoryModule> _memoryModules = new();
    public List<MemoryModule> MemoryModules { get => _memoryModules; set => _memoryModules = value ?? new(); }
    private List<DiskInfo> _disks = new();
    public List<DiskInfo> Disks { get => _disks; set => _disks = value ?? new(); }
    private List<VolumeInfo> _volumes = new();
    public List<VolumeInfo> Volumes { get => _volumes; set => _volumes = value ?? new(); }
    private List<AdapterInfo> _adapters = new();
    public List<AdapterInfo> Adapters { get => _adapters; set => _adapters = value ?? new(); }
    private List<SoftwareEntry> _software = new();
    public List<SoftwareEntry> Software { get => _software; set => _software = value ?? new(); }
    private List<HotfixEntry> _hotfixes = new();
    public List<HotfixEntry> Hotfixes { get => _hotfixes; set => _hotfixes = value ?? new(); }
    private List<AntivirusEntry> _antivirus = new();
    public List<AntivirusEntry> Antivirus { get => _antivirus; set => _antivirus = value ?? new(); }
    private List<PrinterEntry> _printers = new();
    public List<PrinterEntry> Printers { get => _printers; set => _printers = value ?? new(); }
    private List<UsbDeviceEntry> _usbDevices = new();
    public List<UsbDeviceEntry> UsbDevices { get => _usbDevices; set => _usbDevices = value ?? new(); }
    private List<UsbStorageHistoryEntry> _usbStorageHistory = new();
    public List<UsbStorageHistoryEntry> UsbStorageHistory { get => _usbStorageHistory; set => _usbStorageHistory = value ?? new(); }

    // Users / services / peripherals (Phase 3 collectors)
    private List<LocalAccountEntry> _localAccounts = new();
    public List<LocalAccountEntry> LocalAccounts { get => _localAccounts; set => _localAccounts = value ?? new(); }
    /// <summary>Members of the local Administrators group as "DOMAIN\name" (groups suffixed "(group)").</summary>
    private List<string> _localAdministrators = new();
    public List<string> LocalAdministrators { get => _localAdministrators; set => _localAdministrators = value ?? new(); }
    private List<UserProfileEntry> _userProfiles = new();
    public List<UserProfileEntry> UserProfiles { get => _userProfiles; set => _userProfiles = value ?? new(); }
    private List<LogonSessionEntry> _logonSessions = new();
    public List<LogonSessionEntry> LogonSessions { get => _logonSessions; set => _logonSessions = value ?? new(); }
    private List<ServiceEntry> _services = new();
    public List<ServiceEntry> Services { get => _services; set => _services = value ?? new(); }
    private List<StartupEntry> _startupItems = new();
    public List<StartupEntry> StartupItems { get => _startupItems; set => _startupItems = value ?? new(); }
    private List<ScheduledTaskEntry> _scheduledTasks = new();
    public List<ScheduledTaskEntry> ScheduledTasks { get => _scheduledTasks; set => _scheduledTasks = value ?? new(); }
    private List<MonitorEntry> _monitors = new();
    public List<MonitorEntry> Monitors { get => _monitors; set => _monitors = value ?? new(); }
    private List<GpuInfo> _gpus = new();
    public List<GpuInfo> Gpus { get => _gpus; set => _gpus = value ?? new(); }

    // Scalar groups. Settable so a reopened scan can hand its deserialized object straight over; the detail
    // pane re-reads them through NotifyInventoryUpdated like System/Os.
    private UpdateInfo _updates = new();
    private SecurityInfo _security = new();
    private BatteryInfo? _battery;
    private int? _thermalTempC;
    public UpdateInfo Updates { get => _updates; set => Set(ref _updates, value ?? new UpdateInfo()); }
    public SecurityInfo Security { get => _security; set => Set(ref _security, value ?? new SecurityInfo()); }
    /// <summary>Null on machines without a battery.</summary>
    public BatteryInfo? Battery { get => _battery; set => Set(ref _battery, value); }
    /// <summary>Hottest ACPI thermal zone, when the firmware exposes one over WMI (rare on desktops).</summary>
    public int? ThermalTempC { get => _thermalTempC; set => Set(ref _thermalTempC, value); }

    // --- Memory summary (filled by the memory collector) ---
    private long _totalMemoryBytes;
    private int _memorySlotsUsed;
    private int _memorySlotsTotal;
    public long TotalMemoryBytes { get => _totalMemoryBytes; set { if (Set(ref _totalMemoryBytes, value)) Raise(nameof(TotalMemoryGb)); } }
    public int MemorySlotsUsed { get => _memorySlotsUsed; set => Set(ref _memorySlotsUsed, value); }
    public int MemorySlotsTotal { get => _memorySlotsTotal; set => Set(ref _memorySlotsTotal, value); }
    public double TotalMemoryGb => Math.Round(_totalMemoryBytes / 1024d / 1024d / 1024d, 1);

    // --- Per-collector outcomes ---
    public List<CollectorResult> Collectors { get; } = new();

    // --- Grid summary columns for list-valued data ---
    private int _cpuCount, _softwareCount, _diskCount, _adapterCount;
    private int _serviceCount, _stoppedAutoServiceCount, _hotfixCount, _localAccountCount, _monitorCount;
    public int CpuCount { get => _cpuCount; set => Set(ref _cpuCount, value); }
    public int SoftwareCount { get => _softwareCount; set => Set(ref _softwareCount, value); }
    public int DiskCount { get => _diskCount; set => Set(ref _diskCount, value); }
    public int AdapterCount { get => _adapterCount; set => Set(ref _adapterCount, value); }
    public int ServiceCount { get => _serviceCount; set => Set(ref _serviceCount, value); }
    /// <summary>Services set to start automatically that are not running — the classic "something's wrong" count.</summary>
    public int StoppedAutoServiceCount { get => _stoppedAutoServiceCount; set => Set(ref _stoppedAutoServiceCount, value); }
    public int HotfixCount { get => _hotfixCount; set => Set(ref _hotfixCount, value); }
    public int LocalAccountCount { get => _localAccountCount; set => Set(ref _localAccountCount, value); }
    public int MonitorCount { get => _monitorCount; set => Set(ref _monitorCount, value); }

    /// <summary>First MAC, for the grid's single MAC column.</summary>
    public string? PrimaryMac => MacAddresses.Count > 0 ? MacAddresses[0] : null;

    /// <summary>Comma-joined IPs, for a single grid column.</summary>
    public string IpList => string.Join(", ", IpAddresses);

    /// <summary>Sorted open-port list for the detail view.</summary>
    public string OpenPortsDisplay => string.Join(", ", OpenPorts.OrderBy(p => p));

    /// <summary>"Product (on, up to date); Product2 (OFF)" over the antivirus-kind entries, for the grid/CSV.</summary>
    public string? AntivirusSummary
    {
        get
        {
            var av = Antivirus.Where(a => a.Kind == "Antivirus").ToList();
            if (av.Count == 0) return null;
            return string.Join("; ", av.Select(a =>
                (a.Product ?? "?") + " (" + (a.Enabled switch { true => "on", false => "OFF", _ => "?" })
                + (a.UpToDate == false ? ", out of date" : "") + ")"));
        }
    }

    /// <summary>Local Administrators as one comma-joined line.</summary>
    public string? LocalAdministratorsDisplay => LocalAdministrators.Count == 0 ? null : string.Join(", ", LocalAdministrators);

    /// <summary>"3 running · 1 auto-stopped" for the Services section header.</summary>
    public string? ServicesSummary => Services.Count == 0 ? null
        : $"{Services.Count(s => string.Equals(s.State, "Running", StringComparison.OrdinalIgnoreCase))} running of {Services.Count}"
          + (StoppedAutoServiceCount > 0 ? $", {StoppedAutoServiceCount} automatic but stopped" : "");

    /// <summary>First GPU for the grid/CSV.</summary>
    public string? PrimaryGpu => Gpus.Count > 0 ? Gpus[0].Name : null;

    public Machine(string address)
    {
        Address = address ?? throw new ArgumentNullException(nameof(address));
        AddressSortKey = ComputeSortKey(address);
        IpAddresses.Add(address);
    }

    private static long ComputeSortKey(string address)
    {
        var parts = address.Split('.');
        if (parts.Length != 4) return long.MaxValue;
        long key = 0;
        foreach (var part in parts)
        {
            if (!byte.TryParse(part, out var b)) return long.MaxValue;
            key = (key << 8) | b;
        }
        return key;
    }

    /// <summary>Force a full re-read of this machine's row and detail after a background inventory pass. Must be
    /// invoked on the UI thread: inventory sets scalar and nested fields (System.*, Os.*, RAM, Status) and fills
    /// list fields on worker threads, and cross-thread per-property notifications don't reliably refresh an
    /// already-bound row — so we re-raise everything, including the System/Os sub-objects, in one shot.</summary>
    public void NotifyInventoryUpdated()
    {
        System.RaiseAll();
        Os.RaiseAll();
        Updates.RaiseAll();
        Security.RaiseAll();
        Battery?.RaiseAll();
        RaiseAll();
    }

    /// <summary>Record (or update) a collector's outcome, keyed by name.</summary>
    public void SetCollector(string name, CollectorStatus status, string? error = null)
    {
        var existing = Collectors.FirstOrDefault(c => c.Name == name);
        if (existing is null)
        {
            Collectors.Add(new CollectorResult(name) { Status = status, Error = error });
        }
        else
        {
            existing.Status = status;
            existing.Error = error;
        }
    }

    /// <summary>Refresh the denormalized summary counts after inventory fills the lists.</summary>
    public void RefreshCounts()
    {
        CpuCount = Cpus.Count;
        SoftwareCount = Software.Count;
        DiskCount = Disks.Count;
        AdapterCount = Adapters.Count;
        ServiceCount = Services.Count;
        StoppedAutoServiceCount = Services.Count(s =>
            string.Equals(s.StartMode, "Auto", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(s.State, "Running", StringComparison.OrdinalIgnoreCase));
        HotfixCount = Hotfixes.Count;
        LocalAccountCount = LocalAccounts.Count;
        MonitorCount = Monitors.Count;
    }
}
