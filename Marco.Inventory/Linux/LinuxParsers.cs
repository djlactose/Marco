using System.Globalization;
using Marco.Core.Model;

namespace Marco.Inventory.Linux;

/// <summary>
/// Pure parsers for the output of the standard Linux inventory commands. No I/O — the SSH runner executes the
/// commands and hands the text here, so all the fiddly parsing is unit-testable against canned output.
/// </summary>
public static class LinuxParsers
{
    /// <summary>Parse os-release (KEY=VALUE, quoted) into a lookup. Use PRETTY_NAME / VERSION_ID / ID.</summary>
    public static Dictionary<string, string> ParseOsRelease(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(text))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim().Trim('"');
            dict[key] = val;
        }
        return dict;
    }

    /// <summary>Total RAM in bytes from /proc/meminfo (MemTotal is in kB).</summary>
    public static long ParseMemTotalBytes(string procMeminfo)
    {
        foreach (var line in Lines(procMeminfo))
        {
            if (!line.StartsWith("MemTotal", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split(new[] { ' ', '\t', ':' }, StringSplitOptions.RemoveEmptyEntries);
            // MemTotal: 16333764 kB
            for (int i = 1; i < parts.Length; i++)
                if (long.TryParse(parts[i], out var kb)) return kb * 1024;
        }
        return 0;
    }

    /// <summary>Parse `lscpu` (KEY: VALUE) into a single CpuInfo.</summary>
    public static CpuInfo ParseLscpu(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(text))
        {
            int c = line.IndexOf(':');
            if (c <= 0) continue;
            map[line[..c].Trim()] = line[(c + 1)..].Trim();
        }

        int logical = Int(map, "CPU(s)");
        int coresPerSocket = Int(map, "Core(s) per socket");
        int sockets = Int(map, "Socket(s)");
        int cores = coresPerSocket > 0 && sockets > 0 ? coresPerSocket * sockets : coresPerSocket;

        double mhz = 0;
        if (map.TryGetValue("CPU max MHz", out var mx)) double.TryParse(mx, NumberStyles.Float, CultureInfo.InvariantCulture, out mhz);
        if (mhz == 0 && map.TryGetValue("CPU MHz", out var cur)) double.TryParse(cur, NumberStyles.Float, CultureInfo.InvariantCulture, out mhz);

        return new CpuInfo
        {
            Name = map.TryGetValue("Model name", out var n) ? n : null,
            LogicalProcessors = logical,
            Cores = cores > 0 ? cores : logical,
            ClockMhz = (int)Math.Round(mhz),
            Socket = sockets > 0 ? $"{sockets} socket(s)" : null,
        };
    }

    /// <summary>Parse `lsblk -b -d -n -P -o NAME,SIZE,TYPE,MODEL,SERIAL` (KEY="VALUE" pairs). Disks only.</summary>
    public static List<DiskInfo> ParseLsblk(string text)
    {
        var disks = new List<DiskInfo>();
        foreach (var line in Lines(text))
        {
            var kv = ParsePairs(line);
            if (!kv.TryGetValue("TYPE", out var type) || !type.Equals("disk", StringComparison.OrdinalIgnoreCase)) continue;
            disks.Add(new DiskInfo
            {
                Model = kv.TryGetValue("MODEL", out var m) && m.Length > 0 ? m : null,
                SizeBytes = kv.TryGetValue("SIZE", out var s) && long.TryParse(s, out var b) ? b : 0,
                MediaType = "Disk",
                Serial = kv.TryGetValue("SERIAL", out var se) && se.Length > 0 ? se : null,
            });
        }
        return disks;
    }

    /// <summary>Parse `df -B1 -T` output. Skips pseudo filesystems.</summary>
    public static List<VolumeInfo> ParseDf(string text)
    {
        var vols = new List<VolumeInfo>();
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "tmpfs", "devtmpfs", "squashfs", "overlay", "proc", "sysfs", "cgroup", "cgroup2", "efivarfs", "ramfs", "devpts" };
        bool header = true;
        foreach (var line in Lines(text))
        {
            if (header) { header = false; continue; } // Filesystem Type 1B-blocks Used Available Use% Mounted on
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 7) continue;
            var fstype = p[1];
            if (skip.Contains(fstype)) continue;
            long.TryParse(p[2], out var size);
            long.TryParse(p[4], out var avail);
            vols.Add(new VolumeInfo
            {
                Letter = p[^1],            // mount point
                FileSystem = fstype,
                CapacityBytes = size,
                FreeBytes = avail,
            });
        }
        return vols;
    }

    /// <summary>Parse `ip -o link` (MACs) + `ip -o -4 addr` (IPs), joined by interface. Skips loopback.</summary>
    public static List<AdapterInfo> ParseIp(string linkText, string addrText)
    {
        var byIface = new Dictionary<string, AdapterInfo>(StringComparer.OrdinalIgnoreCase);

        // link: "2: eth0: <...> ... link/ether 00:11:22:33:44:55 brd ..."
        foreach (var line in Lines(linkText))
        {
            var name = IfaceName(line);
            if (name is null or "lo") continue;
            var adapter = byIface.TryGetValue(name, out var a) ? a : (byIface[name] = new AdapterInfo { Name = name });
            int e = line.IndexOf("link/ether ", StringComparison.Ordinal);
            if (e >= 0)
            {
                var rest = line[(e + "link/ether ".Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (rest.Length > 0) adapter.Mac = rest[0].ToUpperInvariant();
            }
        }

        // addr: "2: eth0    inet 192.168.1.5/24 brd ..."
        foreach (var line in Lines(addrText))
        {
            var name = IfaceName(line);
            if (name is null or "lo") continue;
            var adapter = byIface.TryGetValue(name, out var a) ? a : (byIface[name] = new AdapterInfo { Name = name });
            int inet = line.IndexOf("inet ", StringComparison.Ordinal);
            if (inet >= 0)
            {
                var token = line[(inet + 5)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (token is not null)
                {
                    var ip = token.Split('/')[0];
                    if (ip.Length > 0) adapter.IpAddresses.Add(ip);
                }
            }
        }

        return byIface.Values.Where(a => a.IpAddresses.Count > 0 || a.Mac is not null).ToList();
    }

    /// <summary>Parse `dpkg-query -W -f='${Package}\t${Version}\t${Maintainer}\n'`.</summary>
    public static List<SoftwareEntry> ParseDpkg(string text) => ParseTabbedPackages(text);

    /// <summary>Parse `rpm -qa --qf '%{NAME}\t%{VERSION}-%{RELEASE}\t%{VENDOR}\n'`.</summary>
    public static List<SoftwareEntry> ParseRpm(string text) => ParseTabbedPackages(text);

    /// <summary>Parse `apk info -v` (name-version lines, e.g. "musl-1.2.4-r2").</summary>
    public static List<SoftwareEntry> ParseApk(string text)
    {
        var list = new List<SoftwareEntry>();
        foreach (var line in Lines(text))
        {
            var s = line.Trim();
            if (s.Length == 0) continue;
            // Split name from version at the first "-<digit>".
            int idx = -1;
            for (int i = 1; i < s.Length - 1; i++)
                if (s[i] == '-' && char.IsDigit(s[i + 1])) { idx = i; break; }
            if (idx > 0)
                list.Add(new SoftwareEntry { DisplayName = s[..idx], Version = s[(idx + 1)..], Source = SoftwareSource.Package });
            else
                list.Add(new SoftwareEntry { DisplayName = s, Source = SoftwareSource.Package });
        }
        return Dedup(list);
    }

    private static List<SoftwareEntry> ParseTabbedPackages(string text)
    {
        var list = new List<SoftwareEntry>();
        foreach (var line in Lines(text))
        {
            var p = line.Split('\t');
            if (p.Length == 0 || p[0].Trim().Length == 0) continue;
            list.Add(new SoftwareEntry
            {
                DisplayName = p[0].Trim(),
                Version = p.Length > 1 && p[1].Trim().Length > 0 ? p[1].Trim() : null,
                Publisher = p.Length > 2 && p[2].Trim().Length > 0 ? p[2].Trim() : null,
                Source = SoftwareSource.Package,
            });
        }
        return Dedup(list);
    }

    /// <summary>First current login from `who` (username in column 1), or null.</summary>
    public static string? ParseWhoCurrentUser(string whoText)
    {
        foreach (var line in Lines(whoText))
        {
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length > 0 && p[0].Length > 0) return p[0];
        }
        return null;
    }

    /// <summary>Username from the first line of `last -n 1` (skips "wtmp begins" footer).</summary>
    public static string? ParseLastUser(string lastText)
    {
        foreach (var line in Lines(lastText))
        {
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            if (p[0].Equals("wtmp", StringComparison.OrdinalIgnoreCase) || p[0].Equals("reboot", StringComparison.OrdinalIgnoreCase)) continue;
            return p[0];
        }
        return null;
    }

    /// <summary>Boot time from `uptime -s` ("2024-01-10 08:30:00").</summary>
    public static DateTime? ParseBootTime(string uptimeS)
    {
        var s = uptimeS.Trim();
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
    }

    // --- helpers ---

    private static IEnumerable<string> Lines(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0) yield return line;
        }
    }

    private static int Int(Dictionary<string, string> map, string key)
        => map.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : 0;

    private static string? IfaceName(string line)
    {
        // "2: eth0: ..." or "2: eth0    inet ..."
        int colon = line.IndexOf(':');
        if (colon < 0) return null;
        var rest = line[(colon + 1)..].TrimStart();
        var name = new string(rest.TakeWhile(c => c != ':' && c != ' ' && c != '@' && c != '\t').ToArray());
        return name.Length > 0 ? name : null;
    }

    private static Dictionary<string, string> ParsePairs(string line)
    {
        // NAME="sda" SIZE="512110190592" TYPE="disk" MODEL="Samsung SSD 860" SERIAL="S3Z..."
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && line[i] == ' ') i++;
            int eq = line.IndexOf('=', i);
            if (eq < 0) break;
            var key = line[i..eq];
            if (eq + 1 >= line.Length || line[eq + 1] != '"') { i = eq + 1; continue; }
            int end = line.IndexOf('"', eq + 2);
            if (end < 0) break;
            dict[key.Trim()] = line[(eq + 2)..end];
            i = end + 1;
        }
        return dict;
    }

    private static List<SoftwareEntry> Dedup(List<SoftwareEntry> list)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SoftwareEntry>();
        foreach (var e in list.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase))
            if (seen.Add($"{e.DisplayName} {e.Version}")) result.Add(e);
        return result;
    }
}
