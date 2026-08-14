using Marco.Core.Inventory;
using Marco.Core.Wmi;

namespace Marco.Inventory.Registry;

public sealed class RemoteRegistryFactory : IRemoteRegistryFactory
{
    public IRemoteRegistry Create(string host, WmiCredential? credential)
        => new RemoteRegistry(host, credential);
}
