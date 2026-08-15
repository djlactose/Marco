using System.Text.Json;

namespace Marco.Core.Update;

/// <summary>
/// Parses GitHub release API payloads into <see cref="ReleaseInfo"/>. Pure string-in/record-out so it is testable
/// with fixtures and never throws on malformed input — a bad payload is just "no release found".
/// A release is only usable when it is not a draft, its tag parses as a <see cref="MarcoVersion"/>, and it carries
/// both the <c>Marco.exe</c> and <c>Marco.exe.sha256</c> assets.
/// </summary>
public static class GitHubReleaseParser
{
    /// <summary>Parses a single release object (the <c>/releases/latest</c> endpoint).</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses a release array (the <c>/releases</c> endpoint) and returns the highest usable version —
    /// including pre-releases, which is exactly what the beta channel wants.</summary>
    public static ReleaseInfo? ParseLatestFromList(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            ReleaseInfo? best = null;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var release = FromElement(element);
                if (release is null) continue;
                if (best is null || release.Version > best.Version) best = release;
            }
            return best;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ReleaseInfo? FromElement(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;

        if (!el.TryGetProperty("tag_name", out var tagEl) || tagEl.GetString() is not { } tag) return null;
        if (!MarcoVersion.TryParse(tag, out var version)) return null;

        var prerelease = el.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
        var htmlUrl = el.TryGetProperty("html_url", out var h) ? h.GetString() : null;
        var body = el.TryGetProperty("body", out var b) ? b.GetString() : null;

        string? exeUrl = null, shaUrl = null;
        long exeSize = 0;
        if (el.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name == "Marco.exe")
                {
                    exeUrl = url;
                    exeSize = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0;
                }
                else if (name == "Marco.exe.sha256")
                {
                    shaUrl = url;
                }
            }
        }

        if (exeUrl is null || shaUrl is null) return null;
        return new ReleaseInfo(version, tag, prerelease, exeUrl, shaUrl, exeSize, htmlUrl ?? "", body);
    }
}
