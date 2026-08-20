using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marco.Export.Diff;

namespace Marco.App.ViewModels;

/// <summary>One row of the diff grid: a field change, or an added/removed machine as a pseudo-row. Flat and
/// string-shaped so the DataGrid can sort/filter it without converters.</summary>
public sealed class DiffRowDisplay
{
    public string Machine { get; }
    /// <summary>How the pair was identified — "serial" / "MAC" / "MAC?" (randomized) / "addr+name"; empty for
    /// added/removed machines, which by definition matched nothing.</summary>
    public string MatchTag { get; }
    public string Category { get; }
    public string Item { get; }
    public string Change { get; }
    public string OldValue { get; }
    public string NewValue { get; }
    public string Severity { get; }

    public DiffRowDisplay(MachineRef machine, FieldChange change)
    {
        Machine = machine.DisplayName;
        MatchTag = machine.MatchedBy switch
        {
            MatchKind.Serial => "serial",
            MatchKind.Mac => "MAC",
            MatchKind.WeakMac => "MAC?",
            MatchKind.AddressName => "addr+name",
            _ => "",
        };
        Category = change.Category.ToString();
        Item = change.Item;
        Change = change.Kind.ToString();
        OldValue = change.OldValue ?? "";
        NewValue = change.NewValue ?? "";
        Severity = change.Severity.ToString();
    }

    public DiffRowDisplay(MachineRef machine, bool added)
    {
        Machine = machine.DisplayName;
        MatchTag = "";
        Category = nameof(DiffCategory.Identity);
        Item = "Machine";
        Change = added ? "Added" : "Removed";
        OldValue = added ? "" : machine.DisplayName;
        NewValue = added ? machine.DisplayName : "";
        Severity = nameof(DiffSeverity.Notable);
    }
}

public partial class ScanDiffViewModel : ObservableObject
{
    private readonly ScanDiffResult _diff;

    public string HeaderSummary => _diff.Summary;
    public string SourcesText { get; }
    public ObservableCollection<DiffRowDisplay> Rows { get; } = new();
    public ICollectionView RowsView { get; }

    [ObservableProperty] private string _filterText = "";

    public ScanDiffViewModel(ScanDiffResult diff, string olderName, string newerName)
    {
        _diff = diff;
        SourcesText = $"{olderName} ({diff.Meta.OlderTimestamp:g}, {diff.Meta.OlderMachineCount} machines)  →  "
                    + $"{newerName} ({diff.Meta.NewerTimestamp:g}, {diff.Meta.NewerMachineCount} machines)";

        foreach (var m in diff.Added) Rows.Add(new DiffRowDisplay(m, added: true));
        foreach (var m in diff.Removed) Rows.Add(new DiffRowDisplay(m, added: false));
        foreach (var machine in diff.Changed)
            foreach (var change in machine.Changes)
                Rows.Add(new DiffRowDisplay(machine.Machine, change));

        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DiffRowDisplay.Machine)));
        RowsView.Filter = FilterRow;
    }

    partial void OnFilterTextChanged(string value) => RowsView.Refresh();

    private bool FilterRow(object obj)
    {
        if (obj is not DiffRowDisplay row) return false;
        var f = FilterText?.Trim();
        if (string.IsNullOrEmpty(f)) return true;
        return Has(row.Machine) || Has(row.Category) || Has(row.Item) || Has(row.Change)
            || Has(row.OldValue) || Has(row.NewValue) || Has(row.Severity);

        bool Has(string s) => s.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void ExportJson()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export diff as JSON",
            Filter = "JSON files (*.json)|*.json",
            FileName = $"marco-diff-{DateTime.Now:yyyyMMdd-HHmm}.json",
        };
        if (dialog.ShowDialog() != true) return;
        try { DiffExporters.ExportJson(_diff, dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Export diff", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export diff as CSV",
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"marco-diff-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dialog.ShowDialog() != true) return;
        try { DiffExporters.ExportCsv(_diff, dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Export diff", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
