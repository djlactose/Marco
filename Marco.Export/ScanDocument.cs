using Marco.Core.Model;

namespace Marco.Export;

/// <summary>Scan-level metadata embedded in every export, for provenance.</summary>
public sealed record ScanMetadata(
    DateTime Timestamp,
    string? Operator,
    IReadOnlyList<string> RangesScanned,
    int TotalTargets,
    int AliveCount,
    string Tool = "Marco",
    string Version = "1.0");

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
    IReadOnlyList<CollectorResultDto> Collectors)
{
    public static MachineDto From(Machine m) => new(
        m.Address, m.Name, m.Fqdn, m.DeviceType, m.IsVirtual, m.Vendor,
        m.IpAddresses.ToList(), m.MacAddresses.ToList(), m.Status, m.StatusDetail,
        m.DiscoveryMethod, m.IcmpTtl, m.LastScanned,
        SystemInfoDto.From(m.System), OsInfoDto.From(m.Os),
        m.TotalMemoryBytes, m.MemorySlotsUsed, m.MemorySlotsTotal,
        m.Cpus.ToList(), m.MemoryModules.ToList(), m.Disks.ToList(), m.Volumes.ToList(),
        m.Adapters.Select(AdapterDto.From).ToList(), m.Software.ToList(),
        m.Collectors.Select(c => new CollectorResultDto(c.Name, c.Status, c.Error)).ToList());

    public Machine ToMachine()
    {
        var m = new Machine(Address) { Name = Name, Fqdn = Fqdn, DeviceType = DeviceType, IsVirtual = IsVirtual,
            Vendor = Vendor, Status = Status, StatusDetail = StatusDetail, DiscoveryMethod = DiscoveryMethod,
            IcmpTtl = IcmpTtl, LastScanned = LastScanned, TotalMemoryBytes = TotalMemoryBytes,
            MemorySlotsUsed = MemorySlotsUsed, MemorySlotsTotal = MemorySlotsTotal };
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
