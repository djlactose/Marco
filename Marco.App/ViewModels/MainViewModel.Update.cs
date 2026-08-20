using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marco.Core;
using Marco.Core.Update;

namespace Marco.App.ViewModels;

public partial class MainViewModel
{
    private UpdateService? _updater;
    private bool? _includeBetaSetting;      // null until the operator explicitly toggles the checkbox
    private DispatcherTimer? _updateTimer;
    private DispatcherTimer? _deferredRecheckTimer;
    private UpdateCheckResult? _lastUpdateResult;
    private UpdateCheckResult? _pendingUpdatePrompt; // found while a run was active; asked when it finishes
    private string? _promptedVersion;                // background checks ask once per version per session
    private string? _whatsNewUrl;
    private int _updateCheckInFlight;

    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private bool _includeBetaUpdates;
    [ObservableProperty] private string _updateCheckStatus = "";
    [ObservableProperty] private bool _whatsNewVisible;
    [ObservableProperty] private string _whatsNewText = "";
    /// <summary>The release notes shown inline when the banner is expanded ("" = none shipped with the
    /// release, so the What's-new link falls back to opening the release page).</summary>
    [ObservableProperty] private string _whatsNewNotes = "";
    [ObservableProperty] private bool _whatsNewExpanded;

    public string VersionDisplay => $"Marco v{AppVersion.Display}" + (AppVersion.IsBeta ? " (beta)" : "");

