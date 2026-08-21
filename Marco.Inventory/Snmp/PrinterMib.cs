using Marco.Core.Ipp;
using Marco.Core.Model;
using Marco.Core.Snmp;

namespace Marco.Inventory.Snmp;

/// <summary>
/// The OIDs the printer / network-device runner reads (SNMPv2-MIB, IF-MIB, HOST-RESOURCES-MIB RFC 2790,
/// Printer-MIB RFC 3805, ENTITY-MIB) and the pure parsers that turn walked rows into the model. Every parser
/// is a static function over varbinds so it can be unit-tested against captured tables; nothing here does I/O.
/// </summary>
public static class PrinterMib
{
    private static SnmpOid O(string s) => SnmpOid.Parse(s);

    // --- SNMPv2-MIB system group (scalars) ---
    public static readonly SnmpOid SysDescr = O("1.3.6.1.2.1.1.1.0");
    public static readonly SnmpOid SysObjectId = O("1.3.6.1.2.1.1.2.0");
    public static readonly SnmpOid SysUpTime = O("1.3.6.1.2.1.1.3.0");
    public static readonly SnmpOid SysContact = O("1.3.6.1.2.1.1.4.0");
    public static readonly SnmpOid SysName = O("1.3.6.1.2.1.1.5.0");
    public static readonly SnmpOid SysLocation = O("1.3.6.1.2.1.1.6.0");

    // --- IF-MIB ---
    public static readonly SnmpOid IfEntry = O("1.3.6.1.2.1.2.2.1");          // .2 descr .3 type .5 speed .6 physAddress .8 operStatus
    public static readonly SnmpOid IfXEntry = O("1.3.6.1.2.1.31.1.1.1");      // .1 name .15 highSpeed (Mb/s)
    public static readonly SnmpOid IpAdEntIfIndex = O("1.3.6.1.2.1.4.20.1.2"); // index: the IP address

    // --- HOST-RESOURCES-MIB ---
    public static readonly SnmpOid HrDeviceEntry = O("1.3.6.1.2.1.25.3.2.1"); // .2 type .3 descr .5 status
    public static readonly SnmpOid HrDeviceTypePrinter = O("1.3.6.1.2.1.25.3.1.5");
    public static readonly SnmpOid HrPrinterStatus = O("1.3.6.1.2.1.25.3.5.1.1");
    public static readonly SnmpOid HrPrinterDetectedErrorState = O("1.3.6.1.2.1.25.3.5.1.2");

    // --- Printer-MIB (RFC 3805) ---
    public static readonly SnmpOid PrtGeneralPrinterName = O("1.3.6.1.2.1.43.5.1.1.16");
    public static readonly SnmpOid PrtGeneralSerialNumber = O("1.3.6.1.2.1.43.5.1.1.17");
    public static readonly SnmpOid PrtCoverEntry = O("1.3.6.1.2.1.43.6.1.1");      // .2 description .3 status
    public static readonly SnmpOid PrtInputEntry = O("1.3.6.1.2.1.43.8.2.1");      // .8 capacityUnit .9 maxCapacity .10 currentLevel .11 status .12 mediaName .13 name .18 description
    public static readonly SnmpOid PrtMarkerEntry = O("1.3.6.1.2.1.43.10.2.1");    // .3 counterUnit .4 lifeCount
    public static readonly SnmpOid PrtMarkerSuppliesEntry = O("1.3.6.1.2.1.43.11.1.1"); // .2 markerIndex .3 colorantIndex .4 class .5 type .6 description .7 supplyUnit .8 maxCapacity .9 level
    public static readonly SnmpOid PrtMarkerColorantValue = O("1.3.6.1.2.1.43.12.1.1.4");
    public static readonly SnmpOid PrtConsoleDisplayBufferText = O("1.3.6.1.2.1.43.16.5.1.2");
    public static readonly SnmpOid PrtAlertEntry = O("1.3.6.1.2.1.43.18.1.1");     // .2 severityLevel .4 group .5 groupIndex .7 code .8 description

    // --- ENTITY-MIB ---
    public static readonly SnmpOid EntPhysicalEntry = O("1.3.6.1.2.1.47.1.1.1.1"); // .9 firmwareRev .10 softwareRev .11 serialNum .12 mfgName .13 modelName

