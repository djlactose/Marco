using System.Globalization;
using System.Text;

namespace Marco.Export;

/// <summary>
/// Excel-importable CSV export. List-valued data (software, disks, adapters) can't live in a one-row-per-machine
/// file, so it is written to companion files keyed by machine address: machines.csv plus software.csv, disks.csv,
/// adapters.csv. The main file carries summary counts so a single-file export is still meaningful.
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

        WriteMetadata(doc, Path.Combine(directory, "scan-info.txt"));
        return written;
    }

    /// <summary>Single-file variant: just the machine rows (for a quick Excel import).</summary>
    public string WriteMachines(ScanDocument doc, string path)
    {
        var sb = new StringBuilder();
        Row(sb, "Address", "Name", "FQDN", "Type", "Status", "Vendor", "MAC", "Virtual",
            "Manufacturer", "Model", "Serial", "Chassis", "Domain", "LoggedOnUser",
            "OS", "OSVersion", "OSBuild", "Arch", "InstallDate", "LastBoot",
            "CPU", "Cores", "LogicalCPUs", "RAM_GB", "SlotsUsed", "SlotsTotal",
            "Disks", "SoftwareCount", "Adapters", "BIOS", "Motherboard", "LastScanned");

        foreach (var m in doc.Machines)
        {
            var cpu = m.Cpus.FirstOrDefault();
            Row(sb,
                m.Address, m.Name, m.Fqdn, m.DeviceType.ToString(), m.Status.ToString(), m.Vendor,
                string.Join(" ", m.MacAddresses), m.IsVirtual ? "Yes" : "",
                m.System.Manufacturer, m.System.Model, m.System.SerialNumber, m.System.ChassisType,
                m.System.Domain, m.System.LoggedOnUser,
                m.Os.Caption, m.Os.Version, m.Os.Build, m.Os.Architecture,
                Date(m.Os.InstallDate), Date(m.Os.LastBoot),
                cpu?.Name, Num(cpu?.Cores), Num(cpu?.LogicalProcessors),
                Gb(m.TotalMemoryBytes), Num(m.MemorySlotsUsed), Num(m.MemorySlotsTotal),
                Num(m.Disks.Count), Num(m.Software.Count), Num(m.Adapters.Count),
                m.System.BiosVersion, m.System.MotherboardModel, DateTime2(m.LastScanned));
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
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    private string WriteDisks(ScanDocument doc, string path)
    {
        var sb = new StringBuilder();
        Row(sb, "MachineAddress", "MachineName", "Model", "Size_GB", "MediaType", "Serial", "SmartStatus");
        foreach (var m in doc.Machines)
            foreach (var d in m.Disks)
                Row(sb, m.Address, m.Name, d.Model, Gb(d.SizeBytes), d.MediaType, d.Serial, d.SmartStatus);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
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
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
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

    private static string Num(int? n) => n?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string Date(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "";
    private static string DateTime2(DateTime? d) => d?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
    private static string Gb(long bytes) => bytes > 0 ? (bytes / 1024d / 1024d / 1024d).ToString("0.#", CultureInfo.InvariantCulture) : "";
}
