using Marco.Core.Model;

namespace Marco.Core.Lifecycle;

/// <summary>
/// Pure lifecycle evaluation: OS end-of-support from the bundled table plus hardware age from the BIOS date.
/// Matching is deliberately conservative — no table hit means Unknown, never a judgment. Windows matches on
/// build number + client/server kind (builds collide across the two lines: 17763 is Win10 1809 AND Server 2019),
/// with the edition (Enterprise/Education/LTSC vs Home/Pro) choosing which client date applies.
/// </summary>
public static class LifecycleEvaluator
{
    private const int EndingSoonDays = 180;

    public static LifecycleInfo? Evaluate(Machine m, EolTableData table, DateTime today)
    {
        var (label, eos, extended) = MatchOs(m, table);
        var status = eos is null ? OsSupportStatus.Unknown
            : eos <= today ? OsSupportStatus.EndOfLife
            : eos <= today.AddDays(EndingSoonDays) ? OsSupportStatus.EndingSoon
            : OsSupportStatus.Supported;

        double? ageYears = m.System.BiosDate is { } bios && bios <= today
            ? Math.Round((today - bios).TotalDays / 365.25, 1)
            : null;

        if (status == OsSupportStatus.Unknown && ageYears is null) return null;
        return new LifecycleInfo(label, eos, extended, status, ageYears);
    }

    private static (string? Label, DateTime? Eos, DateTime? Extended) MatchOs(Machine m, EolTableData table)
    {
        if (m.DeviceType is DeviceType.Windows or DeviceType.WindowsServer)
        {
            var build = m.Os.Build?.Trim();
            if (string.IsNullOrEmpty(build)) return (null, null, null);

            bool isServer = m.Updates.InstallationType is { } it
                ? it.Contains("Server", StringComparison.OrdinalIgnoreCase)
                : m.DeviceType == DeviceType.WindowsServer;
            var entry = table.Windows.FirstOrDefault(w =>
                w.Build == build && string.Equals(w.Kind, isServer ? "server" : "client", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return (null, null, null);

            string label = entry.Release is null ? entry.Product : $"{entry.Product} {entry.Release}";
            if (isServer)
                return (label, entry.EosExtended ?? entry.EosMainstream, entry.EsuEnd ?? entry.EosExtended);

            bool enterprise = m.Updates.EditionId is { } ed
                && (ed.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
                    || ed.Contains("Education", StringComparison.OrdinalIgnoreCase));
            var eos = enterprise ? entry.EosEnterprise ?? entry.EosHomePro : entry.EosHomePro ?? entry.EosEnterprise;
            return (label, eos, entry.EsuEnd);
        }

        if (m.DeviceType == DeviceType.UnixLinux && m.Os.Caption is { } caption)
        {
            foreach (var entry in table.Linux)
            {
                if (!caption.Contains(entry.Distro, StringComparison.OrdinalIgnoreCase)) continue;
                bool versionHit = caption.Contains(entry.VersionMatch, StringComparison.Ordinal)
                    || (m.Os.Version is { } v && v.StartsWith(entry.VersionMatch, StringComparison.Ordinal));
                if (!versionHit) continue;
                return ($"{entry.Distro} {entry.VersionMatch}", entry.Eos, entry.EosExtended);
            }
        }

        return (null, null, null);
    }
}
