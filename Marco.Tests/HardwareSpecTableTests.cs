using Marco.Core.Hardware;
using Marco.Core.Model;
using Xunit;

namespace Marco.Tests;

public class HardwareSpecTableTests
{
    // --- bundled table ---

    [Fact]
    public void EmbeddedTable_LoadsAndHasADate()
    {
        var t = HardwareSpecTable.Embedded;
        Assert.True(t.Count > 50);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", t.Updated);
    }

    [Fact]
    public void EmbeddedTable_EveryEntry_HasVendorAndPattern_AndSaneFacts()
    {
        // Reflection-free sanity pass over the bundled data through the public matcher is impractical, so probe
        // representative hosts instead; the loader already proves the JSON parses.
        var t = HardwareSpecTable.Embedded;
        Assert.NotNull(t.Match("Dell Inc.", "OptiPlex 7090", null, "Low Profile Desktop", null, "DIMM"));
        Assert.NotNull(t.Match("HP", "HP EliteDesk 800 G6 Small Form Factor PC", null, "Low Profile Desktop", null, "DIMM"));
        Assert.NotNull(t.Match("LENOVO", "11DT000VUS", "ThinkCentre M70q Gen 2", "Mini PC", null, "SODIMM"));
    }

    [Fact]
    public void Dell_OptiPlex_FormFactor_SeparatesMicroFromTower()
    {
        var t = HardwareSpecTable.Embedded;
        var micro = t.Match("Dell Inc.", "OptiPlex 7090", null, "Desktop", null, "SODIMM")!;
        var tower = t.Match("Dell Inc.", "OptiPlex 7090", null, "Desktop", null, "DIMM")!;
        var unknownFf = t.Match("Dell Inc.", "OptiPlex 7090", null, "Desktop", null, null)!;

        Assert.Equal(2, micro.MemorySlots);
        Assert.Equal(64, micro.MaxMemoryGb);
        Assert.Equal(4, tower.MemorySlots);
        Assert.Equal(128, tower.MaxMemoryGb);
        Assert.Same(tower, unknownFf); // no form factor reported → never the SODIMM-only entry
    }

    [Fact]
    public void Dell_OptiPlex7010_PlusAndNonPlus_AreDistinct()
    {
        var t = HardwareSpecTable.Embedded;
        Assert.Equal(128, t.Match("Dell Inc.", "OptiPlex SFF Plus 7010", null, null, null, "DIMM")!.MaxMemoryGb);
        Assert.Equal(64, t.Match("Dell Inc.", "OptiPlex SFF 7010", null, null, null, "DIMM")!.MaxMemoryGb);
        Assert.Equal("DDR5", t.Match("Dell Inc.", "OptiPlex Micro Plus 7010", null, null, null, "SODIMM")!.MemoryType);
        Assert.Equal("DDR4", t.Match("Dell Inc.", "OptiPlex Micro 7010", null, null, null, "SODIMM")!.MemoryType);
    }

    [Fact]
    public void Dell_ThinClient_IsExcludedFromTheDesktopFamily()
    {
        var spec = HardwareSpecTable.Embedded.Match("Dell Inc.", "OptiPlex 3000 Thin Client", null, null, null, null)!;
        Assert.False(spec.HasMemoryFacts);
        Assert.False(spec.HasBayFacts);
        Assert.Null(Machine.DescribeSpec(spec, "Dell Inc.", "OptiPlex 3000 Thin Client"));
    }

    [Fact]
    public void Lenovo_MatchesOnProductVersion_NotTheMachineTypeCode()
    {
        var spec = HardwareSpecTable.Embedded.Match("LENOVO", "20XW004GUS", "ThinkPad T14 Gen 2", "Notebook", null, "SODIMM")!;
        Assert.Equal(48, spec.MaxMemoryGb);
        Assert.Null(HardwareSpecTable.Embedded.Match("LENOVO", "20XW004GUS", null, "Notebook", null, "SODIMM"));
    }

