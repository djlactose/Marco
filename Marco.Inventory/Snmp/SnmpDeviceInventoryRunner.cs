using System.Net;
using System.Security;
using Marco.Core.Inventory;
using Marco.Core.Ipp;
using Marco.Core.Model;
using Marco.Core.Snmp;

namespace Marco.Inventory.Snmp;

/// <summary>
/// Inventories printers and network devices: SNMP v1/v2c for the standard system / interface / Host-Resources /
/// Printer MIBs, plus credential-free IPP (port 631) for the printer's own queue, state and — when the Printer
/// MIB is silent — supply levels. Owns community-string try-and-remember like the other runners. A device that
/// arrived classified as network gear but turns out to carry a printer (Host Resources says so) is re-typed to
/// Printer and gets the printer groups too. Each group is failure-isolated.
/// </summary>
public sealed class SnmpDeviceInventoryRunner : IInventoryRunner
{
    public const int DefaultPort = 161;
    public const string IppCredentialLabel = "IPP (no credentials)";

    /// <summary>Collector group names for printers (enable/disable keys; must exist in the catalogue).</summary>
    public static readonly IReadOnlyList<string> PrinterCollectorNames = new[]
        { "System", "Network", "PrinterStatus", "Supplies", "PageCounts", "Trays", "PrintQueue" };
    /// <summary>Collector group names for non-printer SNMP devices.</summary>
    public static readonly IReadOnlyList<string> NetworkCollectorNames = new[] { "System", "Network" };

    private static readonly string[] IppPrinterAttributes =
    {
        "printer-state", "printer-state-reasons", "queued-job-count", "printer-make-and-model", "printer-info",
        "printer-location", "printer-firmware-string-version", "printer-firmware-name", "printer-uuid",
        "marker-names", "marker-types", "marker-colors", "marker-levels", "marker-high-levels", "marker-low-levels",
        "printer-alert", "printer-alert-description", "printer-up-time", "printer-uri-supported",
    };
    private static readonly string[] IppJobAttributes =
    {
        "job-id", "job-name", "job-state", "job-state-reasons", "job-originating-user-name",
        "job-impressions", "job-impressions-completed", "time-at-creation",
    };
    private const int MaxJobs = 25;

