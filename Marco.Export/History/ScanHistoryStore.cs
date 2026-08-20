using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Marco.Core.Storage;

namespace Marco.Export.History;

/// <summary>
/// Keeps a rolling copy of every completed run in the scans directory: one gzipped <see cref="ScanDocument"/> per
/// run plus an <c>index.json</c> cache for cheap listing. Several Marco windows share the directory, so index
/// writes are serialised with a named mutex (same pattern as RunLog) and file names embed a random suffix so
/// concurrent saves never collide. The index is a cache, not a source of truth: entries whose file vanished are
/// dropped, and bare documents (from a lost or corrupt index) are re-adopted by reading just their metadata head.
/// All methods are best-effort against a syncing/AV-locked folder and never throw for IO races the caller can't fix.
/// </summary>
public sealed class ScanHistoryStore
{
    private const string IndexFileName = "index.json";

    private static readonly JsonSerializerOptions IndexOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;
    private readonly string _indexPath;
    private readonly Mutex? _mutex;
    private readonly JsonExporter _exporter = new();

    public ScanHistoryStore(string directory)
    {
        _directory = directory;
        _indexPath = Path.Combine(directory, IndexFileName);
        try { _mutex = new Mutex(initiallyOwned: false, "Marco.ScanHistory." + PathKey.For(_indexPath)); }
        catch { _mutex = null; } // best effort without the cross-process lock
    }

    /// <summary>"20260820-134502-a1b2" — sortable timestamp plus a random suffix so two windows starting a run
    /// in the same second still get distinct ids (and therefore distinct files, no coordination needed).</summary>
    public static string NewRunId(DateTime timestamp)
        => $"{timestamp:yyyyMMdd-HHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}";

    /// <summary>Save (or phase-upgrade) a run. Saving an id that already exists overwrites its file and index
    /// entry — that is how a discovery-only save becomes the inventoried save of the same run. Prunes the oldest
    /// entries beyond <paramref name="limit"/> afterwards, under the same lock.</summary>
    public ScanHistoryEntry Save(ScanDocument document, string runId, ScanHistoryPhase phase, int limit = 30)
    {
        Directory.CreateDirectory(_directory);
        var fileName = runId + ".json.gz";
        var path = Path.Combine(_directory, fileName);
        var temp = path + ".tmp";
        _exporter.Export(document, temp, compress: true);
        MoveWithRetry(temp, path);

        var entry = new ScanHistoryEntry(
            runId, fileName, document.Metadata.Timestamp, document.Metadata.Operator,
            document.Metadata.RangesScanned?.ToList() ?? new List<string>(),
            document.Metadata.TotalTargets, document.Metadata.AliveCount,
            // Collector results only exist after an inventory pass; LastScanned is set by discovery too.
            document.Machines.Count(m => m.Collectors is { Count: > 0 }),
            phase, document.Metadata.Version, document.Metadata.SchemaVersion,
            TryGetLength(path));

        WithIndex(entries =>
        {
            entries.RemoveAll(e => string.Equals(e.Id, runId, StringComparison.OrdinalIgnoreCase));
            entries.Add(entry);
            PruneLocked(entries, limit);
        });
        return entry;
    }

    /// <summary>Every saved run, newest first, reconciled against the directory (missing files dropped,
    /// unindexed documents adopted with <see cref="ScanHistoryPhase.Unknown"/>).</summary>
    public IReadOnlyList<ScanHistoryEntry> List()
    {
        var result = new List<ScanHistoryEntry>();
        WithIndex(entries => result.AddRange(entries));
        return result.OrderByDescending(e => e.Timestamp).ToList();
    }

    public ScanDocument LoadDocument(ScanHistoryEntry entry)
        => _exporter.Load(Path.Combine(_directory, entry.File));

