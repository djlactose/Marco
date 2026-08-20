using System.Windows;
using Marco.Core.Clients;

namespace Marco.App.Views;

public partial class ClientProfileDialog : Window
{
    private readonly ClientProfile? _editing;

    /// <summary>Set when the dialog closes with Save.</summary>
    public ClientProfile? Result { get; private set; }

    public ClientProfileDialog(ClientProfile? editing = null)
    {
        _editing = editing;
        InitializeComponent();
        if (editing is not null)
        {
            Title = "Edit client";
            NameBox.Text = editing.Name;
            TargetsBox.Text = editing.TargetsText;
            CompanyBox.Text = editing.CompanyName ?? "";
            LogoBox.Text = editing.LogoPath ?? "";
            AccentBox.Text = editing.AccentColor ?? "";
            PreparedByBox.Text = editing.PreparedBy ?? "";
            NotesBox.Text = editing.Notes ?? "";
        }
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnBrowseLogo(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a logo",
            Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true) LogoBox.Text = dialog.FileName;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show("The client needs a name.", "Client profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var basis = _editing ?? ClientProfile.New(name);
        Result = basis with
        {
            Name = name,
            TargetsText = TargetsBox.Text,
            CompanyName = Trimmed(CompanyBox.Text),
            LogoPath = Trimmed(LogoBox.Text),
            AccentColor = Trimmed(AccentBox.Text),
            PreparedBy = Trimmed(PreparedByBox.Text),
            Notes = Trimmed(NotesBox.Text),
        };
        DialogResult = true;

        static string? Trimmed(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
