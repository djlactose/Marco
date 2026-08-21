using Marco.Core.Ipp;
using Marco.Core.Model;
using Marco.Core.Snmp;
using Marco.Inventory.Snmp;
using Xunit;

namespace Marco.Tests;

public class PrinterMibTests
{
    private static IReadOnlyList<SnmpVarBind> Walk(SnmpOidTable t, string prefix)
    {
        var p = SnmpOid.Parse(prefix);
        return t.Entries.Where(kv => p.IsPrefixOf(kv.Key)).Select(kv => new SnmpVarBind(kv.Key, kv.Value)).ToList();
    }

    // --- supply percent matrix -------------------------------------------------------------------------

    [Theory]
    [InlineData(12, 100, 19, 12)]     // percent unit
    [InlineData(250, 1000, 13, 25)]   // grams: level/max
    [InlineData(1100, 1000, 13, 100)] // clamp when level > max (Brother waste)
    [InlineData(-3, 100, 19, null)]   // some remaining → no number
    [InlineData(-2, -2, 13, null)]    // unknown
    [InlineData(45, -2, 13, 45)]      // max unknown but level in 0..100 → read as percent
    [InlineData(4500, -2, 13, null)]  // max unknown, level too big to be a percent
    [InlineData(0, 100, 19, 0)]
    [InlineData(null, 100, 19, null)]
    public void SupplyPercent_HandlesSentinelsAndUnits(int? level, int? max, int? unit, int? expected)
        => Assert.Equal(expected, PrinterMib.SupplyPercent(level, max, unit));

    [Fact]
    public void Supply_LowAndEmpty_InvertForReceptacles()
    {
        var toner = new PrinterSupply { Percent = 8 };
        Assert.True(toner.IsLow); Assert.False(toner.IsEmpty);
        Assert.True(new PrinterSupply { Percent = 0 }.IsEmpty);
        Assert.False(new PrinterSupply { Percent = 11 }.IsLow);

        var waste = new PrinterSupply { IsReceptacle = true, Percent = 95 };
        Assert.True(waste.IsLow);                                              // 95 % full → nearly full
        Assert.True(new PrinterSupply { IsReceptacle = true, Percent = 100 }.IsEmpty); // "empty" here means needs replacing
        Assert.False(new PrinterSupply { IsReceptacle = true, Percent = 30 }.IsLow);
        Assert.Equal("30 % full", new PrinterSupply { IsReceptacle = true, Percent = 30 }.LevelDisplay);
        Assert.Equal("Some remaining", new PrinterSupply { SomeRemaining = true }.LevelDisplay);
        Assert.Equal("Low", new PrinterSupply { DeviceFlagsLow = true }.LevelDisplay);
    }

    // --- error-state bitmap ---------------------------------------------------------------------------

    [Theory]
    [InlineData("20", new[] { PrinterErrorStates.LowToner })]
    [InlineData("2000", new[] { PrinterErrorStates.LowToner })]
    [InlineData("0C", new[] { PrinterErrorStates.DoorOpen, PrinterErrorStates.Jammed })]
    [InlineData("00 04", new[] { PrinterErrorStates.InputTrayEmpty })]
    [InlineData("00000000", new string[0])]
    [InlineData("", new string[0])]
    public void ErrorState_DecodesBitmapOfAnyLength(string hex, string[] expected)
    {
        var v = SnmpValue.OctetString(Convert.FromHexString(hex.Replace(" ", "")));
        Assert.Equal(expected, PrinterMib.DecodeErrorState(v));
    }

    [Fact]
    public void ErrorState_AcceptsInteger()
    {
        Assert.Equal(new[] { PrinterErrorStates.NoPaper }, PrinterMib.DecodeErrorState(SnmpValue.Integer(0x40)));
        Assert.Equal(new[] { PrinterErrorStates.LowPaper, PrinterErrorStates.OutputFull }, PrinterMib.DecodeErrorState(SnmpValue.Integer(0x8008)));
        Assert.Empty(PrinterMib.DecodeErrorState(null));
    }

