using Marco.Core.Compliance;
using Marco.Export;

namespace Marco.Report;

/// <summary>Branding for a generated report — sourced from the active client profile, with a neutral default
/// for no-client scans.</summary>
public sealed record ReportBranding(
    string? CompanyName = null,
    string? LogoPath = null,
    string? AccentColor = null,
    string? PreparedBy = null)
{
    public static ReportBranding Neutral { get; } = new();
}

/// <summary>Everything the HTML report builder needs. Compliance/lifecycle come from the machines themselves
/// (each Machine carries its own results), so the report degrades gracefully when they were never evaluated.</summary>
public sealed record ReportInput(
    ScanDocument Document,
    FleetSummary? Fleet,
    ReportBranding Branding,
    string? ClientName,
    DateTime GeneratedAt,
    string EolTableUpdated,
    bool IncludeAppendix = true);
