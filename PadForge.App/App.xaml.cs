using System;
using System.Diagnostics;
using System.IO;
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

            // Elevation is guaranteed by app.manifest (requireAdministrator).
            // Windows prompts on launch and shows the UAC shield on the icon.

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
            try { System.IO.File.AppendAllText(@"C:\PadForge\crash.log",
                $"[{DateTime.Now:HH:mm:ss}] DOMAIN: {(e.ExceptionObject is Exception ex2 ? $"{ex2.GetType().Name}: {ex2.Message}\n{ex2.StackTrace}" : e.ExceptionObject?.ToString())}\n\n"); }
            catch { }

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

            try { System.IO.File.AppendAllText(@"C:\PadForge\crash.log",
                $"[{DateTime.Now:HH:mm:ss}] DISPATCHER: {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception.StackTrace}\n\n"); }
            catch { }

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

    }
}
