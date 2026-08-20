using System.Text;
using System.Text.Json;

namespace Marco.Core.Clients;

/// <summary>The shareable single-profile file (.marcoclient.json): the profile plus its logo inlined as base64
/// so branding travels as one file. There is no credential field in this schema BY DESIGN — and DPAPI-sealed
/// passwords could not travel anyway. The recipient assigns their own credentials to the imported client.</summary>
public sealed record SharedClientFile(int SchemaVersion, ClientProfile Profile, string? LogoBase64);

public static class ClientProfileSharing
{
    public const string Extension = ".marcoclient.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Write the shareable file. The profile's LogoPath is a LOCAL path; the export embeds the bytes
    /// and blanks the path, so nothing machine-specific leaks.</summary>
    public static void Export(ClientProfile profile, string path)
    {
        string? logo = null;
        if (profile.LogoPath is { } lp && File.Exists(lp))
        {
            try { logo = Convert.ToBase64String(File.ReadAllBytes(lp)); }
            catch { /* unreadable logo — export without it */ }
        }
        var doc = new SharedClientFile(ClientProfileList.CurrentSchemaVersion, profile with { LogoPath = null }, logo);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, Options), new UTF8Encoding(false));
    }

    /// <summary>Read a shared file. <paramref name="logosDirectory"/> receives the embedded logo (as
    /// {clientId}.png) and the returned profile's LogoPath points at it.</summary>
    public static ClientProfile Import(string path, string logosDirectory)
    {
        var doc = JsonSerializer.Deserialize<SharedClientFile>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("The client file is empty or malformed.");
        var profile = doc.Profile;
        if (doc.LogoBase64 is { } logo)
        {
            Directory.CreateDirectory(logosDirectory);
            var logoPath = Path.Combine(logosDirectory, profile.Id + ".png");
            File.WriteAllBytes(logoPath, Convert.FromBase64String(logo));
            profile = profile with { LogoPath = logoPath };
        }
        return profile;
    }
}
