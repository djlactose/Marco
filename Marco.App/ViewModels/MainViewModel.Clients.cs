using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marco.App.Views;
using Marco.Core.Clients;

namespace Marco.App.ViewModels;

/// <summary>One row of the client dropdown; a null <see cref="Profile"/> is the "(No client)" choice.</summary>
public sealed class ClientChoice
{
    public ClientProfile? Profile { get; }
    public string Display => Profile?.Name ?? "(No client)";
    public ClientChoice(ClientProfile? profile) => Profile = profile;
    public override string ToString() => Display;
}

public partial class MainViewModel
{
    private ClientProfileStore? _clientStore;
    private ClientProfileStore ClientStore => _clientStore ??= new ClientProfileStore(_paths.ClientsFile);

    /// <summary>Dropdown choices: "(No client)" plus every profile, name order.</summary>
    public ObservableCollection<ClientChoice> ClientChoices { get; } = new();

    [ObservableProperty] private ClientChoice? _selectedClientChoice;

    public ClientProfile? ActiveClient => SelectedClientChoice?.Profile;

    /// <summary>True while programmatic selection (startup restore, list refresh) is in flight — the change
    /// hook must not prompt about targets or persist settings during those.</summary>
    private bool _restoringClient;

    private void LoadClients(string? activeClientId)
    {
        _restoringClient = true;
        try
        {
            ClientChoices.Clear();
            ClientChoices.Add(new ClientChoice(null));
            foreach (var profile in ClientStore.Load())
                ClientChoices.Add(new ClientChoice(profile));
            SelectedClientChoice = ClientChoices.FirstOrDefault(c =>
                string.Equals(c.Profile?.Id, activeClientId, StringComparison.OrdinalIgnoreCase))
                ?? ClientChoices[0];
        }
        finally { _restoringClient = false; }
    }

    partial void OnSelectedClientChoiceChanged(ClientChoice? value)
    {
        _baselineStore = null; // baseline is per-client (baseline-{id}.json)
        if (_restoringClient || value is null) return;

        if (value.Profile is { } profile && !string.IsNullOrWhiteSpace(profile.TargetsText)
            && !string.Equals(profile.TargetsText.Trim(), TargetsText.Trim(), StringComparison.Ordinal))
        {
            bool overwrite = string.IsNullOrWhiteSpace(TargetsText)
                || MessageBox.Show($"Replace the current targets with {profile.Name}'s saved targets?",
                       "Switch client", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (overwrite) TargetsText = profile.TargetsText;
        }
        StatusLine = value.Profile is { } p ? $"Client: {p.Name}." : "No client selected.";
        SaveSettings();
        EvaluateBaseline(); // repaint against this client's baseline
    }

    /// <summary>The current targets box becomes this client's saved targets.</summary>
    [RelayCommand]
    private void SaveTargetsToClient()
    {
        if (ActiveClient is not { } profile) { StatusLine = "Select a client first."; return; }
        var updated = profile with { TargetsText = TargetsText };
        ClientStore.Upsert(updated);
        LoadClients(updated.Id);
        StatusLine = $"Saved targets to {updated.Name}.";
    }

    [RelayCommand]
    private void OpenClientManager()
    {
        var window = new ClientManagerWindow(ClientStore, _paths) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        if (window.Changed)
            LoadClients(ActiveClient?.Id);
    }
}
