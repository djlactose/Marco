using Marco.Core.Scanning;
using Marco.Discovery;
using Xunit;

namespace Marco.Tests;

public class OuiLookupTests
{
    private const string Sample =
        "# comment line\n" +
        "005056\tVMware\tHypervisor\n" +
        "00000C\tCisco\tNetwork\n" +
        "000874\tDell\tPc\n" +
        "0007E9\tZebra Technologies\tPrinter\n";

    private readonly OuiLookup _lookup = OuiLookup.LoadFrom(Sample);

    [Theory]
    [InlineData("00:50:56:AA:BB:CC")]
    [InlineData("00-50-56-aa-bb-cc")]
    [InlineData("005056AABBCC")]
    [InlineData("0050.56aa.bbcc")]
    public void NormalizesMacFormats(string mac)
    {
        var entry = _lookup.Lookup(mac);
        Assert.NotNull(entry);
        Assert.Equal("VMware", entry!.Vendor);
        Assert.Equal(OuiCategory.Hypervisor, entry.Category);
    }

    [Fact]
    public void ResolvesCategoryPerVendor()
    {
        Assert.Equal(OuiCategory.Network, _lookup.Lookup("00:00:0C:12:34:56")!.Category);
        Assert.Equal(OuiCategory.Pc, _lookup.Lookup("00:08:74:12:34:56")!.Category);
        Assert.Equal(OuiCategory.Printer, _lookup.Lookup("00:07:E9:12:34:56")!.Category);
    }

    [Fact]
    public void UnknownPrefix_ReturnsNull()
        => Assert.Null(_lookup.Lookup("AA:BB:CC:DD:EE:FF"));

    [Fact]
    public void MalformedMac_ReturnsNull()
    {
        Assert.Null(_lookup.Lookup(null));
        Assert.Null(_lookup.Lookup(""));
        Assert.Null(_lookup.Lookup("xyz"));
        Assert.Null(_lookup.Lookup("00:50")); // too short
    }

    [Fact]
    public void SkipsCommentsAndBlankLines()
        => Assert.Equal(4, _lookup.Count);

    [Fact]
    public void FullVendorTable_FillsVendor_WithNoCategory_AndCuratedWins()
    {
        var vendors = new Dictionary<uint, string>
        {
            [0x005056] = "VMware full-registry name",  // also curated → curated must win
            [0xE45E1B] = "Google, Inc.",               // not curated → vendor only
        };
        var lookup = OuiLookup.LoadFrom(Sample, vendors);

        // Curated entry keeps its category and curated vendor name.
        var vm = lookup.Lookup("00:50:56:AA:BB:CC")!;
        Assert.Equal(OuiCategory.Hypervisor, vm.Category);
        Assert.Equal("VMware", vm.Vendor);

        // Full-registry-only entry resolves the vendor with no category.
        var g = lookup.Lookup("E4:5E:1B:11:22:33")!;
        Assert.Equal("Google, Inc.", g.Vendor);
        Assert.Equal(OuiCategory.None, g.Category);
    }

    [Fact]
    public void EmbeddedTable_ResolvesFullRegistryVendors()
    {
        var embedded = OuiLookup.LoadEmbedded();
        Assert.True(embedded.Count > 10000, $"expected the full IEEE registry (~40k), got {embedded.Count}");

        // A known hypervisor prefix keeps its curated category.
        Assert.Equal(OuiCategory.Hypervisor, embedded.Lookup("00:15:5D:01:02:03")!.Category);

        // Arbitrary real-world prefixes now resolve a vendor (they were blank with the curated-only table).
        Assert.Contains("Google", embedded.Lookup("E4:5E:1B:00:00:00")!.Vendor);
        Assert.Contains("Intel", embedded.Lookup("04:F0:EE:00:00:00")!.Vendor);
    }
}
