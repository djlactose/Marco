using Marco.Core.Model;

namespace Marco.Export;

/// <summary>Scan-level metadata embedded in every export, for provenance. <paramref name="Version"/> is the app
/// version that produced the export ("0.0" for pre-versioning exports); <paramref name="SchemaVersion"/> tracks
/// the document format itself, independent of app releases.</summary>
public sealed record ScanMetadata(
    DateTime Timestamp,
    string? Operator,
    IReadOnlyList<string> RangesScanned,
    int TotalTargets,
    int AliveCount,
    string Tool = "Marco",
    string Version = "0.0",
    string SchemaVersion = "1");

/// <summary>Serializable scan document (metadata + machines). Machines are projected to DTOs so JSON is clean and
/// a saved scan can be reopened without depending on the observable model's constructors.</summary>
public sealed record ScanDocument(ScanMetadata Metadata, IReadOnlyList<MachineDto> Machines)
{
    public static ScanDocument From(ScanMetadata meta, IEnumerable<Machine> machines)
        => new(meta, machines.Select(MachineDto.From).ToList());

    public IReadOnlyList<Machine> ToMachines() => Machines.Select(m => m.ToMachine()).ToList();
}

public sealed record MachineDto(
    string Address,
    string? Name,
    string? Fqdn,
    DeviceType DeviceType,
    bool IsVirtual,
    string? Vendor,
    IReadOnlyList<string> IpAddresses,
    IReadOnlyList<string> MacAddresses,
    MachineStatus Status,
    string? StatusDetail,
    DiscoveryMethod DiscoveryMethod,
    int? IcmpTtl,
    DateTime? LastScanned,
    SystemInfoDto System,
    OsInfoDto Os,
    long TotalMemoryBytes,
    int MemorySlotsUsed,
    int MemorySlotsTotal,
    IReadOnlyList<CpuInfo> Cpus,
    IReadOnlyList<MemoryModule> MemoryModules,
    IReadOnlyList<DiskInfo> Disks,
    IReadOnlyList<VolumeInfo> Volumes,
    IReadOnlyList<AdapterDto> Adapters,
    IReadOnlyList<SoftwareEntry> Software,
    IReadOnlyList<CollectorResultDto> Collectors,
    string? TargetBlock = null,
    // Schema additions (all optional so scans written by earlier versions still open). The list element types
    // are the plain model classes: they carry only settable data properties, so they serialise as-is.
    IReadOnlyList<int>? OpenPorts = null,
    IReadOnlyList<HotfixEntry>? Hotfixes = null,
    IReadOnlyList<AntivirusEntry>? Antivirus = null,
    IReadOnlyList<PrinterEntry>? Printers = null,
    IReadOnlyList<UsbDeviceEntry>? UsbDevices = null,
    IReadOnlyList<UsbStorageHistoryEntry>? UsbStorageHistory = null,
    UpdateInfo? Updates = null,
    SecurityInfo? Security = null,
    IReadOnlyList<LocalAccountEntry>? LocalAccounts = null,
    IReadOnlyList<string>? LocalAdministrators = null,
    IReadOnlyList<UserProfileEntry>? UserProfiles = null,
    IReadOnlyList<LogonSessionEntry>? LogonSessions = null,
    IReadOnlyList<ServiceEntry>? Services = null,
    IReadOnlyList<StartupEntry>? StartupItems = null,
    IReadOnlyList<ScheduledTaskEntry>? ScheduledTasks = null,
    IReadOnlyList<MonitorEntry>? Monitors = null,
    IReadOnlyList<GpuInfo>? Gpus = null,
    BatteryInfo? Battery = null,
    int? ThermalTempC = null)
{
    public static MachineDto From(Machine m) => new(
        m.Address, m.Name, m.Fqdn, m.DeviceType, m.IsVirtual, m.Vendor,
        m.IpAddresses.ToList(), m.MacAddresses.ToList(), m.Status, m.StatusDetail,
        m.DiscoveryMethod, m.IcmpTtl, m.LastScanned,
        SystemInfoDto.From(m.System), OsInfoDto.From(m.Os),
        m.TotalMemoryBytes, m.MemorySlotsUsed, m.MemorySlotsTotal,
        m.Cpus.ToList(), m.MemoryModules.ToList(), m.Disks.ToList(), m.Volumes.ToList(),
        m.Adapters.Select(AdapterDto.From).ToList(), m.Software.ToList(),
        m.Collectors.Select(c => new CollectorResultDto(c.Name, c.Status, c.Error)).ToList(),
        m.TargetBlock,
        m.OpenPorts.OrderBy(p => p).ToList(),
        m.Hotfixes.ToList(), m.Antivirus.ToList(), m.Printers.ToList(), m.UsbDevices.ToList(), m.UsbStorageHistory.ToList(),
        m.Updates, m.Security,
        m.LocalAccounts.ToList(), m.LocalAdministrators.ToList(), m.UserProfiles.ToList(), m.LogonSessions.ToList(),
        m.Services.ToList(), m.StartupItems.ToList(), m.ScheduledTasks.ToList(),
        m.Monitors.ToList(), m.Gpus.ToList(), m.Battery, m.ThermalTempC);

    public Machine ToMachine()
    {
        var m = new Machine(Address) { Name = Name, Fqdn = Fqdn, DeviceType = DeviceType, IsVirtual = IsVirtual,
            Vendor = Vendor, Status = Status, StatusDetail = StatusDetail, DiscoveryMethod = DiscoveryMethod,
            IcmpTtl = IcmpTtl, LastScanned = LastScanned, TotalMemoryBytes = TotalMemoryBytes,
            MemorySlotsUsed = MemorySlotsUsed, MemorySlotsTotal = MemorySlotsTotal, TargetBlock = TargetBlock,
            Battery = Battery, ThermalTempC = ThermalTempC };
        foreach (var ip in IpAddresses) if (!m.IpAddresses.Contains(ip)) m.IpAddresses.Add(ip);
        foreach (var mac in MacAddresses) m.MacAddresses.Add(mac);
        System.ApplyTo(m.System);
        Os.ApplyTo(m.Os);
        m.Cpus.AddRange(Cpus);
        m.MemoryModules.AddRange(MemoryModules);
        m.Disks.AddRange(Disks);
        m.Volumes.AddRange(Volumes);
        m.Software.AddRange(Software);
        foreach (var a in Adapters) m.Adapters.Add(a.ToAdapter());
        foreach (var c in Collectors) m.SetCollector(c.Name, c.Status, c.Error);
        if (OpenPorts is not null) foreach (var p in OpenPorts) m.OpenPorts.Add(p);
        if (Hotfixes is not null) m.Hotfixes.AddRange(Hotfixes);
        if (Antivirus is not null) m.Antivirus.AddRange(Antivirus);
        if (Printers is not null) m.Printers.AddRange(Printers);
        if (UsbDevices is not null) m.UsbDevices.AddRange(UsbDevices);
        if (UsbStorageHistory is not null) m.UsbStorageHistory.AddRange(UsbStorageHistory);
        if (Updates is not null) m.Updates = Updates;
        if (Security is not null) m.Security = Security;
        if (LocalAccounts is not null) m.LocalAccounts.AddRange(LocalAccounts);
        if (LocalAdministrators is not null) m.LocalAdministrators.AddRange(LocalAdministrators);
        if (UserProfiles is not null) m.UserProfiles.AddRange(UserProfiles);
        if (LogonSessions is not null) m.LogonSessions.AddRange(LogonSessions);
        if (Services is not null) m.Services.AddRange(Services);
        if (StartupItems is not null) m.StartupItems.AddRange(StartupItems);
        if (ScheduledTasks is not null) m.ScheduledTasks.AddRange(ScheduledTasks);
        if (Monitors is not null) m.Monitors.AddRange(Monitors);
        if (Gpus is not null) m.Gpus.AddRange(Gpus);
        m.RefreshCounts();
        return m;
    }
}

