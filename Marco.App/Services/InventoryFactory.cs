using Marco.Core.Inventory;
using Marco.Credentials;
using Marco.Inventory;
using Marco.Inventory.Registry;
using Marco.Inventory.Wmi;

namespace Marco.App.Services;

/// <summary>Wires the concrete WMI session factory, remote-registry factory, and Phase 2 collector set into an
/// <see cref="InventoryRunner"/>.</summary>
public static class InventoryFactory
{
    public static InventoryRunner CreateRunner() => new(
        new SystemManagementWmiSessionFactory(timeoutSeconds: 20),
        new RemoteRegistryFactory(),
        InventoryCollectors.Phase2());

    /// <summary>A verifier that tests credentials via the same WMI connect path inventory uses (shorter timeout
    /// for snappier feedback).</summary>
    public static CredentialVerifier CreateVerifier() =>
        new(new SystemManagementWmiSessionFactory(timeoutSeconds: 15));
}
