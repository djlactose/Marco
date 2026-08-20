using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marco.App.Views;
using Marco.Core.Compliance;

namespace Marco.App.ViewModels;

public partial class MainViewModel
{
    private IReadOnlyList<RuleDefinition>? _rules;
    private Dictionary<string, bool>? _complianceOverrides;

    /// <summary>"Fleet 84% · 2 critical" — the status-bar segment doubling as the Fleet health button;
    /// null (no evaluated machines) hides it.</summary>
    [ObservableProperty] private string? _fleetComplianceText;

    /// <summary>Effective rule set: embedded defaults + user packs + operator toggles, loaded lazily and
    /// invalidated when the toggles change. Loader warnings land in the run log, never in the operator's face.</summary>
    private IReadOnlyList<RuleDefinition> ComplianceRules
        => _rules ??= RulePackLoader.LoadEffectiveRules(_paths.ComplianceDirectory, _complianceOverrides,
            w => _runLog.Note($"Compliance: {w}"));

    /// <summary>Evaluate every machine in the grid against the current rules and refresh the fleet segment.
    /// Reads happen on a worker (machines are quiescent when this runs), assignment on the UI thread.</summary>
    [RelayCommand]
    private async Task EvaluateComplianceAsync()
    {
        if (Machines.Count == 0) { FleetComplianceText = null; return; }
        var machines = Machines.ToList();
        var rules = ComplianceRules;
        // Lifecycle first: the os-supported rule reads Machine.Lifecycle.
        var evaluated = await Task.Run(() =>
        {
            var eol = Marco.Core.Lifecycle.OsEolTable.Data;
            var today = DateTime.Today;
            return machines.Select(m =>
            {
                var lifecycle = Marco.Core.Lifecycle.LifecycleEvaluator.Evaluate(m, eol, today);
                m.Lifecycle = lifecycle; // scalar assign; INPC raise is safe off-thread like Status
                return ComplianceEvaluator.Evaluate(m, rules);
            }).ToList();
        });
        for (int i = 0; i < machines.Count; i++)
            machines[i].Compliance = evaluated[i];

        var fleet = ComplianceEvaluator.Summarize(machines);
        FleetComplianceText = fleet.EvaluatedMachines == 0 ? null
            : (fleet.AverageScore is { } avg ? $"Fleet {avg}%" : "Fleet —")
              + (fleet.CriticalFailures > 0 ? $" · {fleet.CriticalFailures} critical" : "");
    }

    [RelayCommand]
    private async Task OpenFleetHealthAsync()
    {
        if (Machines.Count == 0) return;
        var fleet = ComplianceEvaluator.Summarize(Machines);
        var window = new FleetHealthWindow(fleet, ComplianceRules) { Owner = Application.Current.MainWindow };
        window.ShowDialog();

        if (!window.RulesChanged) return;
        // Persist only the deltas from pack defaults (the CollectorOverrides pattern), then re-evaluate.
        _complianceOverrides = RulePackLoader.OverridesFor(RulePackLoader.LoadDefaultPack().Rules, window.EnabledMap);
        _rules = null;
        SaveSettings();
        await EvaluateComplianceAsync();
    }
}
