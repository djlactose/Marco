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

    // Mutating credentials mid-run could dispose a SecureString a connect is actively using.
    private bool CanMutateCredentials() => !IsScanning;

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

    private bool CanInventory() => !IsScanning && Machines.Count > 0;

    [RelayCommand(CanExecute = nameof(CanInventorySelected))]
    private Task InventorySelectedAsync()
        => SelectedMachine is { } m ? RunInventoryAsync(new[] { m }, force: true) : Task.CompletedTask;

    private bool CanInventorySelected() => !IsScanning && SelectedMachine is not null;

    [RelayCommand(CanExecute = nameof(CanInventory))]
    private Task InventoryAliveAsync()
        => RunInventoryAsync(Machines.Where(m => m.IsAlive).ToList());

    /// <param name="force">Attempt every target even when its device type has no inventory support
    /// (explicit "Inventory selected"); bulk runs skip printers and network gear.</param>
    private async Task RunInventoryAsync(IReadOnlyList<Machine> targets, bool force = false)
    {
        if (targets.Count == 0) { StatusLine = "No hosts to inventory."; return; }

        _cts = new CancellationTokenSource();
        _pause = new PauseController();
        var pause = _pause;
        IProgress<ScanProgress> progress = new Progress<ScanProgress>(OnProgress);

        int done = 0, authed = 0, skipped = 0, inFlight = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Seed the snapshot so a Pause/Cancel before the first host reports still composes inventory wording.
        _lastProgress = new ScanProgress(ScanPhase.Inventory, 0, targets.Count, 0, 0, TimeSpan.Zero, null);

        try
        {
            IsScanning = true;
            IsPaused = false;
            IsCancelling = false;
            ProgressIndeterminate = false;
            ProgressFraction = 0;

            var candidates = ResolveCandidates();
            _runLog.Note($"Inventory of {targets.Count} host(s) started.");

            var options = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, BuildSettings().InventoryConcurrency),
                CancellationToken = _cts.Token,
            };

            await Task.Run(() => Parallel.ForEachAsync(targets, options, async (m, token) =>
            {
                // Pause parks new hosts here; hosts already mid-inventory finish (a WMI/SSH session can't be
                // suspended half-way), which is what the "N hosts finishing" status counts.
                await pause.WaitWhilePausedAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                // Report on entry as well as on completion so "N hosts finishing" is right from the first host.
                progress.Report(new ScanProgress(ScanPhase.Inventory, Volatile.Read(ref done), targets.Count, 0, 0,
                    sw.Elapsed, null, Interlocked.Increment(ref inFlight)));
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
                                ? await _linuxInventory.InventoryAsync(m, applicable, null, null, token).ConfigureAwait(false)
                                : await _inventory.InventoryAsync(m, applicable, null, null, token).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                }
                _runLog.InventoryAttempt(m.Address, outcome.Authenticated, outcome.Status.ToString(), outcome.CredentialLabel);

                // Raise collection change notifications on the UI thread so the detail view re-renders.
                Application.Current.Dispatcher.Invoke(() => m.NotifyInventoryUpdated());

                int completed = Interlocked.Increment(ref done);
                if (outcome.Authenticated) Interlocked.Increment(ref authed);
                progress.Report(new ScanProgress(ScanPhase.Inventory, completed, targets.Count, 0, 0,
                    sw.Elapsed, null, Volatile.Read(ref inFlight)));
            }));

            StatusLine = $"Inventory complete. {authed}/{targets.Count} authenticated in {sw.Elapsed.TotalSeconds:0.0}s."
                + (skipped > 0 ? $" Skipped {skipped} printer/network device(s)." : "");
        }
        catch (OperationCanceledException)
        {
            StatusLine = $"Inventory cancelled. {done}/{targets.Count} done.";
            _runLog.Note($"Inventory cancelled. {done}/{targets.Count} done.");
        }
        catch (Exception ex)
        {
            StatusLine = $"Inventory failed: {ex.Message}";
            _runLog.Note($"Inventory failed: {ex.Message}");
        }
        finally
        {
            IsPaused = false;
            IsCancelling = false;
            _pause = null;
            _cts?.Dispose();
            _cts = null;
            ProgressFraction = 0;
            IsScanning = false;
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
            Filter = "JSON scan files (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = _paths.ExportsDirectory,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var doc = new JsonExporter().Load(dialog.FileName);
            Machines.Clear();
            SelectedMachine = null;
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
            StatusLine = $"Loaded scan from {Path.GetFileName(dialog.FileName)} ({doc.Metadata.Timestamp:g}).";
            InventoryAliveCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the scan:\n{ex.Message}",
                "Open scan", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanOpenScan() => !IsScanning;

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
