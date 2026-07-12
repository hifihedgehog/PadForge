using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using PadForge.Common.Input;
using PadForge.Resources.Strings;

namespace PadForge
{
    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool SetDllDirectory(string lpPathName);
    }

    public partial class App : Application
    {
        /// <summary>Best-effort "quiet all hardware outputs" hook,
        /// assigned by MainWindow once services exist. Invoked from the
        /// crash handler and ProcessExit.</summary>
        public static Action PanicQuiesce;

        /// <summary>The UI culture the process started with, captured
        /// before the saved language override is applied. The
        /// fresh-install language, used by Reset to Defaults.</summary>
        public static System.Globalization.CultureInfo StartupUICulture { get; private set; }
            = Thread.CurrentThread.CurrentUICulture;

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

        /// <summary>
        /// Background task sweeping HIDMaestro virtual controllers orphaned
        /// by a prior session that didn't cleanly dispose (crash, force-kill,
        /// power loss). Kicked off during OnStartup so the main window can
        /// render immediately; awaited from <see cref="InputManager.InitializeSdl"/>
        /// before SDL enumerates devices so the orphans never surface in
        /// Devices list or XInput slots. By the time the user's engine Start
        /// fires, the sweep is typically already complete; the Wait there
        /// is the safety catch when a heavy kernel cleanup runs long.
        /// </summary>
        public static System.Threading.Tasks.Task OrphanSweepTask { get; private set; }

        /// <summary>Translator for <see cref="Engine.Common.Mapping.MappingExpression.ErrorFormatter"/>.
        /// Maps each ParseError enum value to its Pad_Formula_Error_* resource
        /// string with the supplied positional args. Wired in <see cref="OnStartup"/>.</summary>
        private static string LocalizeMappingExpressionError(Engine.Common.Mapping.MappingExpression.ParseError code, object[] args)
        {
            args ??= System.Array.Empty<object>();
            var s = Strings.Instance;
            string tmpl = code switch
            {
                Engine.Common.Mapping.MappingExpression.ParseError.UnexpectedTokenAtEnd       => s.Pad_Formula_Error_UnexpectedTokenAtEnd,
                Engine.Common.Mapping.MappingExpression.ParseError.InvalidNumber              => s.Pad_Formula_Error_InvalidNumber,
                Engine.Common.Mapping.MappingExpression.ParseError.SingleEqualsNotSupported   => s.Pad_Formula_Error_SingleEqualsNotSupported,
                Engine.Common.Mapping.MappingExpression.ParseError.UnexpectedCharacter        => s.Pad_Formula_Error_UnexpectedCharacter,
                Engine.Common.Mapping.MappingExpression.ParseError.ExpectedColonInTernary     => s.Pad_Formula_Error_ExpectedColonInTernary,
                Engine.Common.Mapping.MappingExpression.ParseError.ExpectedRParen             => s.Pad_Formula_Error_ExpectedRParen,
                Engine.Common.Mapping.MappingExpression.ParseError.ExpectedRParenAfterArgs    => s.Pad_Formula_Error_ExpectedRParenAfterArgs,
                Engine.Common.Mapping.MappingExpression.ParseError.ExpectedRBracketAfterIndex => s.Pad_Formula_Error_ExpectedRBracketAfterIndex,
                Engine.Common.Mapping.MappingExpression.ParseError.ExpectedTokenSuffix        => s.Pad_Formula_Error_ExpectedTokenSuffix,
                Engine.Common.Mapping.MappingExpression.ParseError.UnknownIdentifier          => s.Pad_Formula_Error_UnknownIdentifier,
                Engine.Common.Mapping.MappingExpression.ParseError.UnexpectedToken            => s.Pad_Formula_Error_UnexpectedToken,
                Engine.Common.Mapping.MappingExpression.ParseError.UnexpectedParseError       => s.Pad_Formula_Error_UnexpectedParseError,
                _ => code.ToString(),
            };
            try { return string.Format(tmpl, args); }
            catch { return tmpl; }
        }

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

            // WPF data-binding failures are silent in production: they only
            // reach the debugger trace channel, so a broken binding looks
            // like a control that ignores clicks (the #155 Reset on Release
            // checkbox produced no save and no event, indistinguishable
            // from a wiring bug until traced). Forward binding ERRORS to
            // diag.log so a dead binding names itself in the field.
            System.Diagnostics.PresentationTraceSources.Refresh();
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(
                new DiagBindingTraceListener());
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level =
                System.Diagnostics.SourceLevels.Error;

            // Wire the MappingExpression parse-error translator (Issue
            // #61). The Engine project can't reference Strings.Instance
            // (App-only), so the parser holds a delegate it calls per
            // error site; we resolve to the active culture's resource
            // string here at startup.
            Engine.Common.Mapping.MappingExpression.ErrorFormatter = LocalizeMappingExpressionError;

            // Put the single-file extraction directory on Win32's DLL
            // search path so SDL3's native LoadLibrary("xinput1_4.dll")
            // finds our OpenXInput-derived copy there instead of
            // Microsoft's System32 xinput1_4.dll.  Uses SetDllDirectory
            // (not NativeLibrary.Load) to avoid triggering OpenXInput's
            // DllMain mid-process — that DllMain does work that's
            // unsafe under loader lock and hangs if invoked when other
            // threads are already active.  The OS loader will call it
            // normally when SDL3 later resolves the DLL via the
            // extended search path.
            try
            {
                string extractionDir = null;
                try
                {
                    foreach (System.Diagnostics.ProcessModule mod in System.Diagnostics.Process.GetCurrentProcess().Modules)
                    {
                        string p = null;
                        try { p = mod?.FileName; } catch { continue; }
                        if (!string.IsNullOrEmpty(p) && p.IndexOf(@"\.net\", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            extractionDir = Path.GetDirectoryName(p);
                            if (!string.IsNullOrEmpty(extractionDir)) break;
                        }
                    }
                }
                catch { /* module enumeration failed — scan as fallback */ }

                if (string.IsNullOrEmpty(extractionDir))
                {
                    try
                    {
                        var netRoot = Path.Combine(Path.GetTempPath(), ".net", "PadForge");
                        if (Directory.Exists(netRoot))
                        {
                            extractionDir = Directory.EnumerateDirectories(netRoot)
                                .Where(d => { try { return File.Exists(Path.Combine(d, "xinput1_4.dll")); } catch { return false; } })
                                .OrderByDescending(d => { try { return Directory.GetLastWriteTimeUtc(d); } catch { return DateTime.MinValue; } })
                                .FirstOrDefault();
                        }
                    }
                    catch { /* scan failed — no filter this run */ }
                }

                if (!string.IsNullOrEmpty(extractionDir))
                {
                    NativeMethods.SetDllDirectory(extractionDir);
                }
            }
            catch { /* outer catch — never allow this to take down startup */ }

            // Replay any HIDMaestro OEM-name overrides left by a prior
            // session that didn't get a clean Clear (crash, force-kill,
            // power loss). Restores the DirectInput OEM table to its
            // pre-override state before we create any virtuals or apply
            // new overrides. Idempotent — no-op when no orphan records
            // exist. Requires admin (HKLM write); PadForge auto-elevates.
            try { HIDMaestro.HMOemNameOverride.RecoverOrphans(); }
            catch { /* best effort — continue without recovery */ }

            // Sweep any HIDMaestro virtual devices left over from a prior
            // session that didn't cleanly dispose (crash, force-kill,
            // power loss). Sweep runs on a background thread so OnStartup
            // returns immediately; InputManager.UpdateDevices awaits this
            // task before the first enumeration so stale HM HIDs are gone
            // by the time PadForge looks at its device list.
            OrphanSweepTask = System.Threading.Tasks.Task.Run(() =>
            {
                try { HIDMaestro.HMContext.RemoveAllVirtualControllers(); }
                catch { /* best effort — continue without sweep */ }
            });

            // Reconcile BthPS3 PSM patching to the crash-safe state once per
            // launch (issue #199): armed only if a DS3 is actually paired, off
            // otherwise, so a machine with BthPS3 installed but no DS3 in use
            // keeps the profile driver dormant and out of the upstream
            // use-after-free path. Also (re)asserts AutoEnableFilter=0 via the
            // reconcile's SetPsmPatching ownership. No-op when BthPS3 isn't
            // installed; runs on a background thread so it never blocks startup.
            System.Threading.Tasks.Task.Run(() =>
            {
                try { PadForge.Services.Ds3PairingService.ReconcilePsmPatchForCrashSafety("startup"); }
                catch { /* best effort. mitigation must never block launch */ }
            });

            // Capture the pre-override UI culture first: this is the
            // Windows display language a fresh install runs under, and
            // Reset to Defaults restores it. InstalledUICulture is the
            // wrong stand-in (OS install language, which can differ from
            // the user's display language).
            StartupUICulture = Thread.CurrentThread.CurrentUICulture;

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

            // Migrate the legacy HKCU\Run launch-at-logon entry (which never
            // worked once PadForge required elevation) to a Task Scheduler
            // entry. Idempotent — no-op when no legacy entry exists. Runs
            // before the UI shows the Settings toggle so the toggle's bound
            // state always reflects the migrated reality.
            PadForge.Common.StartupHelper.MigrateLegacyEntryIfNeeded();

            // Apply system theme (follows OS light/dark setting), then pin
            // the Ember accent over whatever the system accent was (#175).
            ApplicationThemeManager.ApplySystemTheme();
            PadForge.Common.EmberTheme.ApplyAccent();

            // Wire up global unhandled exception handlers for diagnostics.
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            // Quiet hardware outputs on exit paths that bypass
            // MainWindow.OnClosing (Environment.Exit, AppDomain unload).
            // ProcessExit does NOT fire after an unhandled exception;
            // the crash handler carries its own invoke for that path.
            // Idempotent after a clean stop.
            AppDomain.CurrentDomain.ProcessExit += (s2, e2) =>
            { try { PanicQuiesce?.Invoke(); } catch { } };
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
            try { System.IO.File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"),
                $"[{DateTime.Now:HH:mm:ss}] DOMAIN: {(e.ExceptionObject is Exception ex2 ? $"{ex2.GetType().Name}: {ex2.Message}\n{ex2.StackTrace}" : e.ExceptionObject?.ToString())}\n\n"); }
            catch { }

            // Stop any live rumble / sustained haptic tone BEFORE the
            // fatal dialog blocks, and BEFORE the GPU-loss early return:
            // ProcessExit does not fire after an unhandled exception, so
            // this is the only quiesce a fatal crash gets. The Steam
            // Deck's rumble command has no firmware timeout, so leaving
            // it running would buzz the trackpads indefinitely.
            try { PanicQuiesce?.Invoke(); } catch { }

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

            try { System.IO.File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"),
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

    /// <summary>Forwards WPF data-binding ERROR traces to diag.log
    /// (BINDERR lines). Errors only, so a healthy session writes
    /// nothing. Never throws: a diagnostics listener must not take the
    /// UI down.</summary>
    internal sealed class DiagBindingTraceListener : System.Diagnostics.TraceListener
    {
        public override void Write(string message) { }

        public override void WriteLine(string message)
        {
            try
            {
                if (!string.IsNullOrEmpty(message))
                    Engine.SdlDiagLog.WriteLine("BINDERR " + message);
            }
            catch { }
        }
    }
}
