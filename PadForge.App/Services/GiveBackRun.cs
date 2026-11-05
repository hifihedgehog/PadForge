using System;
using System.IO.Pipes;
using System.Threading;
using PadForge.Common;
using PadForge.Engine.Touchpads;

namespace PadForge.Services
{
    /// <summary>
    /// What a give-back task starts once a device is owed
    /// (<see cref="GiveBackTask"/>): PadForge.exe with one of two switches, as
    /// SYSTEM, with no window and none of the app. It talks to HidHide's control
    /// device and removes a driver package the way the app does, on every
    /// platform and every Windows PadForge runs on.
    ///
    /// <para>The give-back is tried for two minutes, since at a boot HidHide's
    /// control device or the collection can still be coming up. A run that never
    /// got through exits 1, and what the task holds behind it tries, then the
    /// next boot. An attempt that hangs in a driver call never comes back, so a
    /// watchdog ends the run the same way past <see cref="RunLimitMs"/>. A
    /// registration's instance has no time limit of its own, for its wait.</para>
    /// </summary>
    internal static class GiveBackRun
    {
        internal const string TouchpadSwitch = "--give-back-touchpad";
        internal const string PackageSwitch = "--give-back-package";

        internal const int TryEveryMs = 5000;
        internal const int Tries = 24;

        /// <summary>The longest a run lasts: its tries, and as long again for the
        /// attempts themselves.</summary>
        internal const int RunLimitMs = 2 * Tries * TryEveryMs;

        internal enum Attempt
        {
            Done,
            TryAgain,

            /// <summary>HidHide's control device is not there. At a boot it
            /// can still be coming, so this counts as done only at the last
            /// try: a driver that is gone took its hides with it.</summary>
            NothingAnswers,
        }

        /// <summary>The run's ways out to the machine, each a test's to
        /// replace.</summary>
        internal sealed class Seams
        {
            public Func<string, bool> AppHolds = GiveBackTask.AnAppHolds;
            public Func<string, bool> TaskStands = GiveBackTask.Stands;
            public Func<bool> UpdateInProgress = () => UpdateService.IsUpdateInProgress(Environment.ProcessPath);
            public Func<string, Attempt> TakeHideOff = NewHideTaker();
            public Func<string, Attempt> RemovePackages = RemovePackagesNow;
            public Action<string> DeleteTask = GiveBackTask.DeleteByName;
            public Func<bool> HidHideInstalled = () => DriverInstaller.TryGetHidHideStatus(out _);
            public Action<int, string> Wait = WaitUnlessAnAppHolds;
            public Func<string, IDisposable> OneAtATime = TakeTheTurn;
        }

        /// <summary>Whether the command line is a task's: a switch, the ID and
        /// the task's name.</summary>
        internal static bool IsRun(string[] args)
            => args is { Length: 3 } && (args[0] == TouchpadSwitch || args[0] == PackageSwitch);

        /// <summary>The whole run. 0 when nothing is left to do here, 1 when
        /// what the task holds behind this run is to try. That runs with no look at
        /// the app, so a run says 0 when an app took the device in the meantime.</summary>
        internal static int Run(string[] args, Seams seams = null)
        {
            // With no seams, the test host gives nothing back.
            if (seams == null && GiveBackTask.OffLimitsForTest) return 1;
            string id = args[1], task = args[2];
            if (seams == null) StartWatchdog(task);
            seams ??= new Seams();
            try
            {
                var attempt = args[0] == TouchpadSwitch ? seams.TakeHideOff : seams.RemovePackages;
                using var turn = seams.OneAtATime(task);
                if (turn == null) return 0;
                for (int i = 0; i < Tries; i++)
                {
                    // An app took the device again, and the task it registered
                    // watches it now. A task that is gone owes nothing.
                    if (seams.AppHolds(task) || !seams.TaskStands(task)) return 0;
                    // An update's helper replaces this exe now, and this run
                    // would hold the very file.
                    if (seams.UpdateInProgress()) return 1;
                    var result = attempt(id);
                    // A driver that is gone took its hides with it. One still
                    // installed that never answered keeps them for when it does.
                    if (result == Attempt.Done || (result == Attempt.NothingAnswers && i == Tries - 1 && !seams.HidHideInstalled()))
                    {
                        // An app that took the device during the attempt registered
                        // this task for itself, and its cover is not this run's to end.
                        if (!seams.AppHolds(task)) seams.DeleteTask(task);
                        return 0;
                    }
                    if (i + 1 < Tries) seams.Wait(TryEveryMs, task);
                }
            }
            catch
            {
            }
            return seams.AppHolds(task) ? 0 : 1;
        }

        /// <summary>The wait between two tries. A registration waits for this run
        /// to finish, so the wait ends as soon as an app holds the device: the next
        /// try's first look leaves it to that app.</summary>
        internal static void WaitUnlessAnAppHolds(int ms, string task)
        {
            const int LookEveryMs = 100;
            for (int waited = 0; waited < ms && !GiveBackTask.AnAppHolds(task); waited += LookEveryMs)
                Thread.Sleep(Math.Min(LookEveryMs, ms - waited));
        }

        /// <summary>A retry finds no hide left, so the restart a failed try
        /// still owes is remembered across tries.</summary>
        private static Func<string, Attempt> NewHideTaker()
        {
            bool restartOwed = false;
            return id => TakeHideOffNow(id, ref restartOwed,
                node => TouchpadCapture.RestartForWindows(node).GetAwaiter().GetResult());
        }

        /// <summary>One try at what <see cref="InputService.TakeHideOff"/> does
        /// for the app, with the restart waited for, since this process ends
        /// after it.</summary>
        internal static Attempt TakeHideOffNow(string instanceId, ref bool restartOwed, Func<string, bool> restart)
        {
            const int ERROR_FILE_NOT_FOUND = 2;
            if (!HidHideController.TryProbe(out int error))
                return error == ERROR_FILE_NOT_FOUND ? Attempt.NothingAnswers : Attempt.TryAgain;
            if (!HidHideController.RemoveFromBlacklist(new[] { instanceId }, out var removed)) return Attempt.TryAgain;
            restartOwed |= removed.Count > 0;
            if (restartOwed && !restart(instanceId)) return Attempt.TryAgain;
            restartOwed = false;
            return Attempt.Done;
        }

        private static Attempt RemovePackagesNow(string bindId)
            => VendorUsbDriverInstaller.RemovePackagesBinding(bindId, _ => { }) ? Attempt.Done : Attempt.TryAgain;

        /// <summary>One run per task, as two can wake at one app's end, held by the
        /// first instance of a pipe with no reader. The app's registration waits for
        /// this pipe to go, after it holds its own (<see cref="GiveBackTask"/>): each
        /// side says it is here and then looks for the other, so at least one sees
        /// the other, and a device is never taken back from an app that just took
        /// it. Null when the turn is another's or could not be asked for.</summary>
        internal static IDisposable TakeTheTurn(string task)
        {
            try
            {
                return new NamedPipeServerStream(GiveBackTask.RunPipeName(task), PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.FirstPipeInstance);
            }
            catch
            {
                return null;
            }
        }

        private static void StartWatchdog(string task)
        {
            new Thread(() =>
            {
                Thread.Sleep(RunLimitMs);
                Environment.Exit(GiveBackTask.AnAppHolds(task) ? 0 : 1);
            })
            { IsBackground = true }.Start();
        }
    }
}
