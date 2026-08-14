using Marco.Core.Scanning;
using Marco.Discovery;

namespace Marco.App.Services;

/// <summary>Wires the concrete discovery services into a <see cref="ScanController"/>. Kept in the App layer so
/// Discovery stays free of composition concerns.</summary>
public static class DiscoveryFactory
{
    private static readonly OuiLookup Oui = OuiLookup.LoadEmbedded();

    public static ScanController CreateController() => new(
        new LivenessProbe(),
        new NameResolver(),
        new ArpResolver(),
        Oui,
        new DeviceClassifier());
}