    private readonly ISnmpSessionFactory _snmp;
    private readonly IIppClient? _ipp;
    private readonly int _port;
    private readonly SnmpOptions _options;
    private readonly SnmpOptions _probeOptions;
    private readonly Dictionary<string, string> _remembered = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public SnmpDeviceInventoryRunner(ISnmpSessionFactory snmp, IIppClient? ipp = null, int port = DefaultPort,
        SnmpOptions? options = null, SnmpOptions? probeOptions = null)
    {
        _snmp = snmp;
        _ipp = ipp;
        _port = port;
        _options = options ?? SnmpOptions.Default;
        _probeOptions = probeOptions ?? new SnmpOptions(TimeoutMs: 2000, Retries: 1);
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
            machine.CurrentActivity = null;
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
        bool isPrinter = machine.DeviceType == DeviceType.Printer;

        var ordered = OrderCandidates(machine.Address, candidates, perHostOverrides)
            .Where(c => c.Kind == CredentialKind.Snmp && !string.IsNullOrEmpty(ToPlainText(c.Credential.Password)))
            .ToList();

        // --- 1. Find a community string that answers ---------------------------------------------------
        SnmpProbeResult? probe = null;
        CredentialCandidate? winner = null;
        bool snmpDisabled = false;
        for (int i = 0; i < ordered.Count && probe is null; i++)
        {
            var candidate = ordered[i];
            ct.ThrowIfCancellationRequested();
            machine.CurrentActivity = ordered.Count == 1
                ? $"Probing SNMP ({candidate.Label})…"
                : $"Probing SNMP ({candidate.Label}, {i + 1}/{ordered.Count})…";
            try
            {
                probe = await SnmpProbe.ProbeAsync(_snmp, machine.Address, _port, ToPlainText(candidate.Credential.Password),
                    candidate.SnmpVersion, _probeOptions, ct).ConfigureAwait(false);
                if (probe is not null) winner = candidate;
            }
            catch (SnmpException ex) when (ex.Kind == SnmpFailureKind.PortUnreachable)
            {
                snmpDisabled = true;
                break; // the device refuses UDP 161 outright — no community will help
            }
        }
        if (winner is not null) Remember(machine.Address, winner.Label);

        ISnmpSession? session = winner is null || probe is null ? null
            : _snmp.Create(machine.Address, _port, ToPlainText(winner.Credential.Password), probe.Version, _options);

        // --- 2. Collect -----------------------------------------------------------------------------------
        int ok = 0, failed = 0, skipped = 0;
        var sources = new List<string>();
        bool ippAnswered = false;
        var ctx = new DeviceContext(machine, isPrinter, probe);

        async Task Group(string name, Func<Task> body, bool requiresSnmp = true)
        {
            if (enabledCollectors is not null && !enabledCollectors.Contains(name)) return;
            ct.ThrowIfCancellationRequested();
            if (requiresSnmp && session is null)
            {
                machine.SetCollector(name, CollectorStatus.NotSupported,
                    snmpDisabled ? "SNMP is switched off on the device." : "No SNMP response.");
                skipped++;
                return;
            }
            machine.CurrentActivity = $"Collecting {name}…";
            machine.SetCollector(name, CollectorStatus.NotRun);
            try { await body().ConfigureAwait(false); machine.SetCollector(name, CollectorStatus.Ok); ok++; }
            catch (OperationCanceledException) { throw; }
            catch (IppException ex) { machine.SetCollector(name, CollectorStatus.NotSupported, ex.Message); skipped++; }
            catch (Exception ex) { machine.SetCollector(name, CollectorStatus.Failed, ex.Message); failed++; }
        }

        using (session)
        {
            if (session is not null) sources.Add($"SNMP {(probe!.Version == SnmpVersion.V1 ? "v1" : "v2c")}");

            await Group("System", () => CollectSystemAsync(session!, ctx, ct)).ConfigureAwait(false);
            if (ctx.ReTyped)
            {
                isPrinter = true;
                machine.DeviceType = DeviceType.Printer;
            }
            await Group("Network", () => CollectNetworkAsync(session!, ctx, ct)).ConfigureAwait(false);

            if (isPrinter)
            {
                ctx.Printer ??= new PrinterDevice();
                await Group("PrinterStatus", () => CollectPrinterStatusAsync(session!, ctx, ct)).ConfigureAwait(false);
                await Group("Supplies", () => CollectSuppliesAsync(session!, ctx, ct)).ConfigureAwait(false);
                await Group("PageCounts", () => CollectPageCountsAsync(session!, ctx, ct)).ConfigureAwait(false);
                await Group("Trays", () => CollectTraysAsync(session!, ctx, ct)).ConfigureAwait(false);
                if (_ipp is not null)
                {
                    await Group("PrintQueue", async () =>
                    {
                        await CollectIppAsync(_ipp, ctx, ct).ConfigureAwait(false);
                        ippAnswered = true;
                    }, requiresSnmp: false).ConfigureAwait(false);
                }
                else
                {
                    machine.SetCollector("PrintQueue", CollectorStatus.NotSupported, "IPP client not configured.");
                }
                if (ippAnswered) sources.Add("IPP");
                ctx.Printer.Sources = sources;
                if (session is not null || ippAnswered) machine.Printer = ctx.Printer;
            }
            else if (ctx.NetworkDevice is not null)
            {
                ctx.NetworkDevice.Sources = sources;
                machine.NetworkDevice = ctx.NetworkDevice;
            }
        }

        // --- 3. Outcome ---------------------------------------------------------------------------------
        if (session is null && !ippAnswered)
        {
            machine.Status = MachineStatus.Unreachable;
            machine.ConnectFailure = snmpDisabled ? ConnectFailure.SnmpDisabled : ConnectFailure.SnmpNoResponse;
            string why = snmpDisabled
                ? "SNMP is switched off on this device (UDP 161 unreachable)"
                : ordered.Count == 0
                    ? "No SNMP credentials (community strings) configured"
                    : "No SNMP v1/v2c response (disabled, firewalled, or wrong community string)";
            machine.StatusDetail = isPrinter && _ipp is not null ? $"{why}, and no IPP on port 631." : why + ".";
            if (ordered.Count == 0 && !snmpDisabled) machine.ConnectFailure = ConnectFailure.NoCredentials;
            return new InventoryOutcome(false, null, machine.Status, machine.StatusDetail);
        }

        machine.LastScanned = DateTime.Now;
        machine.RefreshCounts();
        machine.Status = failed == 0 && skipped == 0 ? MachineStatus.Done : ok > 0 ? MachineStatus.Partial : MachineStatus.Error;
        var notes = new List<string>();
        if (ctx.ReTyped) notes.Add("Re-classified as printer (Host Resources MIB).");
        if (failed > 0) notes.Add($"{failed} collector(s) failed.");
        if (session is null) notes.Add("SNMP did not answer; printer data is from IPP only.");
        else if (skipped > 0 && isPrinter && !ippAnswered) notes.Add("IPP (port 631) not available.");
        machine.StatusDetail = notes.Count == 0 ? null : string.Join(" ", notes);
        return new InventoryOutcome(true, winner?.Label ?? IppCredentialLabel, machine.Status, machine.StatusDetail);
    }

