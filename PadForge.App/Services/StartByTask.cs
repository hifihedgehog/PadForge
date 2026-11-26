using System;
using System.Collections.Generic;
using PadForge.Common;

namespace PadForge.Services
{
    /// <summary>
    /// A start of PadForge without rights while Start at Login is on (#600
    /// item 44): it runs the Start at Login task, which starts PadForge with
    /// rights and no prompt, as at logon, and hands the started copy its
    /// start switches over the signal window.
    ///
    /// <para>The task's access list lets the user it starts PadForge for run
    /// it (<see cref="StartupHelper.RunRight"/>). Any program of that user
    /// can then start PadForge with rights, which Start at Login already
    /// does at every logon, and gains nothing the logon start lacks: the
    /// task runs PadForge's own exe from its protected folder with its fixed
    /// command line and the allow list's variables (LogonStart), and the
    /// started copy reads only the start set's switches from this one
    /// (<see cref="InstanceSignal.TakeStartSwitches"/>).</para>
    ///
    /// <para>Every failure keeps Windows' prompt: a line with a switch the
    /// start set does not hold, a user whose token Windows did not split (the
    /// task would start PadForge without rights, which would come back here),
    /// a task that does not start this exe at its size, a task this user may
    /// not run, or a copy that never came up.</para>
    ///
    /// <para>Runs before WPF loads and uses none of it.</para>
    /// </summary>
    internal static class StartByTask
    {
        /// <summary>Set by the test assembly for the life of its process
        /// (PadForge.Tests StartByTaskOffLimits), never by the app: the task
        /// is not run and the run's own seams are not used.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>How long the started copy has to take the
        /// switches.</summary>
        internal const int HandMs = 5000;

        internal const string NotHandedText = "PadForge started without its switches.";

        private const int TokenElevationType = 18;

        /// <summary>The run's ways out to the machine, each a test's to
        /// replace.</summary>
        internal sealed class Seams
        {
            public Func<bool> TokenIsSplit = StartByTask.TokenIsSplit;
            public Func<bool> TaskStartsThis = StartupHelper.TaskStartsThisExe;
            public Func<IDisposable> MarkPending = () => RunningCopy.MarkStartPending();
            public Func<bool> RunTask = StartupHelper.RunTask;
            public Func<bool> WaitForCopy = () => RunningCopy.WaitForStartedCopy(StartWithRights.AppWindowWaitMs);
            public Func<IReadOnlyList<string>, bool> Hand = words => RunningCopy.HandStartSwitches(words, HandMs);
            public Func<Func<int?>> Watch = () => RunningCopy.Watch();
            public Action<string> SayError = CallerConsole.SayError;
        }

        /// <summary>
        /// Starts PadForge through the task when it can. True when this
        /// process is done and exits with <paramref name="exitCode"/>: 0, or
        /// with <c>--wait</c> the app's own exit code, or 1 when the copy
        /// came up without the switches. False when the start goes on to
        /// Windows' prompt.
        /// </summary>
        internal static bool TryRun(IReadOnlyList<string> args, out int exitCode)
        {
            if (OffLimitsForTest)
            {
                exitCode = 0;
                return false;
            }
            return TryRun(args, new Seams(), out exitCode);
        }

        /// <summary>The same run through the seams given.</summary>
        internal static bool TryRun(IReadOnlyList<string> args, Seams seams, out int exitCode)
        {
            exitCode = 0;
            if (!CommandLineSwitches.CrossesToTheTask(args) || !seams.TokenIsSplit() || !seams.TaskStartsThis()) return false;
            // Held from before the run until the switches are handed, so the
            // started copy waits for them and a logon start does not.
            using (seams.MarkPending())
            {
                if (!seams.RunTask() || !seams.WaitForCopy()) return false;
                var words = CommandLineSwitches.StartSwitchesOnly(args);
                if (!seams.Hand(words) && words.Count > 0)
                {
                    seams.SayError(NotHandedText);
                    exitCode = ClientRun.AnsweredAnError;
                }
            }
            if (CommandLineSwitches.Parse(args).Wait)
                exitCode = seams.Watch()?.Invoke() ?? StartWithRights.NotStarted;
            return true;
        }

        /// <summary>Whether Windows split this user's token at logon
        /// (TokenElevationTypeLimited), the one case where the task's run
        /// starts PadForge with the linked token's rights
        /// (LogonStart.TokenToRun).</summary>
        internal static bool TokenIsSplit()
        {
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            return Automation.AutomationClientIdentity.ReadInt(me.AccessToken, TokenElevationType) == LogonStart.TokenElevationTypeLimited;
        }
    }
}
