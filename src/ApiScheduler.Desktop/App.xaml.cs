using System.Windows;
using System.Windows.Threading;
using ApiScheduler.Core;

namespace ApiScheduler.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        try
        {
            AppPaths.EnsureCreated();
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show($"Access denied to {AppPaths.Root}.\nRun the app as Administrator.", "ApiScheduler", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
