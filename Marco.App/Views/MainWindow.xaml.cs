using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Marco.App.ViewModels;

namespace Marco.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnMachineRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Walks from wherever the click landed up to the row; group-expander headers and column headers
        // terminate at null, so only a real machine row opens the full-screen view.
        var node = e.OriginalSource as DependencyObject;
        while (node is not null and not DataGridRow)
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        if (node is DataGridRow { DataContext: Marco.Core.Model.Machine m }
            && DataContext is MainViewModel vm
            && vm.OpenMachineDetailCommand.CanExecute(m))
        {
            vm.OpenMachineDetailCommand.Execute(m);
        }
    }

    private void OnHistoryListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        var node = e.OriginalSource as DependencyObject;
        while (node is not null and not ListBoxItem)
        {
            if (node is Button) return; // the ✕ delete button — not an open gesture
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        if (node is ListBoxItem { DataContext: HistoryEntryDisplay entry }
            && list.DataContext is MainViewModel vm
            && vm.OpenHistoryEntryCommand.CanExecute(entry))
        {
            vm.OpenHistoryEntryCommand.Execute(entry);
        }
    }

    private void OnCredentialListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        var node = e.OriginalSource as DependencyObject;
        while (node is not null and not ListBoxItem)
        {
            if (node is Button) return; // the ✕ remove button — not an edit gesture
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        if (node is ListBoxItem { DataContext: CredentialDisplay display }
            && list.DataContext is MainViewModel vm
            && vm.EditCredentialCommand.CanExecute(display))
        {
            vm.EditCredentialCommand.Execute(display);
        }
    }
}
