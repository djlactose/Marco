using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Marco.App.Services;
using Marco.App.Views;
using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Scanning;
using Marco.Core.Wmi;
using Marco.Credentials;
using Marco.Export;

namespace Marco.App.ViewModels;

/// <summary>Display wrapper for a credential set in the left-panel list.</summary>
public sealed class CredentialDisplay
{
    public string Label { get; }
    public string KindTag { get; }
    public string Details { get; }
    public CredentialSet Set { get; }
    public CredentialDisplay(CredentialSet set)
    {
        Set = set;
        Label = set.Label;
        KindTag = set.Kind switch { CredentialKind.Linux => "SSH", CredentialKind.Windows => "WMI", _ => "" };
        Details = set.Kind == CredentialKind.Linux
            ? $"SSH · {set.Username}" + (set.SshPort == 22 ? "" : $" · port {set.SshPort}")
            : $"WMI · {(string.IsNullOrWhiteSpace(set.Domain) ? set.Username : $"{set.Domain}\\{set.Username}")}";
    }
}

public partial class MainViewModel
{
    // --- Credentials ---

    [RelayCommand(CanExecute = nameof(CanMutateCredentials))]
    private void AddCredential()
    {
        // Open in the mode matching the selected host, so "select a Linux box → Add credential" lands on SSH.
        var kind = SelectedMachine?.DeviceType == DeviceType.UnixLinux ? CredentialKind.Linux : CredentialKind.Windows;
        var dialog = new CredentialDialog(InventoryFactory.CreateVerifier(), SelectedMachine?.Address, kind)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() == true && dialog.Result is { } set)
        {
            SyncCredentialsFromDisk();
            _credentials.Add(set);
            Credentials.Add(new CredentialDisplay(set));
            SaveCredentials();
            StatusLine = $"Added credential '{set.Label}'.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutateCredentials))]
    private void RemoveCredential(CredentialDisplay? display)
    {
        if (display is null) return;
        if (SyncCredentialsFromDisk())
        {
            display = Relocate(display);
            if (display is null) { StatusLine = "That credential was already removed by another Marco window."; return; }
        }
        _credentials.Remove(display.Set);
        Credentials.Remove(display);
        SaveCredentials();
    }

    [RelayCommand(CanExecute = nameof(CanMutateCredentials))]
    private void EditCredential(CredentialDisplay? display)
    {
        if (display is null) return;
        // The dialog edits the live set, so pick up another window's changes before opening it. A save from the
        // other window while the dialog is open is still last-writer-wins — accepted.
        if (SyncCredentialsFromDisk())
        {
            display = Relocate(display);
            if (display is null) { StatusLine = "That credential was removed by another Marco window."; return; }
        }
        var dialog = new CredentialDialog(InventoryFactory.CreateVerifier(), SelectedMachine?.Address,
            display.Set.Kind, editing: display.Set)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() == true && dialog.Result is { } updated)
        {
            _credentials.Replace(display.Set, updated);
            var index = Credentials.IndexOf(display);
            if (index >= 0) Credentials[index] = new CredentialDisplay(updated);
            SaveCredentials();
            StatusLine = $"Updated credential '{updated.Label}'.";
        }
    }

