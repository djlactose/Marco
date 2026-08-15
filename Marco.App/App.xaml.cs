using System.Windows;
using System.Windows.Threading;
using Marco.App.Services;
using Marco.App.ViewModels;
using Marco.App.Views;
using Marco.Core.Diagnostics;
using Marco.Core.Storage;

namespace Marco.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private RunLog? _runLog;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance. The 5s wait bridges the update-relaunch handoff, where the old process may still be
        // exiting while it hands the mutex over to the freshly swapped exe.
        _instanceMutex = new Mutex(initiallyOwned: false, "Marco.App.SingleInstance");
        bool owned;
        try { owned = _instanceMutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { owned = true; } // previous holder died without releasing — we own it now
        if (!owned)
        {
            MessageBox.Show("Marco is already running.", "Marco", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var paths = AppPaths.Resolve();
        _runLog = new RunLog(paths.RunLogFile);
        WireCrashLogging();

        var settings = SettingsStore.Load(paths.SettingsFile);
        var updater = UpdateBootstrap.Create(paths, _runLog, settings);

        if (updater is not null)
        {
            // Order matters: detect a crash-looping update (and roll back) before applying anything new.
            if (updater.CheckForFailedUpdate()) { Shutdown(); return; }
            if (updater.TryApplyStagedAndRelaunch()) { Shutdown(); return; }
        }

        _viewModel = new MainViewModel(paths, _runLog, settings, updater);
        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        window.Show();

        if (updater is not null)
        {
            updater.MarkStartupSucceeded();
            _ = Task.Run(updater.CleanupArtifacts);
            _viewModel.StartBackgroundUpdateCheck();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.SaveSettings();
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void WireCrashLogging()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _runLog?.Crash("appdomain", (args.ExceptionObject as Exception)?.ToString()
                ?? args.ExceptionObject?.ToString() ?? "unknown");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _runLog?.Crash("task", args.Exception.ToString());
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _runLog?.Crash("dispatcher", e.Exception.ToString());
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "Marco", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
