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
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private string _statusLine = "Ready.";
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private int _aliveCount;
    [ObservableProperty] private int _unreachableCount;
    [ObservableProperty] private int _totalCount;

    public string StorageLocation => _paths.Reason;
    public string Title => $"Marco — Network Inventory — v{Marco.Core.AppVersion.Display}";

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
    }

    private bool CanStart() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartScanAsync()
    {
        List<ScanTarget> targets;
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
                if (answer != MessageBoxResult.Yes) { StatusLine = "Scan cancelled."; return; }
                options = new TargetExpansionOptions { AllowLargeExpansion = true };
            }
            targets = TargetParser.Parse(new[] { TargetsText }, options).ToList();
            _lastRanges = TargetParser.Tokenize(new[] { TargetsText }).ToList();
        }
        catch (TargetParseException ex)
        {
            MessageBox.Show($"Could not parse target '{ex.Token}':\n{ex.Message}",
                "Invalid target", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Machines.Clear();
        SelectedMachine = null;
        AliveCount = UnreachableCount = 0;
        TotalCount = targets.Count;

        _cts = new CancellationTokenSource();
        _pause = new PauseController();
        IsScanning = true;
        IsPaused = false;
        ProgressIndeterminate = false;
        StartScanCommand.NotifyCanExecuteChanged();

        var settings = BuildSettings();
        var progress = new Progress<ScanProgress>(OnProgress);
        _flushTimer.Start();
        _runLog.ScanStarted(_lastRanges, targets.Count);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        bool discoveryCompleted = false;
        try
        {
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
            StatusLine = $"Cancelled. {AliveCount:N0} alive so far.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _flushTimer.Stop();
            FlushPending();
            IsScanning = false;
            IsPaused = false;
            _cts?.Dispose();
            _cts = null;
            StartScanCommand.NotifyCanExecuteChanged();
            InventoryAliveCommand.NotifyCanExecuteChanged();
            InventorySelectedCommand.NotifyCanExecuteChanged();
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

    [RelayCommand]
    private void PauseResume()
    {
        if (_pause is null || !IsScanning) return;
        if (IsPaused) { _pause.Resume(); IsPaused = false; StatusLine = "Resumed."; }
        else { _pause.Pause(); IsPaused = true; StatusLine = "Paused."; }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        _pause?.Resume(); // release any paused workers so they observe cancellation
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

    [RelayCommand]
    private void ClearResults()
    {
        if (IsScanning) return;
        Machines.Clear();
        SelectedMachine = null;
        AliveCount = UnreachableCount = TotalCount = 0;
        ProgressFraction = 0;
        StatusLine = "Cleared.";
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
        AliveCount = p.Alive;
        UnreachableCount = p.Unreachable;
        if (p.Total is > 0) TotalCount = p.Total.Value;
        ProgressFraction = p.Fraction ?? 0;
        ProgressIndeterminate = p.Fraction is null && p.Phase == ScanPhase.Discovery;
        ElapsedText = Format(p.Elapsed);
        EtaText = p.EstimatedRemaining is { } eta && p.Phase == ScanPhase.Discovery ? $"ETA {Format(eta)}" : "";
        if (p.Phase == ScanPhase.Discovery)
            StatusLine = $"Scanning… {p.Completed:N0}/{(p.Total?.ToString("N0") ?? "?")}  ·  {p.Alive:N0} alive";
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
}
