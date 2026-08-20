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
    /// <summary>1 Hz recompose while inventory runs: Machine.CurrentActivity marches between the entry/completion
    /// progress reports, and the status line should march with it.</summary>
    private readonly DispatcherTimer _inventoryStatusTimer;
    private CancellationTokenSource? _cts;
    private PauseController? _pause;

    /// <summary>Credential sets shown in the left panel (label + backing set).</summary>
    public ObservableCollection<CredentialDisplay> Credentials { get; } = new();

    /// <summary>The machine's own NIC subnets, suggested as one-click scan targets.</summary>
    public ObservableCollection<Marco.Discovery.LocalSubnet> SuggestedNetworks { get; } = new();

    /// <summary>The inventory collector checklist (catalogue order). Both runners receive the enabled names, so a
    /// collector unticked here is skipped on every host; heavier ones default off (see CollectorCatalog).</summary>
    public ObservableCollection<CollectorOption> Collectors { get; } = new();

    /// <summary>"Inventory collectors (10 of 14 enabled)" for the checklist's expander header.</summary>
    public string CollectorSummary =>
        $"Inventory collectors ({Collectors.Count(c => c.IsEnabled)} of {Collectors.Count} enabled)";

    /// <summary>The names both runners should run — what the checklist currently says.</summary>
    private HashSet<string> EnabledCollectorNames()
        => new(Collectors.Where(c => c.IsEnabled).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

    private void BuildCollectorOptions(IReadOnlyDictionary<string, bool>? overrides)
    {
        var enabled = CollectorCatalog.EnabledNames(overrides);
        Collectors.Clear();
        foreach (var info in CollectorCatalog.All)
        {
            var option = new CollectorOption(info, enabled.Contains(info.Name));
            option.Changed += () => OnPropertyChanged(nameof(CollectorSummary));
            Collectors.Add(option);
        }
    }

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

    // --- Filter / selection / layout ---
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private Machine? _selectedMachine;

    /// <summary>One collapsible section per target block (CIDR / range) in the results grid.</summary>
    [ObservableProperty] private bool _groupByBlock = true;

    // --- Run state ---
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isPaused;
    /// <summary>Stop was clicked and in-flight work is draining; the run is still "scanning" until it settles
    /// (or, for inventory, until the drain is abandoned to the background).</summary>
    [ObservableProperty] private bool _isCancelling;
    /// <summary>True from Start until the ENTIRE run — discovery plus any chained auto-inventory — has ended.
    /// IsScanning dips false between the phases (discovery's finally runs before inventory begins), so the
    /// Start/Stop button and the idle-only commands key off this to never flicker during the handoff.</summary>
    [ObservableProperty] private bool _isRunning;
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

    /// <summary>Bumped when a run starts and again when a Stop abandons its inventory drain; progress reports
    /// from an abandoned run compare against it and are dropped. UI thread only.</summary>
    private int _runGeneration;

    public string StorageLocation => _paths.Reason;

    /// <summary>The largest Concurrency this PC can safely run (see <see cref="ConcurrencyLimits"/>); the box snaps to it.</summary>
    public int MaxConcurrency => ConcurrencyLimits.Max;

    /// <summary>Why <see cref="MaxConcurrency"/> is what it is — shown as the tooltip on the Concurrency box.</summary>
    public string ConcurrencyExplanation => ConcurrencyLimits.Explanation;

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
        // Numeric address order by default (10.0.0.2 before 10.0.0.10) — the Address column header re-sorts on the
        // same key. With grouping on, groups fall into the same order because each group is placed where its
        // lowest address sorts.
        MachinesView.SortDescriptions.Add(new SortDescription(nameof(Machine.AddressSortKey), ListSortDirection.Ascending));
        ApplyGrouping();

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _flushTimer.Tick += (_, _) => FlushPending();

        _inventoryStatusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _inventoryStatusTimer.Tick += (_, _) => RefreshScanStatus();

        _ = RefreshHistoryAsync(); // populate the scan-history expander in the background
    }

    /// <summary>Seed the observable backing fields directly: during construction nothing observes yet, and the
    /// generated setters must NOT run — OnIncludeBetaUpdatesChanged would persist an explicit channel choice the
    /// operator never made.</summary>
