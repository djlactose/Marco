using System.Windows;
using System.Windows.Input;
using Marco.Core.Model;

namespace Marco.App.Views;

/// <summary>Full-screen viewer for one machine — the same detail template as the bottom pane, maximized.
/// Modeless and owned by the main window (so it closes with the app); Esc closes it. One instance per
/// machine, tracked by MainViewModel so a second open just activates the existing window.</summary>
public partial class MachineDetailWindow : Window
{
    public MachineDetailWindow(Machine machine)
    {
        InitializeComponent();
        DataContext = machine;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            return;
        }
        base.OnKeyDown(e);
    }
}
