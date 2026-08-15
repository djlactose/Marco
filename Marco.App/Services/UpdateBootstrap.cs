using Marco.Core;
using Marco.Core.Diagnostics;
using Marco.Core.Storage;
using Marco.Core.Update;

namespace Marco.App.Services;

/// <summary>
/// Wires the auto-updater. Two environment variables control it: MARCO_NO_UPDATE (any non-empty value disables
/// updates entirely — the GPO/SCCM kill switch) and MARCO_UPDATE_API (overrides the GitHub API base URL; the
/// end-to-end test seam). The channel default follows the build: beta builds track betas, stable builds track
/// stable, until the operator explicitly toggles the setting.
/// </summary>
public static class UpdateBootstrap
{
    public static UpdateService? Create(AppPaths paths, RunLog runLog, AppSettings settings)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MARCO_NO_UPDATE")))
            return null;

        var apiOverride = Environment.GetEnvironmentVariable("MARCO_UPDATE_API");
        var source = new GitHubUpdateSource(
            string.IsNullOrWhiteSpace(apiOverride) ? null : apiOverride,
            userAgentVersion: AppVersion.Display);

        var includeBeta = settings.IncludeBetaUpdates ?? AppVersion.IsBeta;
        return new UpdateService(paths, AppVersion.Current, source, new DefaultStorageProbe(),
            runLog.UpdateEvent, includeBeta);
    }
}