    // --- Vendor fallbacks (best effort) ---
    public static readonly SnmpOid HpSerialNumber = O("1.3.6.1.4.1.11.2.3.9.4.2.1.1.3.3.0");
    public static readonly SnmpOid BrotherSerialNumber = O("1.3.6.1.4.1.2435.2.3.9.4.2.1.5.5.1.0");
    public static readonly SnmpOid BrotherPageCount = O("1.3.6.1.4.1.2435.2.3.9.4.2.1.5.5.10.0");

    /// <summary>Enterprise arc (1.3.6.1.4.1.N) → vendor name, for the manufacturer when nothing better exists.</summary>
    public static readonly IReadOnlyDictionary<uint, string> EnterpriseVendors = new Dictionary<uint, string>
    {
        [11] = "HP", [2435] = "Brother", [1602] = "Canon", [1248] = "Epson", [1347] = "Kyocera", [253] = "Xerox",
        [641] = "Lexmark", [367] = "Ricoh", [18334] = "Konica Minolta", [2385] = "Sharp", [1129] = "OKI", [122] = "Sony",
        [2001] = "Oki Data", [10642] = "Zebra", [9] = "Cisco", [11863] = "TP-Link", [4526] = "Netgear", [14988] = "MikroTik",
        [14823] = "Aruba", [6486] = "Alcatel-Lucent", [2636] = "Juniper", [25506] = "H3C", [2011] = "Huawei", [6574] = "Synology",
        [24681] = "QNAP", [41112] = "Ubiquiti", [8072] = "Net-SNMP", [311] = "Microsoft", [43] = "3Com", [171] = "D-Link",
        [674] = "Dell", [232] = "HPE", [3375] = "F5", [12356] = "Fortinet", [9303] = "APC", [318] = "APC", [476] = "Eaton",
    };

    // =================================================================== generic table helpers

    /// <summary>One row of a walked table: its index arcs and the column → value map.</summary>
    public sealed record TableRow(uint[] Index, Dictionary<uint, SnmpValue> Columns)
    {
        public string Key => string.Join(".", Index);
        public SnmpValue? this[uint column] => Columns.TryGetValue(column, out var v) && v.HasValue ? v : null;
        public string? Text(uint column) => this[column]?.AsText();
        public long? Int(uint column) => this[column]?.Int;
        public uint DeviceIndex => Index.Length > 0 ? Index[0] : 0;
    }

    /// <summary>Group a walk of <c>entry.column.index…</c> OIDs into rows, in first-seen order.</summary>
    public static List<TableRow> Rows(IEnumerable<SnmpVarBind> walk, SnmpOid entry)
    {
        var rows = new Dictionary<string, TableRow>();
        var order = new List<TableRow>();
        foreach (var vb in walk)
        {
            var rest = vb.Oid.IndexAfter(entry);
            if (rest.Length < 2) continue;
            uint column = rest[0];
            var index = rest.Skip(1).ToArray();
            var key = string.Join(".", index);
            if (!rows.TryGetValue(key, out var row))
            {
                row = new TableRow(index, new Dictionary<uint, SnmpValue>());
                rows[key] = row;
                order.Add(row);
            }
            row.Columns[column] = vb.Value;
        }
        return order;
    }

    // =================================================================== status decoding

    public static string DescribePrinterStatus(long? v) => v switch
    {
        1 => "Idle (sleep)", // other(1) is what sleeping Kyocera/Brother engines report — not an error
        2 => "Unknown",
        3 => "Idle",
        4 => "Printing",
        5 => "Warming up",
        _ => "Unknown",
    };

    public static string DescribeDeviceStatus(long? v) => v switch
    {
        1 => "Unknown",
        2 => "Running",
        3 => "Warning",
        4 => "Testing",
        5 => "Down",
        _ => "Unknown",
    };

