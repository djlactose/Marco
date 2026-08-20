using System.Text;
using System.Text.Json;
using Marco.Core.Storage;

namespace Marco.Core.Clients;

/// <summary>
/// Persists client profiles (clients.json in Marco.Data). Mutations run reload-merge-save under a cross-process
/// mutex so concurrent Marco windows don't clobber each other's edits; loads are tolerant (missing/corrupt file
/// reads as an empty list). No secrets in this file, ever — see ClientProfile.
/// </summary>
public sealed class ClientProfileStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Mutex? _mutex;

    public ClientProfileStore(string path)
    {
        _path = path;
        try { _mutex = new Mutex(initiallyOwned: false, "Marco.Clients." + PathKey.For(path)); }
        catch { _mutex = null; }
    }

    public IReadOnlyList<ClientProfile> Load()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<ClientProfile>();
            var list = JsonSerializer.Deserialize<ClientProfileList>(File.ReadAllText(_path), Options);
            return list?.Profiles ?? Array.Empty<ClientProfile>();
        }
        catch
        {
            return Array.Empty<ClientProfile>();
        }
    }

    /// <summary>Add or update (by Id) one profile on top of the current file content.</summary>
    public void Upsert(ClientProfile profile) => Mutate(byId => byId[profile.Id] = profile);

    public void Delete(string id) => Mutate(byId => byId.Remove(id));

    private void Mutate(Action<Dictionary<string, ClientProfile>> change)
    {
        bool owned = false;
        try
        {
            try { owned = _mutex?.WaitOne(TimeSpan.FromSeconds(2)) ?? false; }
            catch (AbandonedMutexException) { owned = true; }
            catch { }

            var byId = Load().ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
            change(byId);
            var json = JsonSerializer.Serialize(new ClientProfileList(ClientProfileList.CurrentSchemaVersion,
                byId.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList()), Options);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, _path, overwrite: true); return; }
                catch (IOException) when (attempt < 2) { Thread.Sleep(50 * (attempt + 1)); }
            }
        }
        finally
        {
            if (owned) { try { _mutex!.ReleaseMutex(); } catch { } }
        }
    }
}
