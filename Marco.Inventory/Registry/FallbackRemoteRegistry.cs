using Marco.Core.Inventory;

namespace Marco.Inventory.Registry;

/// <summary>
/// Tries the fast SMB/OpenRemoteBaseKey path first and, the moment it fails (SMB blocked, or the Remote Registry
/// service is off — common across a VPN), switches to the WMI StdRegProv fallback for this and all later reads.
/// This is why installed-software collection works even when only the WMI/DCOM channel is reachable.
/// </summary>
public sealed class FallbackRemoteRegistry : IRemoteRegistry
{
    private readonly IRemoteRegistry _primary;
    private readonly IRemoteRegistry _fallback;
    private bool _primaryFailed;

    public FallbackRemoteRegistry(IRemoteRegistry primary, IRemoteRegistry fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    /// <summary>Reflects whichever path is actually in use — false once we've fallen back to StdRegProv.</summary>
    public bool SupportsLastWriteTime => !_primaryFailed && _primary.SupportsLastWriteTime;

    private T Run<T>(Func<IRemoteRegistry, T> op)
    {
        if (!_primaryFailed)
        {
            try { return op(_primary); }
            catch { _primaryFailed = true; } // fall through to StdRegProv for this and all subsequent operations
        }
        return op(_fallback);
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string path)
        => Run(r => r.GetSubKeyNames(root, path));

    public IReadOnlyList<RegistryKeyData> EnumerateSubkeys(RegistryRoot root, string path, IReadOnlyCollection<string> valueNames)
        => Run(r => r.EnumerateSubkeys(root, path, valueNames));

    public IReadOnlyDictionary<string, object?> GetValues(RegistryRoot root, string path, IReadOnlyCollection<string> valueNames)
        => Run(r => r.GetValues(root, path, valueNames));

    public void Dispose()
    {
        _primary.Dispose();
        _fallback.Dispose();
    }
}
