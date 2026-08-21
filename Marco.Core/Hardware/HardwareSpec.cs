using System.Text.RegularExpressions;

namespace Marco.Core.Hardware;

/// <summary>
/// One vendor spec-sheet entry: what a model line supports, as opposed to what a particular unit's firmware
/// reports. Matched by vendor plus wildcard patterns; the first matching entry in table order wins, so list
/// specific entries (a form factor, a generation) before general ones.
/// </summary>
/// <param name="Manufacturer">Vendor key — "dell", "hp", "lenovo" … (see <see cref="HardwareSpecTable.VendorKey"/>).</param>
/// <param name="Model">Wildcard pattern ("OptiPlex 7090*", "*EliteDesk 800 G6*SFF*"; "|" separates alternatives),
/// matched case-insensitively against both the model string and the product version (Lenovo's friendly name).</param>
/// <param name="Chassis">Optional pattern on the decoded chassis type ("Low Profile Desktop|Desktop") — disambiguates
/// form factors when the model string does not carry one (older Dell OptiPlex).</param>
/// <param name="Board">Optional pattern on the motherboard model, for white-box desktops keyed by board.</param>
/// <param name="ModuleFormFactor">Optional: the host's installed modules must report this form factor ("SODIMM" /
/// "DIMM"). Separates Micro from Tower/SFF where the vendor uses one model string for all three (Dell OptiPlex
/// before 2023); a host that does not report a form factor never matches such an entry.</param>
/// <param name="Name">Friendly name for display ("Dell OptiPlex 7090 SFF").</param>
/// <param name="MaxMemoryGb">Maximum memory per the spec sheet.</param>
/// <param name="MemorySlots">Physical module slots; 0 for soldered-only designs.</param>
/// <param name="MemoryType">"DDR4", "DDR5", "LPDDR5" …</param>
/// <param name="MemoryFormFactor">"DIMM", "SODIMM", "Soldered".</param>
/// <param name="DriveBays">Internal 2.5"/3.5" drive bays (SATA/SAS), as a count.</param>
/// <param name="M2Slots">M.2 slots usable for storage (not the WLAN slot).</param>
/// <param name="BayNotes">Free text when the bay arrangement is configuration-dependent.</param>
/// <param name="Source">Where the figures came from (vendor manual, spec sheet) — shown on hover/in notes.</param>
public sealed record HardwareSpec(
    string Manufacturer,
    string Model,
    string? Chassis = null,
    string? Board = null,
    string? ModuleFormFactor = null,
    string? Name = null,
    int? MaxMemoryGb = null,
    int? MemorySlots = null,
    string? MemoryType = null,
    string? MemoryFormFactor = null,
    int? DriveBays = null,
    int? M2Slots = null,
    string? BayNotes = null,
    string? Source = null)
{
    public long? MaxMemoryBytes => MaxMemoryGb is { } gb && gb > 0 ? gb * 1024L * 1024 * 1024 : null;

    public bool HasMemoryFacts => MaxMemoryGb is not null || MemorySlots is not null || MemoryType is not null;
    public bool HasBayFacts => DriveBays is not null || M2Slots is not null;

    /// <summary>"DDR4 DIMM, 4 slots, max 128 GB" — the memory half of the spec-sheet line.</summary>
    public string? MemoryDisplay
    {
        get
        {
            var parts = new List<string>();
            bool soldered = MemorySlots == 0 || string.Equals(MemoryFormFactor, "Soldered", StringComparison.OrdinalIgnoreCase);
            var type = string.Join(" ", new[] { MemoryType, soldered ? "soldered" : MemoryFormFactor }.Where(p => !string.IsNullOrWhiteSpace(p)));
            if (type.Length > 0) parts.Add(type);
            if (MemorySlots is { } s && s > 0) parts.Add($"{s} slot{(s == 1 ? "" : "s")}");
            if (MaxMemoryGb is { } gb && gb > 0) parts.Add($"max {gb} GB");
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
    }

    /// <summary>"1× 3.5″/2.5″ bay, 2× M.2" — the storage half of the spec-sheet line. Falls back to the free-text
    /// note alone for models whose bays are purely configuration-dependent (servers).</summary>
    public string? BaysDisplay
    {
        get
        {
            var parts = new List<string>();
            if (DriveBays is { } b) parts.Add(b == 0 ? "no drive bays" : $"{b}× 2.5″/3.5″ bay{(b == 1 ? "" : "s")}");
            if (M2Slots is { } m) parts.Add(m == 0 ? "no M.2" : $"{m}× M.2");
            if (parts.Count == 0) return string.IsNullOrWhiteSpace(BayNotes) ? null : BayNotes;
            var s = string.Join(", ", parts);
            return string.IsNullOrWhiteSpace(BayNotes) ? s : $"{s} ({BayNotes})";
        }
    }

    /// <summary>Total internal storage positions, when any bay fact is known.</summary>
    public int? TotalStoragePositions => HasBayFacts ? (DriveBays ?? 0) + (M2Slots ?? 0) : null;

    private Regex? _modelRx, _chassisRx, _boardRx;

    internal bool Matches(string vendorKey, string? model, string? productVersion, string? chassis, string? board, string? hostModuleFormFactor)
    {
        if (!string.Equals(Manufacturer, vendorKey, StringComparison.OrdinalIgnoreCase)) return false;
        if (ModuleFormFactor is not null
            && !string.Equals(ModuleFormFactor, hostModuleFormFactor, StringComparison.OrdinalIgnoreCase)) return false;
        _modelRx ??= HardwareSpecTable.Glob(Model);
        bool modelHit = (model is not null && _modelRx.IsMatch(model))
                        || (productVersion is not null && _modelRx.IsMatch(productVersion));
        if (!modelHit) return false;
        if (Chassis is not null)
        {
            _chassisRx ??= HardwareSpecTable.Glob(Chassis);
            if (chassis is null || !_chassisRx.IsMatch(chassis)) return false;
        }
        if (Board is not null)
        {
            _boardRx ??= HardwareSpecTable.Glob(Board);
            if (board is null || !_boardRx.IsMatch(board)) return false;
        }
        return true;
    }
}

public sealed record HardwareSpecTableData(int SchemaVersion, string Updated, IReadOnlyList<HardwareSpec> Specs);
