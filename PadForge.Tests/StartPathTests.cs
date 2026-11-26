using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Resources.Strings;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// How PadForge.exe starts (#597). The exe starts with its caller's
    /// rights, so a copy that hands switches to the running PadForge can
    /// answer on its caller's console. <c>Program.Main</c> decides what the
    /// process is before any of WPF loads, only the start that is the app
    /// asks Windows for administrator rights, and the app reads the same
    /// switches its second copies send.
    /// </summary>
    // RunningCopy.IntegritySeam is one for the process: the classes that
    // set it share this collection, which runs them one at a time.
    [Collection("SettingsManagerStatics")]
    public class StartPathTests
    {
        private static string RepoText(params string[] parts) => AuditDelta20261002Tests.RepoText(parts);

        // ── The manifest and the entry ──

        /// <summary>A copy Windows raised through its prompt cannot write to
        /// the console that started it. With <c>requireAdministrator</c> every
        /// switch raised the prompt and no caller saw an answer.</summary>
        [Fact]
        public void TheManifest_StartsTheExeWithItsCallersRights()
        {
            string text = RepoText("PadForge.App", "app.manifest");
            Assert.DoesNotContain("requireAdministrator", text, StringComparison.Ordinal);
            Assert.DoesNotContain("highestAvailable", text, StringComparison.Ordinal);

            var level = Assert.Single(XDocument.Parse(text).Descendants(),
                e => e.Name.LocalName == "requestedExecutionLevel");
            Assert.Equal("asInvoker", (string)level.Attribute("level"));
            Assert.Equal("false", (string)level.Attribute("uiAccess"));
        }

        /// <summary>The process is one of four things, decided in this order:
        /// a give-back task's run, a client run, the app's start without
        /// rights (which starts it once more with them, unless an update is
        /// replacing the exe), and the app. The app's entry sits in a method
        /// of its own that is never inlined, so the checks before it load
        /// none of WPF.</summary>
        [Fact]
        public void Main_DecidesWhatTheProcessIs_InOrder_BeforeAnyOfWpf()
        {
            string main = FenceSource.Body(RepoText("PadForge.App", "Program.cs"), "public static void Main(string[] args)");
            string[] order =
            {
                "ProtectedStart.Run();",
                "if (GiveBackRun.IsRun(args))",
                "if (ClientRun.TryRun(args, out int clientExit))",
                "if (!Environment.IsPrivilegedProcess)",
                "if (UpdateService.IsUpdateInProgress(Environment.ProcessPath)) return;",
                "Environment.ExitCode = StartWithRights.Run(args);",
                "RunTheApp();",
            };
            int at = -1;
            foreach (string step in order)
            {
                int next = main.IndexOf(step, StringComparison.Ordinal);
                Assert.True(next > at, "Program.Main does not take \"" + step + "\" in its place");
                at = next;
            }
            Assert.DoesNotContain("new App()", main, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Windows", main, StringComparison.Ordinal);

            var entry = typeof(App).Assembly.EntryPoint;
            var runTheApp = entry.DeclaringType.GetMethod("RunTheApp", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(runTheApp);
            Assert.True(runTheApp.MethodImplementationFlags.HasFlag(MethodImplAttributes.NoInlining));
        }

        /// <summary>The restart with rights hands on every word but the
        /// caller's own <c>--wait</c>, uses Windows' prompt through the
        /// shell's <c>runas</c> verb, and waits only when asked.</summary>
        [Fact]
        public void TheRestartWithRights_AsksThroughTheShell_AndHandsOnTheSwitches()
        {
            string run = FenceSource.Body(RepoText("PadForge.App", "Services", "StartWithRights.cs"),
                "internal static int Run(IReadOnlyList<string> args)");
            Assert.Contains("UseShellExecute = true,", run, StringComparison.Ordinal);
            Assert.Contains("Verb = \"runas\",", run, StringComparison.Ordinal);
            Assert.Contains("foreach (string a in CommandLineSwitches.WithoutWait(args)) start.ArgumentList.Add(a);", run, StringComparison.Ordinal);
            Assert.True(run.IndexOf("if (!wait) return 0;", StringComparison.Ordinal)
                < run.IndexOf("app.WaitForExit();", StringComparison.Ordinal));
            // The copy works in the exe's folder, never the caller's.
            Assert.Contains("WorkingDirectory = System.IO.Path.GetDirectoryName(exe),", run, StringComparison.Ordinal);
            Assert.DoesNotContain("Environment.CurrentDirectory", run, StringComparison.Ordinal);
            // The copy Windows starts hands over to one from the protected
            // folder, and a wait goes on with the app itself.
            Assert.True(run.IndexOf("if (app.ExitCode != ProtectedStart.HandedOver) return app.ExitCode;", StringComparison.Ordinal)
                < run.IndexOf("RunningCopy.Watch(AppWindowWaitMs)", StringComparison.Ordinal));
            // The test host starts nothing.
            Assert.Equal(StartWithRights.NotStarted, StartWithRights.Run(new[] { "--profile", "Racing" }));
        }

        // ── The app reads its own switches ──

        /// <summary><c>--show</c>, <c>--minimized</c> and <c>--tray</c>
        /// decide the window's first state over the Start Minimized settings.
        /// Minimized with Minimize to System Tray on is the tray, as Start
        /// Minimized is.</summary>
        [Theory]
        [InlineData("AsSet", false, false, "Shown")]
        [InlineData("AsSet", false, true, "Shown")]
        [InlineData("AsSet", true, false, "Minimized")]
        [InlineData("AsSet", true, true, "Tray")]
        [InlineData("Shown", true, true, "Shown")]
        [InlineData("Shown", true, false, "Shown")]
        [InlineData("Tray", false, false, "Tray")]
        [InlineData("Tray", true, false, "Tray")]
        [InlineData("Minimized", false, false, "Minimized")]
        [InlineData("Minimized", false, true, "Tray")]
        public void TheCommandLine_PlacesTheWindowOverTheSettings(string asked, bool startMinimized, bool minimizeToTray,
            string first)
        {
            Assert.Equal(Enum.Parse<CommandLineSwitches.WindowStart>(first), MainWindow.FirstWindowState(
                Enum.Parse<CommandLineSwitches.WindowStart>(asked), startMinimized, minimizeToTray));
        }

        /// <summary>The window's first state is read from the command line
        /// in the one place the settings were read before.</summary>
        [Fact]
        public void TheWindowsFirstState_ComesFromTheCommandLine()
        {
            string window = RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("var firstWindow = FirstWindowState(App.StartupRequest.Window,", window, StringComparison.Ordinal);
            Assert.Contains("ShouldStartMinimized = firstWindow == CommandLineSwitches.WindowStart.Minimized;", window, StringComparison.Ordinal);
            Assert.Contains("ShouldStartMinimizedToTray = firstWindow == CommandLineSwitches.WindowStart.Tray;", window, StringComparison.Ordinal);
        }

        /// <summary><c>--engine</c> decides whether the engine starts, and the
        /// touchpads' give-back takes the same decision. The steps before the
        /// engine run before it starts, so a controller turned off on the
        /// command line never appears. The steps after it run at the tail of
        /// its start, or at once when it is not to start. The host that runs
        /// them is made first.</summary>
        [Fact]
        public void TheStartsOwnSteps_RunAroundTheEngine()
        {
            string window = RepoText("PadForge.App", "MainWindow.xaml.cs");
            string[] order =
            {
                "StartAutomationHost();",
                "bool startEngine = App.StartupRequest.Engine ?? _viewModel.Settings.AutoStartEngine;",
                "RunStartSteps(CommandLineSwitches.StartPhase.BeforeEngine);",
                "_inputService.GiveBackTouchpadsNobodyKept(startEngine);",
                "if (startEngine)",
                "_inputService.StepsForTheFirstStart = () => RunStartSteps(CommandLineSwitches.StartPhase.AfterEngine);",
                "_inputService.Start();",
                "else",
                "RunStartSteps(CommandLineSwitches.StartPhase.AfterEngine);",
            };
            int at = window.IndexOf("StartAutomationHost();", StringComparison.Ordinal);
            Assert.True(at > 0);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(window, @"\bStartAutomationHost\(\);"));
            foreach (string step in order)
            {
                int next = window.IndexOf(step, at, StringComparison.Ordinal);
                Assert.True(next >= at, "the start does not take \"" + step + "\" in its place");
                at = next + step.Length;
            }
            Assert.DoesNotContain("GiveBackTouchpadsNobodyKept(_viewModel.Settings.AutoStartEngine)", window, StringComparison.Ordinal);

            // The engine's start runs them once: they are taken before they
            // run, so a later start does not run them again.
            string start = FenceSource.Body(RepoText("PadForge.App", "Services", "InputService.cs"), "public void Start()");
            int take = start.IndexOf("var afterStart = StepsForTheFirstStart;", StringComparison.Ordinal);
            int clear = start.IndexOf("StepsForTheFirstStart = null;", StringComparison.Ordinal);
            int run = start.IndexOf("afterStart();", StringComparison.Ordinal);
            Assert.True(take > 0 && clear > take && run > clear);

            // The steps run without asking Automation: a start is the user's own.
            string steps = FenceSource.Body(RepoText("PadForge.App", "MainWindow.Automation.cs"),
                "private void RunStartSteps(CommandLineSwitches.StartPhase phase)");
            Assert.Contains("_automation.Commands.RunOwn(step.Line);", steps, StringComparison.Ordinal);
        }

        // ── The running copy's message-only windows ──

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessageW(string name);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        private static IntPtr FindMessageWindow(string name) => FindWindowExW(HWND_MESSAGE, IntPtr.Zero, null, name);

        /// <summary>Names of a test's own: a window name and a registered
        /// message are one for the whole session.</summary>
        private static (string Signal, string Ready, string Show, string Exit) TestNames()
        {
            string tag = "PadForge.Tests.StartPath." + Guid.NewGuid().ToString("N");
            return (tag + ".Instance", tag + ".Ready", tag + ".Show", tag + ".Exit");
        }

        /// <summary>Runs <paramref name="body"/> on an STA thread with a
        /// dispatcher of its own, which an HwndSource needs.</summary>
        private static void OnSta(Action body)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { error = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the window test did not finish");
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }

        /// <summary>Lets the thread's queue run until it is idle, so a posted
        /// message has been taken.</summary>
        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        /// <summary>A second start that comes while the copy is still starting
        /// is remembered and honored once the window exists. From then on a
        /// request shows the window at once.</summary>
        [Fact]
        public void AShowThatCameBeforeTheWindow_IsHonoredWhenItExists() => OnSta(() =>
        {
            var names = TestNames();
            using var signal = new InstanceSignal(names.Signal, names.Ready, names.Show, names.Exit);
            Assert.True(signal.Open());
            Assert.Equal(signal.SignalWindow, FindMessageWindow(names.Signal));
            Assert.Equal(RegisterWindowMessageW(names.Show), signal.ShowMessage);

            Assert.True(PostMessageW(signal.SignalWindow, signal.ShowMessage, IntPtr.Zero, IntPtr.Zero));
            Pump();
            int shown = 0;
            signal.Attach(() => shown++, null);
            Assert.Equal(1, shown);
            // Honored once, not again at a later attach.
            signal.Attach(() => shown++, null);
            Assert.Equal(1, shown);

            Assert.True(PostMessageW(signal.SignalWindow, signal.ShowMessage, IntPtr.Zero, IntPtr.Zero));
            Pump();
            Assert.Equal(2, shown);
        });

        /// <summary>A copy that is closing takes a show and drops it, and a
        /// show that waited for the window is forgotten, so nothing brings
        /// back a window in its shutdown. On 2026-11-26 a second start's show
        /// reached the window after its tray icon was gone and threw.</summary>
        [Fact]
        public void AShowWhileTheCopyCloses_IsDropped_AndOneThatWaitedIsForgotten() => OnSta(() =>
        {
            var names = TestNames();
            using var signal = new InstanceSignal(names.Signal, names.Ready, names.Show, names.Exit);
            Assert.True(signal.Open());
            int shown = 0;
            signal.Attach(() => shown++, null);
            // The control: a show before the close is honored.
            Assert.True(PostMessageW(signal.SignalWindow, signal.ShowMessage, IntPtr.Zero, IntPtr.Zero));
            Pump();
            Assert.Equal(1, shown);
            signal.MarkClosing();
            Assert.True(PostMessageW(signal.SignalWindow, signal.ShowMessage, IntPtr.Zero, IntPtr.Zero));
            Pump();
            Assert.Equal(1, shown);

            // A show that came before the window, then the close: forgotten.
            var later = TestNames();
            using var starting = new InstanceSignal(later.Signal, later.Ready, later.Show, later.Exit);
            Assert.True(starting.Open());
            Assert.True(PostMessageW(starting.SignalWindow, starting.ShowMessage, IntPtr.Zero, IntPtr.Zero));
            Pump();
            starting.MarkClosing();
            int shownLater = 0;
            starting.Attach(() => shownLater++, null);
            Assert.Equal(0, shownLater);
        });

        /// <summary>No message but the one registered show message does
        /// anything, and none is remembered.</summary>
        [Fact]
        public void NoOtherMessage_ShowsTheWindow() => OnSta(() =>
        {
            var names = TestNames();
            using var signal = new InstanceSignal(names.Signal, names.Ready, names.Show, names.Exit);
            Assert.True(signal.Open());
            uint other = RegisterWindowMessageW(names.Show + ".Other");
            Assert.True(PostMessageW(signal.SignalWindow, other, IntPtr.Zero, IntPtr.Zero));
            Assert.True(PostMessageW(signal.SignalWindow, 0x0400 /* WM_USER */, IntPtr.Zero, IntPtr.Zero));
            Pump();
            int shown = 0;
            signal.Attach(() => shown++, null);
            Assert.Equal(0, shown);
        });

        /// <summary>The ready window stands from the end of the start until
        /// the copy begins to close. The signal window stays to the end, so a
        /// copy that waits for this one's exit still finds its process.</summary>
        [Fact]
        public void TheReadyWindow_StandsFromTheStartsEndUntilTheClose() => OnSta(() =>
        {
            var names = TestNames();
            var signal = new InstanceSignal(names.Signal, names.Ready, names.Show, names.Exit);
            // Nothing is made before the signal window.
            signal.MarkReady();
            Assert.False(signal.IsReady);
            Assert.Equal(IntPtr.Zero, FindMessageWindow(names.Ready));

            Assert.True(signal.Open());
            Assert.Equal(IntPtr.Zero, FindMessageWindow(names.Ready));
            signal.MarkReady();
            Assert.True(signal.IsReady);
            Assert.NotEqual(IntPtr.Zero, FindMessageWindow(names.Ready));

            signal.MarkClosing();
            Assert.False(signal.IsReady);
            Assert.Equal(IntPtr.Zero, FindMessageWindow(names.Ready));
            Assert.NotEqual(IntPtr.Zero, FindMessageWindow(names.Signal));

            signal.Dispose();
            Assert.Equal(IntPtr.Zero, FindMessageWindow(names.Signal));
            Assert.Equal(IntPtr.Zero, signal.SignalWindow);
        });

        /// <summary>The test host stands no window where a second copy of a
        /// PadForge that runs on this PC would find it.</summary>
        [Fact]
        public void TheAppsOwnNames_AreNeverStoodByTheTestHost() => OnSta(() =>
        {
            var names = TestNames();
            foreach (var signal in new[]
            {
                new InstanceSignal(),
                new InstanceSignal(RunningCopy.SignalWindowName.ToUpperInvariant(), names.Ready, names.Show, names.Exit),
                new InstanceSignal(names.Signal, RunningCopy.ReadyWindowName, names.Show, names.Exit),
                new InstanceSignal(names.Signal, names.Ready, RunningCopy.ShowMessageName, names.Exit),
                new InstanceSignal(names.Signal, names.Ready, names.Show, RunningCopy.ExitMessageName.ToLowerInvariant()),
            })
            {
                using (signal)
                {
                    Assert.False(signal.Open());
                    Assert.Equal(IntPtr.Zero, signal.SignalWindow);
                }
            }
            Assert.Equal(IntPtr.Zero, FindMessageWindow(names.Signal));
        });

        /// <summary>The running copy's window lets no message through from a
        /// process of lower integrity, as Windows has it by default: a posted
        /// message carries no sender to check. What a second copy without
        /// rights asks goes over the signal pipe of the window's name, which
        /// stands from the window's making, reads its client's token and is
        /// one of PadForge's own pipe names, so a client takes it only from
        /// Administrators or the system (#600 item 44).
        /// The app makes its signal window as soon as it holds the
        /// single-instance lock, and a copy that lost the race for the lock
        /// asks the winner for its window.</summary>
        [Fact]
        public void TheSignalWindow_LetsNoMessageThroughFromLowerIntegrity_AndItsPipeTakesTheAsks()
        {
            string source = RepoText("PadForge.App", "Services", "InstanceSignal.cs");
            string open = FenceSource.Body(source, "public bool Open()");
            Assert.DoesNotContain("ChangeWindowMessageFilter", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MSGFLT_ALLOW", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WM_COPYDATA", source, StringComparison.Ordinal);
            Assert.Contains("_pipe = new ExternalControlService(Answer, RunningCopy.SignalPipeName(_signalName));", open, StringComparison.Ordinal);
            // The pipe stands before the window a start looks for.
            Assert.True(open.IndexOf("_pipe.Start();", StringComparison.Ordinal)
                < open.IndexOf("_signal = MessageOnlyWindow(_signalName);", StringComparison.Ordinal));
            Assert.Contains("if (request.Refusal != null) return new ExternalControlService.Answer(ExternalControlService.Refused);",
                FenceSource.Body(source, "private ExternalControlService.Answer Answer(ExternalControlService.Request request)"), StringComparison.Ordinal);
            Assert.True(PadForge.Services.Automation.PipeOwner.IsOwnName(RunningCopy.SignalPipeName(RunningCopy.SignalWindowName)));
            Assert.DoesNotContain("SendMessageTimeout", RepoText("PadForge.App", "Services", "RunningCopy.cs"), StringComparison.Ordinal);
            Assert.Equal("PadForge.Exit", RunningCopy.ExitMessageName);

            string app = RepoText("PadForge.App", "App.xaml.cs");
            int mutex = app.IndexOf("out bool isNewInstance", StringComparison.Ordinal);
            int signal = app.IndexOf("InstanceSignal.Open();", StringComparison.Ordinal);
            int args = app.IndexOf("StartupRequest = PadForge.Common.CommandLineSwitches.Parse(StartupArgs);", StringComparison.Ordinal);
            int attach = app.IndexOf("InstanceSignal.Attach(window.ShowForACaller, window.ExitForACaller);", StringComparison.Ordinal);
            int ready = app.IndexOf("InstanceSignal.MarkReady();", StringComparison.Ordinal);
            Assert.True(mutex > 0 && signal > mutex && args > signal && attach > args && ready > attach);

            // The window's exit is the tray icon's, run once the message's
            // own handling has returned, and Activity keeps it.
            string exit = FenceSource.Body(RepoText("PadForge.App", "MainWindow.Automation.cs"), "internal void ExitForACaller()");
            Assert.Contains("_automation?.NoteExitBySignal();", exit, StringComparison.Ordinal);
            Assert.Contains("Dispatcher.BeginInvoke(new Action(ExitFromTray));", exit, StringComparison.Ordinal);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>The exit message closes PadForge, and one that comes
        /// while the copy is still starting is honored once the window
        /// exists, after a show that came with it (#600 item 44).</summary>
        [Fact]
        public void TheExitMessage_ClosesOnce_AndOneThatCameBeforeTheWindow_IsHonoredWhenItExists() => OnSta(() =>
        {
            var names = TestNames();
            using var signal = new InstanceSignal(names.Signal, names.Ready, names.Show, names.Exit);
            Assert.True(signal.Open());
            Assert.Equal(RegisterWindowMessageW(names.Exit), signal.ExitMessage);
            Assert.NotEqual(signal.ShowMessage, signal.ExitMessage);

            var order = new System.Collections.Generic.List<string>();
            SendMessageW(signal.SignalWindow, signal.ExitMessage, IntPtr.Zero, IntPtr.Zero);
            SendMessageW(signal.SignalWindow, signal.ShowMessage, IntPtr.Zero, IntPtr.Zero);
            Assert.Empty(order);
            signal.Attach(() => order.Add("show"), () => order.Add("exit"));
            Assert.Equal(new[] { "show", "exit" }, order);
            // Honored once, not again at a later attach.
            signal.Attach(() => order.Add("show"), () => order.Add("exit"));
            Assert.Equal(2, order.Count);

            SendMessageW(signal.SignalWindow, signal.ExitMessage, IntPtr.Zero, IntPtr.Zero);
            Assert.Equal(new[] { "show", "exit", "exit" }, order);
        });

        /// <summary>A copy without rights asks the copy of the checked signal
        /// window to close, over its signal pipe, and waits for its process
        /// to end. The test host is the
        /// running copy here and does not end, so the wait runs out, and the
        /// message has arrived. With no window there is no copy.</summary>
        [Fact]
        public void TheExitAsk_ReachesTheCheckedWindow_AndWaitsForTheEnd() => OnSta(() =>
        {
            var names = TestNames();
            using var signal = new InstanceSignal(names.Signal, names.Ready, names.Show, names.Exit);
            Assert.True(signal.Open());
            int exits = 0;
            signal.Attach(null, () => exits++);

            // The window of a process without rights is passed over.
            Assert.Equal(RunningCopy.Closing.NotRunning, RunningCopy.Exit(names.Signal, names.Exit, 0, 0));
            RunningCopy.IntegritySeam = pid => pid == (uint)Environment.ProcessId ? 0x3000 : null;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Assert.Equal(RunningCopy.Closing.StillRunning, RunningCopy.Exit(names.Signal, names.Exit, 0, 300));
                Assert.True(clock.ElapsedMilliseconds >= 250);
                Pump();
                Assert.Equal(1, exits);
            }
            finally
            {
                RunningCopy.IntegritySeam = null;
            }
            // The app's own names reach nothing from the test host.
            Assert.Equal(RunningCopy.Closing.NotRunning, RunningCopy.Exit());
        });

        private static System.Windows.Interop.HwndSource MessageWindow(string name)
            => new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters(name)
            {
                ParentWindow = HWND_MESSAGE,
                WindowStyle = 0,
                Width = 0,
                Height = 0,
            });

        /// <summary>Any program of the user can make message-only windows of
        /// the signal window's name. However many one process makes, they cost
        /// one integrity read, and the walk goes on to the running copy's
        /// window wherever it stands among them.</summary>
        [Fact]
        public void DecoyWindowsOfTheName_NeverHideTheRunningCopy() => OnSta(() =>
        {
            var names = TestNames();
            var windows = new System.Collections.Generic.List<System.Windows.Interop.HwndSource>();
            try
            {
                for (int i = 0; i < 65; i++) windows.Add(MessageWindow(names.Signal));
                // The window the walk reaches last stands for the copy's.
                IntPtr last = IntPtr.Zero;
                int count = 0;
                for (IntPtr at = FindWindowExW(HWND_MESSAGE, IntPtr.Zero, null, names.Signal); at != IntPtr.Zero;
                     at = FindWindowExW(HWND_MESSAGE, at, null, names.Signal))
                {
                    last = at;
                    count++;
                }
                Assert.Equal(65, count);
                var asked = new System.Collections.Generic.List<uint>();
                RunningCopy.WindowOwnerSeam = hwnd => hwnd == last ? 2u : 1u;
                RunningCopy.IntegritySeam = pid =>
                {
                    asked.Add(pid);
                    return pid == 2 ? 0x3000 : 0x2000;
                };
                try
                {
                    Assert.Equal(last, RunningCopy.FindChecked(names.Signal));
                    Assert.Equal(new uint[] { 1, 2 }, asked);
                }
                finally
                {
                    RunningCopy.WindowOwnerSeam = null;
                    RunningCopy.IntegritySeam = null;
                }
            }
            finally
            {
                foreach (var window in windows) window.Dispose();
            }
        });

        /// <summary>A copy that shows its signal window and no ready window is
        /// starting or closing, and a close may take as long as a close is
        /// given. So the app's wait gives such a copy that long before it
        /// takes it for one still starting. On 2026-11-26's evidence a close
        /// past 15 s turned a plain start into a show the closing copy
        /// dropped, and no PadForge was left running.</summary>
        [Fact]
        public void ACopyThatShowsNoReadyWindow_IsGivenTheTimeOfAClose()
        {
            var settle = typeof(RunningCopy).GetMethod(nameof(RunningCopy.WaitUntilSettled),
                BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(int), typeof(int) });
            Assert.NotNull(settle);
            Assert.True((int)settle.GetParameters()[0].DefaultValue >= RunningCopy.CloseWaitMs,
                "the app's wait gives a closing copy " + settle.GetParameters()[0].DefaultValue + " ms of a close's " + RunningCopy.CloseWaitMs);

            // The wait itself: a copy whose signal window goes while the wait
            // still runs is gone, and one that outlasts the wait is starting.
            var names = TestNames();
            RunningCopy.IntegritySeam = pid => pid == (uint)Environment.ProcessId ? 0x3000 : null;
            try
            {
                Assert.Equal(RunningCopy.Settled.Gone, RunningCopy.WaitUntilSettled(names.Signal, names.Ready, 5000, 0));
                foreach (var (standMs, waitMs, settled) in new[]
                         {
                             (800, 10000, RunningCopy.Settled.Gone),
                             (3000, 300, RunningCopy.Settled.Starting),
                         })
                {
                    using var up = new ManualResetEventSlim();
                    using var done = new ManualResetEventSlim();
                    var copy = new Thread(() =>
                    {
                        var signal = new InstanceSignal(names.Signal, names.Ready, names.Show, names.Exit);
                        signal.Open();
                        up.Set();
                        Thread.Sleep(standMs);
                        signal.Dispose();
                        done.Set();
                    });
                    copy.SetApartmentState(ApartmentState.STA);
                    copy.Start();
                    Assert.True(up.Wait(10000));
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    Assert.Equal(settled, RunningCopy.WaitUntilSettled(names.Signal, names.Ready, waitMs, 0));
                    if (settled == RunningCopy.Settled.Gone) Assert.True(clock.ElapsedMilliseconds >= standMs - 200);
                    Assert.True(done.Wait(10000));
                    copy.Join();
                }
            }
            finally
            {
                RunningCopy.IntegritySeam = null;
            }
        }

        /// <summary>The script libraries answer whether PadForge runs by the
        /// same walk RunningCopy.FindChecked makes: to the last window named
        /// PadForge.Instance, with each process's rights read once, so windows
        /// another program makes under that name cannot hide the running copy.
        /// Each library's own tests run it (ScriptAutoHotkeyTests,
        /// ScriptPowerShellTests, ScriptPythonTests).</summary>
        [Fact]
        public void TheScriptLibraries_WalkEveryWindowOfTheName_AndReadEachProcessOnce()
        {
            foreach (var (file, start, walks, remembers) in new[]
                     {
                         ("PadForge.ahk", "static Runs() {", "while (after := DllCall(\"FindWindowEx\"", "seen.Has(pid)"),
                         ("PadForge.psm1", "private static bool PadForgeRuns()", "while ((after = FindWindowExW(new IntPtr(-3), after, null, \"PadForge.Instance\")) != IntPtr.Zero)", "seen.TryGetValue(pid, out rights)"),
                         ("padforge.py", "def _padforge_runs():", "while True:", "if pid.value not in seen:"),
                     })
            {
                string source = RepoText("PadForge.App", "Resources", "Automation", file);
                int at = source.IndexOf(start, StringComparison.Ordinal);
                Assert.True(at >= 0, file);
                string close = file == "padforge.py" ? "\ndef " : file == "PadForge.psm1" ? "\n        }" : "\n    }";
                int end = source.IndexOf(close, at + start.Length, StringComparison.Ordinal);
                string walk = source.Substring(at, (end < 0 ? source.Length : end) - at);
                Assert.DoesNotContain("64", walk, StringComparison.Ordinal);
                Assert.Contains(walks, walk, StringComparison.Ordinal);
                Assert.Contains(remembers, walk, StringComparison.Ordinal);
            }
        }

        /// <summary>A close may run as long as the engine's stop waits for a
        /// virtual controller's create, plus the stop's other bounded waits
        /// and the MIDI shutdown, so the time a close is given is at least
        /// their sum, read from the bounds themselves, and Stop waits by those
        /// bounds.</summary>
        [Fact]
        public void TheTimeACloseIsGiven_CoversTheStopsBoundedWaits()
        {
            int waits = PadForge.Common.Input.InputManager.LifecycleWaitMs + PadForge.Common.Input.InputManager.StopJoinsMs
                + PadForge.Common.Input.MidiVirtualController.DisconnectTimeoutMs;
            Assert.True(RunningCopy.CloseWaitMs >= waits, RunningCopy.CloseWaitMs + " ms of a close's " + waits);
            Assert.Equal(TimeSpan.FromMilliseconds(PadForge.Common.Input.InputManager.LifecycleWaitMs), PadForge.Common.Input.InputManager.LifecycleWaitBound);
            string stop = FenceSource.Body(RepoText("PadForge.App", "Common", "Input", "InputManager.cs"), "public void Stop()");
            Assert.Contains("_pollingThread.Join(timeout: TimeSpan.FromMilliseconds(PollJoinMs))", stop, StringComparison.Ordinal);
            Assert.Contains("_mouseInjectorThread.Join(timeout: TimeSpan.FromMilliseconds(InjectorJoinMs))", stop, StringComparison.Ordinal);
            Assert.Contains("FlushPendingInput(lockWaitMs: FlushWaitMs);", stop, StringComparison.Ordinal);
            Assert.Contains("AwaitPendingLifecycleTasks(LifecycleWaitBound);", stop, StringComparison.Ordinal);
        }

        /// <summary>The app runs with rights alone (Program.Main), and its lock
        /// is the private namespace's. When that lock cannot be made, the start
        /// takes no name any program could hold first: it is told no lock, and
        /// stops with a word. The test host takes no lock of the app's.</summary>
        [Fact]
        public void ALockThatCannotBeMade_IsNoLock_AndTheStartStopsWithAWord()
        {
            using (var taken = App.TakeSingleInstanceLock(out bool newInstance))
            {
                Assert.Null(taken);
                Assert.False(newInstance);
            }
            string app = RepoText("PadForge.App", "App.xaml.cs");
            Assert.DoesNotContain("new Mutex(", app, StringComparison.Ordinal);
            Assert.DoesNotContain("InstanceMutexName", RepoText("PadForge.App", "Services", "RunningCopy.cs"), StringComparison.Ordinal);
            int take = app.IndexOf("_singleInstanceMutex = TakeSingleInstanceLock(out bool isNewInstance);", StringComparison.Ordinal);
            int none = app.IndexOf("if (_singleInstanceMutex == null)", take, StringComparison.Ordinal);
            int word = app.IndexOf("StopForNoLock();", none, StringComparison.Ordinal);
            int signal = app.IndexOf("InstanceSignal = new Services.InstanceSignal();", StringComparison.Ordinal);
            Assert.True(take > 0 && none > take && word > none && signal > word);
            string stop = FenceSource.Body(app, "private void StopForNoLock()");
            Assert.True(stop.IndexOf("Strings.Instance.Start_NoLock", StringComparison.Ordinal)
                < stop.IndexOf("Shutdown(1);", StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(Strings.Instance.Start_NoLock));
            Assert.NotEqual("Start_NoLock", Strings.Instance.Start_NoLock);
        }

        /// <summary>Two copies started with rights in the same moment: the one
        /// that lost the race for the lock hands its command line to the one
        /// that won, as a second start does, so its switches are run and its
        /// starter reads their exit code. With no switch the winner shows its
        /// window once it is ready. When the winner is gone before it took
        /// the line, the losing copy takes the lock again and becomes the
        /// app.</summary>
        [Fact]
        public void ACopyThatLostTheRaceForTheLock_HandsItsCommandLineToTheWinner()
        {
            var sent = new System.Collections.Generic.List<string>();
            int shown = 0, settles = 0;
            ClientRun.Seams Seams(bool copy) => new ClientRun.Seams
            {
                CopyRuns = () => copy,
                Exchange = line => { sent.Add(line); return line == "engine on" ? "error busy" : "ok"; },
                PipeStands = () => true,
                Settle = () => { settles++; return RunningCopy.Settled.Ready; },
                ShowWindow = () => { shown++; return true; },
                LendForeground = () => { },
                Watch = () => null,
                ExitCopy = () => RunningCopy.Closing.NotRunning,
                HoldForClose = () => null,
                Say = _ => { },
                SayError = _ => { },
                Tell = _ => { },
                TellError = _ => { },
            };

            Assert.True(App.HandToTheCopyThatWon(new[] { "--profile", "Racing" }, out int code, Seams(true)));
            Assert.Equal(ClientRun.Done, code);
            Assert.Equal(new[] { "activate Racing" }, sent);
            Assert.Equal(0, shown);

            // The winner's answer is the start's exit code.
            sent.Clear();
            Assert.True(App.HandToTheCopyThatWon(new[] { "--engine", "on" }, out code, Seams(true)));
            Assert.Equal(ClientRun.AnsweredAnError, code);

            sent.Clear();
            Assert.True(App.HandToTheCopyThatWon(Array.Empty<string>(), out code, Seams(true)));
            Assert.Equal((1, 1), (shown, settles));
            Assert.Empty(sent);

            Assert.False(App.HandToTheCopyThatWon(new[] { "--profile", "Racing" }, out _, Seams(false)));
            Assert.Empty(sent);

            // The start lets go of the lock it opened before it hands over,
            // so the winner's end is seen, and takes the lock again when the
            // winner is gone.
            string start = FenceSource.Body(RepoText("PadForge.App", "App.xaml.cs"), "protected override void OnStartup(StartupEventArgs e)");
            int release = start.IndexOf("_singleInstanceMutex.Dispose();", StringComparison.Ordinal);
            int hand = start.IndexOf("HandToTheCopyThatWon(e.Args, out int handed)", StringComparison.Ordinal);
            int again = start.IndexOf("_singleInstanceMutex = TakeSingleInstanceLock(out isNewInstance);", StringComparison.Ordinal);
            Assert.True(release > 0 && hand > release && again > hand);
        }

        /// <summary>The error dialog says crash.log holds the details only
        /// when the append went through. When it did not, the dialog says so,
        /// and that its own text is the report.</summary>
        [Fact]
        public void TheErrorDialog_SaysCrashLogHoldsTheDetails_OnlyWhenTheyWereWritten()
        {
            // The sentence that says the file holds the details, without the
            // report page's place that follows it.
            string wrote = Strings.Instance.App_UnexpectedError_Format.Split('\n')
                .First(line => line.Contains("crash.log", StringComparison.Ordinal)).Split(". ")[0];
            Assert.DoesNotContain("{", wrote, StringComparison.Ordinal);
            App.CrashLogAppendSeam = (path, text) => throw new System.IO.IOException("The disk is full.");
            try
            {
                Assert.False(App.AppendCrashLog("x"));
                string text = App.CrashText(App.AppendCrashLog("x"), "Boom", "at Somewhere()");
                Assert.DoesNotContain(wrote.Trim(), text, StringComparison.Ordinal);
                Assert.Contains("Boom", text, StringComparison.Ordinal);
                Assert.Contains("at Somewhere()", text, StringComparison.Ordinal);

                var appended = new System.Collections.Generic.List<string>();
                App.CrashLogAppendSeam = (path, text2) => appended.Add(text2);
                Assert.True(App.AppendCrashLog("y"));
                Assert.Equal(new[] { "y" }, appended);
                Assert.Contains(wrote.Trim(), App.CrashText(true, "Boom", "at Somewhere()"), StringComparison.Ordinal);
            }
            finally
            {
                App.CrashLogAppendSeam = null;
            }
            // Both handlers say what their own append came to.
            string app = RepoText("PadForge.App", "App.xaml.cs");
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(app, @"CrashText\(written, ").Count);
            Assert.DoesNotContain("File.AppendAllText(logPath", app, StringComparison.Ordinal);
        }

        /// <summary>The signal pipe reads its client's token, as the control
        /// pipe does: the user's own second copy at medium integrity is taken,
        /// and a process at low integrity or in an app container cannot make
        /// the running copy show its window or close. The token is the seam's
        /// here, for the cases a test bench cannot make.</summary>
        // A copy with rights posts the window's message, so the pipe's
        // token check is read in a test host without them.
        [UnelevatedFact]
        public void TheSignalPipe_TakesTheUsersOwnCopy_AndRefusesALowIntegrityOrSandboxedOne() => OnSta(() =>
        {
            var desktop = PadForge.Services.Automation.AutomationClientIdentity.ThisDesktop();
            RunningCopy.IntegritySeam = pid => pid == (uint)Environment.ProcessId ? 0x3000 : null;
            try
            {
                foreach (var (integrity, container, taken) in new[] { (0x2000, false, true), (0x1000, false, false), (0x2000, true, false), (0x3000, false, true) })
                {
                    var names = TestNames();
                    using var signal = new InstanceSignal(names.Signal, names.Ready, names.Show, names.Exit)
                    {
                        ReadToken = _ => new PadForge.Services.Automation.AutomationClientToken(desktop.Server, desktop.Session, integrity, container),
                    };
                    Assert.True(signal.Open());
                    Assert.True(signal.PipeServes);
                    int shown = 0, exits = 0;
                    signal.Attach(() => shown++, () => exits++);
                    string token = integrity.ToString("X") + (container ? " in an app container" : string.Empty);
                    Assert.True(taken == RunningCopy.Show(names.Signal, names.Show, 0), token);
                    Pump();
                    Assert.True((taken ? 1 : 0) == shown, token);
                    Assert.Equal(RunningCopy.Closing.StillRunning, RunningCopy.Exit(names.Signal, names.Exit, 0, 100));
                    Pump();
                    Assert.True((taken ? 1 : 0) == exits, token);
                }
            }
            finally
            {
                RunningCopy.IntegritySeam = null;
            }
        });

        /// <summary>With rights, as PadForge runs: a program PadForge starts as
        /// the user, at medium integrity, asks this copy over its signal pipe,
        /// takes the pipe's server by its owner, and the window shows.</summary>
        [ElevatedFact]
        public void ACopyWithoutRights_AsksACopyWithRights_OverTheSignalPipe() => OnSta(() =>
        {
            var names = TestNames();
            using var signal = new InstanceSignal(names.Signal, names.Ready, names.Show, names.Exit);
            Assert.True(signal.Open());
            int shown = 0;
            signal.Attach(() => shown++, null);
            string probe = PluginLauncherTests.ProbeExe;
            Assert.True(System.IO.File.Exists(probe), "the helper was not built beside the tests: " + probe);
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PadForge.Tests.SignalPipe." + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            string report = System.IO.Path.Combine(dir, "report.txt");
            PadForge.Services.Plugins.IPluginProcess process = null;
            try
            {
                var launch = new PadForge.Services.Plugins.PluginLauncher().Start(
                    new PadForge.Services.Plugins.PluginCommand(probe,
                        PadForge.Services.Plugins.PluginFile.Quote(probe) + " ask " + names.Signal + " show", System.IO.Path.GetDirectoryName(probe)),
                    new System.Collections.Generic.Dictionary<string, string> { ["PADFORGE_PROBE_REPORT"] = report });
                Assert.True(launch.Process != null, "not started: " + launch.Refusal + ", error " + launch.Error);
                process = launch.Process;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!System.IO.File.Exists(report) && clock.ElapsedMilliseconds < 20000)
                {
                    Pump();
                    Thread.Sleep(50);
                }
                string text = System.IO.File.ReadAllText(report);
                Assert.Contains("owner=1", text, StringComparison.Ordinal);
                Assert.Contains("reply=ok", text, StringComparison.Ordinal);
                Pump();
                Assert.Equal(1, shown);
            }
            finally
            {
                process?.Stop();
                process?.Dispose();
                try { System.IO.Directory.Delete(dir, true); } catch (System.IO.IOException) { }
            }
        });

        /// <summary>The start-pending event lives in the session's namespace,
        /// where any program of the user can make it first. One whose list
        /// lets nobody open it is no pending start, and the question throws
        /// nothing, so a plain start is never stopped by it.</summary>
        [Fact]
        public void AStartPendingEventNobodyMayOpen_IsNoPendingStart_AndThrowsNothing()
        {
            var names = TestNames();
            // The control: an ordinary event of the name is a pending start.
            using (var open = new EventWaitHandle(false, EventResetMode.ManualReset, RunningCopy.StartPendingName(names.Signal)))
                Assert.True(RunningCopy.StartIsPending(names.Signal));

            var closed = TestNames();
            using var squatter = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, RunningCopy.StartPendingName(closed.Signal),
                out bool created, new System.Security.AccessControl.EventWaitHandleSecurity());
            Assert.True(created);
            Assert.False(RunningCopy.StartIsPending(closed.Signal));
        }
    }
}
