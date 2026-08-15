namespace Marco.Core.Update;

/// <summary>A published GitHub release the updater can install: parsed version, both asset URLs, and the release
/// notes body (surfaced as "What's new" after the update applies).</summary>
public sealed record ReleaseInfo(
    MarcoVersion Version,
    string TagName,
    bool IsPrerelease,
    string ExeDownloadUrl,
    string Sha256DownloadUrl,
    long ExeSizeBytes,
    string HtmlUrl,
    string? Body);
