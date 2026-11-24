using System;
using System.Collections.Generic;
using System.Diagnostics;
using PadForge.Common;

namespace PadForge.Services
{
    /// <summary>
    /// Starts PadForge as the app, with the administrator rights it needs for
    /// HIDMaestro's driver and device nodes and for HidHide (#597).
    ///
    /// <para>PadForge.exe carries no demand for those rights in its manifest.
    /// A copy that hands switches to the running PadForge, or serves an
    /// assistant, has to run with its caller's rights to answer on the
    /// caller's console. So the exe starts as its caller, and the one start
    /// that is the app asks Windows for the rights itself, once, with the
    /// same prompt the manifest raised. A start that already has them, as from
    /// the logon task, an update or an administrator's prompt, asks for
    /// nothing.</para>
    /// </summary>
    internal static class StartWithRights
    {
        /// <summary>The exit code when PadForge could not be started with
        /// rights. A person who says no to the prompt chose not to start
        /// it.</summary>
        internal const int NotStarted = 1;

        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app: no copy of the exe is started.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>
        /// Starts this exe again with rights and the same switches, and
        /// returns this process's exit code. With <c>--wait</c> it stays until
        /// that copy exits and returns its code.
        /// </summary>
        internal static int Run(IReadOnlyList<string> args)
        {
            if (OffLimitsForTest) return NotStarted;
            bool wait = CommandLineSwitches.Parse(args).Wait;
            var start = new ProcessStartInfo(Environment.ProcessPath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
            };
            foreach (string a in CommandLineSwitches.WithoutWait(args)) start.ArgumentList.Add(a);
            try
            {
                using var app = Process.Start(start);
                if (app == null) return NotStarted;
                if (!wait) return 0;
                app.WaitForExit();
                return app.ExitCode;
            }
            catch
            {
                // The prompt turned down (Windows' ERROR_CANCELLED), or the
                // exe could not be started at all.
                return NotStarted;
            }
        }
    }
}
