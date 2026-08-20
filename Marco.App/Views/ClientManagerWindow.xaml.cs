using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Marco.Core.Clients;
using Marco.Core.Storage;

namespace Marco.App.Views;

/// <summary>Add/edit/delete/share client profiles. Mutates the store directly; the caller reloads its dropdown
/// when <see cref="Changed"/> is set.</summary>
public partial class ClientManagerWindow : Window
{
    public sealed record ClientRow(ClientProfile Profile)
    {
        public string Name => Profile.Name;
        public string Summary
        {
            get
            {
                var targets = Profile.TargetsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var parts = new List<string>
                {
                    targets.Length == 0 ? "no targets" : $"{targets.Length} target line{(targets.Length == 1 ? "" : "s")}",
                };
                if (Profile.CompanyName is not null) parts.Add(Profile.CompanyName);
                if (Profile.LogoPath is not null) parts.Add("logo");
                return string.Join(" · ", parts);
            }
        }
    }

    private readonly ClientProfileStore _store;
    private readonly AppPaths _paths;

    public bool Changed { get; private set; }

    public ClientManagerWindow(ClientProfileStore store, AppPaths paths)
    {
        _store = store;
        _paths = paths;
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        ClientList.ItemsSource = _store.Load().Select(p => new ClientRow(p)).ToList();
    }

    private ClientProfile? Selected => (ClientList.SelectedItem as ClientRow)?.Profile;

    private void OnNew(object sender, RoutedEventArgs e)
    {
        var dialog = new ClientProfileDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } profile) return;
        _store.Upsert(profile);
        Changed = true;
        Refresh();
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected) return;
        var dialog = new ClientProfileDialog(selected) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } profile) return;
        _store.Upsert(profile);
        Changed = true;
        Refresh();
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => OnEdit(sender, e);

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected) return;
        var answer = MessageBox.Show(
            $"Delete client '{selected.Name}'?\n\nCredentials assigned to it stay in the credential store (as that client id) until you edit or remove them.",
            "Delete client", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _store.Delete(selected.Id);
        Changed = true;
        Refresh();
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export client profile",
            Filter = $"Marco client (*{ClientProfileSharing.Extension})|*{ClientProfileSharing.Extension}",
            FileName = selected.Name + ClientProfileSharing.Extension,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            ClientProfileSharing.Export(selected, dialog.FileName);
            MessageBox.Show("Exported. The file carries targets and branding only — the recipient assigns their own credentials.",
                "Export client", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export client", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import client profile",
            Filter = $"Marco client (*{ClientProfileSharing.Extension})|*{ClientProfileSharing.Extension}|JSON (*.json)|*.json",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var profile = ClientProfileSharing.Import(dialog.FileName, _paths.LogosDirectory);
            var existing = _store.Load().FirstOrDefault(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var answer = MessageBox.Show(
                    $"A client with this identity already exists ('{existing.Name}').\n\nYes = update it with the imported version.\nNo = import as a separate copy.",
                    "Import client", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel) return;
                if (answer == MessageBoxResult.No)
                    profile = profile with { Id = Guid.NewGuid().ToString("n"), Name = profile.Name + " (copy)" };
            }
            _store.Upsert(profile);
            Changed = true;
            Refresh();
            MessageBox.Show($"Imported '{profile.Name}'. No credentials came with it — assign yours to this client in the credential dialog.",
                "Import client", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not import the client file:\n{ex.Message}", "Import client",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