    [Fact]
    public void Status_SleepingEngine_IsNotAnError()
    {
        Assert.Equal("Idle (sleep)", PrinterMib.DescribePrinterStatus(1));
        Assert.Equal("Printing", PrinterMib.DescribePrinterStatus(4));
        Assert.Equal("Down", PrinterMib.DescribeDeviceStatus(5));
    }

    // --- supplies from the fixtures --------------------------------------------------------------------

    [Fact]
    public void Hp_Supplies_JoinColorants_AndFlagReceptacle()
    {
        var t = SnmpOidTable.FromFixture("snmp-hp-m479.txt");
        var supplies = PrinterMib.ParseSupplies(Walk(t, "1.3.6.1.2.1.43.11.1.1"), Walk(t, "1.3.6.1.2.1.43.12.1.1.4"));

        Assert.Equal(6, supplies.Count);
        var black = supplies[0];
        Assert.Equal("Black Cartridge HP 414A", black.Name);
        Assert.Equal("toner", black.Type);
        Assert.Equal("black", black.Colorant);
        Assert.Equal(8, black.Percent);
        Assert.True(black.IsLow);
        Assert.Equal("K", black.ShortName);
        Assert.Equal("cyan", supplies[1].Colorant);
        Assert.Equal(80, supplies[1].Percent);

        var drum = supplies[4];
        Assert.Equal("drum", drum.Type);
        Assert.Equal("black", drum.Colorant); // colorant index 0 → sniffed from "Black Imaging Drum"
        Assert.Equal("Drum", drum.ShortName);

        var waste = supplies[5];
        Assert.True(waste.IsReceptacle);
        Assert.Equal(30, waste.Percent);
        Assert.False(waste.IsLow);
    }

    [Fact]
    public void Brother_Supplies_AreSomeRemaining_NotLow()
    {
        var t = SnmpOidTable.FromFixture("snmp-brother-l3770.txt");
        var supplies = PrinterMib.ParseSupplies(Walk(t, "1.3.6.1.2.1.43.11.1.1"), Walk(t, "1.3.6.1.2.1.43.12.1.1.4"));
        var toners = supplies.Where(s => s.Type == "toner").ToList();
        Assert.Equal(4, toners.Count);
        Assert.All(toners, s => { Assert.Null(s.Percent); Assert.True(s.SomeRemaining); Assert.False(s.IsLow); Assert.Equal("Some remaining", s.LevelDisplay); });
        var drum = supplies.Single(s => s.Type == "drum");
        Assert.Equal(90, drum.Percent); // 16200 of 18000 impressions
        Assert.Equal("impressions", drum.Unit);
    }

    [Fact]
    public void Kyocera_TwoMarkerRows_PrimaryCounterIsTheNonZeroOne()
    {
        var t = SnmpOidTable.FromFixture("snmp-kyocera-m2640.txt");
        var rows = PrinterMib.Rows(Walk(t, "1.3.6.1.2.1.43.10.2.1"), PrinterMib.PrtMarkerEntry);
        Assert.Equal(2, rows.Count);
        Assert.Equal(120553, rows[0].Int(4));
        var supplies = PrinterMib.ParseSupplies(Walk(t, "1.3.6.1.2.1.43.11.1.1"), Walk(t, "1.3.6.1.2.1.43.12.1.1.4"));
        Assert.Equal("TK-1170", supplies[0].Name);
        Assert.Equal(40, supplies[0].Percent);
        Assert.True(supplies[1].IsReceptacle);
        Assert.True(supplies[1].IsLow); // 95 % full
    }

    // --- trays, covers, alerts, console --------------------------------------------------------------

