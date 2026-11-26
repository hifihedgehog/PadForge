using System;
using System.Collections.Generic;
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
using PadForge.Services;

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

        // The single-instance lock: a mutex in the private namespace only a
        // copy with administrator rights can make or open (InstanceLock).
        private IDisposable _singleInstanceMutex;

        /// <summary>The copy Move PadForge to Its Own Folder made, started once
        /// this one has closed (#600). Null otherwise.</summary>
        internal static string StartWhenClosed;

        /// <summary>Starts the moved copy as this one's last act: its signal
        /// window and its lock go first, so the new copy is the app and not a
        /// second start, and this exe's entry leaves HidHide's list, which the
        /// new copy puts its own on as every start does.</summary>
        private void StartMovedCopy(object sender, ExitEventArgs e)
        {
            string exe = StartWhenClosed;
            if (string.IsNullOrEmpty(exe)) return;
            try
            {
                string self = PadForge.Common.HidHideController.CurrentProcessNativeImagePath();
                if (!string.IsNullOrEmpty(self)) PadForge.Common.HidHideController.RemoveWhitelistEntries(new[] { self });
            }
            catch { }
            try { InstanceSignal?.Dispose(); } catch { }
            // The lock lets go of its mutex before its handle closes
            // (InstanceLock.Dispose).
            try { _singleInstanceMutex?.Dispose(); } catch { }
            _singleInstanceMutex = null;
            try
            {
                PadForge.Common.ShellLaunch.StartOwnHelper(new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                });
            }
            catch { }
        }

        /// <summary>Timestamp of the last dispatcher error shown. Used to rate-limit popups.</summary>
        private readonly Stopwatch _lastErrorTime = new Stopwatch();

        /// <summary>Suppressed error count since last shown popup.</summary>
        private int _suppressedErrorCount;

        /// <summary>Occurrences per dispatcher-exception signature this
        /// session. A storyboard that dies during template application is
        /// retried and rethrown by every subsequent layout pass (the
        /// 2026-07-13 workshop forge-bar storm logged 300k+ identical
        /// entries and re-showed the same dialog every 3 seconds), so the
        /// dialog shows once per signature per session and crash.log keeps
        /// the first three full entries plus a periodic one-line counter.
        /// Dispatcher-thread only.</summary>
        private readonly Dictionary<string, int> _dispatcherErrorSignatures = new();

        /// <summary>Set when GPU render thread is zombied. Suppresses all cascading exceptions.</summary>
        private bool _gpuLost;

        /// <summary>Set at the end of OnStartup, after the main window is
        /// constructed and shown (or deliberately held for the tray). A
        /// dispatcher exception before this point is a failed launch.
        /// Application.MainWindow is NOT a usable signal for that: WPF
        /// auto-assigns it inside the Window base constructor, so a window
        /// that died mid-InitializeComponent still occupies it.</summary>
        private bool _startupUiReady;

        /// <summary>Window state before sleep for restore on wake.</summary>
        private WindowState _windowStateBeforeSleep;
        private bool _windowVisibleBeforeSleep;

        /// <summary>
        /// Background task sweeping HIDMaestro virtual controllers orphaned
        /// by a prior session that didn't cleanly dispose (crash, force-kill,
        /// power loss). Kicked off during OnStartup so the main window can
        /// render immediately. Nothing awaits it for correctness anymore
        /// (an earlier InitializeSdl await was removed when the SDL3 fork
        /// grew its own HM filter; round 33, C3 retired the stale claim
        /// here): the ordering property lives INSIDE the task, which ends
        /// with a bounded wait (up to 5 s) for the HIDMAESTRO devnodes to
        /// be genuinely ABSENT, so a same-session VC create cannot adopt a
        /// dying node, and logs ORPHANSWEEP when one lingers past the
        /// bound. MainWindow only watches it to show the startup overlay.
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

        /// <summary>The update helper's two dialogs (#457). It runs before any
        /// window exists and before settings load, so they follow the Windows
        /// display language rather than PadForge's own.</summary>
        private sealed class UpdateHelperUi : Services.UpdateService.IApplyUi
        {
            public void Report(string text) =>
                MessageBox.Show(text, Strings.Instance.Common_PadForge, MessageBoxButton.OK, MessageBoxImage.Warning);

            /// <summary>A window rather than a message box, so the question
            /// closes itself once the old copy has exited instead of holding
            /// the update until someone answers it.</summary>
            public bool KeepWaiting(string text, Func<bool> exited)
            {
                // The helper says when it is done. Left at the default, closing
                // this window, the helper's only one, would shut it down.
                Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                return AskToKeepWaiting(text, exited, TimeSpan.FromMilliseconds(250));
            }
        }

        /// <summary>The helper's wait question (#457): OK keeps waiting,
        /// Cancel or closing the window skips the update. It answers itself
        /// with OK once <paramref name="exited"/> turns true, checked every
        /// <paramref name="poll"/>. <paramref name="place"/> positions the
        /// window before it shows, for tests.</summary>
        internal static bool AskToKeepWaiting(string text, Func<bool> exited, TimeSpan poll, Action<Window> place = null)
        {
            var ok = new System.Windows.Controls.Button
            {
                Content = Strings.Instance.Common_OK, IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0),
            };
            var cancel = new System.Windows.Controls.Button
            {
                Content = Strings.Instance.Common_Cancel, IsCancel = true, MinWidth = 90,
            };
            var buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0),
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var content = new System.Windows.Controls.StackPanel { Margin = new Thickness(20, 18, 20, 18) };
            content.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 400,
            });
            content.Children.Add(buttons);
            var window = new Window
            {
                Title = Strings.Instance.Common_PadForge,
                Content = content,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            // The theme's own ground and text, whatever the helper loaded.
            window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ApplicationBackgroundBrush");
            window.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextFillColorPrimaryBrush");
            ok.Click += (s, e) => window.DialogResult = true;
            var timer = new DispatcherTimer { Interval = poll };
            timer.Tick += (s, e) =>
            {
                bool gone;
                try { gone = exited(); }
                catch
                {
                    // Whether the old copy exited cannot be seen, so the
                    // question stays up for someone to answer.
                    timer.Stop();
                    return;
                }
                if (!gone) return;
                timer.Stop();
                window.DialogResult = true;
            };
            window.Closed += (s, e) => timer.Stop();
            place?.Invoke(window);
            // Its first tick comes after the window shows: ShowDialog runs it.
            timer.Start();
            return window.ShowDialog() == true;
        }

        /// <summary>The arguments this copy was started with. An update hands
        /// its start options to the installer so PadForge comes back the way
        /// it was launched, --profile included (#457). What it was told to do
        /// once is left out (CommandLineSwitches.StartOptionsOnly).</summary>
        internal static string[] StartupArgs { get; private set; } = Array.Empty<string>();

        /// <summary>The same arguments, read (#600): whether the engine
        /// starts, where the window first is, and the steps a start runs
        /// before and after the engine. Never null. A copy that reaches
        /// OnStartup carries no line that could not be read: that one ended
        /// in Program.Main.</summary>
        internal static PadForge.Common.CommandLineSwitches.Request StartupRequest { get; private set; }
            = new PadForge.Common.CommandLineSwitches.Request();

        /// <summary>What a second copy of the exe finds this one by
        /// (Services/InstanceSignal.cs). Null until this copy holds the
        /// single-instance lock.</summary>
        internal static Services.InstanceSignal InstanceSignal { get; private set; }

        /// <summary>Takes the single-instance lock: the private namespace's,
        /// which no program without rights can take first. Every copy that
        /// reaches the app has administrator rights (Program.Main). Null when
        /// the namespace or the lock could not be made (InstanceLock.Take),
        /// and then this copy takes no lock under a name any program could
        /// hold first: it does not start.</summary>
        internal static IDisposable TakeSingleInstanceLock(out bool newInstance)
        {
            var held = Services.InstanceLock.Take(out bool heldElsewhere);
            newInstance = held != null && !heldElsewhere;
            return held;
        }

        /// <summary>A copy that lost the race for the lock hands its command
        /// line to the copy that won, as a second start does
        /// (<see cref="ClientRun.TryRun"/>): it waits for that copy's ready
        /// window, sends each switch as its line, or asks for the window when
        /// there is none, and its exit code is the run's. True when it is
        /// done. False when the winner is gone before it took the line, and
        /// this copy is to take the lock again.</summary>
        internal static bool HandToTheCopyThatWon(IReadOnlyList<string> args, out int exitCode, ClientRun.Seams seams = null)
            => ClientRun.TryRun(args ?? Array.Empty<string>(), out exitCode, seams);

        /// <summary>The lock could not be made: says so, and the start
        /// ends.</summary>
        private void StopForNoLock()
        {
            try
            {
                MessageBox.Show(Strings.Instance.Start_NoLock, Strings.Instance.Common_PadForge,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            Shutdown(1);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Every window and dialog from here on passes through the
            // catalog's registry of what is open (#600).
            PadForge.Services.Catalog.DialogRegistry.Watch();
            // Make the process's first known-folder lookup here, on the UI
            // thread, before any other thread starts or any window renders.
            // The first lookup resolves windows.storage's delay-loaded
            // SHCreatePropertyBagOnRegKey while it holds the known-folder
            // lock, and resolving it waits for the loader. When WPF renders
            // its first frame on an NVIDIA GPU, the driver loads
            // NvMemMapStoragex.dll, whose DllMain looks up a known folder
            // while it holds the loader. If the HIDMaestro sweep below made
            // the first lookup at that moment, each thread would wait on the
            // other and the window would never paint. The test suite hung
            // this way on 2026-09-23 (see FirstKnownFolderLookup in the tests).
            Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            // The Windows display language reads its own row of the language
            // table, which .NET's parent chain never reaches: nb-NO reads
            // Norwegian and en-AU British English (#505). Done first, so the
            // update helper's windows below read it too. A saved language
            // replaces it further down.
            var windowsLanguage = Thread.CurrentThread.CurrentUICulture;
            var interfaceLanguage = PadForge.Common.LanguageCatalog.InterfaceCulture(windowsLanguage);
            if (!Equals(interfaceLanguage, windowsLanguage))
            {
                Thread.CurrentThread.CurrentUICulture = interfaceLanguage;
                System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = interfaceLanguage;
            }

            // Every window declares the interface language as it loads, so
            // Chinese, Japanese and Korean text takes its own font (#504).
            // Started before the update helper below, whose windows open
            // ahead of everything else here.
            PadForge.Common.UiLanguage.Start();

            // Buttons, inputs and list items take their screen-reader names
            // from text the UI already shows (a label, a tooltip, the text
            // inside) as they first show, nothing on screen changing.
            PadForge.Common.AccessibleNames.Start();

            // In-app updates (#457). A copy started from the update folder with
            // --apply-update is the installer, not PadForge: it replaces the
            // copy that started it and exits. This runs before everything below,
            // the single-instance check included, because the old copy still
            // holds that lock while it closes.
            if (Services.UpdateService.TryRunApplyMode(e.Args, new UpdateHelperUi()))
            {
                // The old copy reads the exit code when the helper leaves
                // before reporting ready (UpdateService.WrongBuildExitCode).
                Shutdown(Environment.ExitCode);
                return;
            }

            // A committed update helper holds a lease on this exe until it
            // starts PadForge again itself. A launch meanwhile, from the Start
            // menu or a launcher, leaves quietly: it would hold the very file
            // being replaced.
            if (Services.UpdateService.IsUpdateInProgress(Environment.ProcessPath))
            {
                Shutdown();
                return;
            }

            _singleInstanceMutex = TakeSingleInstanceLock(out bool isNewInstance);
            if (_singleInstanceMutex == null)
            {
                StopForNoLock();
                return;
            }
            Exit += StartMovedCopy;
            for (int attempt = 0; !isNewInstance; attempt++)
            {
                // Another copy holds the lock. A second start hands its
                // switches to that copy before it ever gets here
                // (Program.Main, ClientRun), so this is two copies started in
                // the same moment, and this one lost. It lets go of the lock
                // it opened, so the winner's end can be seen, and hands its
                // command line to the winner as a second start does. Its
                // start reads the run's exit code.
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                bool done = HandToTheCopyThatWon(e.Args, out int handed);
                if (done || attempt >= 2)
                {
                    Shutdown(done ? handed : ClientRun.NotRunning);
                    return;
                }
                // The winner was gone before it took the line, so this copy
                // takes its place.
                _singleInstanceMutex = TakeSingleInstanceLock(out isNewInstance);
                if (_singleInstanceMutex == null)
                {
                    StopForNoLock();
                    return;
                }
            }

            // From here a second copy finds this one, and its request to show
            // the window is kept until there is a window to show (#600).
            InstanceSignal = new Services.InstanceSignal();
            InstanceSignal.Open();
            // This copy is the app now, so a start with rights that restarted
            // it from the protected folder leaves (#600). An update's helper
            // and a copy that lost the race leave above, and their start
            // waits for their exit code. A copy that hands over to a pending
            // install leaves below, after this: its start, and a --wait on
            // it, end when this copy exits, not when the installed app does.
            Services.ProtectedStart.HandOver();

            StartupArgs = e.Args ?? Array.Empty<string>();
            // The logon task starts this copy with no switch. A start without
            // rights that ran it hands its start switches over the signal
            // pipe, and this copy waits for them only when that start says
            // they are coming (#600 item 44).
            if (StartupArgs.Length == 0)
                StartupArgs = System.Linq.Enumerable.ToArray(InstanceSignal.TakeStartSwitches(Services.InstanceSignal.StartSwitchesWaitMs));
            StartupRequest = PadForge.Common.CommandLineSwitches.Parse(StartupArgs);

            // An update Install Updates Automatically downloaded last session
            // installs now, before the engine, the drivers or a window start.
            // The helper starts PadForge again with every switch of this
            // start, since none of them has run yet.
            if (Services.UpdateService.TryStartPendingInstall(StartupArgs))
            {
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // WPF data-binding failures are silent in production: they only
            // reach the debugger trace channel, so a broken binding looks
            // like a control that ignores clicks (the #155 Reset on Release
            // checkbox produced no save and no event, indistinguishable
            // from a wiring bug until traced). Forward binding ERRORS to
            // the in-memory diagnostics ring so a dead binding names itself
            // in a crash report. Nothing is written to disk.
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
            // extended search path. SetDefaultDllDirectories, which
            // Program.Main calls first, searches a folder set this way
            // (Microsoft, LOAD_LIBRARY_SEARCH_USER_DIRS).
            //
            // The folder is the one this process's own libraries were
            // unpacked into, and nothing else (#600): the protected folder
            // for a start with rights (Services/ProtectedStart.cs). The
            // fallback that took the newest folder in the user's temp folder
            // with an xinput1_4.dll in it is gone, since any program of the
            // user can make one.
            try
            {
                string extractionDir = Services.UnpackFolder.Current();
                if (!string.IsNullOrEmpty(extractionDir))
                {
                    NativeMethods.SetDllDirectory(extractionDir);
                    // The protected folder's other builds and set-aside
                    // folders go, off the start's path, and so do the
                    // copies older builds left in the user's temp folder,
                    // deleted as the user.
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        Services.UnpackFolder.CleanUp(extractionDir);
                        Services.ProtectedWork.RemoveOld(TimeSpan.FromDays(1));
                        Services.TempLeftovers.RetireInTheApp();
                    });
                }
            }
            catch { /* outer catch — never allow this to take down startup */ }

            // Replay any HIDMaestro OEM-name overrides left by a prior
            // session that didn't get a clean Clear (crash, force-kill,
            // power loss). Restores the DirectInput OEM table to its
            // pre-override state before we create any virtuals or apply
            // new overrides. Idempotent — no-op when no orphan records
            // exist. Requires admin (HKLM write), which the app always has
            // (Program.Main).
            try { HIDMaestro.HMOemNameOverride.RecoverOrphans(); }
            catch { /* best effort — continue without recovery */ }

            // Sweep any HIDMaestro virtual devices left over from a prior
            // session that didn't cleanly dispose (crash, force-kill,
            // power loss). Sweep runs on a background thread so startup returns
            // immediately. Nothing waits on it before enumeration: the library
            // filters the virtual devices out whether or not the prior session's
            // kernel cleanup has finished, and blocking the polling thread on
            // the sweep is what pinned startup at ninety seconds. The task is
            // exposed only so the window can show the cleanup overlay while it
            // runs.
            //
            // preserveInstall: TRUE. This is a launch sweep, and HM's own
            // doc names that as the case for it. Devices and orphans are
            // still evicted (the preserve flag guards only package removal
            // and the HKLM\SOFTWARE\HIDMaestro delete), which is the whole
            // reason this sweep exists. The uninstall-grade overload was
            // the only one that existed when this was written (2026-04-20);
            // HM added the preserving one in its 2026-07-21 perf audit and
            // moved its own call site over. Staying on the nuking overload
            // cost three things every single launch: the ~3 s full deploy
            // pipeline (the same-version fast path could never fire), the
            // SteamVRPath hint that lets a steamcmd SteamVR be found, and
            // the VR driver's registration gate. Losing that gate made
            // every launch re-extract driver_hidmaestro.dll into a running
            // vrserver.exe and fail the VR slot with a sharing violation
            // (owner report 2026-08-08).
            OrphanSweepTask = System.Threading.Tasks.Task.Run(() =>
            {
                try { PadForge.Common.Input.HMaestroVirtualController.SweepVirtualControllers(); }
                catch { /* Best effort. Continue without the sweep. */ }
                // HM#38 consumer-side ordering hygiene: RemoveAll returns when
                // the CALL completes, not when PnP removal completes, and a
                // create racing an in-flight removal was one of the freeze's
                // trigger windows. Wait (bounded) for the devnodes to actually
                // be gone before the first enumeration proceeds. Purely an
                // ordering barrier: with the v1.3.22 driver the freeze itself
                // is structurally fixed, and if something genuinely cannot be
                // removed we log and proceed rather than block startup.
                for (int i = 0; i < 25; i++)
                {
                    if (!Common.SetupApiInterop.AnyPresentHidMaestroDevice()) return;
                    System.Threading.Thread.Sleep(200);
                }
                PadForge.Engine.SdlDiagLog.WriteLine(
                    "ORPHANSWEEP devnodes still present after 5 s; proceeding");
            });

            // Apply the normal PS3-family filter policy in the background.
            // This preserves DsHidMini and externally paired devices. A Wii
            // scan can defer enabling. PSM-off affects new connections only,
            // so this is exposure reduction, not a kernel teardown guarantee.
            // An older BthPS3 install is replaced first, while no PlayStation
            // controller is connected over Bluetooth, and the reconcile then
            // arms the filter for whichever drivers are in place.
            System.Threading.Tasks.Task.Run(() =>
            {
                try { PadForge.Services.Ds3PairingService.UpgradeDriversAtStartup(); }
                catch { /* best effort. an update must never block launch */ }
                try { PadForge.Services.Ds3PairingService.ReconcilePsmPatchForCrashSafety("startup"); }
                catch { /* best effort. mitigation must never block launch */ }
            });

            // Capture the pre-override UI culture first: this is the
            // Windows display language, read through its row of the
            // language table at the top of this method, that a fresh
            // install runs under, and Reset to Defaults restores it. InstalledUICulture is the
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
            // UiLanguage started under the Windows language, above. The saved
            // one may draw in another face (Galactic Basic's Aurebesh, #590),
            // and no window has loaded yet to read it.
            PadForge.Common.UiLanguage.Apply();

            // This copy has administrator rights: Program.Main starts the app
            // once more with them when it was started without
            // (Services/StartWithRights.cs), and only that copy gets here.

            // Migrate the legacy HKCU\Run launch-at-logon entry (which never
            // started PadForge while its manifest required administrator
            // rights, and would raise Windows' prompt at every logon now that
            // the exe asks for them itself) to a Task Scheduler entry. It does
            // nothing when no legacy entry exists. Runs before the UI shows
            // the Settings toggle so the toggle's bound state always reflects
            // the migrated reality.
            // The exe's folder is judged, and protected when it is PadForge's
            // own, before anything trusts it, and the tasks that run PadForge
            // are looked at (#600, Services/ExeFolder.cs).
            Services.ExeFolderState.Instance.Start(Environment.ProcessPath);
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
            // control whether Show() is called, required for start-minimized-to-tray.
            var window = new MainWindow();
            MainWindow = window;

            if (window.ShouldStartMinimizedToTray)
            {
                // Don't call Show() at all. The tray icon handles restore.
            }
            else if (window.ShouldStartMinimized)
            {
                window.WindowState = WindowState.Minimized;
                window.Show();
            }
            else if (window.ShouldStartMaximized)
            {
                window.WindowState = WindowState.Maximized;
                window.Show();
            }
            else
            {
                window.Show();
            }

            _startupUiReady = true;

            // There is a window now. A second copy that asked for it, or for
            // a close, while this one was starting gets it, and from here a
            // copy that finds no control pipe knows that is Automation's
            // doing and not a start still under way (#600).
            InstanceSignal.Attach(window.ShowForACaller, window.ExitForACaller);
            InstanceSignal.MarkReady();
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

        /// <summary>One "Type: Message" line per exception in the inner
        /// chain. Wrapper exceptions (XamlParseException rewraps, TIEs,
        /// AggregateExceptions) carry a generic outer message; the real
        /// cause lives at the bottom of the chain. A v4.0.0 Discord
        /// crash report showed only "Set property
        /// 'System.Windows.FrameworkElement.Style' threw an exception."
        /// because both handlers printed the outer level alone.</summary>
        private static string ExceptionMessageChain(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            for (var e = ex; e != null && sb.Length < 4096; e = e.InnerException)
            {
                if (sb.Length > 0)
                    sb.Append("\n--> ");
                sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
                // BAML rewraps know which XAML file failed even when the
                // release build has no line info.
                if (e is System.Windows.Markup.XamlParseException xpe)
                {
                    if (xpe.BaseUri != null)
                        sb.Append(" [").Append(xpe.BaseUri).Append(']');
                    if (xpe.LineNumber > 0)
                        sb.Append(" [line ").Append(xpe.LineNumber)
                          .Append(", pos ").Append(xpe.LinePosition).Append(']');
                }
            }
            return sb.ToString();
        }

        /// <summary>Every stack in the inner chain, outermost first.
        /// Exception.ToString() carries the same data, but interleaves
        /// messages and stacks; this keeps the dialog's message block
        /// (ExceptionMessageChain) and stack block separable.</summary>
        private static string ExceptionStackChain(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            for (var e = ex; e != null && sb.Length < 16384; e = e.InnerException)
            {
                if (sb.Length > 0)
                    sb.Append("\n-- inner (").Append(e.GetType().Name).Append(") --\n");
                sb.Append(e.StackTrace ?? "(no stack)");
            }
            return sb.ToString();
        }

        /// <summary>Test seam: the append to crash.log, given its path and
        /// text. Null in the app.</summary>
        internal static Action<string, string> CrashLogAppendSeam;

        /// <summary>Appends <paramref name="text"/> to crash.log beside the
        /// exe. False when nothing was written: the drive is full, or another
        /// program holds the file.</summary>
        internal static bool AppendCrashLog(string text)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                var seam = CrashLogAppendSeam;
                if (seam != null) seam(path, text);
                else System.IO.File.AppendAllText(path, text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The error dialog's text: the messages, the report page and
        /// the stacks. It says crash.log holds the details only when
        /// <paramref name="written"/>, and otherwise that the dialog's own
        /// text is the report.</summary>
        internal static string CrashText(bool written, string messages, string stacks)
            => string.Format(written ? Strings.Instance.App_UnexpectedError_Format : Strings.Instance.App_UnexpectedErrorNotWritten_Format,
                messages, stacks, UpdateService.ReportPage);

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            bool written = AppendCrashLog(
                $"[{DateTime.Now:HH:mm:ss}] DOMAIN: {(e.ExceptionObject is Exception ex2 ? $"{ExceptionMessageChain(ex2)}\n{ExceptionStackChain(ex2)}" : e.ExceptionObject?.ToString())}\n\n"
                // The in-memory diagnostics ring (SDL narration, stall
                // watchdog, binding errors) is this crash's recent context;
                // a fatal crash is the one place it reaches disk.
                + "-- recent diagnostics --\n" + Engine.SdlDiagLog.Snapshot() + "\n\n");

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
                    CrashText(written, ExceptionMessageChain(ex), ExceptionStackChain(ex)),
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

            string signature = ExceptionMessageChain(e.Exception);
            _dispatcherErrorSignatures.TryGetValue(signature, out int priorHits);
            int hits = priorHits + 1;
            _dispatcherErrorSignatures[signature] = hits;

            // Storm bound: full entries for the first three hits of a
            // signature, then a one-line counter every 500th, nothing in
            // between. A layout-retry storm otherwise appends the same
            // multi-kilobyte stack thousands of times.
            bool written = false;
            if (hits <= 3)
            {
                written = AppendCrashLog(
                    $"[{DateTime.Now:HH:mm:ss}] DISPATCHER{(hits > 1 ? $" (hit {hits})" : string.Empty)}: {signature}\n{ExceptionStackChain(e.Exception)}\n\n"
                    // Before startup completes, a dispatcher exception is a
                    // failed launch (OnStartup aborted): flush the diagnostics
                    // ring like the fatal DOMAIN path does. Steady-state
                    // dispatcher errors skip the appendix so an exception storm
                    // doesn't snowball crash.log.
                    + (!_startupUiReady
                        ? "-- recent diagnostics --\n" + Engine.SdlDiagLog.Snapshot() + "\n\n"
                        : string.Empty));
            }
            else if (hits == 4 || hits % 500 == 0)
            {
                AppendCrashLog(
                    $"[{DateTime.Now:HH:mm:ss}] DISPATCHER (hit {hits}, further identical entries suppressed): {signature.Split('\n')[0]}\n\n");
            }

            // Once the render thread is zombied, suppress ALL cascading exceptions
            // silently: they're all downstream failures from the same GPU device loss.
            if (_gpuLost)
                return;

            // GPU device lost (common after sleep/wake): fall back to software
            // rendering silently and suppress all further render exceptions.
            if (IsGpuLostException(e.Exception))
            {
                _gpuLost = true;
                return;
            }

            // One dialog per signature per session. A storyboard that dies
            // during template application rethrows on every layout retry,
            // and the 3-second limiter alone re-shows the same dialog for
            // as long as the storm lasts.
            if (hits > 1)
            {
                _suppressedErrorCount++;
                if (!_startupUiReady)
                    ShutdownAfterStartupError();
                return;
            }

            // Rate-limit: if an error was shown in the last 3 seconds, suppress
            // the popup to prevent the infinite MessageBox loop that occurs when
            // the 30Hz DispatcherTimer fires during the modal MessageBox.Show()
            // nested dispatcher pump and hits the same exception repeatedly.
            if (_lastErrorTime.IsRunning && _lastErrorTime.ElapsedMilliseconds < 3000)
            {
                _suppressedErrorCount++;
                if (!_startupUiReady)
                    ShutdownAfterStartupError();
                return;
            }

            _lastErrorTime.Restart();
            string suppressed = _suppressedErrorCount > 0
                ? "\n\n" + string.Format(Strings.Instance.App_SuppressedErrors_Format, _suppressedErrorCount)
                : string.Empty;
            _suppressedErrorCount = 0;

            MessageBox.Show(
                CrashText(written, signature, ExceptionStackChain(e.Exception)) + suppressed,
                Strings.Instance.App_Error,
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            // A dispatcher exception before startup completes means
            // OnStartup aborted: the window never finished constructing, so
            // e.Handled=true would leave a windowless elevated process
            // running until the user finds it in Task Manager (v4.0.0
            // light-theme launch crash). Nothing to keep alive. Exit.
            if (!_startupUiReady)
                ShutdownAfterStartupError();
        }

        private void ShutdownAfterStartupError()
        {
            (MainWindow as PadForge.MainWindow)?.PrepareForExit();
            Shutdown(1);
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            base.OnSessionEnding(e);
            // WPF shuts down after an accepted session-ending request, and does
            // not wait for the async close that request starts (wpf
            // Application.cs:2125-2127, Window.cs:4408-4412). So a taken touchpad
            // gets its hide off here, before this returns.
            if (!e.Cancel)
            {
                (MainWindow as PadForge.MainWindow)?.ReleaseTouchpadsForSessionEnd();
                (MainWindow as PadForge.MainWindow)?.PrepareForExit();
            }
        }

        private static bool IsGpuLostException(Exception ex)
        {
            var trace = ex.StackTrace;
            return trace != null &&
                   (trace.Contains("DUCE.Channel.SyncFlush") ||
                    trace.Contains("NotifyPartitionIsZombie"));
        }

    }

    /// <summary>Forwards WPF data-binding ERROR traces to the in-memory
    /// diagnostics ring (BINDERR lines, surfaced in crash.log's appendix).
    /// Errors only, so a healthy session records nothing. Never throws: a
    /// diagnostics listener must not take the UI down.</summary>
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
