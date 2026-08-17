using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marco.App.Services;
using Marco.Core.Diagnostics;
using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Scanning;
using Marco.Core.Storage;
using Marco.Core.Targets;
using Marco.Credentials;

namespace Marco.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ScanController _controller = DiscoveryFactory.CreateController();
    private readonly AppPaths _paths;
    private readonly CredentialStore _credentials = new();
    private readonly InventoryRunner _inventory = InventoryFactory.CreateRunner();
    private readonly Marco.Inventory.Linux.LinuxInventoryRunner _linuxInventory = InventoryFactory.CreateLinuxRunner();
    private readonly RunLog _runLog;

    private readonly ConcurrentQueue<Machine> _pending = new();
    private readonly DispatcherTimer _flushTimer;
    private CancellationTokenSource? _cts;
    private PauseController? _pause;

    /// <summary>Credential sets shown in the left panel (label + backing set).</summary>
    public ObservableCollection<CredentialDisplay> Credentials { get; } = new();

    /// <summary>The machine's own NIC subnets, suggested as one-click scan targets.</summary>
    public ObservableCollection<Marco.Discovery.LocalSubnet> SuggestedNetworks { get; } = new();

    private IReadOnlyList<string> _lastRanges = Array.Empty<string>();

    /// <summary>The target tokens of the scan currently in the grid (started here or loaded from a file). Drives
    /// the window title so several Marco windows can be told apart in the taskbar.</summary>
    private IReadOnlyList<string> LastRanges
    {
        get => _lastRanges;
        set { _lastRanges = value; OnPropertyChanged(nameof(Title)); }
    }

    public ObservableCollection<Machine> Machines { get; } = new();
    public ICollectionView MachinesView { get; }

    // --- Target definition ---
    [ObservableProperty] private string _targetsText = "";

    // --- Scan options ---
    [ObservableProperty] private int _concurrency = 32;
    [ObservableProperty] private bool _icmpEnabled = true;
    [ObservableProperty] private bool _tcpFallback = true;
    [ObservableProperty] private bool _classification = true;
    [ObservableProperty] private bool _resolveNames = true;
    [ObservableProperty] private bool _resolveMac = true;
    [ObservableProperty] private bool _includeUnreachable;

    /// <summary>When set, automatically inventory the alive hosts as soon as discovery finishes.</summary>
    [ObservableProperty] private bool _autoInventory;

    // --- Filter / selection ---
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private Machine? _selectedMachine;

    // --- Run state ---
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isPaused;
    /// <summary>Cancel was clicked and in-flight work is draining; the run is still "scanning" until it settles.</summary>
    [ObservableProperty] private bool _isCancelling;
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private string _statusLine = "Ready.";
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private int _aliveCount;
    [ObservableProperty] private int _unreachableCount;
    [ObservableProperty] private int _totalCount;

    /// <summary>Latest progress snapshot of the current run — the status line is recomposed from it whenever the
    /// pause/cancel state changes, so a report arriving after Pause can't overwrite "Paused".</summary>
    private ScanProgress? _lastProgress;

    public string StorageLocation => _paths.Reason;

    public string Title
    {
        get
        {
            var version = $"v{Marco.Core.AppVersion.Display}";
            if (LastRanges.Count == 0) return $"Marco — Network Inventory — {version}";
            var extra = LastRanges.Count > 1 ? $" (+{LastRanges.Count - 1} more)" : "";
            return $"Marco — {LastRanges[0]}{extra} — {version}";
        }
    }

    public MainViewModel() : this(null) { }

    public MainViewModel(AppPaths? paths, RunLog? runLog = null, AppSettings? settings = null,
        Marco.Core.Update.UpdateService? updater = null)
    {
        _paths = paths ?? AppPaths.Resolve();
        _runLog = runLog ?? new RunLog(_paths.RunLogFile);
        _updater = updater;

        ApplySettings(settings ?? SettingsStore.Load(_paths.SettingsFile));
        LoadCredentials();

        foreach (var net in Marco.Discovery.LocalNetworks.Enumerate())
            SuggestedNetworks.Add(net);

        MachinesView = CollectionViewSource.GetDefaultView(Machines);
        MachinesView.Filter = FilterMachine;

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _flushTimer.Tick += (_, _) => FlushPending();
    }

    /// <summary>Seed the observable backing fields directly: during construction nothing observes yet, and the
    /// generated setters must NOT run — OnIncludeBetaUpdatesChanged would persist an explicit channel choice the
    /// operator never made.</summary>
