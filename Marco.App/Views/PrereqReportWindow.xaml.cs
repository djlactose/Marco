using System.Text;
using System.Windows;
using Marco.Core.Diagnosis;

namespace Marco.App.Views;

/// <summary>One diagnosed cause across the fleet, shaped for the report's ItemsControl.</summary>
public sealed record PrereqGroupDisplay(string Header, string Explanation, string HostsHeader, string Hosts, string? FixScript);

/// <summary>The fleet rollup: failed hosts grouped by diagnosed cause, one copyable fix block per cause.</summary>
public partial class PrereqReportWindow : Window
{
    public IReadOnlyList<PrereqGroupDisplay> Groups { get; }
    public string SummaryText { get; }

    private readonly string _allText;

    public PrereqReportWindow(IReadOnlyList<PrereqCauseGroup> groups)
    {
        Groups = groups.Select(g => new PrereqGroupDisplay(
            $"{g.Machines.Count} host{(g.Machines.Count == 1 ? "" : "s")} — {g.Title}",
            PrereqDoctor.Diagnose(g.Machines[0]).Explanation,
            $"Show the {g.Machines.Count} host{(g.Machines.Count == 1 ? "" : "s")}",
            string.Join(", ", g.Machines.Select(m => m.DisplayName)),
            g.FixScript)).ToList();

        int hosts = groups.Sum(g => g.Machines.Count);
        SummaryText = $"{hosts} host{(hosts == 1 ? "" : "s")} failed inventory, {groups.Count} distinct cause{(groups.Count == 1 ? "" : "s")}";

        var sb = new StringBuilder().AppendLine($"Marco prerequisite doctor — {DateTime.Now:g}").AppendLine();
        foreach (var g in Groups)
        {
            sb.AppendLine(g.Header).AppendLine(g.Explanation).AppendLine($"Hosts: {g.Hosts}");
            if (g.FixScript is not null) sb.AppendLine().AppendLine(g.FixScript);
            sb.AppendLine();
        }
        _allText = sb.ToString();

        InitializeComponent();
        DataContext = this;
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_allText); }
        catch { /* clipboard briefly owned elsewhere */ }
    }
}
