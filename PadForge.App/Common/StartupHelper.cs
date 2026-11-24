using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace PadForge.Common
{
    /// <summary>
    /// Manages PadForge's launch-at-logon entry via a Task Scheduler task.
    /// The app needs administrator rights. A start without them asks Windows
    /// for them (Program.Main, Services/StartWithRights.cs), so an entry in
    /// HKCU\Run would raise the UAC prompt at every logon. While the
    /// manifest required the rights, before #597, Windows passed over that
    /// entry without a word and PadForge never started from it. A scheduled
    /// task with <c>/SC ONLOGON</c> + <c>/RL HIGHEST</c> is the documented
    /// Microsoft path that starts a trusted program at logon with its rights
    /// and no prompt, and a start that has them is the app at once.
    ///
    /// <para>schtasks output is locale-dependent, so this helper drives it by
    /// exit code only (0 = task exists / created / deleted; non-zero = not
    /// found or error). Same lesson as HM #17 / PadForge #69 — never grep
    /// the human-readable text on a localized Windows install.</para>
    /// </summary>
    public static class StartupHelper
    {
        private const string LegacyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "PadForge";
        private const string TaskName = "PadForge";

        /// <summary>Set by the test assembly for the life of its process
        /// (PadForge.Tests StartupOffLimits), never by the app. The task is
        /// made for the running program and replaces the one of that name,
        /// so a test host that created it would put itself in place of the
        /// PadForge its PC starts at logon. While this is set, schtasks is
        /// never started and the legacy Run value is not deleted: the task
        /// reads as absent, as when schtasks fails. False in production,
        /// where every path is as it was.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>
        /// True when PadForge is configured to launch at logon. Reports the
        /// scheduled task as the source of truth, but also reports the legacy
        /// HKCU\Run entry as enabled so a user who had startup on under an
        /// older build sees the toggle accurately reflect their intent until
        /// the migration runs.
        /// </summary>
        public static bool IsStartupEnabled()
        {
            return ScheduledTaskExists() || LegacyRunEntryExists();
        }

        /// <summary>
        /// Creates or removes the launch-at-logon entry. Always purges the
        /// legacy HKCU\Run entry as well. It never started PadForge while
        /// the manifest required administrator rights, it would raise the UAC
        /// prompt at every logon now, and left around it confuses future
        /// IsStartupEnabled() probes.
        /// </summary>
        public static void SetStartupEnabled(bool enabled)
        {
            DeleteLegacyRunEntry();

            if (enabled) CreateScheduledTask();
            else DeleteScheduledTask();
        }

        /// <summary>
        /// Idempotent one-shot migration. Call once during app startup so a
        /// user who toggled launch-at-logon on under an older (HKCU\Run) build
        /// gets auto-upgraded to a working scheduled task without having to
        /// re-toggle the setting. Safe to call when no legacy entry exists.
        /// </summary>
        public static void MigrateLegacyEntryIfNeeded()
        {
            try
            {
                if (LegacyRunEntryExists() && !ScheduledTaskExists())
                    CreateScheduledTask();
                DeleteLegacyRunEntry();
            }
            catch
            {
                // Migration is best-effort; never fail the launch path.
            }
        }

        private static bool LegacyRunEntryExists()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, false);
                return key?.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        private static void DeleteLegacyRunEntry()
        {
            if (OffLimitsForTest) return;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, true);
                key?.DeleteValue(AppName, throwOnMissingValue: false);
            }
            catch { }
        }

        private static bool ScheduledTaskExists()
        {
            return RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;
        }

        private static void CreateScheduledTask()
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            // /SC ONLOGON: trigger whenever a user logs on. /RL HIGHEST: run
            // with the highest privileges the principal is granted (gives
            // elevation without a UAC prompt because Task Scheduler is a
            // trusted launcher). /F: overwrite any existing task with the
            // same name so re-toggling startup on always picks up the
            // current exe path (matters when the user moves PadForge.exe
            // between deploys / manual reinstalls).
            //
            // /TR's value embeds the exe path inside double quotes so
            // Task Scheduler treats it as a single argument even when the
            // path contains spaces. The outer quotes belong to schtasks
            // argument parsing; the inner escaped quotes are the literal
            // quotes Windows passes to CreateProcess at trigger time.
            string taskRun = $"\\\"{exePath}\\\"";
            string args = $"/Create /TN \"{TaskName}\" /TR \"{taskRun}\" /SC ONLOGON /RL HIGHEST /F";
            RunSchtasks(args);
        }

        private static void DeleteScheduledTask()
        {
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        }

        private static int RunSchtasks(string arguments)
        {
            if (OffLimitsForTest) return -1;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return -1;
                p.WaitForExit();
                return p.ExitCode;
            }
            catch
            {
                return -1;
            }
        }
    }
}