#pragma warning disable MVVMTK0034
    private void ApplySettings(AppSettings s)
    {
        _targetsText = s.TargetsText;
        _concurrency = Math.Clamp(s.Concurrency, 1, ConcurrencyLimits.Max); // settings.json may come from a bigger machine
        _icmpEnabled = s.IcmpEnabled;
        _tcpFallback = s.TcpFallback;
        _classification = s.Classification;
        _resolveNames = s.ResolveNames;
        _resolveMac = s.ResolveMac;
        _includeUnreachable = s.IncludeUnreachable;
        _autoInventory = s.AutoInventory;
        _groupByBlock = s.GroupByBlock;
        _includeBetaSetting = s.IncludeBetaUpdates;
        _includeBetaUpdates = s.IncludeBetaUpdates ?? Marco.Core.AppVersion.IsBeta;
        _autoSaveScans = s.AutoSaveScans;
        _scanHistoryLimit = Math.Max(1, s.ScanHistoryLimit);
        _autoSaveDiscoveryOnly = s.AutoSaveDiscoveryOnly;
        BuildCollectorOptions(s.CollectorOverrides);
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
        GroupByBlock = GroupByBlock,
        CollectorOverrides = CollectorCatalog.OverridesFor(EnabledCollectorNames()),
        AutoSaveScans = _autoSaveScans,
        ScanHistoryLimit = _scanHistoryLimit,
        AutoSaveDiscoveryOnly = _autoSaveDiscoveryOnly,
    });

    partial void OnFilterTextChanged(string value) => MachinesView.Refresh();

    /// <summary>Silent snap into [1, <see cref="MaxConcurrency"/>]. The generated setter is equality-guarded, so the
    /// nested assignment re-enters this hook once with an in-range value and stops.</summary>
    partial void OnConcurrencyChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, ConcurrencyLimits.Max);
        if (clamped == value) return;
        Concurrency = clamped;
        StatusLine = value > clamped
            ? $"Concurrency capped at {clamped} — the most this PC can sustain."
            : "Concurrency must be at least 1.";
    }

    partial void OnGroupByBlockChanged(bool value) => ApplyGrouping();

    private void ApplyGrouping()
    {
        using (MachinesView.DeferRefresh())
        {
            MachinesView.GroupDescriptions.Clear();
            if (GroupByBlock)
                MachinesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Machine.TargetBlock)));
        }
    }

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
        {
            InventoryAliveCommand.NotifyCanExecuteChanged(); // CanInventory depends on Machines.Count
            CompareCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanStart() => !IsRunning;

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
        _currentRunId = Marco.Export.History.ScanHistoryStore.NewRunId(DateTime.Now);
        CloseDetailWindows(); // their machines are about to leave the grid
        Machines.Clear();
        SelectedMachine = null;
        AliveCount = UnreachableCount = 0;
        TotalCount = targets.Count;
        // Seed the snapshot so a Pause/Cancel before the first report still composes discovery wording.
        _lastProgress = new ScanProgress(ScanPhase.Discovery, 0, targets.Count, 0, 0, TimeSpan.Zero, null);

        _cts = new CancellationTokenSource();
        _pause = new PauseController();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        int gen = ++_runGeneration;
        bool discoveryCompleted = false;
        try
        {
            // IsRunning spans discovery AND any chained auto-inventory, so the Start/Stop button never
            // flickers back to "Start" in the gap between the phases.
            IsRunning = true;
            try
            {
                // Everything from IsScanning = true onward is inside the try, so no exception can leave the scan
                // commands latched in the "running" state.
                IsScanning = true;
                IsPaused = false;
                IsCancelling = false;
                ProgressIndeterminate = false;

                var settings = BuildSettings();
                // Gen-gated: a report from a run this VM has since moved past must not touch the UI.
                var progress = new Progress<ScanProgress>(p => { if (_runGeneration == gen) OnProgress(p); });
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
                StatusLine = $"Stopped. {AliveCount:N0} alive, {UnreachableCount:N0} unreachable ({probed:N0} of {TotalCount:N0} probed).";
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
            if (discoveryCompleted && AutoInventory && Machines.Any(m => m.IsAlive))
            {
                await RunInventoryAsync(Machines.Where(m => m.IsAlive).ToList());
                // RunInventoryAsync saved the run (phase Inventoried) on success; nothing more to do here.
            }
            else if (discoveryCompleted)
            {
                SaveRunToHistory(Marco.Export.History.ScanHistoryPhase.DiscoveryOnly);
            }
        }
        finally
        {
            IsRunning = false;
        }
    }

    partial void OnSelectedMachineChanged(Machine? value)
    {
        InventorySelectedCommand.NotifyCanExecuteChanged();
        OpenMachineDetailCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCancellingChanged(bool value) => RefreshScanCommands();

    partial void OnIsRunningChanged(bool value) => RefreshScanCommands();

    /// <summary>Every command whose CanExecute reads run state. Called from the IsScanning / IsCancelling change
    /// hooks so a state flip can never leave a button stale.</summary>
    private void RefreshScanCommands()
    {
        StartScanCommand.NotifyCanExecuteChanged();
        PauseResumeCommand.NotifyCanExecuteChanged();
        StopScanCommand.NotifyCanExecuteChanged();
        ClearResultsCommand.NotifyCanExecuteChanged();
        InventoryAliveCommand.NotifyCanExecuteChanged();
        InventorySelectedCommand.NotifyCanExecuteChanged();
        OpenScanCommand.NotifyCanExecuteChanged();
        OpenHistoryEntryCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
        CompareWithCurrentCommand.NotifyCanExecuteChanged();
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

    private bool CanStopScan() => IsRunning && !IsCancelling;

    /// <summary>The Stop face of the Start/Stop button: cancels the whole run, whichever phase it is in.
    /// The UI comes back within ~a second even when WMI/SSH work is mid-flight — the inventory drain is
    /// abandoned to the background (see RunInventoryAsync) rather than awaited.</summary>
    [RelayCommand(CanExecute = nameof(CanStopScan))]
    private void StopScan()
    {
        if (_cts is null) return; // the instant between phases; the next phase starts un-cancelled by design
        IsCancelling = true;      // disables Pause/Stop and switches the status text to "Stopping…"
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
            var text = ScanStatusText.Compose(p, IsPaused, IsCancelling, ComposeActivitySummary());
            if (text.Length > 0) { StatusLine = text; return; }
        }
        if (IsCancelling) StatusLine = "Stopping…";
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

    // --- Full-screen machine view ---

    /// <summary>Open detail windows, one per machine. Closed by <see cref="CloseDetailWindows"/> before the
    /// grid is cleared or repopulated — a viewer bound to a detached Machine would silently freeze.</summary>
    private readonly Dictionary<Machine, Marco.App.Views.MachineDetailWindow> _detailWindows = new();

    private bool CanOpenMachineDetail() => SelectedMachine is not null;

    /// <summary>Open (or re-activate) the maximized detail window for a machine. The parameter comes from a
    /// grid row double-click; the toolbar button and Enter key pass null, meaning the selected row.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenMachineDetail))]
    private void OpenMachineDetail(Machine? machine)
    {
        var target = machine ?? SelectedMachine;
        if (target is null) return;
        if (_detailWindows.TryGetValue(target, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Maximized;
            open.Activate();
            return;
        }
        var window = new Marco.App.Views.MachineDetailWindow(target) { Owner = Application.Current.MainWindow };
        window.Closed += (_, _) => _detailWindows.Remove(target);
        _detailWindows[target] = window;
        window.Show();
    }

    private void CloseDetailWindows()
    {
        foreach (var w in _detailWindows.Values.ToList()) w.Close();
    }

    private bool CanClear() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void ClearResults()
    {
        if (IsRunning) return;
        CloseDetailWindows(); // their machines are about to leave the grid
        Machines.Clear();
        SelectedMachine = null;
        LastRanges = Array.Empty<string>();
        _currentRunId = null;
        AliveCount = UnreachableCount = TotalCount = 0;
        ProgressFraction = 0;
        StatusLine = "Cleared.";
        InventoryAliveCommand.NotifyCanExecuteChanged();
    }

    private ScanSettings BuildSettings() => new()
    {
        DiscoveryConcurrency = Math.Clamp(Concurrency, 1, ConcurrencyLimits.Max),
        InventoryConcurrency = Math.Clamp(Concurrency, 1, ConcurrencyLimits.Max),
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
        EtaText = p.EstimatedRemaining is { } eta
                  && p.Phase is ScanPhase.Discovery or ScanPhase.Inventory
                  && !IsPaused && !IsCancelling
            ? ScanEta.Compose(eta, DateTime.Now) : "";

        // The status is a function of (progress, paused, cancelling): a report arriving while paused reads
        // "Paused at …", not "Scanning…", so in-flight hosts finishing can't overwrite what the operator asked for.
        if (IsScanning && p.Phase is ScanPhase.Discovery or ScanPhase.Inventory)
        {
            var text = ScanStatusText.Compose(p, IsPaused, IsCancelling, ComposeActivitySummary());
            if (text.Length > 0) StatusLine = text;
        }
    }

    private static string Format(TimeSpan t) => ScanEta.FormatDuration(t);
}
