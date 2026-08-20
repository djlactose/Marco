namespace Marco.Core.Model;

/// <summary>
/// Shared hardware-identity normalization used by scan diffing (Marco.Export) and the asset baseline: which
/// serials are real, which MACs are burned-in vs locally administered (randomized/virtual), and a canonical MAC
/// form. Lives in Core so both consumers agree on what identifies a machine.
/// </summary>
public static class HardwareIdentity
{
    private static readonly HashSet<string> BogusSerials = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "0", "1", "none", "invalid", "n/a", "na", "unknown", "default string",
        "to be filled by o.e.m.", "to be filled by oem", "system serial number",
        "chassis serial number", "not specified", "not available", "no serial", "empty",
        "0123456789", "1234567890", "123456789",
    };

    /// <summary>Null when the value is an OEM placeholder rather than a real serial.</summary>
    public static string? NormalizeSerial(string? serial)
    {
        var s = serial?.Trim();
        return s is null || BogusSerials.Contains(s) ? null : s;
    }

    /// <summary>Uppercase hex digits only ("3C5282AABBCC"); 12 chars when it was a valid MAC.</summary>
    public static string NormalizeMac(string mac)
        => new(mac.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Second nibble 2/6/A/E — locally administered, i.e. randomized or virtual, not burned in.</summary>
    public static bool IsLocallyAdministered(string mac)
    {
        var n = NormalizeMac(mac);
        return n.Length >= 2 && n[1] is '2' or '6' or 'A' or 'E';
    }
}
