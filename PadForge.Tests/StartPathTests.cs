using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using System.Xml.Linq;
using PadForge.Common;
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
        private static (string Signal, string Ready, string Show) TestNames()
        {
            string tag = "PadForge.Tests.StartPath." + Guid.NewGuid().ToString("N");
            return (tag + ".Instance", tag + ".Ready", tag + ".Show");
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
            using var signal = new InstanceSignal(names.Signal, names.Ready, names.Show);
            Assert.True(signal.Open());
            Assert.Equal(signal.SignalWindow, FindMessageWindow(names.Signal));
            Assert.Equal(RegisterWindowMessageW(names.Show), signal.ShowMessage);

            Assert.True(PostMessageW(signal.SignalWindow, signal.ShowMessage, IntPtr.Zero, IntPtr.Zero));
            Pump();
            int shown = 0;
            signal.Attach(() => shown++);
            Assert.Equal(1, shown);
            // Honored once, not again at a later attach.
            signal.Attach(() => shown++);
            Assert.Equal(1, shown);

            Assert.True(PostMessageW(signal.SignalWindow, signal.ShowMessage, IntPtr.Zero, IntPtr.Zero));
            Pump();
            Assert.Equal(2, shown);
        });

        /// <summary>No message but the one registered show message does
        /// anything, and none is remembered.</summary>
        [Fact]
        public void NoOtherMessage_ShowsTheWindow() => OnSta(() =>
        {
            var names = TestNames();
            using var signal = new InstanceSignal(names.Signal, names.Ready, names.Show);
            Assert.True(signal.Open());
            uint other = RegisterWindowMessageW(names.Show + ".Other");
            Assert.True(PostMessageW(signal.SignalWindow, other, IntPtr.Zero, IntPtr.Zero));
            Assert.True(PostMessageW(signal.SignalWindow, 0x0400 /* WM_USER */, IntPtr.Zero, IntPtr.Zero));
            Pump();
            int shown = 0;
            signal.Attach(() => shown++);
            Assert.Equal(0, shown);
        });

        /// <summary>The ready window stands from the end of the start until
        /// the copy begins to close. The signal window stays to the end, so a
        /// copy that waits for this one's exit still finds its process.</summary>
        [Fact]
        public void TheReadyWindow_StandsFromTheStartsEndUntilTheClose() => OnSta(() =>
        {
            var names = TestNames();
            var signal = new InstanceSignal(names.Signal, names.Ready, names.Show);
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
                new InstanceSignal(RunningCopy.SignalWindowName.ToUpperInvariant(), names.Ready, names.Show),
                new InstanceSignal(names.Signal, RunningCopy.ReadyWindowName, names.Show),
                new InstanceSignal(names.Signal, names.Ready, RunningCopy.ShowMessageName),
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

        /// <summary>The running copy lets the show message through from a
        /// process without rights, and nothing else. The app makes its signal
        /// window as soon as it holds the single-instance lock, and a copy
        /// that lost the race for the lock asks the winner for its window.</summary>
        [Fact]
        public void TheSignalWindow_LetsTheShowMessageThroughAlone()
        {
            string open = FenceSource.Body(RepoText("PadForge.App", "Services", "InstanceSignal.cs"), "public bool Open()");
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(
                RepoText("PadForge.App", "Services", "InstanceSignal.cs"), @"ChangeWindowMessageFilterEx\(_"));
            Assert.Contains("ChangeWindowMessageFilterEx(_signal.Handle, _showMessage, MSGFLT_ALLOW, IntPtr.Zero);", open, StringComparison.Ordinal);

            string app = RepoText("PadForge.App", "App.xaml.cs");
            int mutex = app.IndexOf("out bool isNewInstance", StringComparison.Ordinal);
            int signal = app.IndexOf("InstanceSignal.Open();", StringComparison.Ordinal);
            int args = app.IndexOf("StartupRequest = PadForge.Common.CommandLineSwitches.Parse(StartupArgs);", StringComparison.Ordinal);
            int attach = app.IndexOf("InstanceSignal.Attach(window.ShowForACaller);", StringComparison.Ordinal);
            int ready = app.IndexOf("InstanceSignal.MarkReady();", StringComparison.Ordinal);
            Assert.True(mutex > 0 && signal > mutex && args > signal && attach > args && ready > attach);
        }
    }
}
