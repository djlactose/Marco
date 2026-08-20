using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Marco.App.Views;
using Marco.Core.Compliance;
using Marco.Report;

namespace Marco.App.ViewModels;

public partial class MainViewModel
{
    private bool CanGenerateReport() => !IsRunning && Machines.Count > 0;

    /// <summary>Generate a branded HTML assessment report (honours the current filter), write it to the reports
    /// folder, and open it in the browser. Branding comes from the chosen client profile.</summary>
    [RelayCommand(CanExecute = nameof(CanGenerateReport))]
    private void GenerateReport()
    {
        if (Machines.Count == 0) return;

        var clients = ClientStore.Load();
        var defaultTitle = (ActiveClient?.Name is { } n ? n + " — " : "") + "Network Assessment";
        var dialog = new ReportDialog(clients, ActiveClient, defaultTitle) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        var client = dialog.SelectedClient;
        var branding = client is null
            ? ReportBranding.Neutral
            : new ReportBranding(client.CompanyName, client.LogoPath, client.AccentColor, client.PreparedBy);

        var doc = BuildDocument(filteredOnly: true);
        var fleet = ComplianceEvaluator.Summarize(MachinesView.Cast<Marco.Core.Model.Machine>());
        var input = new ReportInput(doc, fleet, branding,
            string.IsNullOrWhiteSpace(dialog.ReportTitle) ? client?.Name : dialog.ReportTitle,
            DateTime.Now, Marco.Core.Lifecycle.OsEolTable.Data.Updated, dialog.IncludeAppendix);

        try
        {
            var html = new HtmlReportBuilder().Build(input);
            var safeName = string.Join("_", (client?.Name ?? "marco").Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(_paths.ReportsDirectory, $"{safeName}-{DateTime.Now:yyyyMMdd-HHmm}.html");
            File.WriteAllText(path, html, new System.Text.UTF8Encoding(false));
            _runLog.Note($"Report generated: {Path.GetFileName(path)} ({doc.Machines.Count} machines).");
            StatusLine = $"Report written to {path}";
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not generate the report:\n{ex.Message}", "Generate report",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
