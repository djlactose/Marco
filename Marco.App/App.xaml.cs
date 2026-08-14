using System.Windows;
using System.Windows.Threading;
using Marco.App.ViewModels;
using Marco.App.Views;

namespace Marco.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var window = new MainWindow { DataContext = new MainViewModel() };
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "Marco", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
