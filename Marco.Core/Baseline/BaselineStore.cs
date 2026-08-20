using System.Text;
using System.Text.Json;
using Marco.Core.Storage;

namespace Marco.Core.Baseline;

/// <summary>
/// Persists the known-device baseline (baseline.json in Marco.Data). Concurrent Marco windows must MERGE trusts
/// rather than clobber each other, so mutations run reload-merge-save under a named cross-process mutex — the
/// CredentialStore pattern. Loads are tolerant: a missing or corrupt file reads as "no baseline yet".
/// </summary>
public sealed class BaselineStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Mutex? _mutex;

    public BaselineStore(string path)
    {
        _path = path;
        try { _mutex = new Mutex(initiallyOwned: false, "Marco.Baseline." + PathKey.For(path)); }
        catch { _mutex = null; }
    }

    public bool Exists => File.Exists(_path);

    public AssetBaseline? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<AssetBaseline>(File.ReadAllText(_path), Options);
        }
        catch
        {
            return null; // corrupt = no baseline; blessing again rebuilds it
        }
    }

    /// <summary>Bless: the new baseline replaces whatever existed.</summary>
    public void Replace(AssetBaseline baseline) => WithMutex(() => WriteAtomic(baseline));

    /// <summary>Trust: add entries on top of the CURRENT file content (another window may have trusted devices
    /// since we loaded), replacing entries with the same id.</summary>
    public void AddEntries(IEnumerable<BaselineEntry> entries, string? updatedBy)
        => WithMutex(() =>
        {
            var current = Load();
            var byId = current?.Entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase)
                       ?? new Dictionary<string, BaselineEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                byId[entry.Id] = entry;
            WriteAtomic(new AssetBaseline(AssetBaseline.CurrentSchemaVersion, DateTime.UtcNow, updatedBy,
                current?.SourceScanId, byId.Values.ToList()));
        });

    public void Delete() => WithMutex(() => { try { File.Delete(_path); } catch { } });

    private void WriteAtomic(AssetBaseline baseline)
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(baseline, Options), new UTF8Encoding(false));
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(temp, _path, overwrite: true); return; }
            catch (IOException) when (attempt < 2) { Thread.Sleep(50 * (attempt + 1)); } // sync client/AV lock
        }
    }

    private void WithMutex(Action action)
    {
        bool owned = false;
        try
        {
            try { owned = _mutex?.WaitOne(TimeSpan.FromSeconds(2)) ?? false; }
            catch (AbandonedMutexException) { owned = true; }
            catch { }
            action();
        }
        finally
        {
            if (owned) { try { _mutex!.ReleaseMutex(); } catch { } }
        }
    }
}
