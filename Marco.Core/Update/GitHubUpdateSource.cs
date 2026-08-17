using System.Net.Http;

namespace Marco.Core.Update;

/// <summary>
/// GitHub Releases as the update channel. Stable checks <c>/releases/latest</c> (GitHub excludes pre-releases
/// there); the beta channel lists <c>/releases</c> and takes the highest version. The base URL is overridable
/// (MARCO_UPDATE_API via the bootstrap, or directly here) so end-to-end tests can run against a local server, and
/// the handler is injectable for unit tests. Uses the default handler otherwise, so system proxy settings are
/// respected and redirects (browser_download_url 302s to a CDN) are followed automatically.
/// </summary>
public sealed class GitHubUpdateSource : IUpdateSource, IDisposable
{
    public const string DefaultApiBase = "https://api.github.com/repos/djlactose/Marco";

    private readonly HttpClient _http;
    private readonly string _apiBase;

    public GitHubUpdateSource(string? apiBase = null, HttpMessageHandler? handler = null, string? userAgentVersion = null)
    {
        _apiBase = (apiBase ?? DefaultApiBase).TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        // Per-call CancellationTokenSources enforce the real limits; this is just the outer backstop.
        _http.Timeout = TimeSpan.FromMinutes(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Marco/{userAgentVersion ?? "0.0"}"); // GitHub 403s without one
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<ReleaseInfo?> GetLatestReleaseAsync(bool includeBeta, CancellationToken ct)
    {
        if (includeBeta)
        {
            var json = await _http.GetStringAsync($"{_apiBase}/releases?per_page=20", ct).ConfigureAwait(false);
            return GitHubReleaseParser.ParseLatestFromList(json);
        }
        else
        {
            var json = await _http.GetStringAsync($"{_apiBase}/releases/latest", ct).ConfigureAwait(false);
            return GitHubReleaseParser.ParseRelease(json);
        }
    }

    public async Task<string?> GetChecksumAsync(ReleaseInfo release, CancellationToken ct)
    {
        var text = await _http.GetStringAsync(release.Sha256DownloadUrl, ct).ConfigureAwait(false);
        return Sha256File.ParseChecksumText(text);
    }

    public async Task DownloadExeAsync(ReleaseInfo release, string destinationPath, IProgress<long>? bytesReceived, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(release.ExeDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = File.Create(destinationPath);

        var buffer = new byte[256 * 1024];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
            bytesReceived?.Report(total);
        }
    }

    public void Dispose() => _http.Dispose();
}
