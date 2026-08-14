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
