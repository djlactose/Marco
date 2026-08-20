using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;

namespace Marco.Inventory.Collectors;

/// <summary>
/// Physical disks and fixed volumes. Win32_DiskDrive / Win32_LogicalDisk are the primary (and universal) sources;
/// on Windows 8 / Server 2012 and later the Storage Management classes add what an admin actually wants to know —
/// SSD vs HDD, NVMe vs SATA vs USB, health, firmware, temperature and wear — and root\WMI's SMART predict-failure
/// flag covers older drivers. All of the enrichment is optional: it fills in when present and is skipped without
/// comment where the namespace, provider or permission is missing.
/// </summary>
public sealed class StorageCollector : IInventoryCollector
{
    public string Name => "Storage";

    public async Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct)
    {
        var session = context.Wmi;
        var disks = await session.QueryAsync(WmiQueryHelpers.CimV2,
            "SELECT Index, Model, Size, MediaType, SerialNumber, Status, InterfaceType, FirmwareRevision, Partitions, PNPDeviceID FROM Win32_DiskDrive", ct);
        var diskList = new List<DiskInfo>();
        var byIndex = new Dictionary<int, DiskInfo>();
        var pnpByDisk = new Dictionary<DiskInfo, string>();
        foreach (var d in disks)
        {
            var info = new DiskInfo
            {
                Model = d.GetString("Model")?.Trim(),
                SizeBytes = (long)(d.GetULong("Size") ?? 0),
                MediaType = d.GetString("MediaType"),
                Serial = d.GetString("SerialNumber")?.Trim(),
                SmartStatus = d.GetString("Status"),
                BusType = d.GetString("InterfaceType"),
                Firmware = d.GetString("FirmwareRevision")?.Trim(),
                Partitions = d.GetInt("Partitions"),
            };
            diskList.Add(info);
            if (d.GetInt("Index") is { } idx) byIndex[idx] = info;
            if (d.GetString("PNPDeviceID") is { } pnp) pnpByDisk[info] = pnp;
        }
        // Assigned before enrichment: the later sections only mutate the DiskInfo objects, and the UI
        // won't look until NotifyInventoryUpdated fires after the whole host completes.
        machine.Disks = diskList;

        // Logical volumes: DriveType 3 = local fixed disk.
        var vols = await session.QueryAsync(WmiQueryHelpers.CimV2,
            "SELECT DeviceID, FileSystem, Size, FreeSpace, VolumeName FROM Win32_LogicalDisk WHERE DriveType = 3", ct);
        var volumeList = new List<VolumeInfo>();
        foreach (var v in vols)
        {
            volumeList.Add(new VolumeInfo
            {
                Letter = v.GetString("DeviceID"),
                FileSystem = v.GetString("FileSystem"),
                CapacityBytes = (long)(v.GetULong("Size") ?? 0),
                FreeBytes = (long)(v.GetULong("FreeSpace") ?? 0),
                Label = v.GetString("VolumeName")?.Trim(),
            });
        }
        machine.Volumes = volumeList;
        machine.RefreshCounts();

        // --- optional enrichment from here on ---

        // Partition style per disk (GPT partitions report "GPT: …" as their Type).
        ct.ThrowIfCancellationRequested();
        var parts = await session.QueryOptionalAsync("SELECT DiskIndex, Type FROM Win32_DiskPartition", ct);
        foreach (var p in parts)
        {
            if (p.GetInt("DiskIndex") is not { } di || !byIndex.TryGetValue(di, out var disk)) continue;
            var style = PartitionStyle(p.GetString("Type"));
            if (style is not null && disk.PartitionStyle != "GPT") disk.PartitionStyle = style;
        }

        // Storage Management (Win8/2012+): media type, bus, health, firmware.
        ct.ThrowIfCancellationRequested();
        var physical = await session.QueryOptionalAsync(
            "SELECT DeviceId, SerialNumber, MediaType, BusType, HealthStatus, FirmwareVersion, Size FROM MSFT_PhysicalDisk",
            ct, WmiQueryHelpers.Storage);
        foreach (var p in physical)
        {
            var disk = MatchPhysicalDisk(p, byIndex, machine.Disks);
            if (disk is null) continue;
            if (DescribeMediaType(p.GetInt("MediaType")) is { } media) disk.MediaType = media;
            if (DescribeBusType(p.GetInt("BusType")) is { } bus) disk.BusType = bus;
            disk.HealthStatus = DescribeHealth(p.GetInt("HealthStatus"));
            disk.Firmware = p.GetString("FirmwareVersion")?.Trim() ?? disk.Firmware;
        }

        // Reliability counters (Win10/2016+): temperature, wear, power-on hours.
        ct.ThrowIfCancellationRequested();
        var counters = await session.QueryOptionalAsync(
            "SELECT DeviceId, Temperature, Wear, PowerOnHours FROM MSFT_StorageReliabilityCounter", ct, WmiQueryHelpers.Storage);
        foreach (var c in counters)
        {
            if (!int.TryParse(c.GetString("DeviceId"), out var id) || !byIndex.TryGetValue(id, out var disk)) continue;
            if (c.GetInt("Temperature") is { } t and > 0 and < 150) disk.TempC = t;
            if (c.GetInt("Wear") is { } w and >= 0 and <= 100) disk.WearPercent = w;
            if (c.GetLong("PowerOnHours") is { } h and > 0) disk.PowerOnHours = h;
        }

        // SMART predict-failure and raw temperature via the storage driver (root\WMI), keyed by PnP instance.
        ct.ThrowIfCancellationRequested();
        var predict = await session.QueryOptionalAsync(
            "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus", ct, WmiQueryHelpers.Wmi);
        foreach (var p in predict)
        {
            var disk = MatchByInstance(p.GetString("InstanceName"), pnpByDisk);
            if (disk is null) continue;
            var fail = p.GetBool("PredictFailure");
            disk.SmartPredictFailure = fail;
            if (fail == true) disk.SmartStatus = "Pred Fail";
        }
        if (machine.Disks.Any(d => d.TempC is null))
        {
            var smart = await session.QueryOptionalAsync(
                "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData", ct, WmiQueryHelpers.Wmi);
            foreach (var s in smart)
            {
                var disk = MatchByInstance(s.GetString("InstanceName"), pnpByDisk);
                if (disk is null || disk.TempC is not null) continue;
                disk.TempC = SmartTemperature(s["VendorSpecific"] as byte[]);
            }
        }
    }

    // --- pure helpers (unit-tested) ---

    public static string? DescribeMediaType(int? mediaType) => mediaType switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => null, // 0 = unspecified: keep whatever Win32_DiskDrive said
    };

    public static string? DescribeBusType(int? busType) => busType switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 5 => "SSA", 6 => "Fibre Channel", 7 => "USB",
        8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual",
        15 => "File-backed virtual", 16 => "Storage Spaces", 17 => "NVMe", 18 => "SCM", _ => null,
    };

    public static string? DescribeHealth(int? health) => health switch
    {
        0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", 5 => "Unknown", _ => null,
    };

    /// <summary>Win32_DiskPartition.Type: "GPT: System" / "GPT: Basic Data" vs "Installable File System" (MBR).</summary>
    public static string? PartitionStyle(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        return type.StartsWith("GPT", StringComparison.OrdinalIgnoreCase) ? "GPT" : "MBR";
    }

    /// <summary>ATA SMART attribute block (MSStorageDriver_FailurePredictData.VendorSpecific): 12-byte attribute
    /// records from offset 2 — id, flags(2), value, worst, raw(6), reserved. Temperature is attribute 194
    /// (fallback 190, "airflow"); the current temperature is the first raw byte.</summary>
    public static int? SmartTemperature(byte[]? vendorSpecific)
    {
        if (vendorSpecific is null || vendorSpecific.Length < 14) return null;
        int? airflow = null;
        for (int off = 2; off + 12 <= vendorSpecific.Length; off += 12)
        {
            int id = vendorSpecific[off];
            if (id == 0) continue;
            int raw = vendorSpecific[off + 5];
            if (id == 194 && raw is > 0 and < 150) return raw;
            if (id == 190 && raw is > 0 and < 150) airflow = raw;
        }
        return airflow;
    }

    private static DiskInfo? MatchPhysicalDisk(WmiObject p, Dictionary<int, DiskInfo> byIndex, List<DiskInfo> all)
    {
        if (int.TryParse(p.GetString("DeviceId"), out var id) && byIndex.TryGetValue(id, out var byId)) return byId;
        var serial = p.GetString("SerialNumber")?.Trim();
        if (!string.IsNullOrEmpty(serial))
        {
            var bySerial = all.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase));
            if (bySerial is not null) return bySerial;
        }
        var size = (long)(p.GetULong("Size") ?? 0);
        return size > 0 ? all.FirstOrDefault(d => d.SizeBytes == size) : null;
    }

    /// <summary>root\WMI InstanceName is the PnP device ID with a "_0" suffix.</summary>
    private static DiskInfo? MatchByInstance(string? instanceName, Dictionary<DiskInfo, string> pnpByDisk)
    {
        if (string.IsNullOrWhiteSpace(instanceName)) return null;
        foreach (var (disk, pnp) in pnpByDisk)
            if (instanceName.StartsWith(pnp, StringComparison.OrdinalIgnoreCase)) return disk;
        return null;
    }
}
