using System.Net;
using System.Security;
using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Ssh;

namespace Marco.Inventory.Linux;

/// <summary>
/// Inventories a Linux/Unix host over SSH, populating the same <see cref="Machine"/> model as the Windows path.
/// Owns credential try-and-remember (password auth): tries the remembered set first, then each candidate until
/// one authenticates, then remembers the winner — never mutating a password. Each collector group is
/// failure-isolated so a missing command (e.g. no lscpu) records a per-collector status, not a whole-host abort.
/// </summary>
public sealed class LinuxInventoryRunner : IInventoryRunner
{
    private readonly ISshSessionFactory _factory;
    private readonly int _port;
    private readonly int _timeoutSeconds;
    private readonly Dictionary<string, string> _remembered = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>The collector group names this runner reports (they double as the enable/disable keys and must
    /// exist in <see cref="Marco.Core.Inventory.CollectorCatalog"/>).</summary>
    public static readonly IReadOnlyList<string> CollectorNames = new[]
        { "OperatingSystem", "System", "Cpu", "Memory", "Storage", "Network", "Users", "InstalledSoftware" };

    public LinuxInventoryRunner(ISshSessionFactory factory, int port = 22, int timeoutSeconds = 15)
    {
        _factory = factory;
        _port = port;
        _timeoutSeconds = timeoutSeconds;
    }

    public async Task<InventoryOutcome> InventoryAsync(
        Machine machine,
        IReadOnlyList<CredentialCandidate> candidates,
        IReadOnlyDictionary<string, CredentialCandidate>? perHostOverrides,
        ISet<string>? enabledCollectors,
        CancellationToken ct)
    {
        machine.Status = MachineStatus.Scanning;
        machine.StatusDetail = null;
        try
        {
            return await InventoryCoreAsync(machine, candidates, perHostOverrides, enabledCollectors, ct).ConfigureAwait(false);
        }
        finally
        {
            machine.CurrentActivity = null; // every exit path — auth failure, cancel mid-collector, success
        }
    }

    private async Task<InventoryOutcome> InventoryCoreAsync(
        Machine machine,
        IReadOnlyList<CredentialCandidate> candidates,
        IReadOnlyDictionary<string, CredentialCandidate>? perHostOverrides,
        ISet<string>? enabledCollectors,
        CancellationToken ct)
    {
        machine.ConnectFailure = ConnectFailure.None;
        machine.ConnectFailureLocalAccount = false;

        var ordered = OrderCandidates(machine.Address, candidates, perHostOverrides)
            .Where(c => !c.Credential.UseCurrentToken && !string.IsNullOrEmpty(c.Credential.Username))
            .ToList();
        if (ordered.Count == 0)
        {
            machine.Status = MachineStatus.AuthFailed;
            machine.StatusDetail = "No username/password credentials for SSH (a current-session token can't be used).";
            machine.ConnectFailure = ConnectFailure.NoCredentials;
            return new InventoryOutcome(false, null, machine.Status, machine.StatusDetail);
        }

        ISshSession? session = null;
        CredentialCandidate? winner = null;
        string? lastAuthDetail = null;

        for (int i = 0; i < ordered.Count; i++)
        {
            var candidate = ordered[i];
            ct.ThrowIfCancellationRequested();
            machine.CurrentActivity = ordered.Count == 1
                ? $"Connecting (SSH, {candidate.Label})…"
                : $"Connecting (SSH, {candidate.Label}, {i + 1}/{ordered.Count})…";
            var user = candidate.Credential.Username!;
            var pass = ToPlainText(candidate.Credential.Password);
            try
            {
                int port = candidate.SshPort > 0 ? candidate.SshPort : _port;
                // The blocking connect cannot be interrupted, but a Stop must not wait for it: abandon on
                // cancellation and dispose the session if it lands afterwards.
                session = await Marco.Core.Threading.AbandonableTask.AwaitOrAbandonAsync<ISshSession>(
                    Task.Run(() => _factory.Connect(machine.Address, port, user, pass, _timeoutSeconds, ct), ct),
                    ct,
                    onAbandonedResult: late => late.Dispose()).ConfigureAwait(false);
                winner = candidate;
                break;
            }
            catch (SshException ex) when (ex.Kind == SshFailureKind.AuthFailed)
            {
                lastAuthDetail = ex.Message; // try the next set
            }
            catch (SshException ex) when (ex.Kind is SshFailureKind.Unreachable or SshFailureKind.Timeout)
            {
                machine.Status = MachineStatus.Unreachable;
                machine.StatusDetail = $"SSH: {ex.Message}";
                machine.ConnectFailure = ConnectFailure.SshUnreachable;
                return new InventoryOutcome(false, null, machine.Status, machine.StatusDetail);
            }
        }

        if (session is null || winner is null)
        {
            machine.Status = MachineStatus.AuthFailed;
            machine.StatusDetail = lastAuthDetail ?? "SSH authentication failed for all credential sets.";
            machine.ConnectFailure = ConnectFailure.SshAuthFailed;
            return new InventoryOutcome(false, null, machine.Status, machine.StatusDetail);
        }

        Remember(machine.Address, winner.Label);

        using (session)
        {
            int ok = 0, failed = 0;
            void Group(string name, Action body)
            {
                if (enabledCollectors is not null && !enabledCollectors.Contains(name)) return;
                ct.ThrowIfCancellationRequested();
                machine.CurrentActivity = $"Collecting {name}…";
                machine.SetCollector(name, CollectorStatus.NotRun);
                try { body(); machine.SetCollector(name, CollectorStatus.Ok); ok++; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { machine.SetCollector(name, CollectorStatus.Failed, ex.Message); failed++; }
            }

            Group("OperatingSystem", () => CollectOs(session, machine, ct));
            Group("System", () => CollectSystem(session, machine, ct));
            Group("Cpu", () => CollectCpu(session, machine, ct));
            Group("Memory", () => CollectMemory(session, machine, ct));
            Group("Storage", () => CollectStorage(session, machine, ct));
            Group("Network", () => CollectNetwork(session, machine, ct));
            Group("Users", () => CollectUsers(session, machine, ct));
            Group("InstalledSoftware", () => CollectSoftware(session, machine, ct));

            machine.LastScanned = DateTime.Now;
            machine.RefreshCounts();
            machine.Status = failed == 0 ? MachineStatus.Done : ok > 0 ? MachineStatus.Partial : MachineStatus.Error;
            machine.StatusDetail = failed == 0 ? null : $"{failed} collector(s) failed.";
            return new InventoryOutcome(true, winner.Label, machine.Status, machine.StatusDetail);
        }
    }

    // --- collectors ---

    private static string Run(ISshSession s, string cmd, CancellationToken ct) => s.Run(cmd, ct).StdOut;

    private static void CollectOs(ISshSession s, Machine m, CancellationToken ct)
    {
        var os = LinuxParsers.ParseOsRelease(Run(s, "cat /etc/os-release 2>/dev/null", ct));
        if (os.TryGetValue("PRETTY_NAME", out var pretty)) m.Os.Caption = pretty;
        else if (os.TryGetValue("NAME", out var name)) m.Os.Caption = name;
        if (os.TryGetValue("VERSION_ID", out var vid)) m.Os.Version = vid;

        m.Os.Build = Run(s, "uname -r", ct).Trim();               // kernel release
        m.Os.Architecture = Run(s, "uname -m", ct).Trim();
        var boot = LinuxParsers.ParseBootTime(Run(s, "uptime -s 2>/dev/null", ct));
        if (boot is not null) m.Os.LastBoot = boot;

        var host = Run(s, "hostname -f 2>/dev/null || hostname", ct).Trim();
        if (host.Length > 0)
        {
            if (host.Contains('.')) { m.Fqdn = host; if (string.IsNullOrWhiteSpace(m.Name)) m.Name = host.Split('.')[0]; }
            else if (string.IsNullOrWhiteSpace(m.Name)) m.Name = host;
        }
    }

    private static void CollectSystem(ISshSession s, Machine m, CancellationToken ct)
    {
        // DMI files; serial usually needs root (may be empty / "None").
        var text = Run(s, "for f in sys_vendor product_name product_version product_serial board_vendor board_name chassis_type; do printf '%s=%s\\n' \"$f\" \"$(cat /sys/class/dmi/id/$f 2>/dev/null)\"; done", ct);
        var map = LinuxParsers.ParseOsRelease(text); // KEY=VALUE parser works here too
        string? Clean(string k) => map.TryGetValue(k, out var v) && v.Length > 0
            && !v.Equals("None", StringComparison.OrdinalIgnoreCase)
            && !v.Equals("Not Specified", StringComparison.OrdinalIgnoreCase)
            && !v.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase)
            && !v.Equals("Default string", StringComparison.OrdinalIgnoreCase) ? v : null;
        m.System.Manufacturer = Clean("sys_vendor");
        m.System.Model = Clean("product_name");
        m.System.ProductVersion = Clean("product_version");
        m.System.SerialNumber = Clean("product_serial");
        m.System.MotherboardManufacturer = Clean("board_vendor");
        m.System.MotherboardModel = Clean("board_name");
        // chassis_type is the bare SMBIOS code ("10"); same table as the Windows side, so the expansion
        // estimate and the spec lookup work for Linux hosts too.
        m.System.ChassisType = ChassisTypes.Describe(int.TryParse(Clean("chassis_type"), out var ch) ? ch : null);
    }

    private static void CollectCpu(ISshSession s, Machine m, CancellationToken ct)
    {
        var text = Run(s, "lscpu 2>/dev/null", ct);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("lscpu not available.");
        var cpu = LinuxParsers.ParseLscpu(text);
        m.Cpus = new List<CpuInfo> { cpu };
        m.RefreshCounts();
    }

    private static void CollectMemory(ISshSession s, Machine m, CancellationToken ct)
    {
        var bytes = LinuxParsers.ParseMemTotalBytes(Run(s, "cat /proc/meminfo", ct));
        m.TotalMemoryBytes = bytes;
    }

    private static void CollectStorage(ISshSession s, Machine m, CancellationToken ct)
    {
        // ROTA (rotational → HDD/SSD) and TRAN (bus) are absent from very old util-linux, which then rejects the
        // whole column list — so retry with the classic columns rather than lose the disks.
        var lsblk = Run(s, "lsblk -b -d -n -P -o NAME,SIZE,TYPE,MODEL,SERIAL,ROTA,TRAN 2>/dev/null", ct);
        if (string.IsNullOrWhiteSpace(lsblk))
            lsblk = Run(s, "lsblk -b -d -n -P -o NAME,SIZE,TYPE,MODEL,SERIAL 2>/dev/null", ct);
        m.Disks = LinuxParsers.ParseLsblk(lsblk);
        m.Volumes = LinuxParsers.ParseDf(Run(s, "df -B1 -T 2>/dev/null", ct));
        m.RefreshCounts();
    }

    private static void CollectNetwork(ISshSession s, Machine m, CancellationToken ct)
    {
        var adapters = LinuxParsers.ParseIp(Run(s, "ip -o link 2>/dev/null", ct), Run(s, "ip -o -4 addr 2>/dev/null", ct));
        m.Adapters = adapters;
        foreach (var a in adapters)
            if (!string.IsNullOrWhiteSpace(a.Mac) && !m.MacAddresses.Contains(a.Mac)) m.MacAddresses.Add(a.Mac);
        m.RefreshCounts();
    }

    private static void CollectUsers(ISshSession s, Machine m, CancellationToken ct)
    {
        m.System.LoggedOnUser = LinuxParsers.ParseWhoCurrentUser(Run(s, "who 2>/dev/null", ct));
        var last = LinuxParsers.ParseLastUser(Run(s, "last -n 1 2>/dev/null", ct));
        if (!string.IsNullOrWhiteSpace(last)) m.System.LastLoggedOnUser = last;
    }

    private static void CollectSoftware(ISshSession s, Machine m, CancellationToken ct)
    {
        var mgr = Run(s,
            "command -v dpkg-query >/dev/null 2>&1 && echo dpkg || (command -v rpm >/dev/null 2>&1 && echo rpm || (command -v apk >/dev/null 2>&1 && echo apk || echo none))",
            ct).Trim();

        List<SoftwareEntry> pkgs = mgr switch
        {
            "dpkg" => LinuxParsers.ParseDpkg(Run(s, "dpkg-query -W -f='${Package}\\t${Version}\\t${Maintainer}\\n' 2>/dev/null", ct)),
            "rpm" => LinuxParsers.ParseRpm(Run(s, "rpm -qa --qf '%{NAME}\\t%{VERSION}-%{RELEASE}\\t%{VENDOR}\\n' 2>/dev/null", ct)),
            "apk" => LinuxParsers.ParseApk(Run(s, "apk info -v 2>/dev/null", ct)),
            _ => throw new InvalidOperationException("No supported package manager (dpkg/rpm/apk) found."),
        };
        m.Software = pkgs;
        m.RefreshCounts();
    }

    // --- credential ordering / plumbing ---

    private List<CredentialCandidate> OrderCandidates(
        string host, IReadOnlyList<CredentialCandidate> candidates, IReadOnlyDictionary<string, CredentialCandidate>? overrides)
    {
        if (overrides is not null && overrides.TryGetValue(host, out var forced))
            return new List<CredentialCandidate> { forced };

        string? remembered;
        lock (_lock) _remembered.TryGetValue(host, out remembered);
        if (remembered is null) return candidates.ToList();

        var ordered = new List<CredentialCandidate>();
        var first = candidates.FirstOrDefault(c => c.Label == remembered);
        if (first is not null) ordered.Add(first);
        ordered.AddRange(candidates.Where(c => c.Label != remembered));
        return ordered;
    }

    private void Remember(string host, string label) { lock (_lock) _remembered[host] = label; }

    private static string ToPlainText(SecureString? secure)
        => secure is null ? "" : new NetworkCredential(string.Empty, secure).Password;
}
