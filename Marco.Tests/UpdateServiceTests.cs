using Marco.Core.Storage;
using Marco.Core.Update;
using Xunit;

namespace Marco.Tests;

/// <summary>Stage-path tests against a real temp directory. The swap itself (ApplySwap renames the running exe)
/// is deliberately never exercised here.</summary>
public class UpdateServiceTests
{
    private static MarcoVersion V(string s)
    {
        Assert.True(MarcoVersion.TryParse(s, out var v));
        return v;
    }

    private static ReleaseInfo Release(string version) => new(
        V(version), "v" + version, IsPrerelease: false,
        "https://example.invalid/Marco.exe", "https://example.invalid/Marco.exe.sha256",
        ExeSizeBytes: 22, "https://example.invalid/release", Body: "notes");

    private sealed class Harness : IDisposable
    {
        public TempDirProbe Probe { get; } = new();
        public AppPaths Paths { get; }
        public FakeUpdateSource Source { get; } = new() { Release = Release("9.9.9") };
        public List<(string stage, string detail)> Log { get; } = new();
        public UpdateService Service { get; }

        public Harness()
        {
            Paths = AppPaths.Resolve(Probe);
            Service = new UpdateService(Paths, V("1.0.0"), Source, Probe,
                (stage, detail) => { lock (Log) Log.Add((stage, detail)); }, includeBeta: false);
        }

        public string StagedPath => Path.Combine(Paths.UpdatesDirectory, "Marco-9.9.9.exe");
        public string ManifestPath => Path.Combine(Paths.UpdatesDirectory, "staged.json");
        public string StageLockPath => Path.Combine(Paths.UpdatesDirectory, "stage.lock");
        public bool Logged(string stage) { lock (Log) return Log.Any(e => e.stage == stage); }

        public void Dispose() => Probe.Dispose();
    }

    [Fact]
    public async Task CheckAndStage_StagesPayloadAndWritesManifest()
    {
        using var h = new Harness();

        var result = await h.Service.CheckAndStageAsync();

        Assert.Equal(UpdateState.Staged, result.State);
        Assert.Equal(h.StagedPath, result.StagedExePath);
        Assert.Equal(h.Source.Payload, File.ReadAllBytes(h.StagedPath));
        Assert.Equal(1, h.Source.Downloads);
        Assert.Empty(Directory.GetFiles(h.Paths.UpdatesDirectory, "*.partial"));

        var manifest = h.Service.ReadStagedManifest();
        Assert.NotNull(manifest);
        Assert.Equal("9.9.9", manifest!.Version);
        Assert.Equal("Marco-9.9.9.exe", manifest.FileName);
        Assert.Equal(h.Source.PayloadSha256, manifest.Sha256);
        Assert.False(File.Exists(h.ManifestPath + ".tmp")); // atomic write leaves no temp file behind
        Assert.True(h.Logged("staged"));
    }

    [Fact]
    public async Task CheckAndStage_IsDeferred_WhileAnotherInstanceHoldsTheStageLock()
    {
        using var h = new Harness();
        Directory.CreateDirectory(h.Paths.UpdatesDirectory);

        UpdateCheckResult deferred;
        using (new FileStream(h.StageLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            deferred = await h.Service.CheckAndStageAsync();
        }

        Assert.Equal(UpdateState.Deferred, deferred.State);
        Assert.NotNull(deferred.Release);
        Assert.Equal(0, h.Source.Downloads);
        Assert.Null(h.Service.ReadStagedManifest());
        Assert.True(h.Logged("stage_deferred"));

        // Lock released (the other window finished or died) → this instance stages normally.
        var staged = await h.Service.CheckAndStageAsync();
        Assert.Equal(UpdateState.Staged, staged.State);
        Assert.Equal(1, h.Source.Downloads);
    }

    [Fact]
    public async Task CheckAndStage_SecondCall_ReusesTheStagedFile()
    {
        using var h = new Harness();

        Assert.Equal(UpdateState.Staged, (await h.Service.CheckAndStageAsync()).State);
        Assert.Equal(UpdateState.Staged, (await h.Service.CheckAndStageAsync()).State);

        Assert.Equal(1, h.Source.Downloads);
        Assert.Contains(h.Log, e => e.stage == "staged" && e.detail.StartsWith("already staged"));
    }

    [Fact]
    public async Task CheckAndStage_UpToDate_NeverTouchesTheLockOrDownloads()
    {
        using var h = new Harness();
        h.Source.Release = Release("0.9.0");

        var result = await h.Service.CheckAndStageAsync();

        Assert.Equal(UpdateState.UpToDate, result.State);
        Assert.Equal(0, h.Source.Downloads);
        Assert.False(File.Exists(h.StageLockPath));
    }
}
