namespace Marco.Core.Update;

/// <summary>Persisted (updates\staged.json) description of a downloaded-and-verified update awaiting apply.
/// <paramref name="FileName"/> is relative to the updates directory; <paramref name="Sha256"/> is bare lowercase hex.</summary>
public sealed record StagedManifest(
    string Version,
    string FileName,
    string Sha256,
    DateTime DownloadedUtc,
    string? Notes = null,
    string? HtmlUrl = null);

public enum StagedDecision
{
    /// <summary>No staged update (or an unreadable manifest).</summary>
    None,
    /// <summary>Staged, verified, and newer than the running version — swap it in.</summary>
    Apply,
    /// <summary>Staged version is ≤ current, or is a beta while the channel is stable — discard it.</summary>
    StaleOlderOrEqual,
    /// <summary>Manifest points at a missing file or the hash no longer matches — discard and re-stage.</summary>
    CorruptOrMissing,
}

/// <summary>Pure decision logic for whether a staged update should be applied at startup. File access is injected
/// (mirroring the IStorageProbe seam) so tests never touch disk.</summary>
public static class StagedUpdateEvaluator
{
    /// <param name="fileExists">Existence check, keyed by the manifest's FileName.</param>
    /// <param name="hashProvider">Returns the file's actual lowercase SHA-256 hex, keyed by FileName.</param>
    public static StagedDecision Evaluate(
        MarcoVersion current,
        bool includeBeta,
        StagedManifest? manifest,
        Func<string, bool> fileExists,
        Func<string, string> hashProvider)
    {
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.FileName)) return StagedDecision.None;
        if (!MarcoVersion.TryParse(manifest.Version, out var staged)) return StagedDecision.None;

        if (staged.IsBeta && !includeBeta) return StagedDecision.StaleOlderOrEqual; // channel switched to stable
        if (staged <= current) return StagedDecision.StaleOlderOrEqual;

        if (!fileExists(manifest.FileName)) return StagedDecision.CorruptOrMissing;
        try
        {
            var actual = hashProvider(manifest.FileName);
            if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                return StagedDecision.CorruptOrMissing;
        }
        catch
        {
            return StagedDecision.CorruptOrMissing;
        }

        return StagedDecision.Apply;
    }
}