    /// <summary>Per-host scratch shared by the groups.</summary>
    private sealed class DeviceContext
    {
        public Machine Machine { get; }
        public bool IsPrinter { get; set; }
        public bool ReTyped { get; set; }
        public SnmpProbeResult? Probe { get; }
        public PrinterDevice? Printer { get; set; }
        public NetworkDeviceInfo? NetworkDevice { get; set; }
        /// <summary>hrDeviceIndex values of printer devices (usually just 1).</summary>
        public List<uint> PrinterDeviceIndexes { get; } = new();
        public DeviceContext(Machine m, bool isPrinter, SnmpProbeResult? probe) { Machine = m; IsPrinter = isPrinter; Probe = probe; }
    }

    // =================================================================== groups

    private static async Task CollectSystemAsync(ISnmpSession s, DeviceContext ctx, CancellationToken ct)
    {
        var m = ctx.Machine;
        var scalars = await s.GetMapAsync(new[] { PrinterMib.SysName, PrinterMib.SysLocation, PrinterMib.SysContact }, ct).ConfigureAwait(false);
        string? sysName = scalars.GetValueOrDefault(PrinterMib.SysName)?.AsText();
        string? location = scalars.GetValueOrDefault(PrinterMib.SysLocation)?.AsText();
        string? contact = scalars.GetValueOrDefault(PrinterMib.SysContact)?.AsText();
        string? sysDescr = ctx.Probe?.Description;
        var objectId = ctx.Probe?.ObjectId;
        var uptime = PrinterMib.UptimeFromTicks(ctx.Probe?.UpTimeTicks);

        // Host Resources: which device rows are printers, and their model text.
        var hrDevices = await s.WalkAsync(PrinterMib.HrDeviceEntry, 256, ct).ConfigureAwait(false);
        var printers = PrinterMib.FindPrinterDevices(hrDevices);
        foreach (var p in printers) ctx.PrinterDeviceIndexes.Add(p.Index);
        if (printers.Count > 0 && !ctx.IsPrinter)
        {
            ctx.IsPrinter = true;
            ctx.ReTyped = true;
        }

        // Identity fallbacks, in order of trust.
        var ent = await s.WalkAsync(PrinterMib.EntPhysicalEntry, 96, ct).ConfigureAwait(false);
        var entRows = PrinterMib.Rows(ent, PrinterMib.EntPhysicalEntry);
        string? entSerial = entRows.Select(r => r.Text(11)).FirstOrDefault(t => !PrinterMib.IsPlaceholder(t));
        string? entModel = entRows.Select(r => r.Text(13)).FirstOrDefault(t => !PrinterMib.IsPlaceholder(t));
        string? entMfg = entRows.Select(r => r.Text(12)).FirstOrDefault(t => !PrinterMib.IsPlaceholder(t));
        string? entFirmware = entRows.Select(r => r.Text(10) ?? r.Text(9)).FirstOrDefault(t => !PrinterMib.IsPlaceholder(t));

        string? serial = null;
        if (ctx.IsPrinter)
        {
            var prtSerial = await s.WalkAsync(PrinterMib.PrtGeneralSerialNumber, 8, ct).ConfigureAwait(false);
            serial = prtSerial.Select(vb => vb.Value.AsText()).FirstOrDefault(t => !PrinterMib.IsPlaceholder(t));
        }
        serial ??= entSerial;
        if (serial is null)
        {
            var vendorSerials = await s.GetMapAsync(new[] { PrinterMib.HpSerialNumber, PrinterMib.BrotherSerialNumber }, ct).ConfigureAwait(false);
            serial = vendorSerials.Values.Select(v => v.AsText()).FirstOrDefault(t => !PrinterMib.IsPlaceholder(t));
        }

        string? model = printers.Select(p => p.Description).FirstOrDefault(d => !PrinterMib.IsPlaceholder(d)) ?? entModel;
        string? vendor = entMfg ?? PrinterMib.VendorFromObjectId(objectId) ?? m.Vendor;
        if (model is not null && vendor is not null && model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
            model = model[vendor.Length..].TrimStart(' ', '-', ':');

        if (vendor is not null) m.System.Manufacturer = vendor;
        if (model is not null) m.System.Model = model;
        else if (!ctx.IsPrinter && sysDescr is { Length: > 0 }) m.System.Model = sysDescr.Length > 80 ? sysDescr[..80] : sysDescr;
        if (serial is not null) m.System.SerialNumber = serial;
        if (string.IsNullOrWhiteSpace(m.Name) && sysName is { Length: > 0 }) m.Name = sysName;

        if (ctx.IsPrinter)
        {
            ctx.Printer ??= new PrinterDevice();
            var p = ctx.Printer;
            p.SysName = sysName; p.Location = location; p.Contact = contact; p.Uptime = uptime;
            p.ObjectId = objectId?.ToString(); p.Description = sysDescr; p.Firmware = entFirmware;
        }
        else
        {
            ctx.NetworkDevice = new NetworkDeviceInfo
            {
                SysName = sysName, Description = sysDescr, Location = location, Contact = contact,
                Uptime = uptime, ObjectId = objectId?.ToString(), Firmware = entFirmware,
            };
        }
    }

    private static async Task CollectNetworkAsync(ISnmpSession s, DeviceContext ctx, CancellationToken ct)
    {
        var m = ctx.Machine;
        var ifWalk = new List<SnmpVarBind>();
        foreach (uint col in new uint[] { 2, 3, 5, 6, 8 })
            ifWalk.AddRange(await s.WalkAsync(PrinterMib.IfEntry.Append(col), 256, ct).ConfigureAwait(false));
        var ifXWalk = new List<SnmpVarBind>();
        foreach (uint col in new uint[] { 1, 15 })
            ifXWalk.AddRange(await s.WalkAsync(PrinterMib.IfXEntry.Append(col), 256, ct).ConfigureAwait(false));
        var ipWalk = await s.WalkAsync(PrinterMib.IpAdEntIfIndex, 64, ct).ConfigureAwait(false);

        var adapters = PrinterMib.ParseInterfaces(ifWalk, ifXWalk, ipWalk);
        m.Adapters = adapters;
        foreach (var a in adapters)
        {
            if (!string.IsNullOrWhiteSpace(a.Mac) && !m.MacAddresses.Contains(a.Mac)) m.MacAddresses.Add(a.Mac);
            foreach (var ip in a.IpAddresses) if (!m.IpAddresses.Contains(ip)) m.IpAddresses.Add(ip);
        }
        if (ctx.NetworkDevice is { } nd)
        {
            var (total, up) = PrinterMib.CountInterfaces(ifWalk);
            nd.InterfaceCount = total;
            nd.InterfacesUp = up;
        }
        m.RefreshCounts();
    }

    private static async Task CollectPrinterStatusAsync(ISnmpSession s, DeviceContext ctx, CancellationToken ct)
    {
        var p = ctx.Printer!;
        var indexes = ctx.PrinterDeviceIndexes.Count > 0 ? ctx.PrinterDeviceIndexes : new List<uint> { 1 };
        var oids = new List<SnmpOid>();
        foreach (var i in indexes)
        {
            oids.Add(PrinterMib.HrPrinterStatus.Append(i));
            oids.Add(PrinterMib.HrPrinterDetectedErrorState.Append(i));
            oids.Add(PrinterMib.HrDeviceEntry.Append(5, i));
        }
        var map = await s.GetMapAsync(oids, ct).ConfigureAwait(false);
        uint dev = indexes[0];
        p.Status = PrinterMib.DescribePrinterStatus(map.GetValueOrDefault(PrinterMib.HrPrinterStatus.Append(dev))?.Int);
        p.DeviceStatus = PrinterMib.DescribeDeviceStatus(map.GetValueOrDefault(PrinterMib.HrDeviceEntry.Append(5, dev))?.Int);
        p.ErrorStates = PrinterMib.DecodeErrorState(map.GetValueOrDefault(PrinterMib.HrPrinterDetectedErrorState.Append(dev)));

        p.DisplayText = PrinterMib.ParseConsoleText(await s.WalkAsync(PrinterMib.PrtConsoleDisplayBufferText, 8, ct).ConfigureAwait(false));
        p.Covers = PrinterMib.ParseCovers(await s.WalkAsync(PrinterMib.PrtCoverEntry, 64, ct).ConfigureAwait(false));
        var alerts = new List<SnmpVarBind>();
        foreach (uint col in new uint[] { 2, 4, 7, 8 })
            alerts.AddRange(await s.WalkAsync(PrinterMib.PrtAlertEntry.Append(col), 50, ct).ConfigureAwait(false));
        p.Alerts = PrinterMib.ParseAlerts(alerts);
    }

    private static async Task CollectSuppliesAsync(ISnmpSession s, DeviceContext ctx, CancellationToken ct)
    {
        var p = ctx.Printer!;
        var supplies = await s.WalkAsync(PrinterMib.PrtMarkerSuppliesEntry, 400, ct).ConfigureAwait(false);
        var colorants = await s.WalkAsync(PrinterMib.PrtMarkerColorantValue, 32, ct).ConfigureAwait(false);
        var list = PrinterMib.ParseSupplies(supplies, colorants);

        // When the engine only gives flags (Brother: every level is -3), let the error-state bits grade the toner.
        var toners = list.Where(x => x.Type is "toner" or "ink" && !x.IsReceptacle).ToList();
        if (toners.Count > 0 && toners.All(t => t.Percent is null))
        {
            if (p.ErrorStates.Contains(PrinterErrorStates.NoToner)) toners.ForEach(t => t.DeviceFlagsEmpty = toners.Count == 1);
            if (p.ErrorStates.Contains(PrinterErrorStates.LowToner)) toners.ForEach(t => t.DeviceFlagsLow = toners.Count == 1);
        }
        if (list.Count > 0) p.Supplies = list;
        else if (p.Supplies.Count == 0) throw new InvalidOperationException("The device exposes no prtMarkerSuppliesTable rows.");
    }

    private static async Task CollectPageCountsAsync(ISnmpSession s, DeviceContext ctx, CancellationToken ct)
    {
        var p = ctx.Printer!;
        var markers = new List<SnmpVarBind>();
        foreach (uint col in new uint[] { 3, 4 })
            markers.AddRange(await s.WalkAsync(PrinterMib.PrtMarkerEntry.Append(col), 16, ct).ConfigureAwait(false));
        var rows = PrinterMib.Rows(markers, PrinterMib.PrtMarkerEntry);
        var primary = rows.FirstOrDefault(r => r.Int(4) is { } c && c >= 0);
        if (primary is not null)
        {
            p.TotalPages = primary.Int(4);
            p.PageCountUnit = PrinterMib.CounterUnitName(primary.Int(3));
        }
        if (p.TotalPages is null)
        {
            var brother = await s.GetOneAsync(PrinterMib.BrotherPageCount, ct).ConfigureAwait(false);
            if (brother?.Int is { } b && b >= 0) { p.TotalPages = b; p.PageCountUnit = "impressions"; }
        }
        if (p.TotalPages is null) throw new InvalidOperationException("No page counter (prtMarkerLifeCount) exposed.");
    }

    private static async Task CollectTraysAsync(ISnmpSession s, DeviceContext ctx, CancellationToken ct)
    {
        var p = ctx.Printer!;
        var walk = new List<SnmpVarBind>();
        foreach (uint col in new uint[] { 9, 10, 12, 13, 18 })
            walk.AddRange(await s.WalkAsync(PrinterMib.PrtInputEntry.Append(col), 32, ct).ConfigureAwait(false));
        p.Trays = PrinterMib.ParseTrays(walk);
    }

    private static async Task CollectIppAsync(IIppClient ipp, DeviceContext ctx, CancellationToken ct)
    {
        var p = ctx.Printer!;
        var m = ctx.Machine;
        var attrs = await ipp.GetPrinterAttributesAsync(m.Address, IppPrinterAttributes, ct).ConfigureAwait(false);
        if (!IppOperation.IsSuccess(attrs.Status))
            throw new IppException(IppFailureKind.BadResponse, $"IPP Get-Printer-Attributes failed with status 0x{attrs.Status:X4}{(attrs.StatusMessage is { } sm ? $" ({sm})" : "")}.");
        var g = attrs.PrinterAttributes ?? throw new IppException(IppFailureKind.BadResponse, "IPP response carried no printer-attributes group.");

        p.IppState = g.Int("printer-state") switch { 3 => "idle", 4 => "processing", 5 => "stopped", _ => null };
        p.IppStateReasons = g.Texts("printer-state-reasons").Where(r => r != "none").ToList();
        p.QueuedJobs = g.Int("queued-job-count") is { } q ? (int)q : null;
        p.MakeAndModel = g.Text("printer-make-and-model");
        p.IppUri = g.Text("printer-uri-supported");
        p.Firmware ??= g.Text("printer-firmware-string-version");
        p.Location ??= g.Text("printer-location");
        if (p.Status is null && p.IppState is { } st) p.Status = st == "processing" ? "Printing" : st == "stopped" ? "Stopped" : "Idle";
        if (p.Supplies.Count == 0) p.Supplies = PrinterMib.ParseIppMarkers(g);
        if (m.System.Model is null && p.MakeAndModel is { } mm)
        {
            var vendor = m.System.Manufacturer ?? m.Vendor;
            m.System.Model = vendor is not null && mm.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)
                ? mm[vendor.Length..].TrimStart(' ', '-', ':') : mm;
            m.System.Manufacturer ??= mm.Split(' ', 2)[0];
        }

        try
        {
            var jobs = await ipp.GetJobsAsync(m.Address, "not-completed", MaxJobs, IppJobAttributes, ct).ConfigureAwait(false);
            if (IppOperation.IsSuccess(jobs.Status))
            {
                p.Jobs = jobs.JobGroups.Select(j => new PrintJobEntry
                {
                    Id = (int)(j.Int("job-id") ?? 0),
                    Name = j.Text("job-name"),
                    User = j.Text("job-originating-user-name"),
                    State = j.Int("job-state") switch
                    {
                        3 => "pending", 4 => "held", 5 => "processing", 6 => "stopped", 7 => "canceled", 8 => "aborted", 9 => "completed", _ => null,
                    },
                    Impressions = j.Int("job-impressions") is { } imp ? (int)imp : null,
                    Created = j.Int("time-at-creation") is { } t && p.Uptime is { } up
                        ? DateTime.Now - up + TimeSpan.FromSeconds(t) : null,
                }).ToList();
                p.QueuedJobs ??= p.Jobs.Count;
            }
        }
        catch (IppException) { /* Get-Jobs is optional: some firmware rejects it without authentication */ }
    }

    // =================================================================== credential plumbing

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
