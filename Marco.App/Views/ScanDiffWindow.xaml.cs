using System.Windows;
using Marco.App.ViewModels;

namespace Marco.App.Views;

public partial class ScanDiffWindow : Window
{
    public ScanDiffWindow(ScanDiffViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
