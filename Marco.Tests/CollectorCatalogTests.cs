using Marco.Core.Inventory;
using Marco.Inventory;
using Marco.Inventory.Linux;
using Xunit;

namespace Marco.Tests;

public class CollectorCatalogTests
{
    [Fact]
    public void EveryWindowsCollector_IsInTheCatalogue()
    {
        var names = CollectorCatalog.All.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var collector in InventoryCollectors.Phase2())
            Assert.Contains(collector.Name, names);
        // and the catalogue only lists Windows collectors that exist
        var implemented = InventoryCollectors.Phase2().Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in CollectorCatalog.All.Where(c => c.Windows))
            Assert.Contains(entry.Name, implemented);
    }

    [Fact]
    public void EveryLinuxGroup_IsInTheCatalogue()
    {
        var names = CollectorCatalog.All.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in LinuxInventoryRunner.CollectorNames)
            Assert.Contains(group, names);
        foreach (var entry in CollectorCatalog.All.Where(c => c.Linux))
            Assert.Contains(entry.Name, LinuxInventoryRunner.CollectorNames);
    }

    [Fact]
    public void EveryPrinterAndNetworkGroup_IsInTheCatalogue()
    {
        var names = CollectorCatalog.All.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Marco.Inventory.Snmp.SnmpDeviceInventoryRunner.PrinterCollectorNames)
            Assert.Contains(group, names);
        foreach (var group in Marco.Inventory.Snmp.SnmpDeviceInventoryRunner.NetworkCollectorNames)
            Assert.Contains(group, names);
        foreach (var entry in CollectorCatalog.All.Where(c => c.Printer))
            Assert.Contains(entry.Name, Marco.Inventory.Snmp.SnmpDeviceInventoryRunner.PrinterCollectorNames);
        foreach (var entry in CollectorCatalog.All.Where(c => c.NetworkDevice))
            Assert.Contains(entry.Name, Marco.Inventory.Snmp.SnmpDeviceInventoryRunner.NetworkCollectorNames);
        // Every catalogue entry belongs to at least one platform.
        Assert.All(CollectorCatalog.All, c => Assert.True(c.Windows || c.Linux || c.Printer || c.NetworkDevice, c.Name));
    }

    [Fact]
    public void HeavyCollectors_DefaultOff()
    {
        var defaults = CollectorCatalog.DefaultEnabledNames();
        Assert.DoesNotContain("ScheduledTasks", defaults);
        Assert.DoesNotContain("UsbHistory", defaults);
        Assert.Contains("Security", defaults);
        Assert.Contains("InstalledSoftware", defaults);
    }

    [Fact]
    public void Overrides_RoundTrip_OnlyDifferences()
    {
        var enabled = CollectorCatalog.EnabledNames(null);
        Assert.Null(CollectorCatalog.OverridesFor(enabled)); // defaults → nothing to store

        enabled.Remove("Security");
        enabled.Add("ScheduledTasks");
        var overrides = CollectorCatalog.OverridesFor(enabled)!;
        Assert.Equal(2, overrides.Count);
        Assert.False(overrides["Security"]);
        Assert.True(overrides["ScheduledTasks"]);

        var restored = CollectorCatalog.EnabledNames(overrides);
        Assert.DoesNotContain("Security", restored);
        Assert.Contains("ScheduledTasks", restored);
        Assert.Contains("System", restored);
        Assert.DoesNotContain("UsbHistory", restored);
    }

    [Fact]
    public void UnknownOverride_IsIgnored()
    {
        var restored = CollectorCatalog.EnabledNames(new Dictionary<string, bool> { ["Bogus"] = true, ["Users"] = false });
        Assert.DoesNotContain("Bogus", restored);
        Assert.DoesNotContain("Users", restored);
    }
}
