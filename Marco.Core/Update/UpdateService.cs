using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Marco.Core.Storage;

namespace Marco.Core.Update;

public enum UpdateState { Unknown, UpToDate, Staged, NotifyOnly, Failed }

public sealed record UpdateCheckResult(UpdateState State, ReleaseInfo? Release, string? StagedExePath);

/// <summary>Shown once after an update applies ("Updated to vX — what's new").</summary>
public sealed record WhatsNewInfo(string Version, string? Notes, string? HtmlUrl);

/// <summary>
/// Orchestrates the whole update lifecycle: background check → streamed download → SHA-256 verify → stage in the
/// data directory → swap the running single-file exe (rename-self, move-in, relaunch) either on user click or at
/// the next launch — plus crash-loop rollback via a startup sentinel. Every public method is wrapped so an update
/// failure can never break the app: failures log to the run log and the session continues on the current version.
/// </summary>
public sealed class UpdateService
{
    private const int KeepRollbackProtection = 1; // one rollback max: after it, no .old remains to roll back to

    private readonly AppPaths _paths;
    private readonly MarcoVersion _current;
    private readonly IUpdateSource _source;
    private readonly IStorageProbe _probe;
    private readonly Action<string, string> _log; // (stage, detail) -> RunLog "update" events

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public UpdateService(
        AppPaths paths,
        MarcoVersion current,
        IUpdateSource source,
        IStorageProbe probe,
        Action<string, string> log,
        bool includeBeta)
    {
        _paths = paths;
        _current = current;
        _source = source;
        _probe = probe;
        _log = log;
        IncludeBeta = includeBeta;
    }

    /// <summary>Channel selection; flipped live when the operator toggles the beta checkbox.</summary>
    public bool IncludeBeta { get; set; }

    public string UpdatesDirectory => _paths.UpdatesDirectory;
    public bool ExeDirectoryWritable => _probe.CanCreateAndWrite(_probe.ExeDirectory);

    private string ManifestPath => Path.Combine(UpdatesDirectory, "staged.json");
    private string SentinelPath => Path.Combine(UpdatesDirectory, "startup.pending");
    private string WhatsNewPath => Path.Combine(UpdatesDirectory, "whatsnew.json");
    private string RollbackPath => Path.Combine(UpdatesDirectory, "rollback.json");

    private sealed record SentinelInfo(string Version, int Attempts);
    private sealed record RollbackInfo(string Version);

    // --- Startup-time paths -------------------------------------------------------------------------------------

    /// <summary>
    /// Crash-loop rollback. Called first thing at startup. The swap arms a sentinel; the first launch of a freshly
    /// updated exe bumps its attempt count, and a successful startup clears it (<see cref="MarkStartupSucceeded"/>).
    /// Seeing an already-bumped sentinel therefore means the previous launch of this new version died before its
    /// window appeared — restore the .old exe and relaunch it. Returns true when a rollback relaunch was started
    /// (caller must shut down without showing UI).
    /// </summary>
    public bool CheckForFailedUpdate()
    {
        try
        {
            var sentinel = ReadJson<SentinelInfo>(SentinelPath);
            if (sentinel is null) return false;

            if (sentinel.Attempts < KeepRollbackProtection)
            {
                WriteJson(SentinelPath, sentinel with { Attempts = sentinel.Attempts + 1 });
                return false; // first launch of the new version — give it its chance
            }

            var exe = Environment.ProcessPath;
            var old = exe + ".old";
            if (exe is null || !File.Exists(old))
            {
                TryDelete(SentinelPath);
                return false;
            }

            // The updated exe (the one running right now) never started successfully. Put the previous one back.
            var bad = exe + ".bad";
            TryDelete(bad);
            File.Move(exe, bad);
            File.Move(old, exe);
            WriteJson(RollbackPath, new RollbackInfo(sentinel.Version)); // never re-stage this exact version
            TryDelete(SentinelPath);
            TryDelete(WhatsNewPath);
            TryDelete(ManifestPath);
            _log("rollback", sentinel.Version);
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            _log("apply_failed", $"rollback failed: {ex.GetType().Name}: {ex.Message}");
            TryDelete(SentinelPath);
            return false;
        }
    }

