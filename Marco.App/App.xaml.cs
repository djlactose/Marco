using System.Windows;
using System.Windows.Threading;
using Marco.App.Services;
using Marco.App.ViewModels;
using Marco.App.Views;
using Marco.Core.Diagnostics;
using Marco.Core.Storage;
using Marco.Core.Update;

namespace Marco.App;

public partial class App : Application
{
    private RunLog? _runLog;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.Resolve();
        _runLog = new RunLog(paths.RunLogFile);
        WireCrashLogging();

        var settings = SettingsStore.Load(paths.SettingsFile);
        var updater = UpdateBootstrap.Create(paths, _runLog, settings);

        // Any number of Marco windows may run at once (one per network is a normal way to work). Only the steps
        // that touch the exe or the crash-loop sentinel are serialised, through a short-lived cross-process gate
        // held from here until the window is up. The 5 s wait bridges the update-relaunch handoff, where the old
        // process may still be exiting; if another instance holds the gate longer than that, this one simply
        // skips the startup update steps and opens normally — it never refuses to start.
        using var gate = updater is null ? null : UpdateGate.TryAcquire(paths.Root, TimeSpan.FromSeconds(5));
        bool runStartupUpdateSteps = updater is not null && gate is not null;
        if (updater is not null && gate is null)
            _runLog.UpdateEvent("gate_busy", "another Marco instance is starting or applying an update; startup update steps skipped");

        if (runStartupUpdateSteps)
        {
            // Order matters: detect a crash-looping update (and roll back) before applying anything new.
            if (updater!.CheckForFailedUpdate()) { Shutdown(); return; }
            if (updater.TryApplyStagedAndRelaunch()) { Shutdown(); return; }
        }

        _viewModel = new MainViewModel(paths, _runLog, settings, updater);
        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        window.Show();

        if (runStartupUpdateSteps)
        {
            // Still under the gate: a second new-version instance launched right now must not see the bumped
            // sentinel and mistake a healthy start for a crash loop.
            updater!.MarkStartupSucceeded();
            _ = Task.Run(updater.CleanupArtifacts);
        }
        if (updater is not null)
            _viewModel.StartBackgroundUpdateCheck();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.SaveSettings();
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
