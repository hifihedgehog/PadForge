using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using PadForge.Common.Input;
using PadForge.Resources.Strings;

namespace PadForge
{
    public partial class App : Application
    {
        private Mutex _singleInstanceMutex;

        /// <summary>Timestamp of the last dispatcher error shown. Used to rate-limit popups.</summary>
        private readonly Stopwatch _lastErrorTime = new Stopwatch();

        /// <summary>Suppressed error count since last shown popup.</summary>
        private int _suppressedErrorCount;

        /// <summary>Set when GPU render thread is zombied — suppresses all cascading exceptions.</summary>
        private bool _gpuLost;

        /// <summary>Window state before sleep for restore on wake.</summary>
        private WindowState _windowStateBeforeSleep;
        private bool _windowVisibleBeforeSleep;


        protected override void OnStartup(StartupEventArgs e)
        {
            _singleInstanceMutex = new Mutex(true, "PadForge_SingleInstance", out bool isNewInstance);
            if (!isNewInstance)
            {
                MessageBox.Show(Strings.Instance.App_AlreadyRunning, Strings.Instance.Common_PadForge,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // Apply saved language preference before any UI is created.
            var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PadForge.xml");
            if (File.Exists(settingsPath))
            {
                try
                {
                    var xml = File.ReadAllText(settingsPath);
                    var langMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<Language>([^<]+)</Language>");
                    if (langMatch.Success && !string.IsNullOrEmpty(langMatch.Groups[1].Value))
                    {
                        var culture = new System.Globalization.CultureInfo(langMatch.Groups[1].Value);
                        Thread.CurrentThread.CurrentUICulture = culture;
                        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
                    }
                }
                catch { /* ignore parse errors, use system default */ }
            }

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

            // Apply system theme (follows OS light/dark setting).
            ApplicationThemeManager.ApplySystemTheme();

            // Wire up global unhandled exception handlers for diagnostics.
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            Dispatcher.UnhandledExceptionFilter += Dispatcher_UnhandledExceptionFilter;

            // Proactively handle GPU device loss on sleep/wake by temporarily
            // switching to software rendering before the render thread crashes.
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

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

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                // Hide the window before sleep so WPF doesn't attempt to
                // render when the GPU device is lost during wake. This
                // prevents the render thread from touching D3D on resume.
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        if (MainWindow is MainWindow mw)
                        {
                            _windowStateBeforeSleep = mw.WindowState;
                            _windowVisibleBeforeSleep = mw.IsVisible;
                            mw.Hide();
                        }
                    }
                    catch { }
                });
            }
            else if (e.Mode == PowerModes.Resume)
            {
                // Restore the window after a delay to let the GPU driver
                // re-initialize before WPF tries to render.
                Dispatcher.BeginInvoke(() =>
                {
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    timer.Tick += (s, _) =>
                    {
                        timer.Stop();
                        try
                        {
                            if (MainWindow is MainWindow mw && _windowVisibleBeforeSleep)
                            {
                                mw.Show();
                                mw.WindowState = _windowStateBeforeSleep;
                            }
                        }
                        catch { }
                    };
                    timer.Start();
                });
            }
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // Suppress cascading render thread exceptions after GPU device loss.
            if (_gpuLost)
                return;

            if (e.ExceptionObject is Exception ex)
            {
                MessageBox.Show(
                    string.Format(Strings.Instance.App_UnexpectedError_Format, ex.Message, ex.StackTrace),
                    Strings.Instance.App_FatalError,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void Dispatcher_UnhandledExceptionFilter(object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionFilterEventArgs e)
        {
            // Suppress cascading exceptions after GPU device loss.
            if (_gpuLost)
                e.RequestCatch = true;
        }

        private void App_DispatcherUnhandledException(object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;

            // Once the render thread is zombied, suppress ALL cascading exceptions
            // silently — they're all downstream failures from the same GPU device loss.
            if (_gpuLost)
                return;

            // GPU device lost (common after sleep/wake): fall back to software
            // rendering silently and suppress all further render exceptions.
            if (IsGpuLostException(e.Exception))
            {
                _gpuLost = true;
                return;
            }

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
                ? "\n\n" + string.Format(Strings.Instance.App_SuppressedErrors_Format, _suppressedErrorCount)
                : string.Empty;
            _suppressedErrorCount = 0;

            MessageBox.Show(
                string.Format(Strings.Instance.App_UnexpectedError_Format, e.Exception.Message, e.Exception.StackTrace) + suppressed,
                Strings.Instance.App_Error,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private static bool IsGpuLostException(Exception ex)
        {
            var trace = ex.StackTrace;
            return trace != null &&
                   (trace.Contains("DUCE.Channel.SyncFlush") ||
                    trace.Contains("NotifyPartitionIsZombie"));
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
