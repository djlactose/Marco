using System.Text.RegularExpressions;
using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;

namespace Marco.Inventory.Collectors;

/// <summary>
/// Who can use, and who does use, this machine: local accounts, the members of the local Administrators group,
/// the user profiles on disk (with last use), and every interactive / RDP session (Win32_ComputerSystem.UserName
/// only shows the console user, so a terminal server looked like one person). Per-account last logon comes from
/// Win32_NetworkLoginProfile, which is also used to fill in the "last logged-on user" when nothing better is known.
/// On a domain controller the "local" SAM is the domain, so the account and login-profile enumerations are
/// skipped there rather than walking the directory.
/// </summary>
public sealed class UsersCollector : IInventoryCollector
{
    public string Name => "Users";

    private static readonly Regex AccountRef = new(@"Domain=""(?<d>[^""]*)"",Name=""(?<n>[^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LogonIdRef = new(@"LogonId=""(?<id>[^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Pseudo-accounts that own interactive-type sessions but aren't people.
    private static readonly HashSet<string> SystemSessionDomains = new(StringComparer.OrdinalIgnoreCase)
        { "Window Manager", "Font Driver Host", "NT AUTHORITY", "NT SERVICE" };

    public async Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct)
    {
        var steps = new CollectorSteps();
        var wmi = context.Wmi;
        bool isDc = machine.System.IsDomainController;

        // 1. Local accounts (skipped on DCs — there the "local" SAM is the whole domain).
        if (!isDc)
        {
            await steps.RunAsync("Local accounts", async () =>
            {
                var rows = await wmi.QueryAsync(WmiQueryHelpers.CimV2,
                    "SELECT Name, FullName, SID, Disabled, Lockout, PasswordRequired, PasswordExpires, Description "
                    + "FROM Win32_UserAccount WHERE LocalAccount = TRUE", ct);
                var accounts = new List<LocalAccountEntry>();
                foreach (var r in rows)
                {
                    var name = r.GetString("Name")?.Trim();
                    if (string.IsNullOrEmpty(name)) continue;
                    accounts.Add(new LocalAccountEntry
                    {
                        Name = name,
                        FullName = r.GetString("FullName")?.Trim(),
                        Sid = r.GetString("SID"),
                        Disabled = r.GetBool("Disabled") ?? false,
                        Locked = r.GetBool("Lockout") ?? false,
                        PasswordRequired = r.GetBool("PasswordRequired") ?? true,
                        PasswordExpires = r.GetBool("PasswordExpires") ?? true,
                        Description = r.GetString("Description")?.Trim(),
                    });
                }
                accounts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                machine.LocalAccounts = accounts;
            });
        }

        // 2. Local Administrators — resolve the group by well-known SID (survives renamed/localized groups), then
        //    its members via the Win32_GroupUser association.
        ct.ThrowIfCancellationRequested();
        await steps.RunAsync("Local Administrators", async () =>
        {
            var group = await wmi.QueryFirstAsync(
                "SELECT Domain, Name FROM Win32_Group WHERE LocalAccount = TRUE AND SID = 'S-1-5-32-544'", ct);
            if (group is null) throw new WmiException(WmiFailureKind.NotSupported, "Local Administrators group not found.");
            var domain = group.GetString("Domain") ?? machine.Name ?? context.Host;
            var name = group.GetString("Name") ?? "Administrators";
            var members = await wmi.QueryAsync(WmiQueryHelpers.CimV2,
                $"SELECT PartComponent FROM Win32_GroupUser WHERE GroupComponent = \"Win32_Group.Domain='{Escape(domain)}',Name='{Escape(name)}'\"", ct);
            var admins = new List<string>();
            foreach (var m in members)
            {
                var display = ParseAccountRef(m.GetString("PartComponent"));
                if (display is not null && !admins.Contains(display, StringComparer.OrdinalIgnoreCase))
                    admins.Add(display);
            }
            admins.Sort(StringComparer.OrdinalIgnoreCase);
            machine.LocalAdministrators = admins;

            // Flag the local accounts that are admins.
            foreach (var acct in machine.LocalAccounts)
                acct.IsAdmin = machine.LocalAdministrators.Any(a =>
                    a.Equals($"{domain}\\{acct.Name}", StringComparison.OrdinalIgnoreCase));
        });

        // 3. Profiles on disk.
        ct.ThrowIfCancellationRequested();
        await steps.RunAsync("User profiles", async () =>
        {
            var rows = await wmi.QueryAsync(WmiQueryHelpers.CimV2,
                "SELECT LocalPath, SID, LastUseTime, Loaded FROM Win32_UserProfile WHERE Special = FALSE", ct);
            var profiles = new List<UserProfileEntry>();
            foreach (var r in rows)
            {
                var path = r.GetString("LocalPath");
                if (string.IsNullOrWhiteSpace(path)) continue;
                profiles.Add(new UserProfileEntry
                {
                    User = ProfileLeaf(path),
                    LocalPath = path,
                    Sid = r.GetString("SID"),
                    LastUse = r.GetDateTime("LastUseTime"),
                    Loaded = r.GetBool("Loaded") ?? false,
                });
            }
            profiles.Sort((a, b) => Nullable.Compare(b.LastUse, a.LastUse));
            machine.UserProfiles = profiles;
        });

        // 4. Interactive / RDP sessions.
        ct.ThrowIfCancellationRequested();
        await steps.RunAsync("Logon sessions", async () =>
        {
            var sessions = await wmi.QueryAsync(WmiQueryHelpers.CimV2,
                "SELECT LogonId, LogonType, StartTime FROM Win32_LogonSession WHERE LogonType = 2 OR LogonType = 10 OR LogonType = 11", ct);
            if (sessions.Count == 0) { machine.LogonSessions = new List<LogonSessionEntry>(); return; }
            var byId = sessions
                .Where(s => s.GetString("LogonId") is not null)
                .GroupBy(s => s.GetString("LogonId")!)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var links = await wmi.QueryAsync(WmiQueryHelpers.CimV2, "SELECT Antecedent, Dependent FROM Win32_LoggedOnUser", ct);
            var found = new Dictionary<string, LogonSessionEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in links)
            {
                var id = LogonIdRef.Match(l.GetString("Dependent") ?? "").Groups["id"].Value;
                if (id.Length == 0 || !byId.TryGetValue(id, out var session)) continue;
                var acct = AccountRef.Match(l.GetString("Antecedent") ?? "");
                if (!acct.Success) continue;
                var domain = acct.Groups["d"].Value;
                if (SystemSessionDomains.Contains(domain)) continue;
                var display = domain.Length > 0 ? $"{domain}\\{acct.Groups["n"].Value}" : acct.Groups["n"].Value;
                var type = DescribeLogonType(session.GetInt("LogonType"));
                var start = session.GetDateTime("StartTime");
                var key = $"{display}|{type}";
                if (found.TryGetValue(key, out var existing))
                {
                    if (start is not null && (existing.StartTime is null || start < existing.StartTime)) existing.StartTime = start;
                }
                else
                {
                    found[key] = new LogonSessionEntry { Account = display, LogonType = type, StartTime = start };
                }
            }
            machine.LogonSessions = found.Values.OrderBy(v => v.StartTime ?? DateTime.MaxValue).ToList();
        });

        // 5. Per-account last logon (and a better "last user" when the registry heuristic had nothing).
        if (!isDc)
        {
            ct.ThrowIfCancellationRequested();
            await steps.RunAsync("Last logons", async () =>
            {
                var rows = await wmi.QueryAsync(WmiQueryHelpers.CimV2,
                    "SELECT Name, LastLogon, NumberOfLogons FROM Win32_NetworkLoginProfile", ct);
                DateTime? newest = null; string? newestName = null;
                foreach (var r in rows)
                {
                    var name = r.GetString("Name")?.Trim();
                    var last = r.GetDateTime("LastLogon");
                    if (string.IsNullOrEmpty(name) || last is null) continue;
                    var leaf = name.Contains('\\') ? name[(name.LastIndexOf('\\') + 1)..] : name;
                    var acct = machine.LocalAccounts.FirstOrDefault(a => a.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase));
                    if (acct is not null) acct.LastLogon = last;
                    if (IsPersonName(name) && (newest is null || last > newest)) { newest = last; newestName = name; }
                }
                if (newestName is not null && string.IsNullOrWhiteSpace(machine.System.LastLoggedOnUser)
                    && string.IsNullOrWhiteSpace(machine.System.LoggedOnUser))
                    machine.System.LastLoggedOnUser = newestName;
            });
        }

        machine.RefreshCounts();
        steps.ThrowIfNothingSucceeded();
    }