    [Fact]
    public void ThisLaptop_Vostro5630_Resolves()
    {
        var spec = HardwareSpecTable.Embedded.Match("Dell Inc.", "Vostro 16 5630", null, "Notebook", "0FCG4K", null)!;
        Assert.Equal(16, spec.MaxMemoryGb);
        Assert.Equal(0, spec.MemorySlots);
        Assert.Equal("LPDDR5 soldered, max 16 GB", spec.MemoryDisplay);
    }

    // --- matcher mechanics ---

    [Theory]
    [InlineData("Dell Inc.", "dell")]
    [InlineData("Hewlett-Packard", "hp")]
    [InlineData("HP Inc.", "hp")]
    [InlineData("HP", "hp")]
    [InlineData("LENOVO", "lenovo")]
    [InlineData("Micro-Star International Co., Ltd.", "msi")]
    [InlineData("Super Micro Computer, Inc.", "supermicro")]
    [InlineData("Shuttle Inc.", "shuttle")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void VendorKey_NormalisesCommonSpellings(string? manufacturer, string? expected)
        => Assert.Equal(expected, HardwareSpecTable.VendorKey(manufacturer));

    [Fact]
    public void Glob_SupportsWildcardsAndAlternatives_CaseInsensitively()
    {
        var rx = HardwareSpecTable.Glob("OptiPlex 70?0*|*EliteDesk 800 G6*SFF*");
        Assert.Matches(rx, "optiplex 7090");
        Assert.Matches(rx, "OptiPlex 7070 Micro");
        Assert.Matches(rx, "HP EliteDesk 800 G6 SFF PC");
        Assert.DoesNotMatch(rx, "OptiPlex 5090");
        Assert.DoesNotMatch(rx, "EliteDesk 800 G6 Tower PC");
    }

    [Fact]
    public void Overrides_WinOverBundledEntries_AndCacheIsKeyedByAllInputs()
    {
        var t = HardwareSpecTable.FromSpecs(
            new HardwareSpec("dell", "OptiPlex 7090*", Chassis: "Low Profile Desktop", MaxMemoryGb: 1),
            new HardwareSpec("dell", "OptiPlex 7090*", MaxMemoryGb: 2));
        Assert.Equal(1, t.Match("Dell Inc.", "OptiPlex 7090", null, "Low Profile Desktop", null)!.MaxMemoryGb);
        Assert.Equal(2, t.Match("Dell Inc.", "OptiPlex 7090", null, "Desktop", null)!.MaxMemoryGb);
        Assert.Null(t.Match("HP", "OptiPlex 7090", null, "Desktop", null));
        Assert.Null(t.Match("Dell Inc.", null, null, null, null));
    }

    [Fact]
    public void LoadWithOverride_ToleratesMissingAndBrokenFiles()
    {
        var missing = HardwareSpecTable.LoadWithOverride(Path.Combine(Path.GetTempPath(), "marco-no-such-" + Guid.NewGuid().ToString("N") + ".json"));
        Assert.Null(missing.OverrideError);
        Assert.Equal(0, missing.OverrideCount);

        var broken = Path.GetTempFileName();
        try
        {
            File.WriteAllText(broken, "{ not json");
            var t = HardwareSpecTable.LoadWithOverride(broken);
            Assert.NotNull(t.OverrideError);
            Assert.True(t.Count > 50); // bundled data still there
        }
        finally { File.Delete(broken); }
    }

    [Fact]
    public void OverrideTemplate_ParsesWithCommentsAndAddsNothing()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, HardwareSpecTable.OverrideTemplate);
            var t = HardwareSpecTable.LoadWithOverride(path);
            Assert.Null(t.OverrideError);
            Assert.Equal(0, t.OverrideCount);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OverrideFile_EntryIsConsultedFirst()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{ "SchemaVersion": 1, "Updated": "", "Specs": [ { "Manufacturer": "dell", "Model": "OptiPlex 7090*", "MaxMemoryGb": 256, "Name": "Corrected" } ] }""");
            var t = HardwareSpecTable.LoadWithOverride(path);
            Assert.Equal(1, t.OverrideCount);
            Assert.Equal("Corrected", t.Match("Dell Inc.", "OptiPlex 7090", null, "Desktop", null, "DIMM")!.Name);
        }
        finally { File.Delete(path); }
    }

    // --- how the spec shows up on a machine ---

    private static Machine OptiPlex7090Sff()
    {
        var m = new Machine("10.0.0.5");
        m.System.Manufacturer = "Dell Inc.";
        m.System.Model = "OptiPlex 7090";
        m.System.ChassisType = "Low Profile Desktop";
        m.TotalMemoryBytes = 16L << 30;
        m.MemorySlotsUsed = 2; m.MemorySlotsTotal = 4;
        m.MemoryModules = new List<MemoryModule> { new() { CapacityBytes = 8L << 30, FormFactor = "DIMM", MemoryTypeName = "DDR4" } };
        m.Disks = new List<DiskInfo> { new() { Model = "NVMe", BusType = "NVMe" } };
        return m;
    }

    [Fact]
    public void Machine_UsesSpecSheetMax_AndNotesDifferingFirmware()
    {
        var m = OptiPlex7090Sff();
        Assert.Equal("16 GB, 2 of 4 slots used, max 128 GB (spec sheet)", m.MemorySummary);
        m.MaxMemoryBytes = 32L << 30; // stale SMBIOS table
        Assert.Equal("16 GB, 2 of 4 slots used, max 128 GB (spec sheet; firmware reports 32 GB)", m.MemorySummary);
        Assert.Equal(128L << 30, m.EffectiveMaxMemoryBytes);
        Assert.StartsWith("Dell OptiPlex 7090 Tower/SFF spec sheet: DDR4 DIMM, 4 slots, max 128 GB · 2× 2.5″/3.5″ bays, 2× M.2", m.SpecSheetSummary);
    }

    [Fact]
    public void Machine_ExpansionOutlook_UsesSpecBays_WhenKnown()
    {
        var m = OptiPlex7090Sff();
        Assert.StartsWith("Spec sheet: 2× 2.5″/3.5″ bays, 2× M.2", m.ExpansionOutlook);
        Assert.EndsWith("1 internal disk installed, up to 3 free.", m.ExpansionOutlook);

        m.System.Model = "Unknown Box 9000"; // not in the table → chassis estimate
        Assert.StartsWith("Estimate:", m.ExpansionOutlook);
    }

    [Fact]
    public void Machine_SpecIsNull_ForUnknownVendorOrModel()
    {
        var m = new Machine("10.0.0.6");
        Assert.Null(m.Spec);
        m.System.Manufacturer = "Dell Inc.";
        Assert.Null(m.Spec);
        m.System.Model = "Inspiron 1234567";
        Assert.Null(m.Spec);
        Assert.Null(m.SpecSheetSummary);
    }

    [Fact]
    public void Spec_WithNoExpansion_SaysSo()
    {
        var spec = new HardwareSpec("dell", "x", DriveBays: 0, M2Slots: 1);
        Assert.Equal("Spec sheet: no drive bays, 1× M.2 — 1 internal disk installed, all positions in use.",
            ExpansionEstimator.Describe(spec, false, "Notebook", null, null, null, 1));
        var sealed_ = new HardwareSpec("dell", "x", DriveBays: 0, M2Slots: 0);
        Assert.StartsWith("Spec sheet: no internal drive bays", ExpansionEstimator.Describe(sealed_, false, "Notebook", null, null, null, 1));
        Assert.Null(ExpansionEstimator.Describe(spec, true, "Desktop", null, null, null, 1)); // VM
    }
}
