using CommunityToolkit.Mvvm.ComponentModel;
using Marco.Core.Inventory;

namespace Marco.App.ViewModels;

/// <summary>One row of the "Inventory collectors" checklist in the left panel: a catalogue entry plus the
/// operator's on/off choice. Toggling raises <see cref="Changed"/> so the owner can refresh its summary and
/// persist the overrides.</summary>
public sealed partial class CollectorOption : ObservableObject
{
    public CollectorInfo Info { get; }
    public string Name => Info.Name;
    public string DisplayName => Info.DisplayName;
    public string Description => Info.Description;

    /// <summary>"Windows" / "Linux" / "Printers" / "Windows, Linux" / "" — shown as a small tag when a collector
    /// does not apply to every platform.</summary>
    public string PlatformTag
    {
        get
        {
            if (Info.Windows && Info.Linux) return ""; // applies to computers generally — no tag, as before
            var parts = new List<string>();
            if (Info.Windows) parts.Add("Windows");
            if (Info.Linux) parts.Add("Linux");
            if (Info.Printer && Info.NetworkDevice) parts.Add("Printers & network");
            else if (Info.Printer) parts.Add("Printers");
            else if (Info.NetworkDevice) parts.Add("Network devices");
            return string.Join(", ", parts);
        }
    }

    public string ToolTipText => Description + (PlatformTag.Length > 0 ? $" ({PlatformTag} only)" : "")
                                 + (Info.DefaultEnabled ? "" : " Off by default.");

    [ObservableProperty] private bool _isEnabled;

    public event Action? Changed;

    public CollectorOption(CollectorInfo info, bool enabled)
    {
        Info = info;
        _isEnabled = enabled;
    }

    partial void OnIsEnabledChanged(bool value) => Changed?.Invoke();
}
