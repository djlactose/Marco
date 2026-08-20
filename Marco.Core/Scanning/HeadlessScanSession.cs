using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Targets;

namespace Marco.Core.Scanning;

/// <summary>
/// Discovery → inventory for the headless CLI: the same engine the UI uses, minus the pause/abandon machinery
/// (Ctrl+C cancels and waits). Routes each host by device type to the Windows or Linux runner, tries only
/// credentials meant for that host kind, and skips printers/network gear — mirroring the interactive routing
/// without depending on WPF. Progress is a simple line callback.
/// </summary>
public sealed class HeadlessScanSession
{
    private readonly ScanController _controller;
    private readonly IInventoryRunner _windows;
    private readonly IInventoryRunner _linux;
    private readonly Action<string>? _log;

    public HeadlessScanSession(ScanController controller, IInventoryRunner windows, IInventoryRunner linux,
        Action<string>? log = null)
    {
        _controller = controller;
        _windows = windows;
        _linux = linux;
        _log = log;
    }

    public sealed record Result(IReadOnlyList<Machine> Machines, int Alive, int Unreachable, int Authenticated, int Skipped);

    public async Task<Result> RunAsync(
        IReadOnlyList<ScanTarget> targets,
        ScanSettings settings,
        IReadOnlyList<CredentialCandidate> candidates,
        ISet<string>? enabledCollectors,
        bool inventory,
        bool includeUnreachable,
        CancellationToken ct)
    {
        var machines = new List<Machine>();
        var gate = new object();
        int lastReported = 0;
        await _controller.RunDiscoveryAsync(targets, settings, targets.Count, includeUnreachable,
            m => { lock (gate) machines.Add(m); },
            new Progress<ScanProgress>(p =>
            {
                if (p.Completed >= lastReported + 50 || p.Completed == p.Total)
                {
                    lastReported = p.Completed;
                    _log?.Invoke($"Discovery {p.Completed}/{p.Total}… {p.Alive} alive");
                }
            }),
            pause: null, ct).ConfigureAwait(false);

        int alive = machines.Count(m => m.IsAlive);
        int unreachable = machines.Count - alive;
        _log?.Invoke($"Discovery done: {alive} alive, {unreachable} unreachable.");

        int authenticated = 0, skipped = 0;
        if (inventory)
        {
            var alives = machines.Where(m => m.IsAlive).ToList();
            _log?.Invoke($"Inventorying {alives.Count} host(s)…");
            int done = 0;
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = settings.EffectiveInventoryConcurrency,
                CancellationToken = ct,
            };
            await Parallel.ForEachAsync(alives, options, async (m, token) =>
            {
                bool isLinux = m.DeviceType == DeviceType.UnixLinux;
                if (m.DeviceType is DeviceType.Printer or DeviceType.NetworkDevice)
                {
                    m.StatusDetail = "Skipped: printers/network devices aren't inventoried.";
                    Interlocked.Increment(ref skipped);
                }
                else
                {
                    var hostKind = isLinux ? CredentialKind.Linux : CredentialKind.Windows;
                    var applicable = candidates.Where(c => c.AppliesTo(hostKind)).ToList();
                    if (applicable.Count == 0)
                    {
                        m.StatusDetail = isLinux ? "No Linux/SSH credentials." : "No Windows credentials.";
                    }
                    else
                    {
                        var runner = isLinux ? _linux : _windows;
                        var outcome = await runner.InventoryAsync(m, applicable, null, enabledCollectors, token).ConfigureAwait(false);
                        if (outcome.Authenticated) Interlocked.Increment(ref authenticated);
                    }
                }
                int n = Interlocked.Increment(ref done);
                if (n % 25 == 0 || n == alives.Count) _log?.Invoke($"Inventory {n}/{alives.Count}…");
            }).ConfigureAwait(false);
            _log?.Invoke($"Inventory done: {authenticated}/{alives.Count} authenticated"
                + (skipped > 0 ? $", {skipped} skipped." : "."));
        }

        return new Result(machines, alive, unreachable, authenticated, skipped);
    }
}
