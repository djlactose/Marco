using System.Text.Json.Serialization;

namespace Marco.Core.Lifecycle;

public enum OsSupportStatus { Unknown = 0, Supported, EndingSoon, EndOfLife }

/// <summary>
/// A machine's lifecycle facts: OS end-of-support (from the bundled EOL table) and hardware age (from the BIOS
/// release date — a firmware update makes old hardware look newer, hence "approx" everywhere it is shown).
/// The Warranty fields are reserved for a later vendor-API integration and stay null in v1.
/// </summary>
public sealed record LifecycleInfo(
    string? OsReleaseLabel,
    DateTime? OsEndOfSupport,
    DateTime? OsExtendedEnd,
    OsSupportStatus OsSupport,
    double? HardwareAgeYears,
    DateTime? WarrantyEnd = null,
    string? WarrantyStatus = null)
{
    /// <summary>"EOL 2025-10" / "ends 2026-11" / "OK" for the grid column; null keeps the cell empty.</summary>
    [JsonIgnore]
    public string? Display => OsSupport switch
    {
        OsSupportStatus.EndOfLife => OsEndOfSupport is { } d ? $"EOL {d:yyyy-MM}" : "EOL",
        OsSupportStatus.EndingSoon => OsEndOfSupport is { } d ? $"ends {d:yyyy-MM}" : "ending soon",
        OsSupportStatus.Supported => "OK",
        _ => null,
    };

    [JsonIgnore]
    public string? SupportSummary => OsSupport switch
    {
        OsSupportStatus.EndOfLife => $"{OsReleaseLabel}: support ENDED {OsEndOfSupport:yyyy-MM-dd}"
            + (OsExtendedEnd is { } e && e > OsEndOfSupport ? $" (extended/ESU until {e:yyyy-MM-dd})" : ""),
        OsSupportStatus.EndingSoon => $"{OsReleaseLabel}: support ends {OsEndOfSupport:yyyy-MM-dd}",
        OsSupportStatus.Supported => $"{OsReleaseLabel}: supported until {OsEndOfSupport:yyyy-MM-dd}",
        _ => null,
    };

    [JsonIgnore]
    public string? HardwareAgeSummary => HardwareAgeYears is { } y
        ? $"Hardware ≈ {y:0.#} years old (firmware date)" : null;
}
