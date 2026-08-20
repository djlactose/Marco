using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Marco.Core.Compliance;

namespace Marco.App.Views;

/// <summary>Fleet compliance rollup: average score, top failing rules, and the per-rule enable checklist.
/// The caller reads <see cref="RulesChanged"/>/<see cref="EnabledMap"/> after the dialog closes.</summary>
public partial class FleetHealthWindow : Window
{
    public sealed record IssueDisplay(string Severity, Brush SeverityBrush, string Name, string CountText);

    public sealed class RuleToggle
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required string SeverityText { get; init; }
        public bool IsEnabled { get; set; }
    }

    public string SummaryText { get; }
    public string CountsText { get; }
    public IReadOnlyList<IssueDisplay> Issues { get; }
    public bool NoIssues => Issues.Count == 0;
    public IReadOnlyList<RuleToggle> RuleToggles { get; }

    private readonly Dictionary<string, bool> _initialEnabled;

    public FleetHealthWindow(FleetSummary fleet, IReadOnlyList<RuleDefinition> rules)
    {
        SummaryText = fleet.AverageScore is { } avg
            ? $"Fleet compliance: {avg}%"
            : "Fleet compliance: not enough data";
        CountsText = $"{fleet.EvaluatedMachines} machine(s) evaluated · "
            + $"{fleet.CriticalFailures} critical and {fleet.HighFailures} high failures across the fleet";

        Issues = fleet.TopIssues.Take(10).Select(i => new IssueDisplay(
            i.Severity.ToString(), SeverityBrush(i.Severity), i.Name,
            $"{i.MachineCount} machine{(i.MachineCount == 1 ? "" : "s")}")).ToList();

        RuleToggles = rules
            .OrderByDescending(r => r.Severity).ThenBy(r => r.Name)
            .Select(r => new RuleToggle
            {
                Id = r.Id, Name = r.Name, Description = r.Description,
                SeverityText = $"  ({r.Severity})", IsEnabled = r.Enabled,
            }).ToList();
        _initialEnabled = RuleToggles.ToDictionary(t => t.Id, t => t.IsEnabled);

        InitializeComponent();
        DataContext = this;
    }

    public bool RulesChanged { get; private set; }

    public IReadOnlyDictionary<string, bool> EnabledMap
        => RuleToggles.ToDictionary(t => t.Id, t => t.IsEnabled);

    protected override void OnClosing(CancelEventArgs e)
    {
        RulesChanged = RuleToggles.Any(t => _initialEnabled[t.Id] != t.IsEnabled);
        base.OnClosing(e);
    }

    private static Brush SeverityBrush(RuleSeverity s) => s switch
    {
        RuleSeverity.Critical => Brushes.Firebrick,
        RuleSeverity.High => Brushes.Chocolate,
        RuleSeverity.Medium => Brushes.Goldenrod,
        _ => Brushes.Gray,
    };
}
