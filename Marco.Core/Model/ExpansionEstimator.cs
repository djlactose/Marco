namespace Marco.Core.Model;

/// <summary>
/// Best-effort "is there room for another drive?" estimate. Windows exposes no notion of physical drive bays, so
/// this combines what it does expose — chassis type, board expansion slots (Win32_SystemSlot, which includes M.2
/// on boards that report it) and the installed disk count — into one sentence. Every non-null result starts with
/// "Estimate:" so it can never be read as a measured fact; when nothing meaningful is known it returns null so the
/// UI and report show nothing rather than a guess.
/// </summary>
public static class ExpansionEstimator
{
    private static readonly string[] Portable =
        { "Portable", "Laptop", "Notebook", "Sub Notebook", "Handheld", "Tablet", "Convertible", "Detachable" };
    private static readonly string[] Compact =
        { "All in One", "Space-saving", "Low Profile Desktop", "Sealed-case PC", "Lunch Box" };
    private static readonly string[] Tower =
        { "Desktop", "Mini Tower", "Tower", "Main System Chassis", "Pizza Box" };
    private static readonly string[] Server =
        { "Rack Mount Chassis", "Blade", "Blade Enclosure", "RAID Chassis", "Multi-system Chassis", "Expansion Chassis" };

    /// <summary>Disks that occupy a bay or slot — everything except USB-attached (external drives, card readers).</summary>
    public static int CountInternal(IEnumerable<DiskInfo> disks)
        => disks.Count(d => !string.Equals(d.BusType, "USB", StringComparison.OrdinalIgnoreCase));

    public static string? Describe(bool isVirtual, string? chassisType,
        int? slotsFree, int? slotsTotal, string? freeSlotNames, int internalDiskCount)
    {
        if (isVirtual) return null;

        var disks = internalDiskCount == 1 ? "1 internal disk" : $"{internalDiskCount} internal disks";
        var slots = SlotClause(slotsFree, slotsTotal, freeSlotNames);

        if (Matches(chassisType, Portable))
            return $"Estimate: laptop/portable chassis, {disks} — internal drive expansion unlikely.";
        if (Matches(chassisType, Compact))
            return $"Estimate: compact chassis, {disks}{slots} — limited room for additional drives.";
        if (Matches(chassisType, Tower))
            return $"Estimate: tower/desktop chassis, {disks}{slots} — likely room for additional drives.";
        if (Matches(chassisType, Server))
            return $"Estimate: server chassis, {disks}{slots} — drive bays are not reported by WMI; check the vendor bay configuration.";

        // Unknown / Other / unreported chassis: only worth saying something if the slot probe returned data.
        return slots.Length == 0 ? null : $"Estimate: {disks}{slots}.";
    }

    private static string SlotClause(int? free, int? total, string? names)
    {
        if (total is not { } t || t <= 0) return "";
        var f = free ?? 0;
        var clause = $", {f} of {t} expansion slot{(t == 1 ? "" : "s")} free";
        if (f > 0 && !string.IsNullOrWhiteSpace(names)) clause += $" ({names})";
        return clause;
    }

    private static bool Matches(string? chassis, string[] names) =>
        chassis is not null && names.Any(n => string.Equals(n, chassis, StringComparison.OrdinalIgnoreCase));
}
