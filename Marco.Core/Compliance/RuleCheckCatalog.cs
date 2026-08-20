using Marco.Core.Model;

namespace Marco.Core.Compliance;

/// <summary>
/// The coded checks rule packs reference by id. Each is a small pure function Machine → (status, detail).
/// Null inputs yield Unknown, never Fail; conditional rules pass on the safe branch (RDP disabled passes the
/// NLA rule). Parameters (thresholds) come from the rule definition so packs can retune without code.
/// </summary>
public static class RuleCheckCatalog
{
    public delegate (RuleStatus Status, string? Detail) RuleCheck(Machine m, IReadOnlyDictionary<string, int> p);

    public static IReadOnlyDictionary<string, RuleCheck> Checks { get; } = new Dictionary<string, RuleCheck>(StringComparer.OrdinalIgnoreCase)
    {
        ["bitlocker-os-volume"] = (m, _) =>
        {
            var vols = m.Security.BitLockerVolumes;
            if (vols.Count == 0) return (RuleStatus.Unknown, null);
            var os = vols.Where(v => string.Equals(v.VolumeType, "OS", StringComparison.OrdinalIgnoreCase)).ToList();
            if (os.Count == 0) return (RuleStatus.Unknown, "no OS volume reported");
            return os.Any(v => string.Equals(v.Protection, "On", StringComparison.OrdinalIgnoreCase))
                ? (RuleStatus.Pass, null)
                : (RuleStatus.Fail, $"OS volume protection: {os[0].Protection ?? "?"}");
        },

        ["smb1-disabled"] = (m, _) => Bool(m.Security.Smb1Enabled, passWhen: false, "SMB1 is enabled"),

        ["disks-healthy"] = (m, _) =>
        {
            if (m.Disks.Count == 0) return (RuleStatus.Unknown, null);
            var bad = m.Disks.Where(d => d.SmartPredictFailure == true
                || string.Equals(d.SmartStatus, "Pred Fail", StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.HealthStatus, "Unhealthy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.HealthStatus, "Warning", StringComparison.OrdinalIgnoreCase)).ToList();
            return bad.Count == 0 ? (RuleStatus.Pass, null)
                : (RuleStatus.Fail, string.Join("; ", bad.Select(d => $"{d.Model}: {d.HealthStatus ?? d.SmartStatus ?? "SMART predicts failure"}")));
        },

        ["av-enabled"] = (m, _) =>
        {
            if (m.Security.DefenderEnabled == true) return (RuleStatus.Pass, "Defender");
            var av = m.Antivirus.Where(a => a.Kind == "Antivirus").ToList();
            if (av.Any(a => a.Enabled == true)) return (RuleStatus.Pass, av.First(a => a.Enabled == true).Product);
            if (m.Security.DefenderEnabled == false || av.Any(a => a.Enabled == false))
                return (RuleStatus.Fail, "no enabled antivirus product");
            return (RuleStatus.Unknown, null);
        },

        ["av-signatures-fresh"] = (m, p) =>
        {
            int max = Get(p, "maxDays", 7);
            if (m.Security.DefenderSignatureAgeDays is { } age)
                return age <= max ? (RuleStatus.Pass, $"{age}d old") : (RuleStatus.Fail, $"signatures {age} days old");
            var enabled = m.Antivirus.Where(a => a.Kind == "Antivirus" && a.Enabled == true).ToList();
            if (enabled.Any(a => a.UpToDate == false)) return (RuleStatus.Fail, "product reports out of date");
            if (enabled.Any(a => a.UpToDate == true)) return (RuleStatus.Pass, null);
            return (RuleStatus.Unknown, null);
        },

        ["firewall-all-profiles"] = (m, _) =>
        {
            var s = m.Security;
            var profiles = new[] { ("Domain", s.FirewallDomain), ("Private", s.FirewallPrivate), ("Public", s.FirewallPublic) };
            var off = profiles.Where(x => x.Item2 == false).Select(x => x.Item1).ToList();
            if (off.Count > 0) return (RuleStatus.Fail, $"{string.Join(", ", off)} off");
            return profiles.Any(x => x.Item2 is null) ? (RuleStatus.Unknown, null) : (RuleStatus.Pass, null);
        },

        ["rdp-requires-nla"] = (m, _) => m.Security.RdpEnabled switch
        {
            false => (RuleStatus.Pass, "RDP disabled"),
            true => Bool(m.Security.RdpNlaRequired, passWhen: true, "RDP enabled without NLA"),
            _ => (RuleStatus.Unknown, null),
        },

        ["secure-boot"] = (m, _) => Bool(m.Security.SecureBoot, passWhen: true, "Secure Boot off"),

        ["tpm-2-enabled"] = (m, _) =>
        {
            var s = m.Security;
            if (s.TpmPresent == false) return (RuleStatus.Fail, "no TPM");
            if (s.TpmPresent is null) return (RuleStatus.Unknown, null);
            if (s.TpmEnabled == false) return (RuleStatus.Fail, "TPM disabled");
            if (s.TpmVersion is null) return (RuleStatus.Unknown, "version unknown");
            return s.TpmVersion.StartsWith("2", StringComparison.Ordinal)
                ? (RuleStatus.Pass, $"TPM {s.TpmVersion}")
                : (RuleStatus.Fail, $"TPM {s.TpmVersion}");
        },

        ["patched-recently"] = (m, p) =>
        {
            int max = Get(p, "maxDays", 60);
            if (m.Updates.LastHotfixDate is not { } last) return (RuleStatus.Unknown, null);
            int days = (int)(DateTime.Now - last).TotalDays;
            return days <= max ? (RuleStatus.Pass, $"last hotfix {days}d ago") : (RuleStatus.Fail, $"last hotfix {days} days ago");
        },

        ["no-passwordless-accounts"] = (m, _) =>
        {
            if (!CollectorOk(m, "Users")) return (RuleStatus.Unknown, null);
            var bad = m.LocalAccounts.Where(a => !a.Disabled && !a.PasswordRequired).Select(a => a.Name).ToList();
            return bad.Count == 0 ? (RuleStatus.Pass, null) : (RuleStatus.Fail, string.Join(", ", bad));
        },

        ["laps-managed"] = (m, _) => Bool(m.Security.LapsManaged, passWhen: true, "LAPS not managing this host"),

        ["uac-enabled"] = (m, _) => Bool(m.Security.UacEnabled, passWhen: true, "UAC disabled"),

        ["smb-signing-required"] = (m, _) => Bool(m.Security.SmbSigningRequired, passWhen: true, "SMB signing not required"),

        ["auto-update-enabled"] = (m, _) => Bool(m.Updates.NoAutoUpdate, passWhen: false, "automatic updates disabled by policy"),

        ["token-filtering-intact"] = (m, _) =>
            Bool(m.Security.LocalAccountTokenFilterPolicy, passWhen: false, "remote token filtering disabled (LocalAccountTokenFilterPolicy=1)"),

        ["no-pending-reboot"] = (m, _) => m.Updates.PendingReboot switch
        {
            true => (RuleStatus.Fail, m.Updates.PendingRebootReasons ?? "reboot pending"),
            false => (RuleStatus.Pass, null),
            _ => (RuleStatus.Unknown, null),
        },

        ["defender-realtime"] = (m, _) => m.Security.DefenderEnabled switch
        {
            // A third-party AV replaces Defender; its own av-enabled rule covers that case.
            false => m.Antivirus.Any(a => a.Kind == "Antivirus" && a.Enabled == true)
                ? (RuleStatus.NotApplicable, "third-party antivirus")
                : (RuleStatus.Fail, "Defender off, no other AV"),
            true => Bool(m.Security.DefenderRealTime, passWhen: true, "real-time protection off"),
            _ => (RuleStatus.Unknown, null),
        },

        ["defender-tamper-protection"] = (m, _) => m.Security.DefenderEnabled == true
            ? Bool(m.Security.DefenderTamperProtected, passWhen: true, "tamper protection off")
            : (RuleStatus.NotApplicable, "Defender not primary"),

        ["admins-limited"] = (m, p) =>
        {
            if (!CollectorOk(m, "Users")) return (RuleStatus.Unknown, null);
            int max = Get(p, "maxAdmins", 5);
            return m.LocalAdministrators.Count <= max
                ? (RuleStatus.Pass, $"{m.LocalAdministrators.Count} member(s)")
                : (RuleStatus.Fail, $"{m.LocalAdministrators.Count} members of local Administrators");
        },

        ["defender-scan-recent"] = (m, p) =>
        {
            if (m.Security.DefenderEnabled != true) return (RuleStatus.NotApplicable, "Defender not primary");
            int max = Get(p, "maxDays", 14);
            if (m.Security.DefenderLastQuickScan is not { } scan) return (RuleStatus.Unknown, null);
            int days = (int)(DateTime.Now - scan).TotalDays;
            return days <= max ? (RuleStatus.Pass, $"{days}d ago") : (RuleStatus.Fail, $"last quick scan {days} days ago");
        },

        ["no-auto-logon"] = (m, _) => Bool(m.Security.AutoAdminLogon, passWhen: false, "automatic logon is enabled (password in registry)"),

        ["os-supported"] = (m, _) => m.Lifecycle?.OsSupport switch
        {
            Marco.Core.Lifecycle.OsSupportStatus.EndOfLife =>
                (RuleStatus.Fail, m.Lifecycle.SupportSummary ?? "OS past end of support"),
            Marco.Core.Lifecycle.OsSupportStatus.EndingSoon =>
                (RuleStatus.Pass, m.Lifecycle.SupportSummary), // still supported — the date in the detail is the nudge
            Marco.Core.Lifecycle.OsSupportStatus.Supported => (RuleStatus.Pass, null),
            _ => (RuleStatus.Unknown, null),
        },
    };

    private static (RuleStatus, string?) Bool(bool? value, bool passWhen, string failDetail) => value switch
    {
        null => (RuleStatus.Unknown, null),
        var v when v == passWhen => (RuleStatus.Pass, null),
        _ => (RuleStatus.Fail, failDetail),
    };

    private static int Get(IReadOnlyDictionary<string, int> p, string key, int fallback)
        => p.TryGetValue(key, out var v) ? v : fallback;

    private static bool CollectorOk(Machine m, string name)
        => m.Collectors.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) && c.Status == CollectorStatus.Ok);
}