    /// <summary>hrPrinterDetectedErrorState is an OCTET STRING bitmap (bit 0 = MSB of byte 0). Agents send one,
    /// two or four bytes; a few send an INTEGER — all are accepted.</summary>
    public static List<string> DecodeErrorState(SnmpValue? value)
    {
        var flags = new List<string>();
        if (value is null) return flags;
        byte b0 = 0, b1 = 0;
        if (value.Bytes is { Length: > 0 } bytes)
        {
            b0 = bytes[0];
            if (bytes.Length > 1) b1 = bytes[1];
        }
        else if (value.Int is { } n)
        {
            if (n > 0xFF) { b0 = (byte)((n >> 8) & 0xFF); b1 = (byte)(n & 0xFF); }
            else b0 = (byte)n;
        }
        if ((b0 & 0x80) != 0) flags.Add(PrinterErrorStates.LowPaper);
        if ((b0 & 0x40) != 0) flags.Add(PrinterErrorStates.NoPaper);
        if ((b0 & 0x20) != 0) flags.Add(PrinterErrorStates.LowToner);
        if ((b0 & 0x10) != 0) flags.Add(PrinterErrorStates.NoToner);
        if ((b0 & 0x08) != 0) flags.Add(PrinterErrorStates.DoorOpen);
        if ((b0 & 0x04) != 0) flags.Add(PrinterErrorStates.Jammed);
        if ((b0 & 0x02) != 0) flags.Add(PrinterErrorStates.Offline);
        if ((b0 & 0x01) != 0) flags.Add(PrinterErrorStates.ServiceRequested);
        if ((b1 & 0x80) != 0) flags.Add(PrinterErrorStates.InputTrayMissing);
        if ((b1 & 0x40) != 0) flags.Add(PrinterErrorStates.OutputTrayMissing);
        if ((b1 & 0x20) != 0) flags.Add(PrinterErrorStates.MarkerSupplyMissing);
        if ((b1 & 0x10) != 0) flags.Add(PrinterErrorStates.OutputNearFull);
        if ((b1 & 0x08) != 0) flags.Add(PrinterErrorStates.OutputFull);
        if ((b1 & 0x04) != 0) flags.Add(PrinterErrorStates.InputTrayEmpty);
        if ((b1 & 0x02) != 0) flags.Add(PrinterErrorStates.OverduePreventMaint);
        return flags;
    }

    /// <summary>The hrDevice rows whose type is printer — (index, description). Never assumes index 1.</summary>
    public static List<(uint Index, string? Description, long? Status)> FindPrinterDevices(IEnumerable<SnmpVarBind> hrDeviceWalk)
    {
        var list = new List<(uint, string?, long?)>();
        foreach (var row in Rows(hrDeviceWalk, HrDeviceEntry))
        {
            var type = row[2]?.Oid;
            if (type is { } t && t == HrDeviceTypePrinter)
                list.Add((row.DeviceIndex, row.Text(3), row.Int(5)));
        }
        return list;
    }

    public static TimeSpan? UptimeFromTicks(long? ticks) => ticks is { } t && t >= 0 ? TimeSpan.FromMilliseconds(t * 10d) : null;

    // =================================================================== supplies

    public static string SupplyTypeName(long? code) => code switch
    {
        1 => "other", 2 => "unknown", 3 => "toner", 4 => "wasteToner", 5 => "ink", 6 => "inkCartridge", 7 => "inkRibbon",
        8 => "wasteInk", 9 => "drum", 10 => "developer", 11 => "fuserOil", 12 => "solidWax", 13 => "ribbonWax", 14 => "wasteWax",
        15 => "fuser", 16 => "coronaWire", 17 => "fuserOilWick", 18 => "cleanerUnit", 19 => "fuserCleaningPad", 20 => "transfer",
        21 => "toner", 22 => "fuserOiler", 23 => "water", 24 => "wasteWater", 25 => "glueWaterAdditive", 26 => "wastePaper",
        27 => "bindingSupply", 28 => "staples", 29 => "inserts", 30 => "covers", 31 => "developerWaste",
        _ => "other",
    };

    public static string? SupplyUnitName(long? code) => code switch
    {
        3 => "tenths of mm", 4 => "micrometers", 7 => "impressions", 8 => "sheets", 11 => "hours", 12 => "thousandths of ounces",
        13 => "tenths of grams", 14 => "hundredths of fluid ounces", 15 => "tenths of ml", 16 => "feet", 17 => "meters",
        18 => "items", 19 => "percent", _ => null,
    };
    public const int SupplyUnitPercent = 19;
    public const int SupplyClassReceptacle = 4;

    public static string CounterUnitName(long? code) => code switch
    {
        7 => "impressions", 8 => "sheets", 9 => "dot row", 16 => "feet", 17 => "meters", _ => "impressions",
    };

