namespace Marco.Core.Clients;

/// <summary>
/// One client engagement: its scan targets, report branding, and notes. Deliberately carries NO credentials —
/// credential sets live in the DPAPI store and reference a profile by <see cref="Id"/>, so this record (and the
/// shared .marcoclient.json made from it) is safe to hand to a teammate. The Id stays stable across
/// export/import so re-shares update cleanly while each operator's credential associations remain local.
/// </summary>
public sealed record ClientProfile(
    string Id,
    string Name,
    string TargetsText,
    string? CompanyName = null,
    string? LogoPath = null,
    string? AccentColor = null,
    string? PreparedBy = null,
    string? Notes = null,
    DateTime CreatedUtc = default)
{
    public static ClientProfile New(string name, string targetsText = "")
        => new(Guid.NewGuid().ToString("n"), name, targetsText, CreatedUtc: DateTime.UtcNow);
}

public sealed record ClientProfileList(int SchemaVersion, IReadOnlyList<ClientProfile> Profiles)
{
    public const int CurrentSchemaVersion = 1;
}
