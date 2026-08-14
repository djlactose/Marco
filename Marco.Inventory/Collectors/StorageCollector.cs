using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;

namespace Marco.Inventory.Collectors;

public sealed class StorageCollector : IInventoryCollector
{
    public string Name => "Storage";

    public async Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct)
    {
        var session = context.Wmi;
        var disks = await session.QueryAsync(WmiQueryHelpers.CimV2,
            "SELECT Model, Size, MediaType, SerialNumber, Status FROM Win32_DiskDrive", ct);
        machine.Disks.Clear();
        foreach (var d in disks)
        {
            machine.Disks.Add(new DiskInfo
            {
                Model = d.GetString("Model")?.Trim(),
                SizeBytes = (long)(d.GetULong("Size") ?? 0),
                MediaType = d.GetString("MediaType"),
                Serial = d.GetString("SerialNumber")?.Trim(),
                SmartStatus = d.GetString("Status"),
            });
        }

        // Logical volumes: DriveType 3 = local fixed disk.
        var vols = await session.QueryAsync(WmiQueryHelpers.CimV2,
            "SELECT DeviceID, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3", ct);
        machine.Volumes.Clear();
        foreach (var v in vols)
        {
            machine.Volumes.Add(new VolumeInfo
            {
                Letter = v.GetString("DeviceID"),
                FileSystem = v.GetString("FileSystem"),
                CapacityBytes = (long)(v.GetULong("Size") ?? 0),
                FreeBytes = (long)(v.GetULong("FreeSpace") ?? 0),
            });
        }
        machine.RefreshCounts();
    }
}
