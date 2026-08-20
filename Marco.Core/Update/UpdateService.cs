using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Marco.Core.Storage;

namespace Marco.Core.Update;

/// <summary>
/// <see cref="Available"/>: a newer release exists but has not been downloaded — the operator is asked first.
/// <see cref="Staged"/>: downloaded and verified, ready to swap in. <see cref="NotifyOnly"/>: newer, but this
/// install can't swap itself (exe folder not writable). <see cref="Deferred"/>: another Marco window is
/// downloading this same release right now; check again later rather than treat it as a failure.
/// </summary>
public enum UpdateState { Unknown, UpToDate, Available, Staged, NotifyOnly, Failed, Deferred }

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
    private string StageLockPath => Path.Combine(UpdatesDirectory, "stage.lock");

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
            FileRetry.Run(() => File.Move(exe, bad));
            FileRetry.Run(() => File.Move(old, exe));
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

    /// <summary>Check only — never downloads. Returns <see cref="UpdateState.Available"/> for a newer release the
    /// operator hasn't been asked about, <see cref="UpdateState.Staged"/> when that release is already downloaded and
    /// verified (e.g. by a previous session or another window), <see cref="UpdateState.NotifyOnly"/> when this
    /// install can't swap itself, else UpToDate/Failed.</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
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

            // An unwritable exe directory (e.g. Program Files fallback mode) can never be swapped — don't offer a
            // 100+ MB download for it; just point the operator at the release page.
            if (!ExeDirectoryWritable)
            {
                _log("notify_only", release.TagName);
                return new UpdateCheckResult(UpdateState.NotifyOnly, release, null);
            }

            var stagedPath = StagedPathFor(release);
            var existing = ReadStagedManifest();
            if (existing is not null && existing.FileName == Path.GetFileName(stagedPath) && Sha256File.Verify(stagedPath, existing.Sha256))
            {
                _log("staged", $"already staged {release.TagName}");
                return new UpdateCheckResult(UpdateState.Staged, release, stagedPath);
            }

            return new UpdateCheckResult(UpdateState.Available, release, null);
        }
        catch (Exception ex)
        {
            _log("check_failed", $"{ex.GetType().Name}: {ex.Message}");
            return new UpdateCheckResult(UpdateState.Failed, null, null);
        }
    }

    /// <summary>Download, verify and stage <paramref name="release"/> (the operator said yes). <paramref name="progress"/>
    /// receives the download fraction 0..1. Returns Staged, Deferred (another window is downloading it), or Failed.</summary>
    public async Task<UpdateCheckResult> StageAsync(ReleaseInfo release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var stagedPath = StagedPathFor(release);
            var fileName = Path.GetFileName(stagedPath);

            // Several Marco windows share one updates directory. Only one of them should download a given release;
            // the others defer and pick up the staged file on their next check. A lock *file* (not a Mutex or
            // Semaphore) because it isn't thread-affine across the awaits below and the kernel releases it if the
            // process dies mid-download.
            using var stageLock = TryOpenStageLock();
            if (stageLock is null)
            {
                _log("stage_deferred", $"{release.TagName}: another Marco instance is staging it");
                return new UpdateCheckResult(UpdateState.Deferred, release, null);
            }

            // Under the lock: the other window may have just finished — don't download it a second time.
            var existing = ReadStagedManifest();
            if (existing is not null && existing.FileName == fileName && Sha256File.Verify(stagedPath, existing.Sha256))
            {
                _log("staged", $"already staged {release.TagName}");
                progress?.Report(1);
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
                _log("stage_failed", $"{release.TagName}: checksum asset unreadable");
                return new UpdateCheckResult(UpdateState.Failed, release, null);
            }

            var partial = $"{stagedPath}.{Environment.ProcessId}.partial";
            if (File.Exists(partial) && Sha256File.Verify(partial, expected))
            {
                // An earlier attempt in this session downloaded and verified this exact payload but the final move
                // failed (sync client / AV briefly locking the file) — don't download the ~70 MB again.
                _log("partial_reused", release.TagName);
            }
            else
            {
                TryDelete(partial);
                _log("download_started", release.TagName);
                using (var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    dlCts.CancelAfter(TimeSpan.FromMinutes(15));
                    IProgress<long>? bytes = progress is null ? null : new Progress<long>(received =>
                        progress.Report(release.ExeSizeBytes > 0 ? Math.Clamp((double)received / release.ExeSizeBytes, 0, 1) : 0));
                    await _source.DownloadExeAsync(release, partial, bytes, dlCts.Token).ConfigureAwait(false);
                }

                if (!Sha256File.Verify(partial, expected))
                {
                    TryDelete(partial);
                    _log("sha_mismatch", release.TagName);
                    return new UpdateCheckResult(UpdateState.Failed, release, null);
                }
            }

            FileRetry.Run(() => File.Move(partial, stagedPath, overwrite: true));
            WriteStagedManifest(new StagedManifest(
                release.Version.ToString(), fileName, expected, DateTime.UtcNow, release.Body, release.HtmlUrl));
            _log("staged", release.TagName);
            progress?.Report(1);
            return new UpdateCheckResult(UpdateState.Staged, release, stagedPath);
        }
        catch (Exception ex)
        {
            // Deliberately keeps the .partial: if it downloaded and verified, the next attempt reuses it instead of
            // re-downloading; CleanupArtifacts reclaims strays at the next launch.
            _log("stage_failed", $"{release.TagName}: {ex.GetType().Name}: {ex.Message}");
            return new UpdateCheckResult(UpdateState.Failed, release, null);
        }
    }

    /// <summary>Check and, when a newer release is available, stage it straight away — the unattended path
    /// (no operator to ask). The interactive app uses <see cref="CheckAsync"/> and asks before <see cref="StageAsync"/>.</summary>
    public async Task<UpdateCheckResult> CheckAndStageAsync(CancellationToken ct = default)
    {
        var check = await CheckAsync(ct).ConfigureAwait(false);
        return check.State == UpdateState.Available && check.Release is not null
            ? await StageAsync(check.Release, null, ct).ConfigureAwait(false)
            : check;
    }

    private string StagedPathFor(ReleaseInfo release) => Path.Combine(UpdatesDirectory, $"Marco-{release.Version}.exe");

    /// <summary>The operator clicked "restart to apply". Returns true when the relaunch was started. Serialised
    /// with the other windows' startup swap/rollback through the update gate.</summary>
    public bool TryApplyAndRestartNow()
    {
        using var gate = UpdateGate.TryAcquire(_paths.Root, TimeSpan.FromSeconds(3));
        if (gate is null)
        {
            _log("gate_busy", "restart-to-apply skipped: another Marco instance is starting or applying an update");
            return false;
        }
        return TryApplyStagedAndRelaunch();
    }

    /// <summary>True when the exe on disk at our own path is already a newer version than the one running — i.e.
    /// another Marco window applied the update. This process keeps running the old image; a fresh launch gets the
    /// new one.</summary>
    public bool ExeOnDiskIsNewer()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null || !File.Exists(exe)) return false;
            var product = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
            if (product is null) return false;
            var plus = product.IndexOf('+'); // ProductVersion mirrors InformationalVersion; strip any "+sha"
            if (plus >= 0) product = product[..plus];
            return MarcoVersion.TryParse(product, out var onDisk) && onDisk > _current;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens <c>updates\stage.lock</c> exclusively; null when another process holds it. A couple of quick
    /// retries absorb an antivirus scanner briefly holding the file. The 0-byte lock file is left in place
    /// (no DeleteOnClose — that invites delete-pending races between two openers).</summary>
    private FileStream? TryOpenStageLock()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return new FileStream(StageLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                if (attempt < 2) Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }

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

        try { FileRetry.Run(() => { if (File.Exists(old)) File.Delete(old); }); }
        catch (Exception ex)
        {
            _log("apply_failed", $"previous .old is locked: {ex.Message}");
            return false;
        }

        try { FileRetry.Run(() => File.Move(exe, old)); }
        catch (Exception ex)
        {
            _log("apply_failed", $"could not rename running exe: {ex.Message}");
            return false;
        }

        try { FileRetry.Run(() => File.Move(staged, exe)); } // cross-volume safe (copy+delete fallback)
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

    /// <summary>Write-to-temp then rename, so a reader in another Marco window (or the next startup) never sees a
    /// half-written manifest or sentinel.</summary>
    private static void WriteJson<T>(string path, T value)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOpts), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
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
