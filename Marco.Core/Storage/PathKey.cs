using System.Security.Cryptography;
using System.Text;

namespace Marco.Core.Storage;

/// <summary>Stable, kernel-object-safe short key for a filesystem path, used to name cross-process primitives
/// (mutexes) that guard a specific file or directory. <c>string.GetHashCode</c> is randomised per process and
/// therefore useless for this; a truncated SHA-256 of the normalised path is not.</summary>
public static class PathKey
{
    public static string For(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { full = path; }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }
}