    /// <summary>Several Marco windows share credentials.dat. If another window saved since we last read or wrote
    /// it, reload and rebuild the display list so our next save doesn't discard their edit. Returns true when the
    /// list was replaced (callers holding a CredentialDisplay must <see cref="Relocate"/> it).</summary>
    private bool SyncCredentialsFromDisk()
    {
        try
        {
            if (!_credentials.ReloadIfChanged(_paths.CredentialsFile)) return false;
        }
        catch (CredentialDecryptException ex)
        {
            _runLog.Note($"Credential reload failed: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _runLog.Note($"Credential reload failed: {ex.Message}");
            return false;
        }
        Credentials.Clear();
        foreach (var set in _credentials.Sets)
            Credentials.Add(new CredentialDisplay(set));
        StatusLine = "Credential list refreshed (changed by another Marco window).";
        return true;
    }

    /// <summary>After a reload, find the display entry that corresponds to a now-stale one: same label, kind and
    /// user name. Null when the other window removed it.</summary>
    private CredentialDisplay? Relocate(CredentialDisplay stale)
        => Credentials.FirstOrDefault(c => ReferenceEquals(c, stale))
        ?? Credentials.FirstOrDefault(c =>
            string.Equals(c.Label, stale.Label, StringComparison.OrdinalIgnoreCase)
            && c.Set.Kind == stale.Set.Kind
            && string.Equals(c.Set.Username, stale.Set.Username, StringComparison.OrdinalIgnoreCase));

    /// <summary>Restore saved credential profiles (DPAPI CurrentUser). A profile sealed by a different
    /// account/machine won't decrypt — that's the intended security property; tell the operator and move on.</summary>
    private void LoadCredentials()
    {
        try
        {
            _credentials.Load(_paths.CredentialsFile);
            foreach (var set in _credentials.Sets)
                Credentials.Add(new CredentialDisplay(set));
        }
        catch (CredentialDecryptException ex)
        {
            _runLog.Note($"Saved credentials could not be decrypted: {ex.Message}");
            StatusLine = "Saved credential profiles could not be decrypted (different account/machine); re-enter them.";
        }
        catch (Exception ex)
        {
            _runLog.Note($"Credential load failed: {ex.Message}");
        }
    }

    private void SaveCredentials()
    {
        try { _credentials.Save(_paths.CredentialsFile); }
        catch (Exception ex) { _runLog.Note($"Credential save failed: {ex.Message}"); }
    }

    // Mutating credentials mid-run could dispose a SecureString a connect is actively using — and an abandoned
    // Stop drain keeps connects in flight after IsScanning drops, so gate on the whole run.
    private bool CanMutateCredentials() => !IsRunning;

    partial void OnIsScanningChanged(bool value)
    {
        RefreshScanCommands();
        if (!value) FlushPendingUpdatePrompt();
    }

    private IReadOnlyList<CredentialCandidate> ResolveCandidates()
    {
        var candidates = _credentials.ToCandidates();
        return candidates.Count > 0
            ? candidates
            : new[] { CredentialSet.CurrentToken().ToCandidate() }; // Windows-kind, so Linux hosts report "no SSH credentials"
    }

    // --- Inventory ---

    private bool CanInventory() => !IsRunning && Machines.Count > 0;

    [RelayCommand(CanExecute = nameof(CanInventorySelected))]
    private Task InventorySelectedAsync()
        => SelectedMachine is { } m ? RunInventoryAsync(new[] { m }, force: true) : Task.CompletedTask;

    private bool CanInventorySelected() => !IsRunning && SelectedMachine is not null;

    [RelayCommand(CanExecute = nameof(CanInventory))]
    private Task InventoryAliveAsync()
        => RunInventoryAsync(Machines.Where(m => m.IsAlive).ToList());

    /// <param name="force">Attempt every target even when its device type has no inventory support
    /// (explicit "Inventory selected"); bulk runs skip printers and network gear.</param>
    /// <summary>The hosts of the inventory run currently on screen — read (UI thread) by
    /// <see cref="ComposeActivitySummary"/> and the 1 Hz status timer. Null outside an inventory run.</summary>
    private IReadOnlyList<Machine>? _activeInventoryTargets;

    /// <summary>"pc-01: Collecting Software…, pc-02: Connecting (lab)… (+3 more)" — capped at two hosts so the
    /// status line stays one line. Null outside inventory (discovery has no per-host activity).</summary>
    private string? ComposeActivitySummary()
    {
        if (_activeInventoryTargets is not { } targets) return null;
        var busy = targets.Where(m => m.CurrentActivity is not null).ToList();
        if (busy.Count == 0) return null;
        var head = string.Join(", ", busy.Take(2).Select(m => $"{m.DisplayName}: {m.CurrentActivity}"));
        return busy.Count > 2 ? $"{head} (+{busy.Count - 2} more)" : head;
    }

    private async Task RunInventoryAsync(IReadOnlyList<Machine> targets, bool force = false)
    {
        if (targets.Count == 0) { StatusLine = "No hosts to inventory."; return; }

        int gen = ++_runGeneration;
        _cts = new CancellationTokenSource();
        var cts = _cts;   // captured: when a Stop abandons the drain, the drain continuation owns disposal
        _pause = new PauseController();
        var pause = _pause;
        // Gen-gated: after a Stop abandons this run, its late reports must not touch the UI.
        IProgress<ScanProgress> progress = new Progress<ScanProgress>(p => { if (_runGeneration == gen) OnProgress(p); });

        int done = 0, authed = 0, skipped = 0, inFlight = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Seed the snapshot so a Pause/Stop before the first host reports still composes inventory wording.
        _lastProgress = new ScanProgress(ScanPhase.Inventory, 0, targets.Count, 0, 0, TimeSpan.Zero, null);
        _activeInventoryTargets = targets;
        bool abandoned = false;

        try
        {
            IsRunning = true; // idempotent when chained from StartScanAsync
            IsScanning = true;
            IsPaused = false;
            IsCancelling = false;
            ProgressIndeterminate = false;
            ProgressFraction = 0;
            _inventoryStatusTimer.Start();

            var candidates = ResolveCandidates();
            // Snapshot the checklist once per run so a toggle mid-run can't give two hosts different collector sets.
            var enabledCollectors = EnabledCollectorNames();
            _runLog.Note($"Inventory of {targets.Count} host(s) started; collectors: {string.Join(",", enabledCollectors.OrderBy(n => n))}.");

            var options = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = BuildSettings().EffectiveInventoryConcurrency,
                CancellationToken = cts.Token,
            };

            var run = Task.Run(() => Parallel.ForEachAsync(targets, options, async (m, token) =>
            {
                // Pause parks new hosts here; hosts already mid-inventory finish (a WMI/SSH session can't be
                // suspended half-way), which is what the "N hosts finishing" status counts.
                await pause.WaitWhilePausedAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                // Report on entry as well as on completion so "N hosts finishing" is right from the first host.
                int entryDone = Volatile.Read(ref done);
                progress.Report(new ScanProgress(ScanPhase.Inventory, entryDone, targets.Count, 0, 0,
                    sw.Elapsed, ScanEta.Estimate(entryDone, targets.Count, sw.Elapsed - pause.PausedTime),
                    Interlocked.Increment(ref inFlight)));
                InventoryOutcome outcome;
                try
                {
                    // Route by device type, and only try credentials meant for that host kind (so a Windows domain
                    // credential is never fired at an SSH server, and vice versa — avoids pointless auth / lockouts).
                    bool isLinux = m.DeviceType == Marco.Core.Model.DeviceType.UnixLinux;
                    if (!force && m.DeviceType is Marco.Core.Model.DeviceType.Printer or Marco.Core.Model.DeviceType.NetworkDevice)
                    {
                        // No WMI/SSH inventory story for these; leave their discovery status untouched.
                        m.StatusDetail = m.DeviceType == Marco.Core.Model.DeviceType.Printer
                            ? "Skipped: printers aren't inventoried. Use 'Inventory selected' to try anyway."
                            : "Skipped: network devices aren't inventoried. Use 'Inventory selected' to try anyway.";
                        outcome = new InventoryOutcome(false, null, m.Status, m.StatusDetail);
                        Interlocked.Increment(ref skipped);
                    }
                    else
                    {
                        var hostKind = isLinux ? CredentialKind.Linux : CredentialKind.Windows;
                        var applicable = candidates.Where(c => c.AppliesTo(hostKind)).ToList();
                        if (applicable.Count == 0)
                        {
                            // Nothing was attempted, so keep the discovery status — red/orange stays reserved
                            // for real auth failures.
                            m.StatusDetail = isLinux
                                ? "Not inventoried: no Linux/SSH credentials configured."
                                : "Not inventoried: no Windows credentials configured.";
                            outcome = new InventoryOutcome(false, null, m.Status, m.StatusDetail);
                        }
                        else
                        {
                            outcome = isLinux
                                ? await _linuxInventory.InventoryAsync(m, applicable, null, enabledCollectors, token).ConfigureAwait(false)
                                : await _inventory.InventoryAsync(m, applicable, null, enabledCollectors, token).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                }
                _runLog.InventoryAttempt(m.Address, outcome.Authenticated, outcome.Status.ToString(), outcome.CredentialLabel);

                // Raise collection change notifications on the UI thread so the detail view re-renders.
                // Async, and null-tolerant: a straggler from an abandoned Stop drain must never block on
                // (or throw from) a dispatcher that may be gone at shutdown.
                Application.Current?.Dispatcher.InvokeAsync(m.NotifyInventoryUpdated);

                int completed = Interlocked.Increment(ref done);
                if (outcome.Authenticated) Interlocked.Increment(ref authed);
                progress.Report(new ScanProgress(ScanPhase.Inventory, completed, targets.Count, 0, 0,
                    sw.Elapsed, ScanEta.Estimate(completed, targets.Count, sw.Elapsed - pause.PausedTime),
                    Volatile.Read(ref inFlight)));
            }));

            try
            {
                await run.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!run.IsCompleted)
            {
                // Stop must hand the UI back within ~a second even while WMI/SSH work is mid-flight, so stop
                // WAITING for the drain instead of trying to interrupt it. The detached continuation observes
                // the outcome and owns the CTS; the bumped generation makes every late progress report from
                // this run inert. Stragglers only touch Machine rows (their INPC is harmless, even after a
                // Clear detaches them), the thread-safe run log, and captured locals.
                abandoned = true;
                _runGeneration++;
                int abandonedInFlight = Volatile.Read(ref inFlight);
                _ = run.ContinueWith(t =>
                {
                    _ = t.Exception; // observe; per-host failures are already recorded on the rows
                    cts.Dispose();
                    _runLog.Note($"Stopped inventory; {abandonedInFlight} in-flight host(s) drained in the background.");
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }

            StatusLine = $"Inventory complete. {authed}/{targets.Count} authenticated in {sw.Elapsed.TotalSeconds:0.0}s."
                + (skipped > 0 ? $" Skipped {skipped} printer/network device(s)." : "");
            SaveRunToHistory(Marco.Export.History.ScanHistoryPhase.Inventoried);
        }
        catch (OperationCanceledException)
        {
            StatusLine = $"Inventory stopped. {done}/{targets.Count} done.";
            _runLog.Note($"Inventory stopped. {done}/{targets.Count} done.");
        }
        catch (Exception ex)
        {
            StatusLine = $"Inventory failed: {ex.Message}";
            _runLog.Note($"Inventory failed: {ex.Message}");
        }
        finally
        {
            _inventoryStatusTimer.Stop();
            _activeInventoryTargets = null;
            IsPaused = false;
            IsCancelling = false;
            // An abandoned drain still holds the captured pause/cts; only clear the VM's references when they
            // are still ours (a new run may have replaced them by the time a straggler finishes).
            if (ReferenceEquals(_pause, pause)) _pause = null;
            if (!abandoned) cts.Dispose();
            if (ReferenceEquals(_cts, cts)) _cts = null;
            ProgressFraction = 0;
            IsScanning = false;
            IsRunning = false; // ends the run face; when chained, StartScanAsync's finally re-clears harmlessly
        }
    }

    // --- Export ---

    private ScanDocument BuildDocument(bool filteredOnly)
    {
        IEnumerable<Machine> source = filteredOnly
            ? MachinesView.Cast<Machine>()
            : Machines;
        var machines = source.ToList();
        var meta = new ScanMetadata(DateTime.Now, Environment.UserName, LastRanges,
            machines.Count, machines.Count(m => m.IsAlive), Version: Marco.Core.AppVersion.Display);
        return ScanDocument.From(meta, machines);
    }

    /// <summary>Reload a previously exported JSON scan into the grid (the ScanDocument round-trip the export
    /// format was designed for).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenScan))]
    private void OpenScan()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a saved scan",
            Filter = "JSON scan files (*.json;*.json.gz)|*.json;*.json.gz|All files (*.*)|*.*",
            InitialDirectory = _paths.ExportsDirectory,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var doc = new JsonExporter().Load(dialog.FileName);
            LoadScanDocument(doc, Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the scan:\n{ex.Message}",
                "Open scan", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Put a loaded document into the grid — shared by "Open scan…" and the scan-history list. Clears
    /// the current-run id: this grid is a restored snapshot, so a later inventory saves as a NEW history entry
    /// instead of overwriting the original.</summary>
    private void LoadScanDocument(ScanDocument doc, string sourceName)
    {
        CloseDetailWindows(); // their machines are about to leave the grid
        Machines.Clear();
        SelectedMachine = null;
        _currentRunId = null;
        var ranges = doc.Metadata.RangesScanned ?? Array.Empty<string>();
        foreach (var machine in doc.ToMachines())
        {
            // Files from before per-row block information: place each host in the range that covers it.
            machine.TargetBlock ??= Marco.Core.Targets.TargetParser.FindBlock(ranges, machine.Address) ?? "Other";
            Machines.Add(machine);
        }

        AliveCount = Machines.Count(m => m.IsAlive);
        UnreachableCount = Machines.Count - AliveCount;
        TotalCount = doc.Metadata.TotalTargets > 0 ? doc.Metadata.TotalTargets : Machines.Count;
        LastRanges = doc.Metadata.RangesScanned?.ToList() ?? new List<string>();
        ProgressFraction = 0;
        StatusLine = $"Loaded scan from {sourceName} ({doc.Metadata.Timestamp:g}).";
        InventoryAliveCommand.NotifyCanExecuteChanged();
    }

    private bool CanOpenScan() => !IsRunning;

    [RelayCommand]
    private void ExportJson()
    {
        if (Machines.Count == 0) { StatusLine = "Nothing to export."; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export scan as JSON",
            Filter = "JSON files (*.json)|*.json",
            FileName = $"marco-scan-{DateTime.Now:yyyyMMdd-HHmm}.json",
            InitialDirectory = _paths.ExportsDirectory,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            new JsonExporter().Export(BuildDocument(filteredOnly: true), dialog.FileName);
            StatusLine = $"Exported JSON to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export JSON", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (Machines.Count == 0) { StatusLine = "Nothing to export."; return; }
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder for the CSV files",
            InitialDirectory = _paths.ExportsDirectory,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var files = new CsvExporter().Export(BuildDocument(filteredOnly: true), dialog.FolderName);
            StatusLine = $"Exported {files.Count} CSV files to {dialog.FolderName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export CSV", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
