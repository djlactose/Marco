using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Marco.App.Services;
using Marco.App.Views;
using Marco.Core.Inventory;
using Marco.Core.Model;
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
        _credentials.Remove(display.Set);
        Credentials.Remove(display);
        SaveCredentials();
    }

    [RelayCommand(CanExecute = nameof(CanMutateCredentials))]
    private void EditCredential(CredentialDisplay? display)
    {
        if (display is null) return;
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
        AddCredentialCommand.NotifyCanExecuteChanged();
        RemoveCredentialCommand.NotifyCanExecuteChanged();
        EditCredentialCommand.NotifyCanExecuteChanged();
        OpenScanCommand.NotifyCanExecuteChanged();
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

        var candidates = ResolveCandidates();
        _cts = new CancellationTokenSource();
        IsScanning = true;
        ProgressIndeterminate = false;
        ProgressFraction = 0;
        StartScanCommand.NotifyCanExecuteChanged();
        InventoryAliveCommand.NotifyCanExecuteChanged();
        InventorySelectedCommand.NotifyCanExecuteChanged();

        int done = 0, authed = 0, skipped = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _runLog.Note($"Inventory of {targets.Count} host(s) started.");

        try
        {
            var options = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, BuildSettings().InventoryConcurrency),
                CancellationToken = _cts.Token,
            };

            await Task.Run(() => Parallel.ForEachAsync(targets, options, async (m, token) =>
            {
                // Route by device type, and only try credentials meant for that host kind (so a Windows domain
                // credential is never fired at an SSH server, and vice versa — avoids pointless auth / lockouts).
                bool isLinux = m.DeviceType == Marco.Core.Model.DeviceType.UnixLinux;
                InventoryOutcome outcome;
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
                _runLog.InventoryAttempt(m.Address, outcome.Authenticated, outcome.Status.ToString(), outcome.CredentialLabel);

                // Raise collection change notifications on the UI thread so the detail view re-renders.
                Application.Current.Dispatcher.Invoke(() => m.NotifyInventoryUpdated());

                int completed = Interlocked.Increment(ref done);
                if (outcome.Authenticated) Interlocked.Increment(ref authed);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ProgressFraction = (double)completed / targets.Count;
                    StatusLine = $"Inventorying… {completed}/{targets.Count}";
                });
            }));

            StatusLine = $"Inventory complete. {authed}/{targets.Count} authenticated in {sw.Elapsed.TotalSeconds:0.0}s."
                + (skipped > 0 ? $" Skipped {skipped} printer/network device(s)." : "");
        }
        catch (OperationCanceledException)
        {
            StatusLine = $"Inventory cancelled. {done}/{targets.Count} done.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Inventory failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
            ProgressFraction = 0;
            StartScanCommand.NotifyCanExecuteChanged();
            InventoryAliveCommand.NotifyCanExecuteChanged();
            InventorySelectedCommand.NotifyCanExecuteChanged();
        }
    }

    // --- Export ---

    private ScanDocument BuildDocument(bool filteredOnly)
    {
        IEnumerable<Machine> source = filteredOnly
            ? MachinesView.Cast<Machine>()
            : Machines;
        var machines = source.ToList();
        var meta = new ScanMetadata(DateTime.Now, Environment.UserName, _lastRanges,
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
            foreach (var machine in doc.ToMachines())
                Machines.Add(machine);

            AliveCount = Machines.Count(m => m.IsAlive);
            UnreachableCount = Machines.Count - AliveCount;
            TotalCount = doc.Metadata.TotalTargets > 0 ? doc.Metadata.TotalTargets : Machines.Count;
            _lastRanges = doc.Metadata.RangesScanned?.ToList() ?? new List<string>();
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
