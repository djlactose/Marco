using System.Collections.Concurrent;
using System.Security.Cryptography;
using Marco.Core.Model;
using Marco.Core.Scanning;
using Marco.Core.Storage;
using Marco.Core.Update;

namespace Marco.Tests;

/// <summary>Configurable liveness probe: alive set, optional per-address throw, optional delay, and an optional
/// per-probe gate (awaited first) so a test can hold hosts "in flight" and release them deliberately.</summary>
internal sealed class FakeLivenessProbe : ILivenessProbe
{
    public HashSet<string> Alive { get; } = new();
    public Dictionary<string, int[]> OpenPorts { get; } = new();
    public Dictionary<string, int?> Ttl { get; } = new();
    public HashSet<string> ThrowFor { get; } = new();
    public int DelayMs { get; set; }
    public Func<string, CancellationToken, Task>? BeforeProbe { get; set; }
    public ConcurrentBag<string> Calls { get; } = new();

    public async Task<LivenessResult> ProbeAsync(string address, ScanSettings settings, CancellationToken ct)
    {
        Calls.Add(address);
        if (BeforeProbe is not null) await BeforeProbe(address, ct).ConfigureAwait(false);
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
    public ConcurrentBag<string> Calls { get; } = new();

    public Task<NameResult> ResolveAsync(string address, CancellationToken ct)
    {
        Calls.Add(address);
        return Task.FromResult(Names.TryGetValue(address, out var n) ? n : NameResult.Empty);
    }
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

/// <summary>Synchronous IProgress: reports are recorded inline on the reporting thread (Progress&lt;T&gt; would
/// post to a captured context and force tests to sleep). <see cref="OnReport"/> lets a test react to a specific
/// snapshot (e.g. complete a TaskCompletionSource when InFlight hits zero).</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly object _lock = new();
    private readonly List<T> _reports = new();

    public Action<T>? OnReport { get; set; }

    public IReadOnlyList<T> Reports
    {
        get { lock (_lock) return _reports.ToList(); }
    }

    public T? Last
    {
        get { lock (_lock) return _reports.Count > 0 ? _reports[^1] : default; }
    }

    public void Report(T value)
    {
        lock (_lock) _reports.Add(value);
        OnReport?.Invoke(value);
    }
}

/// <summary>Update source with a fixed release and an in-memory payload; the checksum is the payload's real
/// SHA-256 so the verify path is exercised. Downloads are counted so tests can prove one was skipped.</summary>
internal sealed class FakeUpdateSource : IUpdateSource
{
    public ReleaseInfo? Release { get; set; }
    public byte[] Payload { get; set; } = System.Text.Encoding.ASCII.GetBytes("marco-fake-exe-payload");
    public bool ChecksumUnavailable { get; set; }
    public int Downloads;

    public string PayloadSha256 => Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

    public Task<ReleaseInfo?> GetLatestReleaseAsync(bool includeBeta, CancellationToken ct)
        => Task.FromResult(Release);

    public Task<string?> GetChecksumAsync(ReleaseInfo release, CancellationToken ct)
        => Task.FromResult(ChecksumUnavailable ? null : PayloadSha256);

    public Task DownloadExeAsync(ReleaseInfo release, string destinationPath, IProgress<long>? bytesReceived, CancellationToken ct)
    {
        Interlocked.Increment(ref Downloads);
        File.WriteAllBytes(destinationPath, Payload);
        bytesReceived?.Report(Payload.Length / 2);
        bytesReceived?.Report(Payload.Length);
        return Task.CompletedTask;
    }
}

/// <summary>Storage probe rooted in a real, unique temp directory (created on demand), so services that need an
/// actual updates/ folder can run against disk without touching the developer's Marco.Data.</summary>
internal sealed class TempDirProbe : IStorageProbe, IDisposable
{
    public string ExeDirectory { get; }
    public string LocalAppDataDirectory { get; }

    public TempDirProbe()
    {
        ExeDirectory = Path.Combine(Path.GetTempPath(), "MarcoTests", Guid.NewGuid().ToString("N"));
        LocalAppDataDirectory = Path.Combine(ExeDirectory, "localappdata");
        Directory.CreateDirectory(ExeDirectory);
    }

    public bool CanCreateAndWrite(string directory) => true;
    public void EnsureDirectory(string directory) => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try { Directory.Delete(ExeDirectory, recursive: true); } catch { }
    }
}
