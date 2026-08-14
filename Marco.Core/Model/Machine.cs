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

    /// <summary>Open TCP ports observed during discovery (feeds classification; not shown directly).</summary>
    public HashSet<int> OpenPorts { get; } = new();

    // --- Inventory groups (non-null so nested bindings always resolve) ---
    public SystemInfo System { get; } = new();
    public OsInfo Os { get; } = new();

    public List<CpuInfo> Cpus { get; } = new();
    public List<MemoryModule> MemoryModules { get; } = new();
    public List<DiskInfo> Disks { get; } = new();
    public List<VolumeInfo> Volumes { get; } = new();
    public List<AdapterInfo> Adapters { get; } = new();
    public List<SoftwareEntry> Software { get; } = new();
    public List<HotfixEntry> Hotfixes { get; } = new();
    public List<AntivirusEntry> Antivirus { get; } = new();
    public List<PrinterEntry> Printers { get; } = new();
    public List<UsbDeviceEntry> UsbDevices { get; } = new();

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
    public int CpuCount { get => _cpuCount; set => Set(ref _cpuCount, value); }
    public int SoftwareCount { get => _softwareCount; set => Set(ref _softwareCount, value); }
    public int DiskCount { get => _diskCount; set => Set(ref _diskCount, value); }
    public int AdapterCount { get => _adapterCount; set => Set(ref _adapterCount, value); }

    /// <summary>First MAC, for the grid's single MAC column.</summary>
    public string? PrimaryMac => MacAddresses.Count > 0 ? MacAddresses[0] : null;

    /// <summary>Comma-joined IPs, for a single grid column.</summary>
    public string IpList => string.Join(", ", IpAddresses);

    /// <summary>Sorted open-port list for the detail view.</summary>
    public string OpenPortsDisplay => string.Join(", ", OpenPorts.OrderBy(p => p));

    public Machine(string address)
    {
        Address = address ?? throw new ArgumentNullException(nameof(address));
        IpAddresses.Add(address);
    }

    /// <summary>Raise change notifications for all inventory-populated collections and computed fields, so the
    /// detail view re-renders after a background inventory pass completes. Safe to call from any thread — WPF
    /// marshals the resulting binding updates to the dispatcher.</summary>
    public void NotifyInventoryUpdated()
    {
        Raise(nameof(PrimaryMac)); Raise(nameof(IpList)); Raise(nameof(OpenPortsDisplay));
        Raise(nameof(Cpus)); Raise(nameof(MemoryModules)); Raise(nameof(Disks)); Raise(nameof(Volumes));
        Raise(nameof(Adapters)); Raise(nameof(Software)); Raise(nameof(Hotfixes)); Raise(nameof(Antivirus));
        Raise(nameof(Printers)); Raise(nameof(UsbDevices)); Raise(nameof(Collectors));
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
    }
}
