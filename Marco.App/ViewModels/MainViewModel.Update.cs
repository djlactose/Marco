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
    private UpdateCheckResult? _lastUpdateResult;
    private string? _whatsNewUrl;
    private int _updateCheckInFlight;

    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private bool _includeBetaUpdates;
    [ObservableProperty] private string _updateCheckStatus = "";
    [ObservableProperty] private bool _whatsNewVisible;
    [ObservableProperty] private string _whatsNewText = "";

    public string VersionDisplay => $"Marco v{AppVersion.Display}" + (AppVersion.IsBeta ? " (beta)" : "");

    /// <summary>Kicks off the silent background check (5 s after startup, then every 12 h for long-running
    /// sessions) and surfaces a pending "What's new" from an update that just applied.</summary>
    public void StartBackgroundUpdateCheck()
    {
        if (_updater?.ReadWhatsNew() is { } whatsNew)
        {
            _whatsNewUrl = whatsNew.HtmlUrl;
            WhatsNewText = $"Updated to v{whatsNew.Version}.";
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

            var result = await Task.Run(() => _updater.CheckAndStageAsync());
            _lastUpdateResult = result;

            // Continuations resume on the UI thread (dispatcher context), so property sets are safe here.
            switch (result.State)
            {
                case UpdateState.Staged:
                    UpdateText = $"Update {result.Release!.TagName} ready — restart to apply";
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
        finally
        {
            Interlocked.Exchange(ref _updateCheckInFlight, 0);
        }
    }

    [RelayCommand]
    private Task CheckForUpdatesNowAsync() => RunUpdateCheckAsync(null, manual: true);

    [RelayCommand]
    private void RestartToUpdate()
    {
        if (_updater is null) return;

        if (_lastUpdateResult?.State == UpdateState.NotifyOnly)
        {
            OpenUrl(_lastUpdateResult.Release?.HtmlUrl);
            return;
        }

        SaveSettings();
        if (_updater.TryApplyAndRestartNow())
        {
            Application.Current.Shutdown();
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

    [RelayCommand]
    private void OpenWhatsNew() => OpenUrl(_whatsNewUrl ?? "https://github.com/djlactose/Marco/releases");

    [RelayCommand]
    private void DismissWhatsNew()
    {
        _updater?.DismissWhatsNew();
        WhatsNewVisible = false;
    }

    [RelayCommand]
    private void OpenBuyMeACoffee() => OpenUrl("https://buymeacoffee.com/djlactose");

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