    [Fact]
    public void Hp_Trays_NameFallbackAndLevels()
    {
        var t = SnmpOidTable.FromFixture("snmp-hp-m479.txt");
        var trays = PrinterMib.ParseTrays(Walk(t, "1.3.6.1.2.1.43.8.2.1"));
        Assert.Equal(2, trays.Count);
        Assert.Equal("Tray 1", trays[0].Name);
        Assert.Equal("Some remaining", trays[0].Status);
        Assert.Equal("Tray 2", trays[1].Name);
        Assert.Equal(72, trays[1].Percent);
        Assert.Equal("OK", trays[1].Status);
        Assert.Equal("na_letter_8.5x11in", trays[1].Media);
    }

    [Fact]
    public void Trays_UseDescriptionWhenNameIsEmpty()
    {
        var t = new SnmpOidTable()
            .Int("1.3.6.1.2.1.43.8.2.1.9.1.1", 500)
            .Int("1.3.6.1.2.1.43.8.2.1.10.1.1", 0)
            .Str("1.3.6.1.2.1.43.8.2.1.13.1.1", "")
            .Str("1.3.6.1.2.1.43.8.2.1.18.1.1", "Cassette 1");
        var trays = PrinterMib.ParseTrays(Walk(t, "1.3.6.1.2.1.43.8.2.1"));
        Assert.Equal("Cassette 1", trays[0].Name);
        Assert.Equal("Empty", trays[0].Status);
    }

    [Fact]
    public void Hp_AlertsCoversConsole()
    {
        var t = SnmpOidTable.FromFixture("snmp-hp-m479.txt");
        var alerts = PrinterMib.ParseAlerts(Walk(t, "1.3.6.1.2.1.43.18.1.1"));
        var a = Assert.Single(alerts);
        Assert.Equal("Warning", a.Severity);
        Assert.Equal("marker supplies", a.Group);
        Assert.Equal(1104, a.Code);
        Assert.Equal("Black cartridge low", a.Description);
        Assert.Equal(new[] { "Front Door: closed" }, PrinterMib.ParseCovers(Walk(t, "1.3.6.1.2.1.43.6.1.1")));
        Assert.Equal(new[] { "Ready" }, PrinterMib.ParseConsoleText(Walk(t, "1.3.6.1.2.1.43.16.5.1.2")));
    }

    [Fact]
    public void Alerts_AreCapped()
    {
        var t = new SnmpOidTable();
        for (int i = 1; i <= 80; i++) t.Str($"1.3.6.1.2.1.43.18.1.1.8.1.{i}", $"alert {i}");
        Assert.Equal(50, PrinterMib.ParseAlerts(Walk(t, "1.3.6.1.2.1.43.18.1.1")).Count);
    }

    // --- host resources / identity ----------------------------------------------------------------------

    [Fact]
    public void FindPrinterDevices_UsesTypeColumn_NotIndexOne()
    {
        var t = new SnmpOidTable()
            .Oid("1.3.6.1.2.1.25.3.2.1.2.1", "1.3.6.1.2.1.25.3.1.4")   // network
            .Str("1.3.6.1.2.1.25.3.2.1.3.1", "NIC")
            .Oid("1.3.6.1.2.1.25.3.2.1.2.7", "1.3.6.1.2.1.25.3.1.5")   // printer at index 7
            .Str("1.3.6.1.2.1.25.3.2.1.3.7", "Xerox VersaLink C405")
            .Int("1.3.6.1.2.1.25.3.2.1.5.7", 3);
        var found = PrinterMib.FindPrinterDevices(Walk(t, "1.3.6.1.2.1.25.3.2.1"));
        var p = Assert.Single(found);
        Assert.Equal(7u, p.Index);
        Assert.Equal("Xerox VersaLink C405", p.Description);
        Assert.Equal(3, p.Status);
    }