public sealed record SystemInfoDto(string? Manufacturer, string? Model, string? SerialNumber, string? AssetTag,
    string? ChassisType, string? Domain, bool PartOfDomain, string? LoggedOnUser, string? LastLoggedOnUser,
    string? BiosVersion, DateTime? BiosDate, string? MotherboardManufacturer, string? MotherboardModel)
{
    public string? CurrentOrLastUser =>
        !string.IsNullOrWhiteSpace(LoggedOnUser) ? LoggedOnUser
        : !string.IsNullOrWhiteSpace(LastLoggedOnUser) ? $"{LastLoggedOnUser} (last)"
        : null;

    public static SystemInfoDto From(SystemInfo s) => new(s.Manufacturer, s.Model, s.SerialNumber, s.AssetTag,
        s.ChassisType, s.Domain, s.PartOfDomain, s.LoggedOnUser, s.LastLoggedOnUser, s.BiosVersion, s.BiosDate,
        s.MotherboardManufacturer, s.MotherboardModel);

    public void ApplyTo(SystemInfo s)
    {
        s.Manufacturer = Manufacturer; s.Model = Model; s.SerialNumber = SerialNumber; s.AssetTag = AssetTag;
        s.ChassisType = ChassisType; s.Domain = Domain; s.PartOfDomain = PartOfDomain; s.LoggedOnUser = LoggedOnUser;
        s.LastLoggedOnUser = LastLoggedOnUser;
        s.BiosVersion = BiosVersion; s.BiosDate = BiosDate; s.MotherboardManufacturer = MotherboardManufacturer;
        s.MotherboardModel = MotherboardModel;
    }
}

public sealed record OsInfoDto(string? Caption, string? Version, string? Build, string? Architecture,
    DateTime? InstallDate, DateTime? LastBoot, string? RegisteredOwner, string? RegisteredOrganization)
{
    public static OsInfoDto From(OsInfo o) => new(o.Caption, o.Version, o.Build, o.Architecture,
        o.InstallDate, o.LastBoot, o.RegisteredOwner, o.RegisteredOrganization);

    public void ApplyTo(OsInfo o)
    {
        o.Caption = Caption; o.Version = Version; o.Build = Build; o.Architecture = Architecture;
        o.InstallDate = InstallDate; o.LastBoot = LastBoot; o.RegisteredOwner = RegisteredOwner;
        o.RegisteredOrganization = RegisteredOrganization;
    }
}

public sealed record AdapterDto(string? Name, string? Mac, long SpeedBps, IReadOnlyList<string> IpAddresses,
    string? SubnetMask, string? Gateway, IReadOnlyList<string> DnsServers, bool DhcpEnabled)
{
    public static AdapterDto From(AdapterInfo a) => new(a.Name, a.Mac, a.SpeedBps, a.IpAddresses.ToList(),
        a.SubnetMask, a.Gateway, a.DnsServers.ToList(), a.DhcpEnabled);

    public AdapterInfo ToAdapter()
    {
        var a = new AdapterInfo { Name = Name, Mac = Mac, SpeedBps = SpeedBps, SubnetMask = SubnetMask,
            Gateway = Gateway, DhcpEnabled = DhcpEnabled };
        a.IpAddresses.AddRange(IpAddresses);
        a.DnsServers.AddRange(DnsServers);
        return a;
    }
}

public sealed record CollectorResultDto(string Name, CollectorStatus Status, string? Error);
