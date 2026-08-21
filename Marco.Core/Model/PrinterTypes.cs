namespace Marco.Core.Model;

/// <summary>
/// Device-side printer inventory, read from the printer itself over SNMP (Printer MIB / Host Resources MIB) and
/// IPP — as opposed to <see cref="PrinterEntry"/>, which is a print *queue* on a Windows host. Plain settable
/// properties so the object serializes as-is in scan files.
/// </summary>
public sealed class PrinterDevice
{
    /// <summary>hrPrinterStatus: "Idle" / "Printing" / "Warming up" / "Idle (sleep)" / "Unknown".</summary>
    public string? Status { get; set; }
    /// <summary>hrDeviceStatus: "Running" / "Warning" / "Testing" / "Down" / "Unknown".</summary>
    public string? DeviceStatus { get; set; }
    /// <summary>Decoded hrPrinterDetectedErrorState flags ("Low toner", "Paper jam", "Door open", …).</summary>
    public List<string> ErrorStates { get; set; } = new();
    /// <summary>What the front-panel display currently shows (prtConsoleDisplayBuffer), one line per entry.</summary>
    public List<string> DisplayText { get; set; } = new();
    public List<PrinterAlert> Alerts { get; set; } = new();
    public List<PrinterSupply> Supplies { get; set; } = new();
    public List<PrinterTray> Trays { get; set; } = new();
    /// <summary>Covers/doors and their state ("Front door: closed").</summary>
    public List<string> Covers { get; set; } = new();

    /// <summary>prtMarkerLifeCount of the primary marker — the engine page (impression) counter.</summary>
    public long? TotalPages { get; set; }
    /// <summary>prtMarkerCounterUnit name ("impressions", "sheets", …).</summary>
    public string? PageCountUnit { get; set; }
    /// <summary>Vendor-private colour / mono counters where a known OID exists.</summary>
    public long? ColorPages { get; set; }
    public long? MonoPages { get; set; }

