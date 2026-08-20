using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marco.App.Services;
using Marco.Core.Actions;
using Marco.Core.Model;
using Marco.Discovery.Wol;

namespace Marco.App.ViewModels;

/// <summary>One entry in the row context menu.</summary>
public sealed class HostActionItem
{
    public string Header { get; }
    public HostActionKind Kind { get; }
    public HostActionItem(HostActionKind kind) { Kind = kind; Header = HostActions.Label(kind); }
}

public partial class MainViewModel
{
    private readonly WolSender _wol = new();

    /// <summary>Contextual actions for the selected host; rebuilt when the selection changes.</summary>
    public ObservableCollection<HostActionItem> HostActionItems { get; } = new();

    /// <summary>"Wake missing (3)" for the toolbar; null when nothing is wakeable.</summary>
    [ObservableProperty] private string? _wakeMissingText;

    private void RebuildHostActions()
    {
        HostActionItems.Clear();
        if (SelectedMachine is { } m)
            foreach (var kind in HostActionRules.AvailableFor(m))
                HostActionItems.Add(new HostActionItem(kind));
    }

    private void RefreshWakeMissing()
    {
        int wakeable = Machines.Count(m => !m.IsAlive && m.PrimaryMac is not null);
        WakeMissingText = wakeable == 0 ? null : $"Wake missing ({wakeable})";
    }

    [RelayCommand]
    private async Task RunHostAction(HostActionItem? item)
    {
        if (item is null || SelectedMachine is not { } m) return;
        if (item.Kind == HostActionKind.Wake) { await WakeAsync(new[] { m }); return; }
        StatusLine = HostActions.Launch(item.Kind, m);
    }

    private bool CanWakeMissing() => !IsRunning && Machines.Any(m => !m.IsAlive && m.PrimaryMac is not null);

    [RelayCommand(CanExecute = nameof(CanWakeMissing))]
    private Task WakeMissing()
    {
        var asleep = Machines.Where(m => !m.IsAlive && m.PrimaryMac is not null).ToList();
        if (asleep.Count == 0) return Task.CompletedTask;
        var answer = MessageBox.Show(
            $"Send Wake-on-LAN packets to {asleep.Count} host(s)?\n\n"
            + "Magic packets are broadcast on this segment; hosts on other routed subnets wake only if your "
            + "routers forward directed broadcasts (most don't by default).",
            "Wake missing", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return Task.CompletedTask;
        return WakeAsync(asleep);
    }

    private async Task WakeAsync(IReadOnlyList<Machine> machines)
    {
        var woken = new List<Machine>();
        foreach (var m in machines)
        {
            if (m.PrimaryMac is not { } mac) continue;
            try
            {
                var dests = _wol.Send(mac, m.TargetBlock);
                _runLog.WolSent(m.Address, mac, dests);
                woken.Add(m);
            }
            catch (Exception ex)
            {
                _runLog.Note($"WoL to {m.Address} failed: {ex.Message}");
            }
        }
        if (woken.Count == 0) { StatusLine = "No hosts could be woken (no usable MAC)."; return; }
        StatusLine = $"Sent Wake-on-LAN to {woken.Count} host(s); rechecking shortly…";

        // Give the hosts time to boot, then probe just those addresses and update their rows in place (never the
        // whole scan pipeline — that would clear the grid).
        await Task.Delay(TimeSpan.FromSeconds(45));
        int now = 0;
        var probe = new Marco.Discovery.LivenessProbe();
        var settings = BuildSettings();
        foreach (var m in woken)
        {
            try
            {
                var result = await probe.ProbeAsync(m.Address, settings, CancellationToken.None);
                if (result.IsAlive)
                {
                    m.IsAlive = true;
                    if (m.Status is MachineStatus.Unreachable or MachineStatus.Pending) m.Status = MachineStatus.Alive;
                    now++;
                }
            }
            catch { /* recheck is best-effort */ }
        }
        RefreshWakeMissing();
        StatusLine = $"Woke {woken.Count} host(s); {now} responded on recheck.";
    }
}
