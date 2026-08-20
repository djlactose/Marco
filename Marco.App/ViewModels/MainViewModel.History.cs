using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Marco.App.Views;
using Marco.Export;
using Marco.Export.History;

namespace Marco.App.ViewModels;

/// <summary>Display wrapper for a saved run in the left-panel scan-history list.</summary>
public sealed class HistoryEntryDisplay
{
    public ScanHistoryEntry Entry { get; }
    public string TimeText { get; }
    public string RangesText { get; }
    public string CountsText { get; }
    /// <summary>"Inventoried" / "Discovery" badge; empty when the phase is unknown (index rebuilt from a bare file).</summary>
    public string PhaseTag { get; }
    public string ToolTipText { get; }

    public HistoryEntryDisplay(ScanHistoryEntry entry)
    {
        Entry = entry;
        TimeText = entry.Timestamp.ToString("MMM d, HH:mm");
        RangesText = entry.Ranges.Count == 0 ? "(no ranges)"
            : entry.Ranges.Count == 1 ? entry.Ranges[0]
            : $"{entry.Ranges[0]} (+{entry.Ranges.Count - 1} more)";
        CountsText = $"{entry.AliveCount}/{entry.TotalTargets} alive";
        PhaseTag = entry.Phase switch
        {
            ScanHistoryPhase.Inventoried => "Inventoried",
            ScanHistoryPhase.DiscoveryOnly => "Discovery",
            _ => "",
        };
        var size = entry.SizeBytes >= 1024 * 1024 ? $"{entry.SizeBytes / (1024.0 * 1024):0.0} MB"
            : $"{Math.Max(1, entry.SizeBytes / 1024)} KB";
        ToolTipText = $"{entry.Timestamp:g} by {entry.Operator}\n{string.Join(", ", entry.Ranges)}\n"
            + $"{CountsText}" + (entry.InventoriedCount > 0 ? $", {entry.InventoriedCount} inventoried" : "")
            + $" · {size} · Marco {entry.AppVersion}\nDouble-click to reopen this run.";
    }
}

public partial class MainViewModel
{
    private ScanHistoryStore? _historyStore;
    /// <summary>Id of the run currently on screen when it was produced HERE (Start scan). An inventory pass saves
    /// over its own discovery save under this id; null after Clear/Open (those grids aren't "our run", so a later
    /// inventory becomes a new history entry instead of overwriting somebody's saved snapshot).</summary>
    private string? _currentRunId;

    private bool _autoSaveScans = true;
    private int _scanHistoryLimit = 30;
    private bool _autoSaveDiscoveryOnly = true;

    /// <summary>Saved runs, newest first (left-panel "Scan history" expander).</summary>
    public ObservableCollection<HistoryEntryDisplay> History { get; } = new();

    public string HistorySummary => $"Scan history ({History.Count})";

    private ScanHistoryStore HistoryStore => _historyStore ??= new ScanHistoryStore(_paths.ScansDirectory);