    [Fact]
    public void VendorFromObjectId_AndPlaceholders()
    {
        Assert.Equal("HP", PrinterMib.VendorFromObjectId(SnmpOid.Parse("1.3.6.1.4.1.11.2.3.9.1")));
        Assert.Equal("Brother", PrinterMib.VendorFromObjectId(SnmpOid.Parse("1.3.6.1.4.1.2435.2.3.9.1")));
        Assert.Equal("Cisco", PrinterMib.VendorFromObjectId(SnmpOid.Parse("1.3.6.1.4.1.9.1.1208")));
        Assert.Null(PrinterMib.VendorFromObjectId(SnmpOid.Parse("1.3.6.1.2.1.1")));
        Assert.Null(PrinterMib.VendorFromObjectId(null));
        Assert.True(PrinterMib.IsPlaceholder(""));
        Assert.True(PrinterMib.IsPlaceholder("0000000"));
        Assert.True(PrinterMib.IsPlaceholder("N/A"));
        Assert.False(PrinterMib.IsPlaceholder("VNC1K23456"));
    }

    [Fact]
    public void Interfaces_SkipLoopback_JoinNamesSpeedsAndIps()
    {
        var t = SnmpOidTable.FromFixture("snmp-hp-m479.txt");
        var adapters = PrinterMib.ParseInterfaces(Walk(t, "1.3.6.1.2.1.2.2.1"), Walk(t, "1.3.6.1.2.1.31.1.1.1"), Walk(t, "1.3.6.1.2.1.4.20.1.2"));
        var a = Assert.Single(adapters);
        Assert.Equal("eth0", a.Name);                     // ifName preferred over ifDescr
        Assert.Equal("3C:D9:2B:11:22:33", a.Mac);
        Assert.Equal(1_000_000_000L, a.SpeedBps);         // ifHighSpeed 1000 Mb/s
        Assert.Equal(new[] { "10.0.0.9" }, a.IpAddresses);
        Assert.Equal((1, 1), PrinterMib.CountInterfaces(Walk(t, "1.3.6.1.2.1.2.2.1")));
    }

    [Fact]
    public void Interfaces_AsciiMac_Parses()
    {
        var t = SnmpOidTable.FromFixture("snmp-brother-l3770.txt");
        var a = Assert.Single(PrinterMib.ParseInterfaces(Walk(t, "1.3.6.1.2.1.2.2.1"), Array.Empty<SnmpVarBind>(), Array.Empty<SnmpVarBind>()));
        Assert.Equal("00:1B:A9:AA:BB:CC", a.Mac);
    }

    // --- IPP markers ------------------------------------------------------------------------------------

    [Fact]
    public void IppMarkers_MapColorsTypesAndLevels()
    {
        var g = new IppAttributeGroup(IppTag.PrinterAttributes, new List<IppAttribute>
        {
            new("marker-names", IppTag.NameWithoutLanguage, new[] { V("Black Toner"), V("Cyan Toner"), V("Waste Toner Box") }),
            new("marker-types", IppTag.Keyword, new[] { V("toner"), V("toner"), V("wasteToner") }),
            new("marker-colors", IppTag.NameWithoutLanguage, new[] { V("#000000"), V("#00FFFF"), V("none") }),
            new("marker-levels", IppTag.Integer, new[] { I(5), I(-3), I(92) }),
        });
        var s = PrinterMib.ParseIppMarkers(g);
        Assert.Equal(3, s.Count);
        Assert.Equal("black", s[0].Colorant); Assert.Equal(5, s[0].Percent); Assert.True(s[0].IsLow);
        Assert.Equal("cyan", s[1].Colorant); Assert.True(s[1].SomeRemaining); Assert.Null(s[1].Percent);
        Assert.True(s[2].IsReceptacle); Assert.Equal(92, s[2].Percent); Assert.True(s[2].IsLow);
    }

    private static IppValue V(string s) => new(IppTag.NameWithoutLanguage, System.Text.Encoding.UTF8.GetBytes(s), text: s);
    private static IppValue I(int n) => new(IppTag.Integer, BitConverter.GetBytes(n), n);
}
