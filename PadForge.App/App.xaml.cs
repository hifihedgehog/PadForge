using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using ModernWpf;
using PadForge.Common.Input;

namespace PadForge
{
    public partial class App : Application
    {
        private Mutex _singleInstanceMutex;

        /// <summary>Timestamp of the last dispatcher error shown. Used to rate-limit popups.</summary>
        private readonly Stopwatch _lastErrorTime = new Stopwatch();

        /// <summary>Suppressed error count since last shown popup.</summary>
        private int _suppressedErrorCount;

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

            // vJoy device node creation/removal requires admin privileges.
            // If the vJoy driver is installed, relaunch elevated.
            if (!IsRunningAsAdmin() && IsVJoyDriverInstalled())
            {
                try
                {
                    var exePath = Environment.ProcessPath
                        ?? Process.GetCurrentProcess().MainModule?.FileName;
                    if (exePath != null)
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = exePath,
                            Verb = "runas",
                            UseShellExecute = true,
                            Arguments = string.Join(" ", e.Args)
                        };
                        Process.Start(psi);
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // UAC cancelled — continue unelevated (vJoy won't work fully).
                }
                _singleInstanceMutex?.ReleaseMutex();
                Shutdown();
                return;
            }

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
            e.Handled = true;

            // Rate-limit: if an error was shown in the last 3 seconds, suppress
            // the popup to prevent the infinite MessageBox loop that occurs when
            // the 30Hz DispatcherTimer fires during the modal MessageBox.Show()
            // nested dispatcher pump and hits the same exception repeatedly.
            if (_lastErrorTime.IsRunning && _lastErrorTime.ElapsedMilliseconds < 3000)
            {
                _suppressedErrorCount++;
                return;
            }

            _lastErrorTime.Restart();
            string suppressed = _suppressedErrorCount > 0
                ? $"\n\n({_suppressedErrorCount} additional error(s) suppressed)"
                : string.Empty;
            _suppressedErrorCount = 0;

            MessageBox.Show(
                $"An unexpected error occurred:\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}{suppressed}",
                "PadForge — Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private static bool IsRunningAsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static bool IsVJoyDriverInstalled()
        {
            // Fast file check — avoids loading the DLL just to test.
            string vjoyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
            return File.Exists(Path.Combine(vjoyDir, "vJoyInterface.dll"));
        }
    }
}