#pragma warning disable MVVMTK0034
    private void ApplySettings(AppSettings s)
    {
        _targetsText = s.TargetsText;
        _concurrency = s.Concurrency;
        _icmpEnabled = s.IcmpEnabled;
        _tcpFallback = s.TcpFallback;
        _classification = s.Classification;
        _resolveNames = s.ResolveNames;
        _resolveMac = s.ResolveMac;
        _includeUnreachable = s.IncludeUnreachable;
        _autoInventory = s.AutoInventory;
        _includeBetaSetting = s.IncludeBetaUpdates;
        _includeBetaUpdates = s.IncludeBetaUpdates ?? Marco.Core.AppVersion.IsBeta;
    }
#pragma warning restore MVVMTK0034

    /// <summary>Persist current options; called on exit and immediately when the beta toggle changes.</summary>
    public void SaveSettings() => SettingsStore.Save(_paths.SettingsFile, new AppSettings
    {
        IncludeBetaUpdates = _includeBetaSetting,
        TargetsText = TargetsText,
        Concurrency = Concurrency,
        IcmpEnabled = IcmpEnabled,
        TcpFallback = TcpFallback,
        Classification = Classification,
        ResolveNames = ResolveNames,
        ResolveMac = ResolveMac,
        IncludeUnreachable = IncludeUnreachable,
        AutoInventory = AutoInventory,
    });

    partial void OnFilterTextChanged(string value) => MachinesView.Refresh();

    private bool FilterMachine(object obj)
    {
        if (obj is not Machine m) return false;
        var f = FilterText?.Trim();
        if (string.IsNullOrEmpty(f)) return true;

        return Contains(m.Address, f)
            || Contains(m.Name, f)
            || Contains(m.Fqdn, f)
            || Contains(m.Vendor, f)
            || Contains(m.DeviceType.ToString(), f)
            || Contains(m.Status.ToString(), f)
            || Contains(m.System.Manufacturer, f)
            || Contains(m.Os.Caption, f)
            || m.MacAddresses.Any(mac => Contains(mac, f));

        static bool Contains(string? s, string term)
            => s is not null && s.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private void FlushPending()
    {
        // Runs on the UI thread (DispatcherTimer). Drain the worker-produced queue in one batch so a wide sweep
        // doesn't post one dispatcher operation per host.
        bool added = false;
        while (_pending.TryDequeue(out var m))
        {
            Machines.Add(m);
            added = true;
        }
        if (added && SelectedMachine is null && Machines.Count > 0)
            SelectedMachine = Machines[0];
        if (added)
            InventoryAliveCommand.NotifyCanExecuteChanged(); // CanInventory depends on Machines.Count
    }

    private bool CanStart() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartScanAsync()
    {
        List<ScanTarget> targets;
        List<string> ranges;
        long estimate;
        try
        {
            var options = new TargetExpansionOptions();
            estimate = TargetParser.EstimateCount(new[] { TargetsText }, options);
            if (estimate == 0)
            {
                StatusLine = "No valid targets entered.";
                return;
            }
            if (estimate > options.LargeExpansionThreshold)
            {
                var answer = MessageBox.Show(
                    $"This will scan {estimate:N0} addresses, which is above the {options.LargeExpansionThreshold:N0} guard.\n\nProceed anyway?",
                    "Large scan", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) { StatusLine = "Scan not started."; return; }
                options = new TargetExpansionOptions { AllowLargeExpansion = true };
            }
            targets = TargetParser.Parse(new[] { TargetsText }, options).ToList();
            ranges = TargetParser.Tokenize(new[] { TargetsText }).ToList();
        }
        catch (TargetParseException ex)
        {
            MessageBox.Show($"Could not parse target '{ex.Token}':\n{ex.Message}",
                "Invalid target", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LastRanges = ranges;
        Machines.Clear();
        SelectedMachine = null;
        AliveCount = UnreachableCount = 0;
        TotalCount = targets.Count;
        // Seed the snapshot so a Pause/Cancel before the first report still composes discovery wording.
        _lastProgress = new ScanProgress(ScanPhase.Discovery, 0, targets.Count, 0, 0, TimeSpan.Zero, null);

        _cts = new CancellationTokenSource();
        _pause = new PauseController();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        bool discoveryCompleted = false;
        try
        {
            // Everything from IsScanning = true onward is inside the try, so no exception can leave the scan
            // commands latched in the "running" state.
            IsScanning = true;
            IsPaused = false;
            IsCancelling = false;
            ProgressIndeterminate = false;

            var settings = BuildSettings();
            var progress = new Progress<ScanProgress>(OnProgress);
            _flushTimer.Start();
            _runLog.ScanStarted(ranges, targets.Count);

            StatusLine = $"Scanning {targets.Count:N0} addresses…";
            await Task.Run(() => _controller.RunDiscoveryAsync(
                targets, settings, targets.Count, IncludeUnreachable,
                m => _pending.Enqueue(m), progress, _pause, _cts.Token));
            StatusLine = AliveCount > 0
                ? (AutoInventory
                    ? $"Discovery done: {AliveCount:N0} alive. Starting inventory…"
                    : $"Discovery done: {AliveCount:N0} alive, {UnreachableCount:N0} unreachable. Now click ‘Inventory alive’ to pull system/software details.")
                : $"Discovery done: 0 alive, {UnreachableCount:N0} unreachable.";
            _runLog.ScanFinished(AliveCount, UnreachableCount, sw.Elapsed.TotalSeconds);
            discoveryCompleted = true;
        }
        catch (OperationCanceledException)
        {
            int probed = _lastProgress?.Completed ?? 0;
            StatusLine = $"Cancelled. {AliveCount:N0} alive, {UnreachableCount:N0} unreachable ({probed:N0} of {TotalCount:N0} probed).";
            _runLog.ScanCancelled(AliveCount, UnreachableCount, probed, TotalCount, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            StatusLine = $"Scan failed: {ex.Message}";
            _runLog.Note($"Scan failed: {ex.Message}");
        }
        finally
        {
            _flushTimer.Stop();
            FlushPending();
            IsPaused = false;
            IsCancelling = false;
            _pause = null;
            _cts?.Dispose();
            _cts = null;
            IsScanning = false;
        }

        // Chain straight into inventory when requested (after discovery's own cleanup has run).
        if (discoveryCompleted && AutoInventory)
        {
            var alive = Machines.Where(m => m.IsAlive).ToList();
            if (alive.Count > 0)
                await RunInventoryAsync(alive);
        }
    }

    partial void OnSelectedMachineChanged(Machine? value)
        => InventorySelectedCommand.NotifyCanExecuteChanged();

    partial void OnIsCancellingChanged(bool value) => RefreshScanCommands();

    /// <summary>Every command whose CanExecute reads run state. Called from the IsScanning / IsCancelling change
    /// hooks so a state flip can never leave a button stale.</summary>
    private void RefreshScanCommands()
    {
        StartScanCommand.NotifyCanExecuteChanged();
        PauseResumeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ClearResultsCommand.NotifyCanExecuteChanged();
        InventoryAliveCommand.NotifyCanExecuteChanged();
        InventorySelectedCommand.NotifyCanExecuteChanged();
        OpenScanCommand.NotifyCanExecuteChanged();
        AddCredentialCommand.NotifyCanExecuteChanged();
        RemoveCredentialCommand.NotifyCanExecuteChanged();
        EditCredentialCommand.NotifyCanExecuteChanged();
    }

    private bool CanPauseResume() => IsScanning && !IsCancelling && _pause is not null;

    [RelayCommand(CanExecute = nameof(CanPauseResume))]
    private void PauseResume()
    {
        if (_pause is null || !IsScanning || IsCancelling) return;
        if (IsPaused) { _pause.Resume(); IsPaused = false; }
        else { _pause.Pause(); IsPaused = true; }
        RefreshScanStatus();
    }

    private bool CanCancel() => IsScanning && !IsCancelling;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_cts is null || !IsScanning) return;
        IsCancelling = true;      // disables Pause/Cancel and switches the status text to "Cancelling…"
        _cts.Cancel();
        _pause?.Resume();         // release any paused workers so they observe cancellation
        IsPaused = false;
        RefreshScanStatus();
    }

    /// <summary>Recompose the status line from the latest progress plus the current pause/cancel intent — used
    /// right after Pause/Resume/Cancel so the operator sees the change before the next report arrives.</summary>
    private void RefreshScanStatus()
    {
        if (!IsScanning) return;
        if (_lastProgress is { } p)
        {
            var text = ScanStatusText.Compose(p, IsPaused, IsCancelling);
            if (text.Length > 0) { StatusLine = text; return; }
        }
        if (IsCancelling) StatusLine = "Cancelling…";
        else if (IsPaused) StatusLine = "Paused.";
        else StatusLine = "Resumed.";
    }

    [RelayCommand]
    private void AddSubnet(Marco.Discovery.LocalSubnet? subnet)
    {
        if (subnet is null) return;
        AppendTarget(subnet.Cidr);
        StatusLine = $"Added subnet {subnet.Cidr} to targets.";
    }

    [RelayCommand]
    private void AddLocalIp(Marco.Discovery.LocalSubnet? subnet)
    {
        if (subnet is null) return;
        AppendTarget(subnet.IpAddress);
        StatusLine = $"Added {subnet.IpAddress} to targets.";
    }

    /// <summary>Append a target token on its own line if it isn't already present.</summary>
    private void AppendTarget(string token)
    {
        var existing = TargetParser.Tokenize(new[] { TargetsText });
        if (existing.Any(t => string.Equals(t, token, StringComparison.OrdinalIgnoreCase)))
            return;
        TargetsText = string.IsNullOrWhiteSpace(TargetsText)
            ? token
            : TargetsText.TrimEnd() + Environment.NewLine + token;
    }

    [RelayCommand]
    private void BrowseHostFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load host list",
            Filter = "Text and host files (*.txt;*.csv;*.lst)|*.txt;*.csv;*.lst|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var contents = File.ReadAllText(dialog.FileName);
            TargetsText = string.IsNullOrWhiteSpace(TargetsText)
                ? contents
                : TargetsText.TrimEnd() + Environment.NewLine + contents;
            StatusLine = $"Loaded {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read the file:\n{ex.Message}",
                "Load host file", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanClear() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void ClearResults()
    {
        if (IsScanning) return;
        Machines.Clear();
        SelectedMachine = null;
        LastRanges = Array.Empty<string>();
        AliveCount = UnreachableCount = TotalCount = 0;
        ProgressFraction = 0;
        StatusLine = "Cleared.";
        InventoryAliveCommand.NotifyCanExecuteChanged();
    }

    private ScanSettings BuildSettings() => new()
    {
        DiscoveryConcurrency = Math.Max(1, Concurrency),
        IcmpEnabled = IcmpEnabled,
        TcpFallbackEnabled = TcpFallback,
        ClassificationEnabled = Classification,
        ResolveNames = ResolveNames,
        ResolveMac = ResolveMac,
    };

    private void OnProgress(ScanProgress p)
    {
        _lastProgress = p;

        // Inventory reports carry no alive/unreachable counts — don't let them zero the discovery numbers.
        if (p.Phase is ScanPhase.Discovery or ScanPhase.Complete or ScanPhase.Cancelled)
        {
            AliveCount = p.Alive;
            UnreachableCount = p.Unreachable;
            if (p.Total is > 0) TotalCount = p.Total.Value;
        }
        ProgressFraction = p.Fraction ?? 0;
        ProgressIndeterminate = p.Fraction is null && p.Phase == ScanPhase.Discovery;
        ElapsedText = Format(p.Elapsed);
        EtaText = p.EstimatedRemaining is { } eta && p.Phase == ScanPhase.Discovery && !IsPaused && !IsCancelling
            ? $"ETA {Format(eta)}" : "";

        // The status is a function of (progress, paused, cancelling): a report arriving while paused reads
        // "Paused at …", not "Scanning…", so in-flight hosts finishing can't overwrite what the operator asked for.
        if (IsScanning && p.Phase is ScanPhase.Discovery or ScanPhase.Inventory)
        {
            var text = ScanStatusText.Compose(p, IsPaused, IsCancelling);
            if (text.Length > 0) StatusLine = text;
        }
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
}
