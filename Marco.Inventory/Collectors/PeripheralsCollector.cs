using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;

namespace Marco.Inventory.Collectors;

/// <summary>
/// The hardware around the box: monitors with their EDID serials (the most-requested missing asset field), GPUs
/// and driver versions, printers (with the TCP/IP port address so a queue can be tied to a discovered printer),
/// USB devices currently attached, battery health, and the ACPI thermal zone where the firmware exposes one.
/// Independent probes; root\WMI needs admin, which remote WMI already implies.
/// </summary>
public sealed class PeripheralsCollector : IInventoryCollector
{
    public string Name => "Peripherals";

    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public async Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct)
    {
        var steps = new CollectorSteps();
        var wmi = context.Wmi;

        // 1. Monitors (EDID via root\WMI).
        await steps.RunAsync("Monitors", async () =>
        {
            var ids = await wmi.QueryAsync(WmiQueryHelpers.Wmi,
                "SELECT InstanceName, Active, ManufacturerName, ProductCodeID, SerialNumberID, UserFriendlyName, YearOfManufacture, WeekOfManufacture FROM WmiMonitorID", ct);
            var sizes = await wmi.QueryOptionalAsync(
                "SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams", ct, WmiQueryHelpers.Wmi);
            var sizeByInstance = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sizes)
            {
                var inst = s.GetString("InstanceName");
                var d = DiagonalInches(s.GetInt("MaxHorizontalImageSize"), s.GetInt("MaxVerticalImageSize"));
                if (inst is not null && d is { } di) sizeByInstance[inst] = di;
            }

            var monitors = new List<MonitorEntry>();
            foreach (var r in ids)
            {
                var inst = r.GetString("InstanceName");
                var mfg = DecodeWmiString(r["ManufacturerName"]);
                monitors.Add(new MonitorEntry
                {
                    Manufacturer = MonitorVendorName(mfg),
                    Model = DecodeWmiString(r["UserFriendlyName"]),
                    Serial = DecodeWmiString(r["SerialNumberID"]),
                    ProductCode = DecodeWmiString(r["ProductCodeID"]),
                    Year = r.GetInt("YearOfManufacture") is { } y and > 1990 ? y : null,
                    Week = r.GetInt("WeekOfManufacture") is { } w and > 0 ? w : null,
                    DiagonalInches = inst is not null && sizeByInstance.TryGetValue(inst, out var di) ? di : null,
                    Active = r.GetBool("Active") ?? true,
                });
            }
            machine.Monitors = monitors;
        });

        // 2. GPUs.
        ct.ThrowIfCancellationRequested();
        await steps.RunAsync("Video controllers", async () =>
        {
            var rows = await wmi.QueryAsync(WmiQueryHelpers.CimV2,
                "SELECT Name, AdapterRAM, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController", ct);
            var gpus = new List<GpuInfo>();
            foreach (var r in rows)
            {
                var name = r.GetString("Name")?.Trim();
                if (string.IsNullOrEmpty(name) || IsVirtualDisplayAdapter(name)) continue; // RDP/session adapters come and go
                var h = r.GetInt("CurrentHorizontalResolution"); var v = r.GetInt("CurrentVerticalResolution"); var hz = r.GetInt("CurrentRefreshRate");
                gpus.Add(new GpuInfo
                {
                    Name = name,
                    VramBytes = (long)(r.GetULong("AdapterRAM") ?? 0),
                    DriverVersion = r.GetString("DriverVersion"),
                    DriverDate = r.GetDateTime("DriverDate"),
                    Resolution = h is > 0 && v is > 0 ? $"{h}x{v}" + (hz is > 0 ? $" @ {hz} Hz" : "") : null,
                });
            }
            machine.Gpus = gpus;

            // AdapterRAM is a 32-bit field and caps at 4 GB; the display class key carries the real size (QWORD),
            // readable over the SMB registry path. Best-effort.
            try
            {
                var keys = context.Registry.EnumerateSubkeys(RegistryRoot.LocalMachine, DisplayClassKey,
                    new[] { "DriverDesc", "HardwareInformation.qwMemorySize" });
                foreach (var k in keys)
                {
                    var desc = RegistryValues.AsString(RegistryValues.Get(k.Values, "DriverDesc"));
                    var qw = RegistryValues.AsLong(RegistryValues.Get(k.Values, "HardwareInformation.qwMemorySize"));
                    if (desc is null || qw is not { } bytes || bytes <= 0) continue;
                    var gpu = gpus.FirstOrDefault(g => string.Equals(g.Name, desc, StringComparison.OrdinalIgnoreCase));
                    if (gpu is not null && bytes > gpu.VramBytes) gpu.VramBytes = bytes;
                }
            }
            catch { /* registry unavailable — keep the 32-bit value */ }
        });

        // 3. Printers.
        ct.ThrowIfCancellationRequested();
        await steps.RunAsync("Printers", async () =>
        {
            var rows = await wmi.QueryAsync(WmiQueryHelpers.CimV2,
                "SELECT Name, Default, PortName, DriverName, Shared, ShareName, Network, Location, PrinterStatus, WorkOffline FROM Win32_Printer", ct);
            var ports = await wmi.QueryOptionalAsync("SELECT Name, HostAddress FROM Win32_TCPIPPrinterPort", ct);
            var hostByPort = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in ports)
                if (p.GetString("Name") is { } pn && p.GetString("HostAddress") is { } ha) hostByPort[pn] = ha;

            var printers = new List<PrinterEntry>();
            foreach (var r in rows)
            {
                var name = r.GetString("Name")?.Trim();
                if (string.IsNullOrEmpty(name) || IsBuiltInVirtualPrinter(name)) continue;
                var port = r.GetString("PortName");
                printers.Add(new PrinterEntry
                {
                    Name = name,
                    IsDefault = r.GetBool("Default") ?? false,
                    PortName = port,
                    DriverName = r.GetString("DriverName"),
                    Shared = r.GetBool("Shared") ?? false,
                    ShareName = r.GetString("ShareName"),
                    IsNetwork = r.GetBool("Network") ?? false,
                    Location = r.GetString("Location"),
                    HostAddress = port is not null && hostByPort.TryGetValue(port, out var host) ? host : null,
                    Status = r.GetBool("WorkOffline") == true ? "Offline" : DescribePrinterStatus(r.GetInt("PrinterStatus")),
                });
            }
            machine.Printers = printers.OrderBy(p => !p.IsDefault).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        });

        // 4. USB devices currently attached (hubs and controllers filtered out).
        ct.ThrowIfCancellationRequested();
        await steps.RunAsync("USB devices", async () =>
        {
            // SELECT * so the query is valid on Windows 7 (no PNPClass property there); the row count is small.
            var rows = await wmi.QueryAsync(WmiQueryHelpers.CimV2,
                "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\%'", ct);
            var usb = new List<UsbDeviceEntry>();
            foreach (var r in rows)
            {
                var name = r.GetString("Name")?.Trim();
                if (string.IsNullOrEmpty(name) || IsUsbInfrastructure(name, r.GetString("Service"))) continue;
                usb.Add(new UsbDeviceEntry
                {
                    Name = name,
                    Manufacturer = r.GetString("Manufacturer")?.Trim(),
                    PnpClass = r.GetString("PNPClass"),
                    DeviceId = r.GetString("DeviceID"),
                    Status = r.GetString("Status"),
                });
            }
            usb.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            machine.UsbDevices = usb;
        });

        // 5. Battery.
        ct.ThrowIfCancellationRequested();
        await steps.RunAsync("Battery", async () =>
        {
            var rows = await wmi.QueryAsync(WmiQueryHelpers.CimV2,
                "SELECT Name, EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery", ct);
            if (rows.Count == 0) { machine.Battery = null; return; }
            var b = rows[0];
            var info = new BatteryInfo
            {
                Name = b.GetString("Name")?.Trim(),
                ChargePercent = b.GetInt("EstimatedChargeRemaining"),
                Status = DescribeBatteryStatus(b.GetInt("BatteryStatus")),
            };
            var full = await wmi.QueryOptionalAsync("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", ct, WmiQueryHelpers.Wmi);
            var design = await wmi.QueryOptionalAsync("SELECT DesignedCapacity FROM BatteryStaticData", ct, WmiQueryHelpers.Wmi);
            var cycles = await wmi.QueryOptionalAsync("SELECT CycleCount FROM BatteryCycleCount", ct, WmiQueryHelpers.Wmi);
            info.FullChargeCapacityMwh = full.FirstOrDefault()?.GetInt("FullChargedCapacity");
            info.DesignCapacityMwh = design.FirstOrDefault()?.GetInt("DesignedCapacity");
            info.CycleCount = cycles.FirstOrDefault()?.GetInt("CycleCount") is { } c and > 0 ? c : null;
            info.HealthPercent = BatteryHealth(info.FullChargeCapacityMwh, info.DesignCapacityMwh);
            machine.Battery = info;
        });

        // 6. Thermal zone (rarely exposed on desktops — expect "not supported").
        ct.ThrowIfCancellationRequested();
        await steps.RunAsync("Thermal zone", async () =>
        {
            var rows = await wmi.QueryAsync(WmiQueryHelpers.Wmi,
                "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", ct);
            int? hottest = null;
            foreach (var r in rows)
            {
                var c = ThermalZoneCelsius(r.GetInt("CurrentTemperature"));
                if (c is { } t && (hottest is null || t > hottest)) hottest = t;
            }
            machine.ThermalTempC = hottest;
        });

        machine.RefreshCounts();
        steps.ThrowIfNothingSucceeded();
    }

    // --- pure helpers (unit-tested) ---

    /// <summary>root\WMI monitor strings are uint16 code arrays, zero-terminated.</summary>
    public static string? DecodeWmiString(object? value)
    {
        IEnumerable<int>? codes = value switch
        {
            ushort[] u => u.Select(x => (int)x),
            short[] s => s.Select(x => (int)x),
            int[] i => i,
            uint[] u => u.Select(x => (int)x),
            byte[] b => b.Select(x => (int)x),
            string str => str.Select(c => (int)c),
            _ => null,
        };
        if (codes is null) return null;
        var chars = codes.TakeWhile(c => c != 0).Where(c => c is >= 32 and < 0xFFFF).Select(c => (char)c).ToArray();
        var text = new string(chars).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>EDID three-letter PnP vendor IDs for the common brands; unknown IDs pass through.</summary>
    public static string? MonitorVendorName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return code.ToUpperInvariant() switch
        {
            "DEL" => "Dell", "SAM" => "Samsung", "LEN" => "Lenovo", "GSM" => "LG", "AUS" => "ASUS", "ACR" => "Acer",
            "BNQ" => "BenQ", "HWP" or "HPN" => "HP", "PHL" => "Philips", "AOC" => "AOC", "VSC" => "ViewSonic",
            "IVM" => "iiyama", "NEC" => "NEC", "SNY" => "Sony", "APP" => "Apple", "MSI" => "MSI", "GBT" => "Gigabyte",
            "EIZ" or "ENC" => "Eizo", "SHP" => "Sharp", "CMN" => "Chimei Innolux", "AUO" => "AU Optronics",
            "BOE" => "BOE", "LGD" => "LG Display", "SDC" => "Samsung Display", "SEC" => "Seiko Epson",
            "HSD" => "HannStar", "MEI" => "Panasonic", "TOS" => "Toshiba", "FUS" => "Fujitsu", "HPQ" => "HP",
            _ => code,
        };
    }

    /// <summary>EDID image size in cm → diagonal inches (one decimal); null when either side is missing.</summary>
    public static double? DiagonalInches(int? widthCm, int? heightCm)
    {
        if (widthCm is not { } w || heightCm is not { } h || w <= 0 || h <= 0) return null;
        return Math.Round(Math.Sqrt((double)w * w + (double)h * h) / 2.54, 1);
    }

    /// <summary>Full-charge capacity as a percentage of design capacity, capped at 100.</summary>
    public static double? BatteryHealth(int? fullMwh, int? designMwh)
    {
        if (fullMwh is not { } f || designMwh is not { } d || f <= 0 || d <= 0) return null;
        return Math.Round(Math.Min(100d, 100d * f / d), 0);
    }

    /// <summary>MSAcpi_ThermalZoneTemperature.CurrentTemperature is tenths of a kelvin.</summary>
    public static int? ThermalZoneCelsius(int? tenthsKelvin)
    {
        if (tenthsKelvin is not { } t || t <= 0) return null;
        var c = t / 10.0 - 273.15;
        return c is > -50 and < 200 ? (int)Math.Round(c) : null;
    }

    public static string? DescribeBatteryStatus(int? status) => status switch
    {
        1 => "Discharging",
        2 => "On AC",
        3 => "Fully charged",
        4 => "Low",
        5 => "Critical",
        6 => "Charging",
        7 => "Charging (high)",
        8 => "Charging (low)",
        9 => "Charging (critical)",
        10 => "Undefined",
        11 => "Partially charged",
        _ => null,
    };

    public static string? DescribePrinterStatus(int? status) => status switch
    {
        1 => "Other", 2 => "Unknown", 3 => "Idle", 4 => "Printing", 5 => "Warming up",
        6 => "Stopped printing", 7 => "Offline", _ => null,
    };

    private static readonly string[] BuiltInVirtualPrinters =
    {
        "Microsoft Print to PDF", "Microsoft XPS Document Writer", "Fax", "OneNote", "Send To OneNote",
    };

    public static bool IsBuiltInVirtualPrinter(string name)
        => BuiltInVirtualPrinters.Any(v => name.StartsWith(v, StringComparison.OrdinalIgnoreCase));

    /// <summary>Session-scoped display adapters (RDP, Hyper-V console) that aren't hardware.</summary>
    public static bool IsVirtualDisplayAdapter(string name)
        => name.StartsWith("Microsoft Remote Display Adapter", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft Hyper-V Video", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] UsbInfrastructureNames =
    {
        "Root Hub", "Host Controller", "Composite Device", "Generic USB Hub", "USB Hub", "Generic SuperSpeed USB Hub",
    };

    public static bool IsUsbInfrastructure(string name, string? service)
        => UsbInfrastructureNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))
        || service is not null && (service.Equals("usbhub", StringComparison.OrdinalIgnoreCase)
                                   || service.Equals("USBHUB3", StringComparison.OrdinalIgnoreCase)
                                   || service.Equals("usbccgp", StringComparison.OrdinalIgnoreCase)
                                   || service.StartsWith("USBXHCI", StringComparison.OrdinalIgnoreCase)
                                   || service.StartsWith("usbehci", StringComparison.OrdinalIgnoreCase));
}
