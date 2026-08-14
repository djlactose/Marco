using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;

namespace Marco.Inventory.Collectors;

/// <summary>Operating system details, and the PC-vs-Server refinement from ProductType.</summary>
public sealed class OsCollector : IInventoryCollector
{
    public string Name => "OperatingSystem";

    public async Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct)
    {
        var session = context.Wmi;
        var os = await session.QueryFirstAsync(
            "SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime, " +
            "RegisteredUser, Organization, ProductType FROM Win32_OperatingSystem", ct);
        if (os is null)
            throw new WmiException(WmiFailureKind.NotSupported, "Win32_OperatingSystem returned nothing.");

        machine.Os.Caption = os.GetString("Caption")?.Trim();
        machine.Os.Version = os.GetString("Version");
        machine.Os.Build = os.GetString("BuildNumber");
        machine.Os.Architecture = os.GetString("OSArchitecture");
        machine.Os.InstallDate = os.GetDateTime("InstallDate");
        machine.Os.LastBoot = os.GetDateTime("LastBootUpTime");
        machine.Os.RegisteredOwner = os.GetString("RegisteredUser");
        machine.Os.RegisteredOrganization = os.GetString("Organization");

        // ProductType: 1 = Workstation, 2 = Domain Controller, 3 = Server.
        var productType = os.GetInt("ProductType");
        if (productType is 2 or 3)
            machine.DeviceType = DeviceType.WindowsServer;
        else if (productType == 1 && machine.DeviceType is DeviceType.Unknown or DeviceType.Windows)
            machine.DeviceType = DeviceType.Windows;
    }
}
