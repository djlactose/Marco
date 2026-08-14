using Marco.Core.Model;
using Marco.Core.Wmi;

namespace Marco.Core.Inventory;

/// <summary>One credential candidate to try for a host, carrying its display label for attribution.</summary>
public sealed record CredentialCandidate(string Label, WmiCredential Credential);

/// <summary>Result of an inventory pass against a host.</summary>
public sealed record InventoryOutcome(bool Authenticated, string? CredentialLabel, MachineStatus Status, string? Detail);

/// <summary>
/// Runs the selected collectors against one host. Owns credential try-and-remember: for each host it tries the
/// remembered set first, then each candidate in order until one authenticates, then remembers the winner —
/// never mutating or retrying a password. Each collector is failure-isolated so a missing WMI class on an old OS
/// records a per-collector status instead of aborting the host.
/// </summary>
public sealed class InventoryRunner
{
    private readonly IWmiSessionFactory _sessions;
    private readonly IRemoteRegistryFactory _registries;
    private readonly IReadOnlyList<IInventoryCollector> _collectors;
    private readonly Dictionary<string, string> _remembered = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _rememberLock = new();

    public InventoryRunner(
        IWmiSessionFactory sessions,
        IRemoteRegistryFactory registries,
        IEnumerable<IInventoryCollector> collectors)
    {
        _sessions = sessions;
        _registries = registries;
        _collectors = collectors.ToList();
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

        var ordered = OrderCandidates(machine.Address, candidates, perHostOverrides);
        if (ordered.Count == 0)
        {
            machine.Status = MachineStatus.AuthFailed;
            machine.StatusDetail = "No credentials configured.";
            return new InventoryOutcome(false, null, machine.Status, machine.StatusDetail);
        }

        IWmiSession? session = null;
        CredentialCandidate? winner = null;
        string? lastAuthDetail = null;

        foreach (var candidate in ordered)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                session = await _sessions.ConnectAsync(machine.Address, candidate.Credential, ct).ConfigureAwait(false);
                winner = candidate;
                break;
            }
            catch (WmiException wex) when (wex.Kind is WmiFailureKind.AuthFailed or WmiFailureKind.AccessDenied)
            {
                lastAuthDetail = wex.Hint ?? wex.Message; // try the next set; never mutate this one
            }
            catch (WmiException wex) when (wex.Kind is WmiFailureKind.Unreachable or WmiFailureKind.Timeout)
            {
                machine.Status = MachineStatus.Unreachable;
                machine.StatusDetail = wex.Message;
                return new InventoryOutcome(false, null, machine.Status, machine.StatusDetail);
            }
        }

        if (session is null || winner is null)
        {
            machine.Status = MachineStatus.AuthFailed;
            machine.StatusDetail = lastAuthDetail ?? "All credential sets were rejected.";
            return new InventoryOutcome(false, null, machine.Status, machine.StatusDetail);
        }

        Remember(machine.Address, winner.Label);

        using (session)
        using (var registry = _registries.Create(machine.Address, winner.Credential, session))
        {
            var context = new InventoryContext(session, registry);
            int ok = 0, failed = 0;

            foreach (var collector in _collectors)
            {
                if (enabledCollectors is not null && !enabledCollectors.Contains(collector.Name)) continue;
                ct.ThrowIfCancellationRequested();

                machine.SetCollector(collector.Name, CollectorStatus.NotRun);
                try
                {
                    await collector.CollectAsync(context, machine, ct).ConfigureAwait(false);
                    machine.SetCollector(collector.Name, CollectorStatus.Ok);
                    ok++;
                }
                catch (OperationCanceledException) { throw; }
                catch (WmiException wex)
                {
                    machine.SetCollector(collector.Name, MapStatus(wex.Kind), wex.Hint ?? wex.Message);
                    if (wex.Kind is not WmiFailureKind.NotSupported) failed++;
                }
                catch (Exception ex)
                {
                    machine.SetCollector(collector.Name, CollectorStatus.Failed, ex.Message);
                    failed++;
                }
            }

            machine.LastScanned = DateTime.Now;
            machine.RefreshCounts();
            machine.Status = failed == 0 ? MachineStatus.Done
                : ok > 0 ? MachineStatus.Partial
                : MachineStatus.Error;
            machine.StatusDetail = failed == 0 ? null : $"{failed} collector(s) failed.";
            return new InventoryOutcome(true, winner.Label, machine.Status, machine.StatusDetail);
        }
    }

    private List<CredentialCandidate> OrderCandidates(
        string host,
        IReadOnlyList<CredentialCandidate> candidates,
        IReadOnlyDictionary<string, CredentialCandidate>? overrides)
    {
        if (overrides is not null && overrides.TryGetValue(host, out var forced))
            return new List<CredentialCandidate> { forced };

        string? rememberedLabel;
        lock (_rememberLock) _remembered.TryGetValue(host, out rememberedLabel);

        if (rememberedLabel is null) return candidates.ToList();

        // Remembered set first, then the rest in order.
        var ordered = new List<CredentialCandidate>();
        var remembered = candidates.FirstOrDefault(c => c.Label == rememberedLabel);
        if (remembered is not null) ordered.Add(remembered);
        ordered.AddRange(candidates.Where(c => c.Label != rememberedLabel));
        return ordered;
    }

    private void Remember(string host, string label)
    {
        lock (_rememberLock) _remembered[host] = label;
    }

    private static CollectorStatus MapStatus(WmiFailureKind kind) => kind switch
    {
        WmiFailureKind.NotSupported => CollectorStatus.NotSupported,
        WmiFailureKind.AccessDenied or WmiFailureKind.AuthFailed => CollectorStatus.AccessDenied,
        _ => CollectorStatus.Failed,
    };
}
