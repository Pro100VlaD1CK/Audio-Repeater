using System.Windows;

namespace EchoBridge;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Services.LoggingService.Write("Unhandled UI exception", args.Exception);
            MessageBox.Show("An unexpected error occurred. See the local log for details.", "EchoBridge", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Services.LoggingService.Write("Unhandled process exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Services.LoggingService.Write("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        Services.LoggingService.Write("Application started");
    }
}
