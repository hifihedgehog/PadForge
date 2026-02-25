using System;
using System.Threading;
using System.Windows;
using ModernWpf;

namespace PadForge
{
    public partial class App : Application
    {
        private Mutex _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            _singleInstanceMutex = new Mutex(true, "PadForge_SingleInstance", out bool isNewInstance);
            if (!isNewInstance)
            {
                MessageBox.Show("PadForge is already running.", "PadForge",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // Set the application theme to follow system settings.
            ThemeManager.Current.ApplicationTheme = null; // null = follow system

            // Wire up global unhandled exception handlers for diagnostics.
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            // Create main window manually (instead of StartupUri) so we can
            // control whether Show() is called — required for start-minimized-to-tray.
            var window = new MainWindow();
            MainWindow = window;

            if (window.ShouldStartMinimizedToTray)
            {
                // Don't call Show() at all — the tray icon handles restore.
            }
            else if (window.ShouldStartMinimized)
            {
                window.WindowState = WindowState.Minimized;
                window.Show();
            }
            else
            {
                window.Show();
            }
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                MessageBox.Show(
                    $"An unexpected error occurred:\n\n{ex.Message}\n\n{ex.StackTrace}",
                    "PadForge — Fatal Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void App_DispatcherUnhandledException(object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "PadForge — Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true;
        }
    }
}
