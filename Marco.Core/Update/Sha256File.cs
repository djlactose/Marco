using System.Security.Cryptography;

namespace Marco.Core.Update;

/// <summary>SHA-256 helpers for the update pipeline: parse the published "&lt;HEX&gt;  Marco.exe" checksum text
/// (the format publish.ps1/sign.ps1 emit) and verify a downloaded file against it (streaming, never buffering).</summary>
public static class Sha256File
{
    /// <summary>Extracts the hash from checksum-file text. Returns lowercase hex, or null when the first token
    /// isn't a 64-char hex string.</summary>
    public static string? ParseChecksumText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var token = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        return token.ToLowerInvariant();
    }

    public static string ComputeLowerHex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static bool Verify(string filePath, string? expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return false;
        try
        {
            return string.Equals(ComputeLowerHex(filePath), expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // missing/locked file counts as a failed verification
        }
    }
}
