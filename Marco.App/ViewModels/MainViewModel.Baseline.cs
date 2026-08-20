using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marco.Core.Baseline;
using Marco.Core.Model;

namespace Marco.App.ViewModels;

public partial class MainViewModel
{
    private BaselineStore? _baselineStore;
    private BaselineStore BaselineStore => _baselineStore ??= new BaselineStore(_paths.BaselineFile);

    /// <summary>Raised after every baseline evaluation — the seam a future notifications feature consumes.</summary>
    public event EventHandler<BaselineSummary>? BaselineEvaluated;

    /// <summary>"3 unknown devices" for the amber status-bar chip; null (no baseline / nothing unknown) hides it.</summary>
    [ObservableProperty] private string? _unknownDevicesText;

    /// <summary>Paint the grid against the blessed baseline. Runs after discovery completes and again after
    /// inventory (a serial arriving upgrades an UnknownWeak randomized-MAC device to Known). Quiet by design
    /// when no baseline has been blessed yet.</summary>
    private void EvaluateBaseline()
    {
        if (Machines.Count == 0) { UnknownDevicesText = null; return; }
        if (BaselineStore.Load() is not { } baseline) { UnknownDevicesText = null; return; }

        var summary = BaselineEvaluator.Evaluate(Machines.ToList(), baseline);
        int flagged = summary.Unknown + summary.UnknownWeak;
        UnknownDevicesText = flagged == 0 ? null : $"{flagged} unknown device{(flagged == 1 ? "" : "s")}";
        _runLog.Baseline("evaluated", summary.Known, flagged);
        BaselineEvaluated?.Invoke(this, summary);
    }

    private bool CanBless() => !IsRunning && Machines.Count > 0;

    /// <summary>Grid context menu: the current results become THE baseline (replacing any previous one).</summary>
    [RelayCommand(CanExecute = nameof(CanBless))]
    private void BlessCurrentAsBaseline()
    {
        var answer = MessageBox.Show(
            $"Use the current {Machines.Count} device(s) as the known-device baseline?\n\n"
            + "Future scans will flag devices not in this set as NEW. This replaces any previous baseline.",
            "Bless baseline", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        var baseline = BaselineEvaluator.Build(Machines, Environment.UserName, _currentRunId);
        BaselineStore.Replace(baseline);
        _runLog.Baseline("blessed", baseline.Entries.Count, 0);
        StatusLine = $"Baseline blessed: {baseline.Entries.Count} known device(s).";
        EvaluateBaseline();
    }

    /// <summary>Grid context menu on a NEW row: add just this device to the baseline (reload-merge-save, so
    /// concurrent windows' trusts survive each other).</summary>
    [RelayCommand]
    private void TrustDevice(Machine? machine)
    {
        if (machine is null) return;
        if (machine.BaselineStatus == BaselineStatus.UnknownWeak
            && Marco.Core.Model.HardwareIdentity.NormalizeSerial(machine.System.SerialNumber) is null)
        {
            var answer = MessageBox.Show(
                $"{machine.DisplayName} only shows weak identity (randomized MAC or bare address). "
                + "Inventorying it first would record its serial number and make the trust durable.\n\nTrust it anyway?",
                "Trust device", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }
        BaselineStore.AddEntries(new[] { BaselineEvaluator.ToEntry(machine, "Trusted") }, Environment.UserName);
        _runLog.Baseline("trusted", 1, 0);
        StatusLine = $"Trusted {machine.DisplayName}.";
        EvaluateBaseline();
    }

    [RelayCommand]
    private void TrustAllUnknown()
    {
        var unknown = Machines.Where(m => m.BaselineStatus is BaselineStatus.Unknown or BaselineStatus.UnknownWeak).ToList();
        if (unknown.Count == 0) { StatusLine = "Nothing unknown to trust."; return; }
        BaselineStore.AddEntries(unknown.Select(m => BaselineEvaluator.ToEntry(m, "Trusted")), Environment.UserName);
        _runLog.Baseline("trusted", unknown.Count, 0);
        StatusLine = $"Trusted {unknown.Count} device(s).";
        EvaluateBaseline();
    }

    /// <summary>History context menu: a saved run becomes the baseline (e.g. "last month's audit is the truth").</summary>
    [RelayCommand]
    private async Task BlessHistoryEntryAsBaselineAsync(HistoryEntryDisplay? item)
    {
        if (item is null) return;
        var answer = MessageBox.Show(
            $"Use the saved scan from {item.TimeText} as the known-device baseline?\n\nThis replaces any previous baseline.",
            "Bless baseline", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            var store = HistoryStore;
            var doc = await Task.Run(() => store.LoadDocument(item.Entry));
            var baseline = BaselineEvaluator.Build(doc.ToMachines(), Environment.UserName, item.Entry.Id);
            BaselineStore.Replace(baseline);
            _runLog.Baseline("blessed", baseline.Entries.Count, 0);
            StatusLine = $"Baseline blessed from {item.TimeText}: {baseline.Entries.Count} known device(s).";
            EvaluateBaseline();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not bless that scan:\n{ex.Message}", "Bless baseline",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
