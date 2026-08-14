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
    public CredentialSet Set { get; }
    public CredentialDisplay(CredentialSet set)
    {
        Set = set;
        Label = set.Label;
        KindTag = set.Kind switch { CredentialKind.Linux => "SSH", CredentialKind.Windows => "WMI", _ => "" };
    }
}

public partial class MainViewModel
{
    // --- Credentials ---

    [RelayCommand]
    private void AddCredential()
    {
        var dialog = new CredentialDialog(InventoryFactory.CreateVerifier(), SelectedMachine?.Address)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() == true && dialog.Result is { } set)
        {
            _credentials.Add(set);
            Credentials.Add(new CredentialDisplay(set));
            StatusLine = $"Added credential '{set.Label}'.";
        }
    }

    [RelayCommand]
    private void RemoveCredential(CredentialDisplay? display)
    {
        if (display is null) return;
        _credentials.Remove(display.Set);
        Credentials.Remove(display);
    }

    private IReadOnlyList<CredentialCandidate> ResolveCandidates()
    {
        var candidates = _credentials.ToCandidates();
        return candidates.Count > 0
            ? candidates
            : new[] { new CredentialCandidate("Current session", WmiCredential.CurrentToken) };
    }

    // --- Inventory ---

    private bool CanInventory() => !IsScanning && Machines.Count > 0;

    [RelayCommand(CanExecute = nameof(CanInventorySelected))]
    private Task InventorySelectedAsync()
        => SelectedMachine is { } m ? RunInventoryAsync(new[] { m }) : Task.CompletedTask;

    private bool CanInventorySelected() => !IsScanning && SelectedMachine is not null;

    [RelayCommand(CanExecute = nameof(CanInventory))]
    private Task InventoryAliveAsync()
        => RunInventoryAsync(Machines.Where(m => m.IsAlive).ToList());

    private async Task RunInventoryAsync(IReadOnlyList<Machine> targets)
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

        int done = 0, authed = 0;
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
                var hostKind = isLinux ? CredentialKind.Linux : CredentialKind.Windows;
                var applicable = candidates.Where(c => c.AppliesTo(hostKind)).ToList();
                InventoryOutcome outcome;
                if (applicable.Count == 0)
                {
                    m.Status = MachineStatus.AuthFailed;
                    m.StatusDetail = isLinux ? "No Linux/SSH credentials configured." : "No Windows credentials configured.";
                    outcome = new InventoryOutcome(false, null, m.Status, m.StatusDetail);
                }
                else
                {
                    outcome = isLinux
                        ? await _linuxInventory.InventoryAsync(m, applicable, null, null, token).ConfigureAwait(false)
                        : await _inventory.InventoryAsync(m, applicable, null, null, token).ConfigureAwait(false);
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

            StatusLine = $"Inventory complete. {authed}/{targets.Count} authenticated in {sw.Elapsed.TotalSeconds:0.0}s.";
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
            machines.Count, machines.Count(m => m.IsAlive));
        return ScanDocument.From(meta, machines);
    }

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