    public void Delete(ScanHistoryEntry entry)
        => WithIndex(entries =>
        {
            entries.RemoveAll(e => string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            TryDeleteFile(Path.Combine(_directory, entry.File));
        });

    // --- index plumbing ---

    /// <summary>Load-reconcile-mutate-save under the cross-process mutex. Reconciliation runs every time so a
    /// listing after a crash, a corrupt index, or another window's delete is always consistent with the files.</summary>
    private void WithIndex(Action<List<ScanHistoryEntry>> mutate)
    {
        bool owned = false;
        try
        {
            owned = AcquireMutex();
            var entries = LoadEntries();
            Reconcile(entries);
            var before = Snapshot(entries);
            mutate(entries);
            if (before != Snapshot(entries)) SaveEntries(entries);
        }
        catch { /* history bookkeeping must never break a scan */ }
        finally
        {
            if (owned) { try { _mutex!.ReleaseMutex(); } catch { } }
        }
    }

    private List<ScanHistoryEntry> LoadEntries()
    {
        try
        {
            if (!File.Exists(_indexPath)) return new List<ScanHistoryEntry>();
            var index = JsonSerializer.Deserialize<ScanHistoryIndex>(File.ReadAllText(_indexPath), IndexOptions);
            return index?.Entries?.ToList() ?? new List<ScanHistoryEntry>();
        }
        catch
        {
            return new List<ScanHistoryEntry>(); // corrupt index — Reconcile rebuilds it from the documents
        }
    }

    /// <summary>Drop entries whose file vanished; adopt documents the index doesn't know (metadata-head read only).</summary>
    private void Reconcile(List<ScanHistoryEntry> entries)
    {
        entries.RemoveAll(e => !File.Exists(Path.Combine(_directory, e.File)));
        if (!Directory.Exists(_directory)) return;

        var known = new HashSet<string>(entries.Select(e => e.File), StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            var name = Path.GetFileName(path);
            bool isDocument = (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                               || name.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase))
                              && !string.Equals(name, IndexFileName, StringComparison.OrdinalIgnoreCase);
            if (!isDocument || known.Contains(name)) continue;
            if (ScanMetadataReader.TryRead(path) is not { } meta) continue; // not a scan document — leave it alone

            var id = name.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase)
                ? name[..^".json.gz".Length]
                : Path.GetFileNameWithoutExtension(name);
            entries.Add(new ScanHistoryEntry(
                id, name, meta.Timestamp, meta.Operator,
                meta.RangesScanned?.ToList() ?? new List<string>(),
                meta.TotalTargets, meta.AliveCount, InventoriedCount: 0,
                ScanHistoryPhase.Unknown, meta.Version, meta.SchemaVersion, TryGetLength(path)));
        }
    }

    private void PruneLocked(List<ScanHistoryEntry> entries, int limit)
    {
        limit = Math.Max(1, limit);
        if (entries.Count <= limit) return;
        foreach (var old in entries.OrderByDescending(e => e.Timestamp).Skip(limit).ToList())
        {
            TryDeleteFile(Path.Combine(_directory, old.File));
            entries.Remove(old);
        }
    }

    private void SaveEntries(List<ScanHistoryEntry> entries)
    {
        Directory.CreateDirectory(_directory);
        var ordered = entries.OrderByDescending(e => e.Timestamp).ToList();
        var json = JsonSerializer.Serialize(
            new ScanHistoryIndex(ScanHistoryIndex.CurrentSchemaVersion, ordered), IndexOptions);
        var temp = _indexPath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        MoveWithRetry(temp, _indexPath);
    }

    private static string Snapshot(List<ScanHistoryEntry> entries)
        => string.Join("|", entries.OrderBy(e => e.Id).Select(e => $"{e.Id}:{e.Phase}:{e.SizeBytes}"));

    private bool AcquireMutex()
    {
        if (_mutex is null) return false;
        try { return _mutex.WaitOne(TimeSpan.FromSeconds(2)); }
        catch (AbandonedMutexException) { return true; } // previous holder died mid-write; we own it now
        catch { return false; }
    }

    /// <summary>Sync clients and AV scanners briefly lock fresh files in this folder; ride it out like RunLog does.</summary>
    private static void MoveWithRetry(string from, string to)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch { } // already gone, or locked — reconciliation will retry the delete implicitly next time
    }

    private static long TryGetLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }
}
