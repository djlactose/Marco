using System.Text;
using System.Text.Json;

namespace Marco.Core.Storage;

/// <summary>Loads/saves <see cref="AppSettings"/> as JSON. A missing or corrupt file yields defaults, and a failed
/// save is swallowed — settings persistence must never break the app (same philosophy as RunLog).</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(string path, AppSettings settings)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options), new UTF8Encoding(false));
        }
        catch { }
    }
}
