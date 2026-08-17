namespace Marco.Core.Update;

/// <summary>Where updates come from. The only networked seam in the update pipeline — everything downstream of it
/// (verify, stage, swap) is testable without a server.</summary>
public interface IUpdateSource
{
    /// <summary>The newest installable release for the channel, or null when none exists / none is usable.
    /// The beta channel considers pre-releases (and therefore also sees newer stables).</summary>
    Task<ReleaseInfo?> GetLatestReleaseAsync(bool includeBeta, CancellationToken ct);

    /// <summary>The release's published SHA-256 (lowercase hex), or null when unreadable.</summary>
    Task<string?> GetChecksumAsync(ReleaseInfo release, CancellationToken ct);

    /// <summary>Streams the release exe to <paramref name="destinationPath"/> (never buffered in memory), reporting
    /// cumulative bytes received to <paramref name="bytesReceived"/> when supplied.</summary>
    Task DownloadExeAsync(ReleaseInfo release, string destinationPath, IProgress<long>? bytesReceived, CancellationToken ct);
}