    /// <summary>Kicks off the silent background check (5 s after startup, then every 12 h for long-running
    /// sessions) and surfaces a pending "What's new" from an update that just applied.</summary>
    public void StartBackgroundUpdateCheck()
    {
        if (_updater?.ReadWhatsNew() is { } whatsNew)
        {
            _whatsNewUrl = whatsNew.HtmlUrl;
            WhatsNewText = $"Marco updated to v{whatsNew.Version}";
            WhatsNewNotes = ReleaseNotesText.Clean(whatsNew.Notes);
            WhatsNewVisible = true;
        }

        _ = RunUpdateCheckAsync(TimeSpan.FromSeconds(5));

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(12) };
        _updateTimer.Tick += (_, _) => _ = RunUpdateCheckAsync(null);
        _updateTimer.Start();
    }

    private async Task RunUpdateCheckAsync(TimeSpan? initialDelay, bool manual = false)
    {
        if (_updater is null)
        {
            if (manual) UpdateCheckStatus = "Updates are disabled (MARCO_NO_UPDATE).";
            return;
        }
        if (Interlocked.Exchange(ref _updateCheckInFlight, 1) == 1) return; // a check/download is already running

        try
        {
            if (initialDelay is { } delay) await Task.Delay(delay);
            if (manual) UpdateCheckStatus = "Checking…";

            // Check only — nothing is downloaded until the operator says yes in the prompt below.
            var result = await Task.Run(() => _updater.CheckAsync());
            _lastUpdateResult = result;

            // Continuations resume on the UI thread (dispatcher context), so property sets are safe here.
            switch (result.State)
            {
                case UpdateState.Available:
                    UpdateText = $"Update {result.Release!.TagName} available — download and install";
                    UpdateAvailable = true;
                    if (manual) UpdateCheckStatus = "";
                    break;
                case UpdateState.Staged:
                    UpdateText = $"Update {result.Release!.TagName} downloaded — restart to install";
                    UpdateAvailable = true;
                    if (manual) UpdateCheckStatus = "";
                    break;
                case UpdateState.NotifyOnly:
                    UpdateText = $"Update {result.Release!.TagName} available — view release";
                    UpdateAvailable = true;
                    if (manual) UpdateCheckStatus = "";
                    break;
                case UpdateState.UpToDate:
                    if (manual) UpdateCheckStatus = "Up to date.";
                    break;
                default:
                    if (manual) UpdateCheckStatus = "Check failed (see the run log).";
                    break;
            }
        }
        catch (Exception ex)
        {
            // Fire-and-forget caller: never let an update-check problem surface as an unobserved task exception.
            _runLog.Note($"Update check failed: {ex.GetType().Name}: {ex.Message}");
            if (manual) UpdateCheckStatus = "Check failed (see the run log).";
            return;
        }
        finally
        {
            Interlocked.Exchange(ref _updateCheckInFlight, 0);
        }

        try
        {
            if (_lastUpdateResult?.State is UpdateState.Available or UpdateState.Staged or UpdateState.NotifyOnly)
                OfferUpdate(_lastUpdateResult, manual);
        }
        catch (Exception ex)
        {
            _runLog.Note($"Update prompt failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// A found update is offered in a dialog, not just the header link (which is easy to miss). Background checks
    /// ask once per version per session; a manual "Check now" always asks. If a scan or inventory is running the
    /// prompt waits until it finishes rather than interrupting the operator mid-run.
    /// </summary>
    private void OfferUpdate(UpdateCheckResult result, bool manual)
    {
        var version = result.Release?.TagName ?? "";
        if (!manual && string.Equals(_promptedVersion, version, StringComparison.OrdinalIgnoreCase)) return;
        _promptedVersion = version;

        if (IsScanning) { _pendingUpdatePrompt = result; return; }
        _pendingUpdatePrompt = null;
        ShowUpdatePrompt(result);
    }

    /// <summary>Called when a run ends: show the prompt that was held back while scanning.</summary>
    private void FlushPendingUpdatePrompt()
    {
        if (IsScanning || _pendingUpdatePrompt is null) return;
        var pending = _pendingUpdatePrompt;
        _pendingUpdatePrompt = null;
        // Let the run's own final status land first, then ask.
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => ShowUpdatePrompt(pending)), DispatcherPriority.Background);
    }

    private void ShowUpdatePrompt(UpdateCheckResult result)
    {
        if (_updater is null || result.Release is null) return;
        var owner = Application.Current?.MainWindow;
        var tag = result.Release.TagName;
        var have = $"v{AppVersion.Display}";

        switch (result.State)
        {
            case UpdateState.Available:
            {
                var mb = result.Release.ExeSizeBytes / 1024d / 1024d;
                var sizeMb = mb >= 1 ? $" ({mb:0} MB)" : "";
                var answer = ShowYesNo(owner,
                    $"A new version of Marco is available: {tag} (you have {have}).\n\n" +
                    $"Download and install it now{sizeMb}? Marco will restart to apply the update.",
                    "Update available");
                if (answer == MessageBoxResult.Yes) _ = DownloadAndInstallAsync(result.Release);
                break;
            }
            case UpdateState.Staged:
            {
                var answer = ShowYesNo(owner,
                    $"Marco {tag} has been downloaded and verified (you have {have}).\n\nRestart Marco now to install it?",
                    "Update ready");
                if (answer == MessageBoxResult.Yes) ApplyStagedAndRestart();
                break;
            }
            case UpdateState.NotifyOnly:
            {
                var answer = ShowYesNo(owner,
                    $"A new version of Marco is available: {tag} (you have {have}).\n\n" +
                    "Marco can't update itself from this location because its folder isn't writable. Open the release page to download it?",
                    "Update available");
                if (answer == MessageBoxResult.Yes) OpenUrl(result.Release.HtmlUrl);
                break;
            }
        }
    }

    private static MessageBoxResult ShowYesNo(Window? owner, string text, string caption)
        => owner is not null
            ? MessageBox.Show(owner, text, caption, MessageBoxButton.YesNo, MessageBoxImage.Information)
            : MessageBox.Show(text, caption, MessageBoxButton.YesNo, MessageBoxImage.Information);

    /// <summary>The operator said yes: download with progress in the header, verify, swap, restart.</summary>
    private async Task DownloadAndInstallAsync(ReleaseInfo release)
    {
        if (_updater is null) return;
        if (Interlocked.Exchange(ref _updateCheckInFlight, 1) == 1) return; // a check/download is already running

        UpdateCheckResult staged;
        try
        {
            UpdateAvailable = true;
            UpdateText = $"Downloading update {release.TagName}… 0%";
            var progress = new Progress<double>(f => UpdateText = $"Downloading update {release.TagName}… {f * 100:0}%");
            staged = await Task.Run(() => _updater.StageAsync(release, progress));
            if (staged.State == UpdateState.Failed)
            {
                // A sync-client/AV lock can outlast the staging retries; one quiet retry after a pause almost
                // always succeeds, and it reuses the already-verified partial so it costs no bandwidth.
                UpdateText = $"Update {release.TagName} — retrying…";
                await Task.Delay(TimeSpan.FromSeconds(3));
                staged = await Task.Run(() => _updater.StageAsync(release, progress));
            }
            _lastUpdateResult = staged;
        }
        finally
        {
            Interlocked.Exchange(ref _updateCheckInFlight, 0);
        }

        switch (staged.State)
        {
            case UpdateState.Staged:
                UpdateText = $"Update {release.TagName} downloaded — restart to install";
                ApplyStagedAndRestart();
                break;
            case UpdateState.Deferred:
                UpdateText = $"Update {release.TagName} — another Marco window is downloading it";
                ScheduleDeferredRecheck();
                break;
            default:
                // Download/verify/stage failed twice (details in the run log). Keep the header link as a one-click
                // retry rather than downgrading to NotifyOnly, which would strand the operator on the release page.
                UpdateText = $"Update {release.TagName} download failed — click to retry";
                _lastUpdateResult = new UpdateCheckResult(UpdateState.Available, release, null);
                MessageBox.Show(
                    $"The update could not be downloaded or installed. See the run log for details.\n\nYou can retry from the update link in the header, or download {release.TagName} manually from the release page.",
                    "Update failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    /// <summary>One-shot re-check a couple of minutes after a Deferred result. A timer rather than a delay inside
    /// RunUpdateCheckAsync, which would hold the in-flight flag and block a manual "Check now".</summary>
    private void ScheduleDeferredRecheck()
    {
        if (_deferredRecheckTimer is not null) return;
        _deferredRecheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _deferredRecheckTimer.Tick += (_, _) =>
        {
            _deferredRecheckTimer?.Stop();
            _deferredRecheckTimer = null;
            _ = RunUpdateCheckAsync(null);
        };
        _deferredRecheckTimer.Start();
    }

    [RelayCommand]
    private Task CheckForUpdatesNowAsync() => RunUpdateCheckAsync(null, manual: true);

    /// <summary>The header link: does whatever the update's current state calls for — start the download, restart
    /// into a downloaded update, or open the release page.</summary>
    [RelayCommand]
    private void UpdateAction()
    {
        if (_updater is null || _lastUpdateResult is null) return;
        switch (_lastUpdateResult.State)
        {
            case UpdateState.Available when _lastUpdateResult.Release is { } release:
                _ = DownloadAndInstallAsync(release);
                break;
            case UpdateState.Staged:
                ApplyStagedAndRestart();
                break;
            case UpdateState.NotifyOnly:
                OpenUrl(_lastUpdateResult.Release?.HtmlUrl);
                break;
            case UpdateState.Deferred:
                _ = RunUpdateCheckAsync(null, manual: true);
                break;
        }
    }

    private void ApplyStagedAndRestart()
    {
        if (_updater is null) return;

        SaveSettings();
        if (_updater.TryApplyAndRestartNow())
        {
            Application.Current.Shutdown();
        }
        else if (_updater.ExeOnDiskIsNewer())
        {
            // Another Marco window already swapped the exe; this process keeps running the old image (from
            // Marco.exe.old) until it is closed. Nothing left to apply here — just say so.
            UpdateText = "Update applied by another Marco window — close and reopen Marco to use it";
            UpdateAvailable = true;
            _lastUpdateResult = null; // a further click has nothing to do
        }
        else
        {
            // Swap failed (details in the run log) — degrade to pointing at the release page.
            UpdateText = _lastUpdateResult?.Release is { } release
                ? $"Update {release.TagName} available — view release"
                : "Update available — view release";
            if (_lastUpdateResult is not null)
                _lastUpdateResult = _lastUpdateResult with { State = UpdateState.NotifyOnly };
        }
    }

    partial void OnIncludeBetaUpdatesChanged(bool value)
    {
        _includeBetaSetting = value;
        if (_updater is not null) _updater.IncludeBeta = value;
        SaveSettings();
        _ = RunUpdateCheckAsync(null, manual: true);
    }

    /// <summary>The banner's What's-new link: show the shipped release notes right here; only fall back to
    /// the browser when the release carried none.</summary>
    [RelayCommand]
    private void OpenWhatsNew()
    {
        if (WhatsNewNotes.Length > 0) WhatsNewExpanded = !WhatsNewExpanded;
        else OpenUrl(_whatsNewUrl ?? "https://github.com/djlactose/Marco/releases");
    }

    [RelayCommand]
    private void OpenWhatsNewRelease() => OpenUrl(_whatsNewUrl ?? "https://github.com/djlactose/Marco/releases");

    [RelayCommand]
    private void DismissWhatsNew()
    {
        _updater?.DismissWhatsNew();
        WhatsNewVisible = false;
        WhatsNewExpanded = false;
    }

    [RelayCommand]
    private void OpenBuyMeACoffee() => OpenUrl("https://buymeacoffee.com/djlactose");

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