    /// <summary>Auto-save the grid as the current run's history document. UI thread: the DTO projection happens
    /// here (the grid is quiescent at run completion), the gzip + index write on a worker. Failures only log —
    /// history bookkeeping must never break a finished scan.</summary>
    private void SaveRunToHistory(ScanHistoryPhase phase)
    {
        if (!_autoSaveScans || Machines.Count == 0) return;
        if (phase == ScanHistoryPhase.DiscoveryOnly && !_autoSaveDiscoveryOnly) return;

        var runId = _currentRunId ??= ScanHistoryStore.NewRunId(DateTime.Now);
        var doc = BuildDocument(filteredOnly: false);
        var store = HistoryStore;
        int limit = _scanHistoryLimit;
        var client = ActiveClient?.Name;
        _ = Task.Run(() =>
        {
            try
            {
                store.Save(doc, runId, phase, limit, client);
                Application.Current?.Dispatcher.InvokeAsync(() => _ = RefreshHistoryAsync());
            }
            catch (Exception ex)
            {
                _runLog.Note($"Scan history auto-save failed: {ex.Message}");
            }
        });
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        var store = HistoryStore;
        IReadOnlyList<ScanHistoryEntry> entries;
        try { entries = await Task.Run(() => store.List()); }
        catch { return; } // List() is already tolerant; belt and braces around the Task itself
        History.Clear();
        foreach (var entry in entries)
            History.Add(new HistoryEntryDisplay(entry));
        OnPropertyChanged(nameof(HistorySummary));
        CompareCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOpenScan))]
    private async Task OpenHistoryEntryAsync(HistoryEntryDisplay? item)
    {
        if (item is null) return;
        try
        {
            var store = HistoryStore;
            var doc = await Task.Run(() => store.LoadDocument(item.Entry));
            LoadScanDocument(doc, item.Entry.File);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the saved scan:\n{ex.Message}",
                "Scan history", MessageBoxButton.OK, MessageBoxImage.Error);
            _ = RefreshHistoryAsync(); // most likely the file vanished — resync the list
        }
    }

    [RelayCommand]
    private async Task DeleteHistoryEntryAsync(HistoryEntryDisplay? item)
    {
        if (item is null) return;
        var store = HistoryStore;
        await Task.Run(() => store.Delete(item.Entry));
        History.Remove(item);
        OnPropertyChanged(nameof(HistorySummary));
        StatusLine = $"Deleted saved scan from {item.TimeText}.";
    }

    // --- Compare (scan diff) ---

    private bool CanCompare() => !IsRunning && Machines.Count > 0 && History.Count > 0;

    /// <summary>Toolbar "Compare…": current grid vs the most recent saved run that isn't this run's own
    /// auto-save (comparing a run with its own snapshot is always empty), preferring one that scanned
    /// overlapping ranges so unrelated networks don't diff against each other.</summary>
    [RelayCommand(CanExecute = nameof(CanCompare))]
    private Task CompareAsync()
    {
        var candidates = History.Where(h => !string.Equals(h.Entry.Id, _currentRunId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) { StatusLine = "No earlier saved scan to compare with."; return Task.CompletedTask; }
        // Prefer runs of the same client, then runs that scanned overlapping ranges.
        var sameClient = candidates.Where(h =>
            string.Equals(h.Entry.Client, ActiveClient?.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = sameClient.Count > 0 ? sameClient : candidates;
        var baseline = pool.FirstOrDefault(h => h.Entry.Ranges.Intersect(LastRanges, StringComparer.OrdinalIgnoreCase).Any())
                       ?? pool[0];
        return CompareWithCurrentAsync(baseline);
    }

    /// <summary>History context menu: this saved run (older) vs whatever is in the grid now (newer).</summary>
    [RelayCommand(CanExecute = nameof(CanInventory))]
    private async Task CompareWithCurrentAsync(HistoryEntryDisplay? item)
    {
        if (item is null || Machines.Count == 0) return;
        var store = HistoryStore;
        var newer = BuildDocument(filteredOnly: false);
        await ShowDiffAsync(
            () => store.LoadDocument(item.Entry), item.TimeText,
            () => newer, "current grid");
    }

    /// <summary>History context menu: this saved run vs the next older one in the list.</summary>
    [RelayCommand]
    private async Task CompareWithPreviousAsync(HistoryEntryDisplay? item)
    {
        if (item is null) return;
        var index = History.IndexOf(item);
        var previous = index >= 0 && index + 1 < History.Count ? History[index + 1] : null;
        if (previous is null) { StatusLine = "No older saved scan to compare against."; return; }
        var store = HistoryStore;
        await ShowDiffAsync(
            () => store.LoadDocument(previous.Entry), previous.TimeText,
            () => store.LoadDocument(item.Entry), item.TimeText);
    }

    private async Task ShowDiffAsync(Func<ScanDocument> older, string olderName, Func<ScanDocument> newer, string newerName)
    {
        try
        {
            var diff = await Task.Run(() => Marco.Export.Diff.ScanDiffEngine.Compute(older(), newer()));
            var window = new ScanDiffWindow(new ScanDiffViewModel(diff, olderName, newerName))
            {
                Owner = Application.Current.MainWindow,
            };
            window.Show();
            StatusLine = $"Compared {olderName} → {newerName}: {diff.Summary}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not compare the scans:\n{ex.Message}",
                "Compare scans", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
