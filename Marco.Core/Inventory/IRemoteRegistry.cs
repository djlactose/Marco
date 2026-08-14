using Marco.Core.Wmi;

namespace Marco.Core.Inventory;

public enum RegistryRoot { LocalMachine, Users }

/// <summary>Creates a per-host <see cref="IRemoteRegistry"/> over the given credentials.</summary>
public interface IRemoteRegistryFactory
{
    IRemoteRegistry Create(string host, WmiCredential? credential);
}

/// <summary>Raw contents of one registry subkey: its name, values, and last-write time (null when the access path
/// can't provide it, e.g. the StdRegProv fallback). The software parser turns these into deduplicated entries.</summary>
public sealed record RegistryKeyData(
    string SubKeyName,
    IReadOnlyDictionary<string, object?> Values,
    DateTime? LastWriteTimeUtc);

/// <summary>
/// Remote registry reader abstraction. The real implementation prefers a WNetAddConnection2 SMB session plus
/// OpenRemoteBaseKey (bulk, fast) and falls back to StdRegProv over WMI. Abstracted so the software collector's
/// filtering/dedup/install-date logic is unit-testable without a live host.
/// </summary>
public interface IRemoteRegistry : IDisposable
{
    /// <summary>Whether the last-write-time is available on this access path (true for OpenRemoteBaseKey,
    /// false for StdRegProv). The install-date resolver uses this to decide whether to trust key timestamps.</summary>
    bool SupportsLastWriteTime { get; }

    /// <summary>Enumerate the immediate subkeys of a path, returning each subkey's values. <paramref name="valueNames"/>
    /// limits which values are read (bandwidth). Returns empty if the base path is absent.</summary>
    IReadOnlyList<RegistryKeyData> EnumerateSubkeys(
        RegistryRoot root, string path, IReadOnlyCollection<string> valueNames);

    /// <summary>Top-level subkey names of a path (used to enumerate loaded user SIDs under HKU).</summary>
    IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string path);

    /// <summary>Read named values from a single key (not its subkeys). Returns an empty map if the key is absent.</summary>
    IReadOnlyDictionary<string, object?> GetValues(RegistryRoot root, string path, IReadOnlyCollection<string> valueNames);
}
