using Marco.Core.Model;
using Marco.Core.Scanning;

namespace Marco.Tests;

/// <summary>Configurable liveness probe: alive set, optional per-address throw, optional delay.</summary>
internal sealed class FakeLivenessProbe : ILivenessProbe
{
    public HashSet<string> Alive { get; } = new();
    public Dictionary<string, int[]> OpenPorts { get; } = new();
    public Dictionary<string, int?> Ttl { get; } = new();
    public HashSet<string> ThrowFor { get; } = new();
    public int DelayMs { get; set; }

    public async Task<LivenessResult> ProbeAsync(string address, ScanSettings settings, CancellationToken ct)
    {
        if (DelayMs > 0) await Task.Delay(DelayMs, ct).ConfigureAwait(false);
        if (ThrowFor.Contains(address)) throw new InvalidOperationException($"boom:{address}");
        if (!Alive.Contains(address)) return LivenessResult.Dead;
        var ports = OpenPorts.TryGetValue(address, out var p) ? p : new[] { 445, 135 };
        int? ttl = Ttl.TryGetValue(address, out var t) ? t : 128;
        return new LivenessResult(true, DiscoveryMethod.Tcp, ttl, ports);
    }
}

internal sealed class FakeNameResolver : INameResolver
{
    public Dictionary<string, NameResult> Names { get; } = new();

    public Task<NameResult> ResolveAsync(string address, CancellationToken ct)
        => Task.FromResult(Names.TryGetValue(address, out var n) ? n : NameResult.Empty);
}

internal sealed class FakeMacResolver : IMacResolver
{
    public Dictionary<string, string?> Macs { get; } = new();

    public Task<string?> GetMacAsync(string address, CancellationToken ct)
        => Task.FromResult(Macs.TryGetValue(address, out var m) ? m : null);
}

internal sealed class FakeOuiLookup : IOuiLookup
{
    public Dictionary<string, OuiEntry> ByMac { get; } = new(StringComparer.OrdinalIgnoreCase);

    public OuiEntry? Lookup(string? mac)
        => mac is not null && ByMac.TryGetValue(mac, out var e) ? e : null;
}
