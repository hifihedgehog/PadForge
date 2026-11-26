using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A start of PadForge without rights while Start at Login is on (#600
    /// item 44): it runs the Start at Login task in place of Windows'
    /// prompt, the task's access list lets the user it is for run it, and
    /// the started copy takes only the start set's switches, over its signal
    /// window, during its start alone. No task is run or registered here:
    /// every way out is a seam, a pure function or a window of the test's
    /// own names.
    /// </summary>
    // RunningCopy.IntegritySeam is one for the process: the classes that
    // set it share this collection, which runs them one at a time.
    [Collection("SettingsManagerStatics")]
    public class StartByTaskTests
    {
        private const string Sid = "S-1-5-21-3155206576-1441097098-1295047565-1001";

        private static string RepoText(params string[] parts) => AuditDelta20261002Tests.RepoText(parts);

        // ── The task's access list ──

        /// <summary>Full control for Administrators and SYSTEM, read and run
        /// for the user the task starts PadForge for, and for nobody
        /// else.</summary>
        [Fact]
        public void TheRunRight_IsForTheTasksUserAlone()
        {
            Assert.Equal("D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;" + Sid + ")", StartupHelper.RunRight(Sid));
            Assert.Null(StartupHelper.RunRight("Everyone"));
            Assert.Null(StartupHelper.RunRight(null));
            Assert.Equal(0x1200a9, StartupHelper.ReadAndRun);
        }

        /// <summary>The audit reads the list Task Scheduler gives back, in
        /// the form measured on this PC on 2026-11-25: FRFX comes back as
        /// 0x1200a9, ahead of the folder's inherited entries. The task as it
        /// was made before, and an entry for read alone, give the user no
        /// run.</summary>
        [Fact]
        public void TheAudit_KnowsATaskTheUserMayRun()
        {
            const string measuredWithTheRight = "D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;0x1200a9;;;" + Sid + ")(A;ID;0x1f019f;;;BA)(A;ID;0x1f019f;;;SY)(A;ID;FA;;;BA)(A;;FR;;;SY)";
            const string measuredBefore = "D:(A;ID;0x1f019f;;;BA)(A;ID;0x1f019f;;;SY)(A;ID;FA;;;BA)(A;;FR;;;SY)";
            Assert.True(StartupHelper.LetsRun(measuredWithTheRight, Sid));
            Assert.True(StartupHelper.LetsRun(StartupHelper.RunRight(Sid), Sid));
            Assert.False(StartupHelper.LetsRun(measuredBefore, Sid));
            Assert.False(StartupHelper.LetsRun(StartupHelper.RunRight(Sid), "S-1-5-21-1-2-3-1002"));
            Assert.False(StartupHelper.LetsRun("D:(A;;FR;;;" + Sid + ")", Sid));
            Assert.False(StartupHelper.LetsRun("D:(D;;FA;;;" + Sid + ")", Sid));
            Assert.False(StartupHelper.LetsRun("not a descriptor", Sid));
            Assert.False(StartupHelper.LetsRun(null, Sid));
        }

        /// <summary>schtasks keeps a definition's descriptor as text and
        /// applies none of it (measured), so the right is set through Task
        /// Scheduler's own call once the task is made, every way it is made
        /// passes there, and the audit remakes a task whose list lacks it.
        /// The test host reaches no task.</summary>
        [Fact]
        public void TheRight_IsSetOnEveryTaskMade_AndTheAuditRemakesATaskWithoutIt()
        {
            string source = RepoText("PadForge.App", "Common", "StartupHelper.cs");
            string create = FenceSource.Body(source, "private static bool CreateScheduledTask(string exePath, string sid)");
            // A code line, never a comment that names the call.
            var setLine = System.Text.RegularExpressions.Regex.Match(create, @"^[ \t]*SetTaskDescriptor\(RunRight\(sid\)\);",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            Assert.True(setLine.Success, "the task is made without its run right");
            Assert.True(create.IndexOf("/RU SYSTEM /F\") != 0) return false;", StringComparison.Ordinal) < setLine.Index);
            string audit = FenceSource.Body(source, "internal static TaskRemoval AuditTask()");
            Assert.Contains("|| !LetsRun(TaskDescriptor(), sid))", audit, StringComparison.Ordinal);
            Assert.Contains("CreateScheduledTask(task.Exe, sid);", audit, StringComparison.Ordinal);
            string set = FenceSource.Body(source, "private static bool SetTaskDescriptor(string sddl)");
            Assert.Contains("Call(task, \"SetSecurityDescriptor\", sddl, 0);", set, StringComparison.Ordinal);
            Assert.DoesNotContain("SecurityDescriptor>", RepoText("PadForge.App", "Services", "LogonStart.cs"), StringComparison.Ordinal);

            Assert.False(StartupHelper.RunTask());
            Assert.False(StartupHelper.TaskStartsThisExe());
            Assert.Equal(StartupHelper.TaskRemoval.None, StartupHelper.AuditTask());
            string starts = FenceSource.Body(source, "internal static bool TaskStartsThisExe()");
            Assert.Contains("IsCurrent(RunSchtasksOutput($\"/Query /TN \\\"{TaskName}\\\" /XML\"), exe, CurrentUserSid(), FileLength(exe), ProtectedFolder.Natives)", starts, StringComparison.Ordinal);
        }

        // ── The start set ──

        [Theory]
        [InlineData("")]
        [InlineData("--minimized")]
        [InlineData("/tray")]
        [InlineData("--maximized --show")]
        [InlineData("--profile Racing --engine off --wait")]
        [InlineData("--profile=Racing --default-profile")]
        [InlineData("--controllers off --slot 2 on")]
        [InlineData("--record start")]
        public void ALineOfTheStartSet_RunsTheTask(string line)
            => Assert.True(CommandLineSwitches.CrossesToTheTask(Words(line)));

        [Theory]
        [InlineData("--exit")]
        [InlineData("--hide")]
        [InlineData("--state")]
        [InlineData("--record stop")]
        [InlineData("--json")]
        [InlineData("--updated")]
        [InlineData("--profile")]
        [InlineData("--minimized --mcp")]
        [InlineData(UpdateService.ApplyUpdateSwitch + " C:\\PadForge\\PadForge.exe 1234")]
        public void AnyOtherLine_KeepsThePrompt(string line)
            => Assert.False(CommandLineSwitches.CrossesToTheTask(Words(line)));

        /// <summary>Only the start set's words cross, in their order, with
        /// their values. A line that cannot be read gives none.</summary>
        [Fact]
        public void OnlyTheStartSetsWords_Cross()
        {
            Assert.Equal(new[] { "--profile", "Racing", "--slot", "2", "off", "--record", "start", "/tray" },
                CommandLineSwitches.StartSwitchesOnly(new[] { "--profile", "Racing", "--hide", "--wait", "--slot", "2", "off", "--updated", "--exit", "--record", "start", "--json", "/tray" }));
            Assert.Empty(CommandLineSwitches.StartSwitchesOnly(new[] { "--mcp", "--profile", "Racing" }));
            Assert.Empty(CommandLineSwitches.StartSwitchesOnly(new[] { "--profile" }));
            Assert.Empty(CommandLineSwitches.StartSwitchesOnly(new[] { UpdateService.ApplyUpdateSwitch, "C:\\PadForge\\PadForge.exe", "--profile", "x" }));
            Assert.Empty(CommandLineSwitches.StartSwitchesOnly(null));
        }

        private static string[] Words(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // ── The start ──

        private sealed class Run
        {
            public readonly List<string> Order = new List<string>();
            public readonly List<string> Errors = new List<string>();
            public IReadOnlyList<string> Handed;
            public bool TaskRuns = true;
            public bool Split = true;
            public bool StartsThis = true;
            public bool CopyComes = true;
            public bool Takes = true;
            public bool Held;
            public Func<int?> Wait;
            public readonly StartByTask.Seams Seams;

            public Run()
            {
                Seams = new StartByTask.Seams
                {
                    TokenIsSplit = () => { Order.Add("split?"); return Split; },
                    TaskStartsThis = () => { Order.Add("starts this exe?"); return StartsThis; },
                    MarkPending = () => { Held = true; Order.Add("pending"); return new Release(() => { Held = false; Order.Add("released"); }); },
                    RunTask = () => { Order.Add(Held ? "run task while pending" : "run task"); return TaskRuns; },
                    WaitForCopy = () => { Order.Add("wait for copy"); return CopyComes; },
                    Hand = words => { Order.Add(Held ? "hand while pending" : "hand"); Handed = words; return Takes; },
                    Watch = () => { Order.Add("watch"); return Wait == null ? null : new RunningCopy.Hold<int?>(Wait, null); },
                    SayError = Errors.Add,
                };
            }

            public (bool Done, int Code) Go(string line)
            {
                bool done = StartByTask.TryRun(Words(line), Seams, out int code);
                return (done, code);
            }
        }

        private sealed class Release : IDisposable
        {
            private readonly Action _then;
            public Release(Action then) => _then = then;
            public void Dispose() => _then();
        }

        [Fact]
        public void AStartWithoutRights_RunsTheTask_AndHandsTheStartSwitches()
        {
            var run = new Run();
            Assert.Equal((true, 0), run.Go("--profile Racing --tray"));
            Assert.Equal(new[] { "split?", "starts this exe?", "pending", "run task while pending", "wait for copy", "hand while pending", "released" }, run.Order);
            Assert.Equal(new[] { "--profile", "Racing", "--tray" }, run.Handed);
            Assert.Empty(run.Errors);

            // A plain start hands nothing, and still ends the copy's wait.
            var plain = new Run();
            Assert.Equal((true, 0), plain.Go(""));
            Assert.Empty(plain.Handed);
        }

        /// <summary>Every failure goes on to Windows' prompt, which starts
        /// PadForge with the whole line: no task or one this user may not
        /// run, and a task that ran but started no copy in the time.</summary>
        [Fact]
        public void EveryFailure_KeepsThePrompt()
        {
            var refused = new Run { TaskRuns = false };
            Assert.Equal((false, 0), refused.Go("--minimized"));
            Assert.Equal(new[] { "split?", "starts this exe?", "pending", "run task while pending", "released" }, refused.Order);

            // A task that starts another copy of PadForge, or this exe at
            // another size, is never run.
            var elsewhere = new Run { StartsThis = false };
            Assert.Equal((false, 0), elsewhere.Go("--minimized"));
            Assert.Equal(new[] { "split?", "starts this exe?" }, elsewhere.Order);

            // A user who is no administrator gets PadForge without rights
            // from the task, which would come back here and run it again.
            var standard = new Run { Split = false };
            Assert.Equal((false, 0), standard.Go(""));
            Assert.Equal(new[] { "split?" }, standard.Order);

            var noCopy = new Run { CopyComes = false };
            Assert.Equal((false, 0), noCopy.Go("--minimized"));
            Assert.Null(noCopy.Handed);

            var other = new Run();
            Assert.Equal((false, 0), other.Go("--minimized --hide"));
            Assert.Empty(other.Order);

            // The test host's own run reaches no task.
            Assert.False(StartByTask.TryRun(new[] { "--minimized" }, out int code));
            Assert.Equal(0, code);
        }

        [Fact]
        public void ACopyThatTookNoSwitches_Gives1_AndAWaitGivesTheAppsCode()
        {
            var dropped = new Run { Takes = false };
            Assert.Equal((true, 1), dropped.Go("--profile Racing"));
            Assert.Equal(new[] { StartByTask.NotHandedText }, dropped.Errors);

            var nothingToHand = new Run { Takes = false };
            Assert.Equal((true, 0), nothingToHand.Go(""));
            Assert.Empty(nothingToHand.Errors);

            var waits = new Run { Wait = () => 42 };
            Assert.Equal((true, 42), waits.Go("--wait --engine off"));
            Assert.Equal(new[] { "--engine", "off" }, waits.Handed);
            Assert.Equal("watch", waits.Order[^1]);
        }

        /// <summary>Main runs the task only for the app's start without
        /// rights, after an update's hold and before the prompt.</summary>
        [Fact]
        public void Main_TriesTheTask_BetweenTheUpdatesHoldAndThePrompt()
        {
            string main = FenceSource.Body(RepoText("PadForge.App", "Program.cs"), "public static void Main(string[] args)");
            int privileged = main.IndexOf("if (!Environment.IsPrivilegedProcess)", StringComparison.Ordinal);
            int update = main.IndexOf("UpdateService.IsUpdateInProgress(Environment.ProcessPath)", StringComparison.Ordinal);
            int task = main.IndexOf("StartByTask.TryRun(args, out int taskExit)", StringComparison.Ordinal);
            int prompt = main.IndexOf("StartWithRights.Run(args)", StringComparison.Ordinal);
            Assert.True(privileged > 0 && update > privileged && task > update && prompt > task);

            string app = RepoText("PadForge.App", "App.xaml.cs");
            int open = app.IndexOf("InstanceSignal.Open();", StringComparison.Ordinal);
            int take = app.IndexOf("InstanceSignal.TakeStartSwitches(Services.InstanceSignal.StartSwitchesWaitMs)", StringComparison.Ordinal);
            int parse = app.IndexOf("StartupRequest = PadForge.Common.CommandLineSwitches.Parse(StartupArgs);", StringComparison.Ordinal);
            Assert.True(open > 0 && take > open && parse > take);
            Assert.Contains("if (StartupArgs.Length == 0)", app.Substring(open, take - open), StringComparison.Ordinal);
        }

        // ── The started copy's side ──

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

        private static (string Signal, string Ready, string Show, string Exit) TestNames()
        {
            string tag = "PadForge.Tests.StartByTask." + Guid.NewGuid().ToString("N");
            return (tag + ".Instance", tag + ".Ready", tag + ".Show", tag + ".Exit");
        }

        /// <summary>
        /// The started copy waits only while a start says its switches are
        /// coming, takes them over the signal pipe of its checked signal
        /// window, keeps the start
        /// set's alone, and takes nothing once its start is past. The hand
        /// comes from another thread, as from another process.
        /// </summary>
        [Fact]
        public void TheStartedCopy_TakesTheStartSetOnce_AndOnlyDuringItsStart_Live() => OnSta(() =>
        {
            var names = TestNames();
            using var signal = new InstanceSignal(names.Signal, names.Ready, names.Show, names.Exit);
            Assert.True(signal.Open());

            // No start is pending: no wait.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Assert.Empty(signal.TakeStartSwitches(3000));
            Assert.True(clock.ElapsedMilliseconds < 1000);

            RunningCopy.IntegritySeam = pid => pid == (uint)Environment.ProcessId ? 0x3000 : null;
            try
            {
                using var pending = RunningCopy.MarkStartPending(names.Signal);
                Assert.NotNull(pending);
                Assert.True(RunningCopy.StartIsPending(names.Signal));
                bool found = false, handed = false;
                var hand = new Thread(() =>
                {
                    found = RunningCopy.WaitForStartedCopy(2000, names.Signal);
                    handed = found && RunningCopy.HandStartSwitches(new[] { "--tray", "--profile", "Racing", "--exit", "--hide", "--engine", "off" }, 3000, names.Signal);
                });
                hand.Start();
                clock.Restart();
                var taken = signal.TakeStartSwitches(5000);
                Assert.True(hand.Join(5000));
                Assert.True(found);
                Assert.True(handed);
                Assert.True(clock.ElapsedMilliseconds < 4000);
                Assert.Equal(new[] { "--tray", "--profile", "Racing", "--engine", "off" }, taken);

                // Past the wait nothing is taken, the start still pending.
                Assert.False(RunningCopy.HandStartSwitches(new[] { "--profile", "Other" }, 500, names.Signal));

                // A line that is not an array of words is never a start's
                // switches, and a line past the pipe's length is not handed.
                Assert.Equal(new[] { "--minimized" }, RunningCopy.ReadStartSwitches("[\"--minimized\"]"));
                Assert.Empty(RunningCopy.ReadStartSwitches("[]"));
                foreach (string other in new[] { "--minimized", "[1]", "[null]", "{}", "", "[\"a\"" })
                    Assert.Null(RunningCopy.ReadStartSwitches(other));
                Assert.False(RunningCopy.HandStartSwitches(new[] { new string('x', RunningCopy.MostStartSwitchBytes) }, 500, names.Signal));
            }
            finally
            {
                RunningCopy.IntegritySeam = null;
            }
            Assert.False(RunningCopy.StartIsPending(names.Signal));
            // The app's own names are the test host's never.
            Assert.Null(RunningCopy.MarkStartPending());
        });

    }
}
