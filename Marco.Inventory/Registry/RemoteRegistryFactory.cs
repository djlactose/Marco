using Marco.Core.Inventory;
using Marco.Core.Wmi;

namespace Marco.Inventory.Registry;

public sealed class RemoteRegistryFactory : IRemoteRegistryFactory
{
    /// <summary>SMB/OpenRemoteBaseKey path first (fast, gives last-write times), with a StdRegProv-over-WMI
    /// fallback for when SMB or the Remote Registry service isn't reachable.</summary>
    public IRemoteRegistry Create(string host, WmiCredential? credential, IWmiSession wmiSession)
        => new FallbackRemoteRegistry(new RemoteRegistry(host, credential), new StdRegProvRegistry(wmiSession));
}
