using System.Globalization;
using System.Text;

namespace Marco.Export;

/// <summary>
/// Excel-importable CSV export. List-valued data (software, disks, adapters, hotfixes, services, …) can't live in
/// a one-row-per-machine file, so it is written to companion files keyed by machine address alongside
/// machines.csv. The main file carries summary counts and the headline security/servicing facts so a single-file
/// export is still meaningful; security.csv has the full posture, one row per machine.
/// </summary>
public sealed class CsvExporter
{
    /// <summary>Write the machine + companion CSVs into <paramref name="directory"/>. Returns the file paths written.</summary>
    public IReadOnlyList<string> Export(ScanDocument doc, string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();

        written.Add(WriteMachines(doc, Path.Combine(directory, "machines.csv")));
        written.Add(WriteSoftware(doc, Path.Combine(directory, "software.csv")));
        written.Add(WriteDisks(doc, Path.Combine(directory, "disks.csv")));
        written.Add(WriteAdapters(doc, Path.Combine(directory, "adapters.csv")));
        written.Add(WriteSecurity(doc, Path.Combine(directory, "security.csv")));
        written.Add(WriteHotfixes(doc, Path.Combine(directory, "hotfixes.csv")));
        written.Add(WriteServices(doc, Path.Combine(directory, "services.csv")));
        written.Add(WriteStartup(doc, Path.Combine(directory, "startup.csv")));
        written.Add(WriteAccounts(doc, Path.Combine(directory, "accounts.csv")));
        written.Add(WriteProfiles(doc, Path.Combine(directory, "profiles.csv")));
        written.Add(WriteMonitors(doc, Path.Combine(directory, "monitors.csv")));
        written.Add(WriteGpus(doc, Path.Combine(directory, "gpus.csv")));
        written.Add(WritePrinters(doc, Path.Combine(directory, "printers.csv")));
        written.Add(WriteUsb(doc, Path.Combine(directory, "usb.csv")));

        WriteMetadata(doc, Path.Combine(directory, "scan-info.txt"));
        return written;
    }

