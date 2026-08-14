using Marco.Core.Inventory;
using Marco.Credentials;
using Marco.Inventory;
using Marco.Inventory.Linux;
using Marco.Inventory.Registry;
using Marco.Inventory.Ssh;
using Marco.Inventory.Wmi;

namespace Marco.App.Services;

/// <summary>Wires the concrete WMI session factory, remote-registry factory, and Phase 2 collector set into an
/// <see cref="InventoryRunner"/>, plus the SSH-based Linux runner.</summary>
public static class InventoryFactory
{
    public static InventoryRunner CreateRunner() => new(
        new SystemManagementWmiSessionFactory(timeoutSeconds: 20),
        new RemoteRegistryFactory(),
        InventoryCollectors.Phase2());

    /// <summary>SSH-based inventory for Linux/Unix hosts.</summary>
    public static LinuxInventoryRunner CreateLinuxRunner() => new(new SshNetSessionFactory());

    /// <summary>A verifier that tests credentials via the same WMI connect path inventory uses (shorter timeout
    /// for snappier feedback).</summary>
    public static CredentialVerifier CreateVerifier() =>
        new(new SystemManagementWmiSessionFactory(timeoutSeconds: 15));
}