    /// <summary>Percent remaining (or, for receptacles, filled) from the Printer-MIB level/max pair. Sentinels:
    /// -1 other, -2 unknown, -3 "some remaining". Percent-unit supplies and the common "max unknown but level
    /// is 0–100" shape are read as a percentage; otherwise level/max, clamped.</summary>
    public static int? SupplyPercent(long? level, long? max, long? unitCode)
    {
        if (level is not { } l || l < 0) return null;
        if (unitCode == SupplyUnitPercent) return (int)Math.Clamp(l, 0, 100);
        if (max is { } m && m > 0) return (int)Math.Clamp(Math.Round(l * 100d / m), 0, 100);
        if ((max is null || max < 0) && l <= 100) return (int)l;
        return null;
    }

    /// <summary>Colorant from a supply description when the colorant table has nothing ("Black Cartridge HP 414A").</summary>
    public static string? ColorantFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.ToLowerInvariant();
        if (t.Contains("light cyan") || t.Contains("photo cyan")) return "light cyan";
        if (t.Contains("light magenta") || t.Contains("photo magenta")) return "light magenta";
        if (t.Contains("photo black")) return "photo black";
        if (t.Contains("matte black")) return "matte black";
        if (t.Contains("black") || t.Contains("schwarz") || t.Contains("noir") || t.Contains("negro")) return "black";
        if (t.Contains("cyan")) return "cyan";
        if (t.Contains("magenta")) return "magenta";
        if (t.Contains("yellow") || t.Contains("gelb") || t.Contains("jaune") || t.Contains("amarillo")) return "yellow";
        if (t.Contains("gray") || t.Contains("grey")) return "gray";
        return null;
    }

    /// <summary>Type from a description when the type column is missing or "other" ("Imaging Drum", "Fuser Kit").</summary>
    public static string? TypeFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.ToLowerInvariant();
        if (t.Contains("waste")) return t.Contains("ink") ? "wasteInk" : "wasteToner";
        if (t.Contains("drum") || t.Contains("imaging unit") || t.Contains("opc")) return "drum";
        if (t.Contains("fuser")) return "fuser";
        if (t.Contains("transfer")) return "transfer";
        if (t.Contains("staple")) return "staples";
        if (t.Contains("maintenance")) return "maintenanceKit";
        if (t.Contains("ink")) return "ink";
        if (t.Contains("toner") || t.Contains("cartridge")) return "toner";
        return null;
    }

    public static List<PrinterSupply> ParseSupplies(IEnumerable<SnmpVarBind> suppliesWalk, IEnumerable<SnmpVarBind> colorantWalk)
    {
        // Colorant name keyed by "<hrDeviceIndex>.<colorantIndex>" (prtMarkerColorantValue.dev.idx).
        var colorants = new Dictionary<string, string>();
        foreach (var vb in colorantWalk)
        {
            var idx = vb.Oid.IndexAfter(PrtMarkerColorantValue);
            if (idx.Length >= 2 && vb.Value.AsText() is { } name) colorants[$"{idx[0]}.{idx[1]}"] = name;
        }

        var list = new List<PrinterSupply>();
        foreach (var row in Rows(suppliesWalk, PrtMarkerSuppliesEntry))
        {
            long? cls = row.Int(4), typeCode = row.Int(5), unit = row.Int(7), max = row.Int(8), level = row.Int(9);
            var description = row.Text(6);
            string type = SupplyTypeName(typeCode);
            if (type is "other" or "unknown") type = TypeFromText(description) ?? type;
            bool receptacle = cls == SupplyClassReceptacle || type is "wasteToner" or "wasteInk" or "wasteWax" or "wasteWater" or "wastePaper" or "developerWaste";

            string? colorant = null;
            if (row.Int(3) is { } ci && ci > 0 && colorants.TryGetValue($"{row.DeviceIndex}.{ci}", out var c)) colorant = c.ToLowerInvariant();
            colorant ??= type is "toner" or "ink" or "inkCartridge" or "drum" or "inkRibbon" ? ColorantFromText(description) : null;

            list.Add(new PrinterSupply
            {
                Name = description ?? $"{type}{(colorant is null ? "" : " " + colorant)}",
                Type = type,
                Colorant = colorant,
                IsReceptacle = receptacle,
                Level = level,
                MaxCapacity = max,
                Unit = SupplyUnitName(unit),
                Percent = SupplyPercent(level, max, unit),
                SomeRemaining = level == -3,
            });
        }
        return list;
    }

    /// <summary>Supplies from IPP marker-* attributes (CUPS/AirPrint convention: levels are 0–100, -1/-2 unknown,
    /// -3 some remaining; colours are #RRGGBB). Used when the Printer MIB gave nothing.</summary>
    public static List<PrinterSupply> ParseIppMarkers(IppAttributeGroup printer)
    {
        var names = printer.Texts("marker-names");
        var types = printer.Texts("marker-types");
        var colors = printer.Texts("marker-colors");
        var levels = printer.Ints("marker-levels");
        var list = new List<PrinterSupply>();
        int n = Math.Max(names.Count, levels.Count);
        for (int i = 0; i < n; i++)
        {
            string? name = i < names.Count ? names[i] : null;
            string type = i < types.Count ? NormalizeIppMarkerType(types[i]) : (TypeFromText(name) ?? "other");
            long? level = i < levels.Count ? levels[i] : null;
            bool receptacle = type.StartsWith("waste", StringComparison.OrdinalIgnoreCase);
            string? colorant = i < colors.Count ? ColorantFromHex(colors[i]) : null;
            colorant ??= ColorantFromText(name);
            list.Add(new PrinterSupply
            {
                Name = name ?? type,
                Type = type,
                Colorant = colorant,
                IsReceptacle = receptacle,
                Level = level,
                MaxCapacity = 100,
                Unit = "percent",
                Percent = level is { } l && l >= 0 ? (int)Math.Clamp(l, 0, 100) : null,
                SomeRemaining = level == -3,
            });
        }
        return list;
    }

    private static string NormalizeIppMarkerType(string t) => t.ToLowerInvariant() switch
    {
        "toner" or "tonercartridge" or "toner-cartridge" => "toner",
        "ink" or "inkcartridge" or "ink-cartridge" => "ink",
        "opc" or "drum" => "drum",
        "wastetoner" or "waste-toner" => "wasteToner",
        "wasteink" or "waste-ink" => "wasteInk",
        "fuser" => "fuser",
        "transferunit" or "transfer-unit" => "transfer",
        "staplesupply" or "staples" => "staples",
        "developer" => "developer",
        var other => other,
    };

    public static string? ColorantFromHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var h = hex.Trim().TrimStart('#').ToUpperInvariant();
        if (h.Length != 6) return hex.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : ColorantFromText(hex);
        return h switch
        {
            "000000" => "black",
            "00FFFF" => "cyan",
            "FF00FF" => "magenta",
            "FFFF00" => "yellow",
            "808080" => "gray",
            _ => null,
        };
    }

    // =================================================================== trays, covers, alerts, console

    public static List<PrinterTray> ParseTrays(IEnumerable<SnmpVarBind> inputWalk)
    {
        var list = new List<PrinterTray>();
        foreach (var row in Rows(inputWalk, PrtInputEntry))
        {
            long? max = row.Int(9), level = row.Int(10);
            int? pct = level is { } l && l >= 0 && max is { } m && m > 0 ? (int)Math.Clamp(Math.Round(l * 100d / m), 0, 100) : null;
            list.Add(new PrinterTray
            {
                Name = row.Text(13) ?? row.Text(18) ?? $"Tray {row.Key}",
                Media = row.Text(12),
                Level = level,
                MaxCapacity = max,
                Percent = pct,
                Status = level switch
                {
                    0 => "Empty",
                    -3 => "Some remaining",
                    -2 or -1 or null => "Unknown",
                    _ when pct is { } p && p <= 10 => "Low",
                    _ => "OK",
                },
            });
        }
        return list;
    }

    public static List<string> ParseCovers(IEnumerable<SnmpVarBind> coverWalk)
    {
        var list = new List<string>();
        foreach (var row in Rows(coverWalk, PrtCoverEntry))
        {
            var name = row.Text(2) ?? $"Cover {row.Key}";
            var status = row.Int(3) switch { 1 => "other", 3 => "closed", 4 => "open", 5 => "interlock open", 6 => "interlock closed", _ => "unknown" };
            list.Add($"{name}: {status}");
        }
        return list;
    }

    public static List<PrinterAlert> ParseAlerts(IEnumerable<SnmpVarBind> alertWalk, int cap = 50)
    {
        var list = new List<PrinterAlert>();
        foreach (var row in Rows(alertWalk, PrtAlertEntry))
        {
            if (list.Count >= cap) break;
            list.Add(new PrinterAlert
            {
                Severity = row.Int(2) switch { 3 => "Critical", 4 => "Warning", _ => "Other" },
                Group = AlertGroupName(row.Int(4)),
                Code = row.Int(7) is { } c ? (int)c : null,
                Description = row.Text(8),
            });
        }
        return list;
    }

    public static string? AlertGroupName(long? g) => g switch
    {
        1 => "other", 3 => "host resources", 4 => "general", 5 => "cover", 6 => "localization", 8 => "input", 9 => "output",
        10 => "marker", 11 => "marker supplies", 12 => "marker colorant", 13 => "media path", 14 => "channel", 15 => "interpreter",
        16 => "console display buffer", 17 => "console lights", 18 => "alert", 30 => "finisher device", 31 => "finisher supply",
        32 => "finisher supply media input", 33 => "finisher attribute", _ => null,
    };

    public static List<string> ParseConsoleText(IEnumerable<SnmpVarBind> consoleWalk)
        => consoleWalk.Select(vb => vb.Value.AsText()).Where(t => t is not null).Select(t => t!).ToList();

    // =================================================================== interfaces

    public static List<AdapterInfo> ParseInterfaces(IEnumerable<SnmpVarBind> ifWalk, IEnumerable<SnmpVarBind> ifXWalk, IEnumerable<SnmpVarBind> ipWalk)
    {
        var names = new Dictionary<string, string>();
        var highSpeed = new Dictionary<string, long>();
        foreach (var row in Rows(ifXWalk, IfXEntry))
        {
            if (row.Text(1) is { } n) names[row.Key] = n;
            if (row.Int(15) is { } hs) highSpeed[row.Key] = hs;
        }
        var ipsByIf = new Dictionary<string, List<string>>();
        foreach (var vb in ipWalk)
        {
            var idx = vb.Oid.IndexAfter(IpAdEntIfIndex);
            if (idx.Length == 4 && vb.Value.Int is { } ifIndex)
            {
                var ip = string.Join(".", idx);
                if (ip == "0.0.0.0" || ip.StartsWith("127.")) continue;
                if (!ipsByIf.TryGetValue(ifIndex.ToString(), out var l)) ipsByIf[ifIndex.ToString()] = l = new List<string>();
                l.Add(ip);
            }
        }

        var list = new List<AdapterInfo>();
        foreach (var row in Rows(ifWalk, IfEntry))
        {
            long? type = row.Int(3);
            if (type is 24) continue; // softwareLoopback
            var a = new AdapterInfo
            {
                Name = names.TryGetValue(row.Key, out var n) && n.Length > 0 ? n : row.Text(2) ?? $"if{row.Key}",
                Mac = row[6]?.AsMac(),
                SpeedBps = highSpeed.TryGetValue(row.Key, out var hs) && hs > 0 ? hs * 1_000_000L : row.Int(5) ?? 0,
            };
            if (ipsByIf.TryGetValue(row.Key, out var ips)) a.IpAddresses.AddRange(ips);
            list.Add(a);
        }
        return list;
    }

    /// <summary>Count of interfaces with ifOperStatus up(1), for the network-device summary.</summary>
    public static (int Total, int Up) CountInterfaces(IEnumerable<SnmpVarBind> ifWalk)
    {
        int total = 0, up = 0;
        foreach (var row in Rows(ifWalk, IfEntry))
        {
            if (row.Int(3) is 24) continue;
            total++;
            if (row.Int(8) == 1) up++;
        }
        return (total, up);
    }

    // =================================================================== identity helpers

    /// <summary>Vendor from sysObjectID's enterprise arc (1.3.6.1.4.1.N…).</summary>
    public static string? VendorFromObjectId(SnmpOid? objectId)
    {
        if (objectId is not { } oid || oid.Length < 7) return null;
        var ent = new SnmpOid(1, 3, 6, 1, 4, 1);
        if (!ent.IsPrefixOf(oid)) return null;
        return EnterpriseVendors.TryGetValue(oid[6], out var v) ? v : null;
    }

    /// <summary>Serial/asset strings that are really placeholders.</summary>
    public static bool IsPlaceholder(string? s)
        => string.IsNullOrWhiteSpace(s) || s.Trim() is "0" or "-" or "N/A" or "n/a" or "None" or "none" or "Unknown" or "unknown"
           || s.Trim().All(c => c == '0' || c == 'X' || c == 'x' || c == '?' || c == '.');
}
