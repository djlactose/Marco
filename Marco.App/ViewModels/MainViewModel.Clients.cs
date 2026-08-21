using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marco.App.Views;
using Marco.Core.Clients;
using Marco.Core.Inventory;
using Marco.Core.Scanning;
using Marco.Core.Storage;

namespace Marco.App.ViewModels;

/// <summary>One row of the client dropdown; a null <see cref="Profile"/> is the "(No client)" choice, which is
/// itself a saved profile (its scan config lives in settings.json).</summary>
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

    /// <summary>The client combo is disabled during a run — switching clears the grid, which mustn't happen mid-scan.</summary>
    public bool ClientSwitchEnabled => !IsRunning;

    /// <summary>Id of the profile whose scan config is currently live in the VM (null = "(No client)"). Distinct
    /// from the newly-selected choice, so the switch handler can persist the OLD profile before loading the new.</summary>
    private string? _activeProfileId;

    /// <summary>The "(No client)" profile's scan config, preserved so a client being active never overwrites it in
    /// settings.json. Seeded from AppSettings; updated whenever "(No client)" is the active profile.</summary>
    private ScanConfig _noClientConfig = ScanConfig.Defaults;

    /// <summary>True while a client selection is being restored/refreshed programmatically — the switch handler
    /// must not clear the grid or persist during those.</summary>
    private bool _restoringClient;

    /// <summary>The full per-profile scan configuration: targets, discovery toggles, concurrency, collectors.</summary>
    private sealed record ScanConfig(
        string TargetsText, int Concurrency, bool Icmp, bool Tcp, bool Classify, bool Names, bool Mac,
        bool IncludeUnreachable, bool AutoInv, bool Group, Dictionary<string, bool>? Collectors)
    {
        public static ScanConfig Defaults => new("", 32, true, true, true, true, true, false, false, true, null);
    }

    private ScanConfig CaptureScanConfig() => new(
        TargetsText, Concurrency, IcmpEnabled, TcpFallback, Classification, ResolveNames, ResolveMac,
        IncludeUnreachable, AutoInventory, GroupByBlock, CollectorCatalog.OverridesFor(EnabledCollectorNames()));

    private void ApplyScanConfig(ScanConfig c)
    {
        TargetsText = c.TargetsText;
        Concurrency = c.Concurrency;
        IcmpEnabled = c.Icmp;
        TcpFallback = c.Tcp;
        Classification = c.Classify;
        ResolveNames = c.Names;
        ResolveMac = c.Mac;
        IncludeUnreachable = c.IncludeUnreachable;
        AutoInventory = c.AutoInv;
        GroupByBlock = c.Group;
        BuildCollectorOptions(c.Collectors);
    }

    private static ScanConfig FromClient(ClientProfile p) => new(
        p.TargetsText, p.Concurrency, p.IcmpEnabled, p.TcpFallback, p.Classification, p.ResolveNames,
        p.ResolveMac, p.IncludeUnreachable, p.AutoInventory, p.GroupByBlock, p.CollectorOverrides);

    private static ClientProfile WithScanConfig(ClientProfile p, ScanConfig c) => p with
    {
        TargetsText = c.TargetsText, Concurrency = c.Concurrency, IcmpEnabled = c.Icmp, TcpFallback = c.Tcp,
        Classification = c.Classify, ResolveNames = c.Names, ResolveMac = c.Mac,
        IncludeUnreachable = c.IncludeUnreachable, AutoInventory = c.AutoInv, GroupByBlock = c.Group,
        CollectorOverrides = c.Collectors,
    };

    /// <summary>Seed <see cref="_noClientConfig"/> from settings.json (the "(No client)" profile).</summary>
    private void SeedNoClientConfig(AppSettings s) => _noClientConfig = new ScanConfig(
        s.TargetsText, Math.Clamp(s.Concurrency, 1, ConcurrencyLimits.Max), s.IcmpEnabled, s.TcpFallback,
        s.Classification, s.ResolveNames, s.ResolveMac, s.IncludeUnreachable, s.AutoInventory, s.GroupByBlock,
        s.CollectorOverrides);

    /// <summary>Save the current live scan config to whichever profile is active. Best-effort; called from the
    /// switch handler and SaveSettings.</summary>
    private void PersistScanConfigToActiveProfile()
    {
        var cfg = CaptureScanConfig();
        if (_activeProfileId is null) { _noClientConfig = cfg; return; }
        try
        {
            var existing = ClientStore.Load().FirstOrDefault(c => string.Equals(c.Id, _activeProfileId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) ClientStore.Upsert(WithScanConfig(existing, cfg));
        }
        catch (Exception ex) { _runLog.Note($"Client scan-config save failed: {ex.Message}"); }
    }

    /// <summary>(Re)build the dropdown and reselect, without applying config or firing the switch behaviour.</summary>
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
                string.Equals(c.Profile?.Id, activeClientId, StringComparison.OrdinalIgnoreCase)) ?? ClientChoices[0];
            _activeProfileId = SelectedClientChoice?.Profile?.Id;
        }
        finally { _restoringClient = false; }
    }

    /// <summary>At startup, after LoadClients, apply the restored active client's scan config (a real client
    /// overrides the "(No client)" values ApplySettings seeded).</summary>
    private void ApplyActiveClientConfigAtStartup()
    {
        if (ActiveClient is { } client) ApplyScanConfig(FromClient(client));
    }

    partial void OnSelectedClientChoiceChanged(ClientChoice? value)
    {
        _baselineStore = null; // baseline is per-client (baseline-{id}.json)
        if (_restoringClient || value is null) return;

        if (IsRunning)
        {
            // Can't switch mid-scan (it clears the grid); revert the selection.
            StatusLine = "Finish or stop the scan before switching clients.";
            _restoringClient = true;
            SelectedClientChoice = ClientChoices.FirstOrDefault(c =>
                string.Equals(c.Profile?.Id, _activeProfileId, StringComparison.OrdinalIgnoreCase)) ?? ClientChoices[0];
            _restoringClient = false;
            return;
        }

        // 1. Persist the OLD profile's live scan config before we replace it.
        PersistScanConfigToActiveProfile();

        // 2. Clear the grid — a client switch is a fresh context.
        ClearGridForClientSwitch();

        // 3. Load and apply the NEW profile's scan config.
        ApplyScanConfig(value.Profile is { } client ? FromClient(client) : _noClientConfig);

        // 4. Commit the switch and refresh the per-client views.
        _activeProfileId = value.Profile?.Id;
        StatusLine = value.Profile is { } p ? $"Switched to client '{p.Name}'." : "Switched to (No client).";
        SaveSettings();
        _ = RefreshHistoryAsync();
        EvaluateBaseline();
    }

    /// <summary>Clear the results (grid, selection, counts, per-run overlays) on a client switch — but not the
    /// client selection or the just-applied targets.</summary>
    private void ClearGridForClientSwitch()
    {
        CloseDetailWindows();
        Machines.Clear();
        SelectedMachine = null;
        _currentRunId = null;
        HasDoctorFindings = false;
        FleetComplianceText = null;
        UnknownDevicesText = null;
        AliveCount = UnreachableCount = TotalCount = 0;
        ProgressFraction = 0;
        InventoryAliveCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
        GenerateReportCommand.NotifyCanExecuteChanged();
        WakeMissingCommand.NotifyCanExecuteChanged();
        RefreshWakeMissing();
    }

    /// <summary>The 💾 button. With a client active, commit the current scan settings to it (though they already
    /// auto-persist on switch/exit). With "(No client)" active, prompt for a name and promote the current
    /// settings into a new client, adopting it as active without disturbing the grid.</summary>
    [RelayCommand]
    private void SaveTargetsToClient()
    {
        if (ActiveClient is { } profile)
        {
            SaveSettings();          // persists the active client's scan config + settings.json
            LoadClients(profile.Id); // refresh the dropdown from disk
            StatusLine = $"Saved scan settings to {profile.Name}.";
            return;
        }

        // "(No client)": turn the current working settings into a named client.
        var prompt = new TextPromptDialog("New client",
            "Save the current scan settings (targets, discovery options, concurrency, collectors) as a new client named:")
        {
            Owner = Application.Current.MainWindow,
        };
        if (prompt.ShowDialog() != true) return;
        var name = prompt.Value.Trim();
        if (name.Length == 0) { StatusLine = "No name entered — client not created."; return; }

        var created = WithScanConfig(ClientProfile.New(name), CaptureScanConfig());
        ClientStore.Upsert(created);
        // Adopt it as active WITHOUT clearing the grid — nothing contextual changed, the settings simply gained a
        // name. LoadClients reselects under the _restoringClient guard, so the switch handler doesn't fire.
        LoadClients(created.Id);
        _baselineStore = null;
        SaveSettings();
        _ = RefreshHistoryAsync();
        EvaluateBaseline();

        int adopted = AdoptSharedLoginsInto(created.Id);
        StatusLine = adopted > 0
            ? $"Saved current settings and {adopted} login(s) as client '{name}'."
            : $"Saved current settings as client '{name}'.";
    }

    /// <summary>Offer to reassign the shared (unassigned) logins to the new client, so promoting a working
    /// context into a client also takes the logins it was using. Shared credentials otherwise apply to every
    /// client, so this is a deliberate, confirmed narrowing. Skipped while a scan is running (mutating a
    /// credential a connect may be using is unsafe). Returns how many were reassigned.</summary>
    private int AdoptSharedLoginsInto(string clientId)
    {
        if (IsRunning) return 0;
        int sharedCount = _credentials.Sets.Count(s => s.ClientId is null);
        if (sharedCount == 0) return 0;

        var answer = MessageBox.Show(
            $"Assign your {sharedCount} shared login(s) to this client too?\n\n"
            + "They will then be tried only when this client is selected — right now they apply to every client.",
            "Assign logins", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return 0;

        SyncCredentialsFromDisk(); // pick up another window's edits before we retag + save
        var shared = _credentials.Sets.Where(s => s.ClientId is null).ToList();
        foreach (var set in shared) set.ClientId = clientId;
        SaveCredentials();

        Credentials.Clear();
        foreach (var set in _credentials.Sets)
            Credentials.Add(new CredentialDisplay(set));
        return shared.Count;
    }

    [RelayCommand]
    private void OpenClientManager()
    {
        SaveSettings(); // flush the live config to the active profile so the manager sees current targets/settings
        var window = new ClientManagerWindow(ClientStore, _paths) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        if (!window.Changed) return;

        var keep = ActiveClient?.Id;
        LoadClients(keep);
        // Pick up target/branding edits the manager made to the still-active client.
        if (ActiveClient is { } c) ApplyScanConfig(FromClient(c));
    }
}