    /// <summary>Single-file variant: just the machine rows (for a quick Excel import).</summary>
    public string WriteMachines(ScanDocument doc, string path)
    {
        var sb = new StringBuilder();
        Row(sb, "Address", "Name", "FQDN", "Type", "Status", "Vendor", "MAC", "Virtual",
            "Manufacturer", "Model", "Serial", "AssetTag", "Chassis", "Domain", "User", "LastLoggedOnUser",
            "OS", "OSVersion", "OSBuild", "Release", "FullBuild", "Edition", "Arch", "InstallDate", "LastBoot",
            "CPU", "Cores", "LogicalCPUs", "RAM_GB", "SlotsUsed", "SlotsTotal",
            "Disks", "SoftwareCount", "Adapters", "BIOS", "Motherboard",
            "Hotfixes", "LastHotfix", "PendingReboot", "WSUS",
            "Antivirus", "Defender", "Firewall", "BitLocker", "TPM", "SecureBoot", "Firmware", "RDP", "SMB", "LAPS",
            "LocalAdmins", "Services", "AutoServicesStopped", "Monitors", "GPU", "Battery", "OpenPorts", "LastScanned");

        foreach (var m in doc.Machines)
        {
            var cpu = m.Cpus.FirstOrDefault();
            var u = m.Updates; var s = m.Security;
            Row(sb,
                m.Address, m.Name, m.Fqdn, m.DeviceType.ToString(), m.Status.ToString(), m.Vendor,
                string.Join(" ", m.MacAddresses), m.IsVirtual ? "Yes" : "",
                m.System.Manufacturer, m.System.Model, m.System.SerialNumber, m.System.AssetTag, m.System.ChassisType,
                m.System.Domain, m.System.CurrentOrLastUser, m.System.LastLoggedOnUser,
                m.Os.Caption, m.Os.Version, m.Os.Build, u?.DisplayVersion, u?.FullBuild, u?.EditionId, m.Os.Architecture,
                Date(m.Os.InstallDate), Date(m.Os.LastBoot),
                cpu?.Name, Num(cpu?.Cores), Num(cpu?.LogicalProcessors),
                Gb(m.TotalMemoryBytes), Num(m.MemorySlotsUsed), Num(m.MemorySlotsTotal),
                Num(m.Disks.Count), Num(m.Software.Count), Num(m.Adapters.Count),
                m.System.BiosVersion, m.System.MotherboardModel,
                Num(m.Hotfixes?.Count), Date(u?.LastHotfixDate), u?.PendingRebootDisplay, u?.WsusDisplay,
                AntivirusSummary(m), s?.DefenderSummary, s?.FirewallSummary, s?.BitLockerSummary, s?.TpmSummary,
                YesNo(s?.SecureBoot), s?.FirmwareType, s?.RdpSummary, s?.SmbSummary, s?.LapsSummary,
                m.LocalAdministrators is { Count: > 0 } la ? string.Join("; ", la) : "",
                Num(m.Services?.Count), Num(m.Services?.Count(x => IsAutoStopped(x))),
                Num(m.Monitors?.Count), m.Gpus?.FirstOrDefault()?.Name, m.Battery?.Summary,
                m.OpenPorts is { Count: > 0 } op ? string.Join(" ", op) : "",
                DateTime2(m.LastScanned));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // BOM for Excel
        return path;
    }

    private string WriteSoftware(ScanDocument doc, string path)
    {
        var sb = new StringBuilder();
        Row(sb, "MachineAddress", "MachineName", "DisplayName", "Version", "Publisher", "InstallDate", "Source");
        foreach (var m in doc.Machines)
            foreach (var s in m.Software)
                Row(sb, m.Address, m.Name, s.DisplayName, s.Version, s.Publisher, Date(s.InstallDate), s.Source.ToString());
        return Write(path, sb);
    }

    private string WriteDisks(ScanDocument doc, string path)
    {
        var sb = new StringBuilder();
        Row(sb, "MachineAddress", "MachineName", "Model", "Size_GB", "MediaType", "Bus", "Health", "SmartStatus",
            "Temp_C", "Wear_Pct", "PowerOnHours", "Firmware", "PartitionStyle", "Serial");
        foreach (var m in doc.Machines)
            foreach (var d in m.Disks)
                Row(sb, m.Address, m.Name, d.Model, Gb(d.SizeBytes), d.MediaType, d.BusType, d.HealthStatus, d.SmartStatus,
                    Num(d.TempC), Num(d.WearPercent), d.PowerOnHours?.ToString(CultureInfo.InvariantCulture),
                    d.Firmware, d.PartitionStyle, d.Serial);
        return Write(path, sb);
    }

    private string WriteAdapters(ScanDocument doc, string path)
    {
        var sb = new StringBuilder();
        Row(sb, "MachineAddress", "MachineName", "Adapter", "MAC", "Speed_Mbps", "IPs", "Subnet", "Gateway", "DNS", "DHCP");
        foreach (var m in doc.Machines)
            foreach (var a in m.Adapters)
                Row(sb, m.Address, m.Name, a.Name, a.Mac,
                    a.SpeedBps > 0 ? (a.SpeedBps / 1_000_000).ToString(CultureInfo.InvariantCulture) : "",
                    string.Join(" ", a.IpAddresses), a.SubnetMask, a.Gateway,
                    string.Join(" ", a.DnsServers), a.DhcpEnabled ? "Yes" : "No");
        return Write(path, sb);
    }

    private string WriteSecurity(ScanDocument doc, string path)
    {
        var sb = new StringBuilder();
        Row(sb, "MachineAddress", "MachineName", "Antivirus", "AntivirusEnabled", "AntivirusUpToDate",
            "DefenderEnabled", "DefenderRealTime", "DefenderSignatureVersion", "DefenderSignatureAgeDays", "DefenderLastQuickScan", "DefenderTamperProtected",
            "FirewallDomain", "FirewallPrivate", "FirewallPublic", "BitLocker",
            "TpmPresent", "TpmVersion", "TpmEnabled", "TpmActivated", "SecureBoot", "Firmware",
            "UacEnabled", "UacAdminPrompt", "LocalAccountTokenFilterPolicy",
            "RdpEnabled", "RdpNlaRequired", "RdpPort", "Smb1Enabled", "SmbSigningRequired", "SmbEncryptData",
            "VBS", "CredentialGuard", "HVCI", "LAPS", "LocalAdministrators", "Notes");
        foreach (var m in doc.Machines)
        {
            var s = m.Security;
            if (s is null) continue;
            var av = m.Antivirus?.FirstOrDefault(a => a.Kind == "Antivirus");
            Row(sb, m.Address, m.Name, av?.Product, YesNo(av?.Enabled), YesNo(av?.UpToDate),
                YesNo(s.DefenderEnabled), YesNo(s.DefenderRealTime), s.DefenderSignatureVersion, Num(s.DefenderSignatureAgeDays),
                DateTime2(s.DefenderLastQuickScan), YesNo(s.DefenderTamperProtected),
                YesNo(s.FirewallDomain), YesNo(s.FirewallPrivate), YesNo(s.FirewallPublic), s.BitLockerSummary,
                YesNo(s.TpmPresent), s.TpmVersion, YesNo(s.TpmEnabled), YesNo(s.TpmActivated), YesNo(s.SecureBoot), s.FirmwareType,
                YesNo(s.UacEnabled), s.UacAdminPrompt, YesNo(s.LocalAccountTokenFilterPolicy),
                YesNo(s.RdpEnabled), YesNo(s.RdpNlaRequired), Num(s.RdpPort), YesNo(s.Smb1Enabled), YesNo(s.SmbSigningRequired), YesNo(s.SmbEncryptData),
                s.VbsStatus, YesNo(s.CredentialGuardRunning), YesNo(s.HvciRunning), s.LapsSummary,
                m.LocalAdministrators is { Count: > 0 } la ? string.Join("; ", la) : "", s.Notes);
        }
        return Write(path, sb);
    }

    private string WriteHotfixes(ScanDocument doc, string path)
    {
        var sb = new StringBuilder();
        Row(sb, "MachineAddress", "MachineName", "HotfixId", "Description", "InstalledOn", "InstalledBy");
        foreach (var m in doc.Machines)
            foreach (var h in m.Hotfixes ?? Array.Empty<Marco.Core.Model.HotfixEntry>())
                Row(sb, m.Address, m.Name, h.Id, h.Description, Date(h.InstalledOn), h.InstalledBy);
        return Write(path, sb);
    }

    private string WriteServices(ScanDocument doc, string path)
    {
        var sb = new StringBuilder();
        Row(sb, "MachineAddress", "MachineName", "Name", "DisplayName", "State", "StartMode", "Account", "Path", "PID");
        foreach (var m in doc.Machines)
            foreach (var s in m.Services ?? Array.Empty<Marco.Core.Model.ServiceEntry>())
                Row(sb, m.Address, m.Name, s.Name, s.DisplayName, s.State, s.StartMode, s.Account, s.Path, Num(s.ProcessId));
        return Write(path, sb);
    }

    private string WriteStartup(ScanDocument doc, string path)
    {
        var sb = new StringBuilder();
        Row(sb, "MachineAddress", "MachineName", "Kind", "Name", "Command", "Location", "User", "State", "Author", "Created");
        foreach (var m in doc.Machines)
        {
            foreach (var s in m.StartupItems ?? Array.Empty<Marco.Core.Model.StartupEntry>())
                Row(sb, m.Address, m.Name, "Startup", s.Name, s.Command, s.Location, s.User, "", "", "");
            foreach (var t in m.ScheduledTasks ?? Array.Empty<Marco.Core.Model.ScheduledTaskEntry>())
                Row(sb, m.Address, m.Name, "ScheduledTask", t.Name, "", t.Path, t.RunAs, t.State, t.Author, Date(t.Created));
        }
        return Write(path, sb);
    }

    private string WriteAccounts(ScanDocument doc, string path)
    {
        var sb = new StringBuilder();
        Row(sb, "MachineAddress", "MachineName", "Account", "FullName", "Disabled", "Locked", "PasswordRequired", "PasswordExpires",
            "LocalAdmin", "LastLogon", "SID", "Description");
        foreach (var m in doc.Machines)
            foreach (var a in m.LocalAccounts ?? Array.Empty<Marco.Core.Model.LocalAccountEntry>())
                Row(sb, m.Address, m.Name, a.Name, a.FullName, YesNo(a.Disabled), YesNo(a.Locked), YesNo(a.PasswordRequired),
                    YesNo(a.PasswordExpires), YesNo(a.IsAdmin), DateTime2(a.LastLogon), a.Sid, a.Description);
        return Write(path, sb);
    }

    private string WriteProfiles(ScanDocument doc, string path)
    {
        var sb = new StringBuilder();
        Row(sb, "MachineAddress", "MachineName", "User", "LocalPath", "LastUse", "Loaded", "SID");
        foreach (var m in doc.Machines)
            foreach (var p in m.UserProfiles ?? Array.Empty<Marco.Core.Model.UserProfileEntry>())
                Row(sb, m.Address, m.Name, p.User, p.LocalPath, DateTime2(p.LastUse), YesNo(p.Loaded), p.Sid);
        return Write(path, sb);
    }

    private string WriteMonitors(ScanDocument doc, string path)
    {
        var sb = new StringBuilder();
        Row(sb, "MachineAddress", "MachineName", "Manufacturer", "Model", "Serial", "ProductCode", "Year", "Week", "Diagonal_in", "Active");
        foreach (var m in doc.Machines)
            foreach (var mon in m.Monitors ?? Array.Empty<Marco.Core.Model.MonitorEntry>())
                Row(sb, m.Address, m.Name, mon.Manufacturer, mon.Model, mon.Serial, mon.ProductCode, Num(mon.Year), Num(mon.Week),
                    mon.DiagonalInches?.ToString("0.#", CultureInfo.InvariantCulture), YesNo(mon.Active));
        return Write(path, sb);
    }

    private string WriteGpus(ScanDocument doc, string path)
    {
        var sb = new StringBuilder();
        Row(sb, "MachineAddress", "MachineName", "GPU", "VRAM_GB", "DriverVersion", "DriverDate", "Resolution");
        foreach (var m in doc.Machines)
            foreach (var g in m.Gpus ?? Array.Empty<Marco.Core.Model.GpuInfo>())
                Row(sb, m.Address, m.Name, g.Name, Gb(g.VramBytes), g.DriverVersion, Date(g.DriverDate), g.Resolution);
        return Write(path, sb);
    }

    private string WritePrinters(ScanDocument doc, string path)
    {
        var sb = new StringBuilder();
        Row(sb, "MachineAddress", "MachineName", "Printer", "Default", "Port", "HostAddress", "Driver", "Shared", "ShareName", "Network", "Location", "Status");
        foreach (var m in doc.Machines)
            foreach (var p in m.Printers ?? Array.Empty<Marco.Core.Model.PrinterEntry>())
                Row(sb, m.Address, m.Name, p.Name, YesNo(p.IsDefault), p.PortName, p.HostAddress, p.DriverName, YesNo(p.Shared),
                    p.ShareName, YesNo(p.IsNetwork), p.Location, p.Status);
        return Write(path, sb);
    }

    private string WriteUsb(ScanDocument doc, string path)
    {
        var sb = new StringBuilder();
        Row(sb, "MachineAddress", "MachineName", "Kind", "Name", "Manufacturer", "Class", "DeviceId", "Status", "Serial");
        foreach (var m in doc.Machines)
        {
            foreach (var u in m.UsbDevices ?? Array.Empty<Marco.Core.Model.UsbDeviceEntry>())
                Row(sb, m.Address, m.Name, "Attached", u.Name, u.Manufacturer, u.PnpClass, u.DeviceId, u.Status, "");
            foreach (var h in m.UsbStorageHistory ?? Array.Empty<Marco.Core.Model.UsbStorageHistoryEntry>())
                Row(sb, m.Address, m.Name, "StorageHistory", h.FriendlyName, h.Vendor, "", h.Product, "", h.Serial);
        }
        return Write(path, sb);
    }

    private static void WriteMetadata(ScanDocument doc, string path)
    {
        var meta = doc.Metadata;
        var sb = new StringBuilder();
        sb.AppendLine($"Tool:        {meta.Tool} {meta.Version}");
        sb.AppendLine($"Timestamp:   {meta.Timestamp:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Operator:    {meta.Operator}");
        sb.AppendLine($"Ranges:      {string.Join(", ", meta.RangesScanned)}");
        sb.AppendLine($"Targets:     {meta.TotalTargets}");
        sb.AppendLine($"Alive:       {meta.AliveCount}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    // --- CSV primitives ---

    private static string Write(string path, StringBuilder sb)
    {
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    private static void Row(StringBuilder sb, params string?[] cells)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Escape(cells[i]));
        }
        sb.Append("\r\n");
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        bool needsQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (!needsQuote) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string? AntivirusSummary(MachineDto m)
    {
        var av = m.Antivirus?.Where(a => a.Kind == "Antivirus").ToList();
        if (av is null || av.Count == 0) return null;
        return string.Join("; ", av.Select(a => (a.Product ?? "?") + " (" + (a.Enabled switch { true => "on", false => "OFF", _ => "?" })
            + (a.UpToDate == false ? ", out of date" : "") + ")"));
    }

    private static bool IsAutoStopped(Marco.Core.Model.ServiceEntry s)
        => string.Equals(s.StartMode, "Auto", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(s.State, "Running", StringComparison.OrdinalIgnoreCase);

    private static string Num(int? n) => n?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string YesNo(bool? b) => b switch { true => "Yes", false => "No", _ => "" };
    private static string Date(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "";
    private static string DateTime2(DateTime? d) => d?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
    private static string Gb(long bytes) => bytes > 0 ? (bytes / 1024d / 1024d / 1024d).ToString("0.#", CultureInfo.InvariantCulture) : "";
}