    // --- pure helpers (unit-tested) ---

    /// <summary>Win32_GroupUser.PartComponent / Win32_LoggedOnUser.Antecedent reference → "DOMAIN\name", with
    /// nested groups suffixed " (group)".</summary>
    public static string? ParseAccountRef(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var m = AccountRef.Match(reference);
        if (!m.Success) return null;
        var d = m.Groups["d"].Value; var n = m.Groups["n"].Value;
        var display = d.Length > 0 ? $"{d}\\{n}" : n;
        return reference.Contains("Win32_Group.", StringComparison.OrdinalIgnoreCase) ? display + " (group)" : display;
    }

    public static string DescribeLogonType(int? type) => type switch
    {
        2 => "Interactive",
        10 => "RemoteInteractive",
        11 => "CachedInteractive",
        3 => "Network",
        4 => "Batch",
        5 => "Service",
        _ => type is { } t ? $"Type {t}" : "Unknown",
    };

    /// <summary>C:\Users\jdoe → jdoe.</summary>
    public static string? ProfileLeaf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.TrimEnd('\\', '/');
        var i = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return i >= 0 ? trimmed[(i + 1)..] : trimmed;
    }

    private static bool IsPersonName(string name)
    {
        var upper = name.ToUpperInvariant();
        return !upper.StartsWith("NT AUTHORITY\\") && !upper.StartsWith("NT SERVICE\\")
            && !upper.EndsWith("$") && !upper.EndsWith("\\SYSTEM") && !upper.EndsWith("\\LOCAL SERVICE")
            && !upper.EndsWith("\\NETWORK SERVICE") && !upper.StartsWith("WINDOW MANAGER\\") && !upper.StartsWith("FONT DRIVER HOST\\")
            && !upper.Contains("\\DWM-") && !upper.Contains("\\UMFD-") && !upper.EndsWith("\\DEFAULTACCOUNT")
            && !upper.EndsWith("\\WDAGUTILITYACCOUNT");
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");
}
