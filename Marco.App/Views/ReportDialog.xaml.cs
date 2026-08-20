using System.Windows;
using Marco.Core.Clients;

namespace Marco.App.Views;

public partial class ReportDialog : Window
{
    public sealed record ClientChoice(ClientProfile? Profile)
    {
        public string Display => Profile?.Name ?? "(No client — neutral branding)";
    }

    public ClientProfile? SelectedClient => (ClientCombo.SelectedItem as ClientChoice)?.Profile;
    public string ReportTitle => TitleBox.Text.Trim();
    public bool IncludeAppendix => AppendixCheck.IsChecked == true;

    public ReportDialog(IReadOnlyList<ClientProfile> clients, ClientProfile? active, string defaultTitle)
    {
        InitializeComponent();
        var choices = new List<ClientChoice> { new(null) };
        choices.AddRange(clients.Select(c => new ClientChoice(c)));
        ClientCombo.ItemsSource = choices;
        ClientCombo.SelectedItem = choices.FirstOrDefault(c => c.Profile?.Id == active?.Id) ?? choices[0];
        TitleBox.Text = defaultTitle;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
