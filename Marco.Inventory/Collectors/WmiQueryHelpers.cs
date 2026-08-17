using System.Runtime.ExceptionServices;
using Marco.Core.Wmi;

namespace Marco.Inventory.Collectors;

internal static class WmiQueryHelpers
{
    public const string CimV2 = "root\\cimv2";
    public const string Wmi = "root\\WMI";
    public const string SecurityCenter2 = "root\\SecurityCenter2";
    public const string StandardCimV2 = "root\\StandardCimv2";
    public const string Defender = "root\\Microsoft\\Windows\\Defender";
    public const string Storage = "root\\Microsoft\\Windows\\Storage";
    public const string Smb = "root\\Microsoft\\Windows\\SMB";
    public const string DeviceGuard = "root\\Microsoft\\Windows\\DeviceGuard";
    public const string TaskScheduler = "root\\Microsoft\\Windows\\TaskScheduler";
    public const string VolumeEncryption = "root\\CIMV2\\Security\\MicrosoftVolumeEncryption";
    public const string Tpm = "root\\CIMV2\\Security\\MicrosoftTpm";

    /// <summary>Query and return the first object, or null if the class returned nothing.</summary>
    public static async Task<WmiObject?> QueryFirstAsync(
        this IWmiSession session, string wql, CancellationToken ct, string ns = CimV2)
    {
        var list = await session.QueryAsync(ns, wql, ct).ConfigureAwait(false);
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>Query, tolerating a NotSupported (missing class on old OS) as an empty result rather than an error.</summary>
    public static async Task<IReadOnlyList<WmiObject>> QueryTolerantAsync(
        this IWmiSession session, string wql, CancellationToken ct, string ns = CimV2)
    {
        try
        {
            return await session.QueryAsync(ns, wql, ct).ConfigureAwait(false);
        }
        catch (WmiException wex) when (wex.Kind == WmiFailureKind.NotSupported)
        {
            return Array.Empty<WmiObject>();
        }
    }

    /// <summary>Query for optional enrichment: any WMI failure (missing namespace, access denied, provider error)
    /// yields an empty result. Use only where the data is a bonus on top of an already-successful collector, never
    /// for the collector's primary query — a primary failure must surface as the collector's status.</summary>
    public static async Task<IReadOnlyList<WmiObject>> QueryOptionalAsync(
        this IWmiSession session, string wql, CancellationToken ct, string ns = CimV2)
    {
        try
        {
            return await session.QueryAsync(ns, wql, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (WmiException)
        {
            return Array.Empty<WmiObject>();
        }
    }
}

/// <summary>
/// Runs a collector's independent sub-probes so that one unavailable source (a namespace that only exists on
/// client SKUs, an access-denied security provider, a Remote Registry service that is off) is recorded as a note
/// instead of failing the whole collector. If <em>nothing</em> succeeded the first failure is rethrown so the
/// collector's status reflects it (NotSupported / AccessDenied / Failed).
/// </summary>
internal sealed class CollectorSteps
{
    private readonly List<string> _notes = new();
    private ExceptionDispatchInfo? _first;
    private int _ok;

    public async Task RunAsync(string label, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
            _ok++;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Record(label, ex); }
    }

    public void Run(string label, Action step)
    {
        try
        {
            step();
            _ok++;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Record(label, ex); }
    }

    private void Record(string label, Exception ex)
    {
        _notes.Add(ex is WmiException wex
            ? wex.Kind switch
            {
                WmiFailureKind.NotSupported => $"{label}: not available on this system",
                WmiFailureKind.AccessDenied or WmiFailureKind.AuthFailed => $"{label}: access denied",
                _ => $"{label}: {wex.Message}",
            }
            : $"{label}: {ex.Message}");
        _first ??= ExceptionDispatchInfo.Capture(ex);
    }

    public int Succeeded => _ok;

    public string? Notes => _notes.Count == 0 ? null : string.Join("; ", _notes);

    /// <summary>Rethrow the first failure when no step succeeded at all.</summary>
    public void ThrowIfNothingSucceeded()
    {
        if (_ok == 0) _first?.Throw();
    }
}