    /// <summary>Startup fast path: apply a verified staged update and relaunch into it. Returns true when the
    /// relaunch was started (caller must shut down without showing UI).</summary>
    public bool TryApplyStagedAndRelaunch()
    {
        try
        {
            var manifest = ReadStagedManifest();
            var decision = StagedUpdateEvaluator.Evaluate(
                _current, IncludeBeta, manifest,
                name => File.Exists(Path.Combine(UpdatesDirectory, name)),
                name => Sha256File.ComputeLowerHex(Path.Combine(UpdatesDirectory, name)));

            switch (decision)
            {
                case StagedDecision.Apply:
                    return ApplySwap(manifest!);
                case StagedDecision.StaleOlderOrEqual:
                case StagedDecision.CorruptOrMissing:
                    DiscardStaged(manifest, decision);
                    return false;
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            _log("apply_failed", $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Clears the crash-loop sentinel once the main window is actually up.</summary>
    public void MarkStartupSucceeded() => TryDelete(SentinelPath);

    /// <summary>The pending "What's new" for the version now running, if any.</summary>
    public WhatsNewInfo? ReadWhatsNew()
    {
        var info = ReadJson<WhatsNewInfo>(WhatsNewPath);
        if (info is null) return null;
        if (!MarcoVersion.TryParse(info.Version, out var v) || v.CompareTo(_current) != 0)
        {
            TryDelete(WhatsNewPath); // stale — belongs to some other version
            return null;
        }
        return info;
    }

    public void DismissWhatsNew() => TryDelete(WhatsNewPath);

    /// <summary>Post-startup housekeeping: previous exe, partial downloads, stale staged files, outgrown rollback marker.</summary>
    public void CleanupArtifacts()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                TryDelete(exe + ".old"); // fails while the pre-update process is still exiting; next launch gets it
                TryDelete(exe + ".bad");
            }

            foreach (var partial in SafeEnumerate(UpdatesDirectory, "*.partial"))
                TryDelete(partial);

            var manifest = ReadStagedManifest();
            if (manifest is not null && MarcoVersion.TryParse(manifest.Version, out var staged)
                && (staged <= _current || (staged.IsBeta && !IncludeBeta)))
            {
                DiscardStaged(manifest, StagedDecision.StaleOlderOrEqual);
            }

            var rollback = ReadJson<RollbackInfo>(RollbackPath);
            if (rollback is not null
                && (!MarcoVersion.TryParse(rollback.Version, out var failed) || _current >= failed))
            {
                TryDelete(RollbackPath); // we've moved past the version that crashed
            }

            _log("cleanup", "done");
        }
        catch (Exception ex)
        {
            _log("cleanup", $"failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // --- Background check / stage -------------------------------------------------------------------------------

    public async Task<UpdateCheckResult> CheckAndStageAsync(CancellationToken ct = default)
    {
        try
        {
            _log("check_started", IncludeBeta ? "channel=beta" : "channel=stable");

            ReleaseInfo? release;
            using (var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                checkCts.CancelAfter(TimeSpan.FromSeconds(10));
                release = await _source.GetLatestReleaseAsync(IncludeBeta, checkCts.Token).ConfigureAwait(false);
            }

            if (release is null || release.Version <= _current)
            {
                _log("up_to_date", release?.TagName ?? "no release found");
                return new UpdateCheckResult(UpdateState.UpToDate, release, null);
            }

            var rollback = ReadJson<RollbackInfo>(RollbackPath);
            if (rollback is not null && MarcoVersion.TryParse(rollback.Version, out var failed)
                && release.Version.CompareTo(failed) == 0)
            {
                _log("check_failed", $"{release.TagName} previously rolled back after a failed start; waiting for a newer release");
                return new UpdateCheckResult(UpdateState.Failed, release, null);
            }

            _log("available", release.TagName);

            // An unwritable exe directory (e.g. Program Files fallback mode) can never be swapped — don't spend
            // a 100+ MB download on it; just point the operator at the release page.
            if (!ExeDirectoryWritable)
            {
                _log("notify_only", release.TagName);
                return new UpdateCheckResult(UpdateState.NotifyOnly, release, null);
            }

            var fileName = $"Marco-{release.Version}.exe";
            var stagedPath = Path.Combine(UpdatesDirectory, fileName);

            var existing = ReadStagedManifest();
            if (existing is not null && existing.FileName == fileName && Sha256File.Verify(stagedPath, existing.Sha256))
            {
                _log("staged", $"already staged {release.TagName}");
                return new UpdateCheckResult(UpdateState.Staged, release, stagedPath);
            }

            string? expected;
            using (var shaCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                shaCts.CancelAfter(TimeSpan.FromSeconds(30));
                expected = await _source.GetChecksumAsync(release, shaCts.Token).ConfigureAwait(false);
            }
            if (expected is null)
            {
                _log("check_failed", $"{release.TagName}: checksum asset unreadable");
                return new UpdateCheckResult(UpdateState.Failed, release, null);
            }

            var partial = stagedPath + ".partial";
            TryDelete(partial);
            using (var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                dlCts.CancelAfter(TimeSpan.FromMinutes(15));
                await _source.DownloadExeAsync(release, partial, dlCts.Token).ConfigureAwait(false);
            }

            if (!Sha256File.Verify(partial, expected))
            {
                TryDelete(partial);
                _log("sha_mismatch", release.TagName);
                return new UpdateCheckResult(UpdateState.Failed, release, null);
            }

            TryDelete(stagedPath);
            File.Move(partial, stagedPath);
            WriteStagedManifest(new StagedManifest(
                release.Version.ToString(), fileName, expected, DateTime.UtcNow, release.Body, release.HtmlUrl));
            _log("staged", release.TagName);
            return new UpdateCheckResult(UpdateState.Staged, release, stagedPath);
        }
        catch (Exception ex)
        {
            _log("check_failed", $"{ex.GetType().Name}: {ex.Message}");
            return new UpdateCheckResult(UpdateState.Failed, null, null);
        }
    }

    /// <summary>The operator clicked "restart to apply". Returns true when the relaunch was started.</summary>
    public bool TryApplyAndRestartNow() => TryApplyStagedAndRelaunch();

    // --- Swap ---------------------------------------------------------------------------------------------------

    private bool ApplySwap(StagedManifest manifest)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            _log("apply_failed", "ProcessPath is null");
            return false;
        }

        var staged = Path.Combine(UpdatesDirectory, manifest.FileName);
        var old = exe + ".old";

        try { if (File.Exists(old)) File.Delete(old); }
        catch (Exception ex)
        {
            _log("apply_failed", $"previous .old is locked: {ex.Message}");
            return false;
        }

        try { File.Move(exe, old); }
        catch (Exception ex)
        {
            _log("apply_failed", $"could not rename running exe: {ex.Message}");
            return false;
        }

        try { File.Move(staged, exe); } // cross-volume safe (copy+delete fallback)
        catch (Exception ex)
        {
            try { File.Move(old, exe); } catch { /* keep whatever state we can */ }
            _log("apply_failed", $"could not move staged exe into place: {ex.Message}");
            return false;
        }

        WriteJson(SentinelPath, new SentinelInfo(manifest.Version, Attempts: 0));
        WriteJson(WhatsNewPath, new WhatsNewInfo(manifest.Version, manifest.Notes, manifest.HtmlUrl));
        TryDelete(ManifestPath);
        _log("applied", manifest.Version);

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log("apply_failed", $"relaunch failed (the new version will start on next launch): {ex.Message}");
        }
        return true;
    }

    private void DiscardStaged(StagedManifest? manifest, StagedDecision reason)
    {
        if (manifest is null) return;
        TryDelete(Path.Combine(UpdatesDirectory, manifest.FileName));
        TryDelete(ManifestPath);
        _log("cleanup", $"discarded staged {manifest.Version} ({reason})");
    }

    // --- Small file helpers -------------------------------------------------------------------------------------

    public StagedManifest? ReadStagedManifest() => ReadJson<StagedManifest>(ManifestPath);

    private void WriteStagedManifest(StagedManifest manifest) => WriteJson(ManifestPath, manifest);

    private static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOpts), new UTF8Encoding(false)); }
        catch { /* update bookkeeping must never break the app */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static IEnumerable<string> SafeEnumerate(string directory, string pattern)
    {
        try { return Directory.EnumerateFiles(directory, pattern).ToList(); }
        catch { return Array.Empty<string>(); }
    }
}
