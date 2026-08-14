using System.Diagnostics;
using Marco.Core.Model;
using Marco.Core.Targets;

namespace Marco.Core.Scanning;

/// <summary>
/// Orchestrates a discovery sweep: throttled fan-out over the (lazy) target sequence, per-host liveness →
/// naming → MAC/OUI → classification, with incremental emission so the grid fills as hosts respond. Every
/// per-host failure is captured onto that host's row — no single host can throw into the scan loop. Inventory
/// (Phase 2) is layered on via <see cref="InventoryRunner"/>; this type owns discovery only.
/// </summary>
public sealed class ScanController
{
    private readonly ILivenessProbe _liveness;
    private readonly INameResolver _names;
    private readonly IMacResolver _mac;
    private readonly IOuiLookup _oui;
    private readonly IDeviceClassifier _classifier;

    public ScanController(
        ILivenessProbe liveness,
        INameResolver names,
        IMacResolver mac,
        IOuiLookup oui,
        IDeviceClassifier classifier)
    {
        _liveness = liveness;
        _names = names;
        _mac = mac;
        _oui = oui;
        _classifier = classifier;
    }

    public async Task RunDiscoveryAsync(
        IEnumerable<ScanTarget> targets,
        ScanSettings settings,
        int? totalHint,
        bool includeUnreachable,
        Action<Machine> onMachine,
        IProgress<ScanProgress>? progress,
        PauseController? pause,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int completed = 0, alive = 0, unreachable = 0;
        var reportLock = new object();
        long lastReportTicks = 0;

        void Report(bool force)
        {
            if (progress is null) return;
            // Throttle to ~5 Hz unless forced, so a 65k sweep doesn't marshal 65k callbacks.
            long now = sw.ElapsedMilliseconds;
            lock (reportLock)
            {
                if (!force && now - Interlocked.Read(ref lastReportTicks) < 200) return;
                Interlocked.Exchange(ref lastReportTicks, now);
            }
            int done = Volatile.Read(ref completed);
            TimeSpan? eta = null;
            if (totalHint is > 0 && done > 0)
            {
                double perItem = sw.Elapsed.TotalSeconds / done;
                int remaining = Math.Max(0, totalHint.Value - done);
                eta = TimeSpan.FromSeconds(perItem * remaining);
            }
            progress.Report(new ScanProgress(
                ScanPhase.Discovery, done, totalHint,
                Volatile.Read(ref alive), Volatile.Read(ref unreachable),
                sw.Elapsed, eta));
        }

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, settings.DiscoveryConcurrency),
            CancellationToken = ct,
        };

        try
        {
            await Parallel.ForEachAsync(targets, options, async (target, token) =>
            {
                if (pause is not null) await pause.WaitWhilePausedAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                Machine? emit = null;
                try
                {
                    var liveness = await _liveness.ProbeAsync(target.Address, settings, token).ConfigureAwait(false);

                    if (!liveness.IsAlive)
                    {
                        Interlocked.Increment(ref unreachable);
                        if (includeUnreachable)
                        {
                            var dead = new Machine(target.Address)
                            {
                                IsAlive = false,
                                Status = MachineStatus.Unreachable,
                                StatusDetail = "No response to ICMP or TCP probes.",
                                LastScanned = DateTime.Now,
                                DiscoveryMethod = DiscoveryMethod.None,
                            };
                            if (target.IsHostname) dead.Name = target.Address;
                            emit = dead;
                        }
                    }
                    else
                    {
                        emit = await BuildAliveMachineAsync(target, liveness, settings, token).ConfigureAwait(false);
                        Interlocked.Increment(ref alive);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Backstop: never let a host throw into the loop; surface it as an errored row.
                    emit = new Machine(target.Address)
                    {
                        Status = MachineStatus.Error,
                        StatusDetail = ex.Message,
                        LastScanned = DateTime.Now,
                    };
                    if (target.IsHostname) emit.Name = target.Address;
                }
                finally
                {
                    Interlocked.Increment(ref completed);
                }

                if (emit is not null)
                    onMachine(emit);
                Report(force: false);
            }).ConfigureAwait(false);

            Report(force: true);
            progress?.Report(new ScanProgress(
                ScanPhase.Complete, Volatile.Read(ref completed), totalHint,
                Volatile.Read(ref alive), Volatile.Read(ref unreachable), sw.Elapsed, TimeSpan.Zero));
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new ScanProgress(
                ScanPhase.Cancelled, Volatile.Read(ref completed), totalHint,
                Volatile.Read(ref alive), Volatile.Read(ref unreachable), sw.Elapsed, null));
            throw;
        }
    }

    private async Task<Machine> BuildAliveMachineAsync(
        ScanTarget target, LivenessResult liveness, ScanSettings settings, CancellationToken ct)
    {
        var m = new Machine(target.Address)
        {
            IsAlive = true,
            DiscoveryMethod = liveness.Method,
            IcmpTtl = liveness.IcmpTtl,
            Status = MachineStatus.Alive,
            LastScanned = DateTime.Now,
        };
        foreach (var p in liveness.OpenPorts) m.OpenPorts.Add(p);
        if (target.IsHostname) m.Name = target.Address;

        // Name resolution — best effort; a failure must not drop an alive host.
        bool nbnsResponded = false;
        string? nbnsMac = null;
        if (settings.ResolveNames)
        {
            try
            {
                var nr = await _names.ResolveAsync(target.Address, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(nr.Name)) m.Name = nr.Name;
                if (!string.IsNullOrWhiteSpace(nr.Fqdn)) m.Fqdn = nr.Fqdn;
                if (!string.IsNullOrWhiteSpace(nr.Domain)) m.System.Domain = nr.Domain;
                nbnsResponded = nr.NbnsResponded; // NBNS only — a DNS name must not imply Windows
                nbnsMac = nr.Mac;
            }
            catch { /* leave name as-is */ }
        }

        // MAC via ARP (on-subnet only), else the NBNS-reported MAC.
        string? mac = null;
        if (settings.ResolveMac)
        {
            try { mac = await _mac.GetMacAsync(target.Address, ct).ConfigureAwait(false); }
            catch { /* off-subnet or unavailable */ }
        }
        mac ??= nbnsMac;
        if (!string.IsNullOrWhiteSpace(mac))
        {
            m.MacAddresses.Add(mac);
            var entry = _oui.Lookup(mac);
            if (entry is not null)
            {
                m.Vendor = entry.Vendor;
            }
        }

        // Classification.
        if (settings.ClassificationEnabled)
        {
            var ouiCategory = (mac is not null ? _oui.Lookup(mac)?.Category : null) ?? OuiCategory.None;
            var result = _classifier.Classify(m.OpenPorts, ouiCategory, nbnsResponded, liveness.IcmpTtl);
            m.DeviceType = result.DeviceType;
            m.IsVirtual = result.IsVirtual;
        }

        return m;
    }
}