    public string? Firmware { get; set; }
    public string? SysName { get; set; }
    public string? Location { get; set; }
    public string? Contact { get; set; }
    public TimeSpan? Uptime { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? UptimeDisplay => PrinterErrorStates.DescribeUptime(Uptime);
    /// <summary>sysObjectID — the vendor's enterprise OID for the model, useful for later vendor lookups.</summary>
    public string? ObjectId { get; set; }
    /// <summary>Raw sysDescr (on Brother this names the print server NIC rather than the printer).</summary>
    public string? Description { get; set; }

    // --- queue (IPP) ---
    /// <summary>Jobs waiting on the printer itself (IPP queued-job-count, else the size of Get-Jobs).</summary>
    public int? QueuedJobs { get; set; }
    public List<PrintJobEntry> Jobs { get; set; } = new();
    /// <summary>IPP printer-state: "idle" / "processing" / "stopped".</summary>
    public string? IppState { get; set; }
    public List<string> IppStateReasons { get; set; } = new();
    public string? IppUri { get; set; }
    public string? MakeAndModel { get; set; }

    /// <summary>Which protocols answered ("SNMP v2c", "IPP").</summary>
    public List<string> Sources { get; set; } = new();

    /// <summary>Number of supplies at or below the low threshold (or flagged low/empty by the device).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int LowSupplyCount => Supplies.Count(s => s.IsLow || s.IsEmpty);

    /// <summary>True when the device reports a condition that needs a person: jam, door open, out of toner, down…</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasErrorCondition =>
        string.Equals(DeviceStatus, "Down", StringComparison.OrdinalIgnoreCase)
        || ErrorStates.Any(PrinterErrorStates.IsBlocking)
        || string.Equals(IppState, "stopped", StringComparison.OrdinalIgnoreCase);

    /// <summary>"K 12% · C 80% · M 75% · Y 70%" — the colour-coded compact supply line for grids and reports.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SuppliesSummary
    {
        get
        {
            var parts = Supplies.Where(s => !s.IsReceptacle && s.Percent is not null)
                .Select(s => $"{s.ShortName} {s.Percent}%").ToList();
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }
}

/// <summary>One row of prtMarkerSuppliesTable (toner, ink, drum, fuser, waste container…) or an IPP marker.</summary>
public sealed class PrinterSupply
{
    public string? Name { get; set; }
    /// <summary>"toner" / "ink" / "drum" / "fuser" / "wasteToner" / "transfer" / "staples" / …</summary>
    public string? Type { get; set; }
    /// <summary>"black" / "cyan" / "magenta" / "yellow" / … when the supply is colour-specific.</summary>
    public string? Colorant { get; set; }
    /// <summary>True for containers that fill up (waste toner/ink) — the percentage is how *full* they are.</summary>
    public bool IsReceptacle { get; set; }
    public long? Level { get; set; }
    public long? MaxCapacity { get; set; }
    /// <summary>Unit name ("percent", "tenths of grams", "impressions", …).</summary>
    public string? Unit { get; set; }
    /// <summary>Remaining (or, for receptacles, filled) percentage when it can be computed.</summary>
    public int? Percent { get; set; }
    /// <summary>True when the device said "some remaining" without a number (Brother's standard-MIB answer).</summary>
    public bool SomeRemaining { get; set; }
    /// <summary>Set from the device's own low/empty flags when there is no number to judge by.</summary>
    public bool DeviceFlagsLow { get; set; }
    public bool DeviceFlagsEmpty { get; set; }

    public const int LowThresholdPercent = 10;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => DeviceFlagsEmpty || (!IsReceptacle && Percent is 0) || (IsReceptacle && Percent is >= 100);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLow => !IsEmpty && (DeviceFlagsLow
        || (!IsReceptacle && Percent is { } p && p <= LowThresholdPercent)
        || (IsReceptacle && Percent is { } f && f >= 100 - LowThresholdPercent));

    /// <summary>"12 %" / "Some remaining" / "OK" / "Unknown" / "Empty".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string LevelDisplay =>
        IsEmpty ? (IsReceptacle ? "Full" : "Empty")
        : Percent is { } p ? $"{p} %" + (IsReceptacle ? " full" : "")
        : SomeRemaining ? "Some remaining"
        : DeviceFlagsLow ? "Low"
        : Level is { } l && l >= 0 ? $"{l}{(Unit is { } u ? " " + u : "")}"
        : "Unknown";

    /// <summary>"K" / "C" / "M" / "Y" for colour consumables (toner/ink), "Drum" / "Fuser" / "Waste" for the
    /// maintenance parts (a black drum is still "Drum"), otherwise a short form of the name.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ShortName => Type switch
    {
        "drum" or "opc" => "Drum",
        "fuser" => "Fuser",
        "transfer" => "Transfer",
        "wasteToner" or "wasteInk" or "wasteWax" or "wasteWater" => "Waste",
        "developer" => "Developer",
        "staples" => "Staples",
        "maintenanceKit" => "Maint. kit",
        _ => Colorant?.ToLowerInvariant() switch
        {
            "black" => "K",
            "cyan" => "C",
            "magenta" => "M",
            "yellow" => "Y",
            "light cyan" or "lightcyan" => "LC",
            "light magenta" or "lightmagenta" => "LM",
            "photo black" or "matte black" => "PK",
            "gray" => "Gray",
            _ => Name is { Length: > 0 } n ? (n.Length > 12 ? n[..12] : n) : "?",
        },
    };
}

public sealed class PrinterTray
{
    public string? Name { get; set; }
    public string? Media { get; set; }
    public long? Level { get; set; }
    public long? MaxCapacity { get; set; }
    public int? Percent { get; set; }
    /// <summary>"OK" / "Empty" / "Low" / "Unknown" / "Some remaining".</summary>
    public string? Status { get; set; }
}

public sealed class PrinterAlert
{
    /// <summary>"Critical" / "Warning" / "Other".</summary>
    public string? Severity { get; set; }
    public string? Group { get; set; }
    public int? Code { get; set; }
    public string? Description { get; set; }
}

/// <summary>A job on the printer's own queue (IPP Get-Jobs).</summary>
public sealed class PrintJobEntry
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? User { get; set; }
    /// <summary>"pending" / "held" / "processing" / "stopped" / …</summary>
    public string? State { get; set; }
    public int? Impressions { get; set; }
    public DateTime? Created { get; set; }
}

/// <summary>A Windows print queue (from another inventoried host) that points at this printer — derived after a
/// run by <c>PrintServerQueueLinker</c>, never serialized.</summary>
public sealed class PrintServerQueue
{
    public string ServerAddress { get; set; } = "";
    public string? ServerName { get; set; }
    public string QueueName { get; set; } = "";
    public string? ShareName { get; set; }
    public bool Shared { get; set; }
    public string? Status { get; set; }
    public int? QueuedJobs { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Display => $"{ServerName ?? ServerAddress}\\{QueueName}"
        + (QueuedJobs is { } q ? $" · {q} queued" : "")
        + (Shared && ShareName is { Length: > 0 } s ? $" (shared as {s})" : "")
        + (Status is { Length: > 0 } st && st != "OK" ? $" · {st}" : "");
}

/// <summary>Generic SNMP system facts for switches, access points, NAS and anything else that answers SNMP but
/// isn't a printer.</summary>
public sealed class NetworkDeviceInfo
{
    public string? SysName { get; set; }
    public string? Description { get; set; }
    public string? Location { get; set; }
    public string? Contact { get; set; }
    public TimeSpan? Uptime { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? UptimeDisplay => PrinterErrorStates.DescribeUptime(Uptime);
    public string? ObjectId { get; set; }
    public int? InterfaceCount { get; set; }
    public int? InterfacesUp { get; set; }
    public string? Firmware { get; set; }
    public List<string> Sources { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public string? InterfacesDisplay => InterfaceCount is { } n ? $"{InterfacesUp ?? 0} of {n} interfaces up" : null;
}

/// <summary>Names for the hrPrinterDetectedErrorState bits, shared by the parser and the report.</summary>
public static class PrinterErrorStates
{
    public const string LowPaper = "Low paper", NoPaper = "Out of paper", LowToner = "Low toner", NoToner = "Out of toner";
    public const string DoorOpen = "Door open", Jammed = "Paper jam", Offline = "Offline", ServiceRequested = "Service requested";
    public const string InputTrayMissing = "Input tray missing", OutputTrayMissing = "Output tray missing";
    public const string MarkerSupplyMissing = "Supply missing", OutputNearFull = "Output near full", OutputFull = "Output full";
    public const string InputTrayEmpty = "Input tray empty", OverduePreventMaint = "Maintenance overdue";

    /// <summary>Conditions that stop printing until someone acts (as opposed to "low toner").</summary>
    public static bool IsBlocking(string state) => state is NoPaper or NoToner or DoorOpen or Jammed or Offline
        or ServiceRequested or InputTrayMissing or OutputTrayMissing or MarkerSupplyMissing or OutputFull;

    /// <summary>"10 d 4 h" / "3 h 12 m" / "45 m" from an sysUpTime span; null when unknown.</summary>
    public static string? DescribeUptime(TimeSpan? up)
    {
        if (up is not { } t || t < TimeSpan.Zero) return null;
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} d {t.Hours} h";
        if (t.TotalHours >= 1) return $"{t.Hours} h {t.Minutes} m";
        return $"{Math.Max(0, t.Minutes)} m";
    }
}
