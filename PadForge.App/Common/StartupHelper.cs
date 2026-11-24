using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using PadForge.Services;

namespace PadForge.Common
{
    /// <summary>
    /// Manages PadForge's launch-at-logon entry via a Task Scheduler task.
    /// The app needs administrator rights. A start without them asks Windows
    /// for them (Program.Main, Services/StartWithRights.cs), so an entry in
    /// HKCU\Run would raise the UAC prompt at every logon. While the
    /// manifest required the rights, before #597, Windows passed over that
    /// entry without a word and PadForge never started from it. A scheduled
    /// task is the documented Microsoft path that starts a trusted program
    /// at logon with its rights and no prompt.
    ///
    /// <para>The task runs as SYSTEM at the logon of the user who turned the
    /// setting on, and starts PadForge in that user's session through
    /// <see cref="LogonStart"/>, which builds the app's variables itself
    /// (#597). The task of the form before #597 ran PadForge as the user,
    /// with the variables of the user's profile, which every program of the
    /// user can set, and with schtasks' defaults: a 72-hour time limit
    /// (Microsoft, ExecutionTimeLimit, PT72H when not given), after which
    /// Task Scheduler ends the task's process, no start on battery and a stop
    /// on battery, and priority 7. A start with rights that finds that form
    /// makes the task again in this one (<see cref="AuditTask"/>).</para>
    ///
    /// <para>schtasks output is locale-dependent, so this helper drives it by
    /// exit code only (0 = task exists / created / deleted; non-zero = not
    /// found or error). Same lesson as HM #17 / PadForge #69 — never grep
    /// the human-readable text on a localized Windows install. The one text
    /// it reads is the task's definition (/XML), which is not
    /// translated.</para>
    ///
    /// <para>The task runs PadForge with rights at every logon, so it may only
    /// run an exe in a folder of PadForge's own that no other account can
    /// change (#597, <see cref="ExeFolder"/>). Turning the setting on from a
    /// shared folder is refused, and a start with rights removes a task that
    /// runs an exe from one. schtasks is started by its full path in
    /// System32: started by name alone, Windows would look for it in
    /// PadForge's own folder first.</para>
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
        public static bool SetStartupEnabled(bool enabled)
        {
            DeleteLegacyRunEntry();

            if (!enabled)
            {
                DeleteScheduledTask();
                return true;
            }
            string exe = Environment.ProcessPath;
            if (!ExeFolder.MayHoldTasks(exe))
            {
                DeleteScheduledTask();
                return false;
            }
            return CreateScheduledTask(exe, CurrentUserSid());
        }

        /// <summary>
        /// A start with rights: a logon task whose exe sits in a folder other
        /// accounts may change is removed, since a shared folder keeps no
        /// task. An own folder is protected first (<see cref="ExeFolder.Check"/>),
        /// and its task stays, made again in the current form for the same
        /// user when it is of the form before #597, or when its exe, its size
        /// or the unpack folder changed since it was made, as after an
        /// update. True when the task was removed.
        /// </summary>
        internal static bool AuditTask()
        {
            if (OffLimitsForTest || !ScheduledTaskExists()) return false;
            string definition = RunSchtasksOutput($"/Query /TN \"{TaskName}\" /XML");
            var task = ReadTask(definition);
            if (task.Exe != null && ExeFolder.Check(task.Exe) == ExeFolder.Standing.Own)
            {
                string sid = task.Sid ?? CurrentUserSid();
                if (!IsCurrent(definition, task.Exe, sid, FileLength(task.Exe), ProtectedFolder.Natives))
                    CreateScheduledTask(task.Exe, sid);
                return false;
            }
            DeleteScheduledTask();
            return !ScheduledTaskExists();
        }

        /// <summary>Points the logon task at another exe, as the move does,
        /// for the same user. No task is made where none was.</summary>
        internal static bool RepointTask(string exe)
        {
            if (!ScheduledTaskExists()) return true;
            string sid = ReadTask(RunSchtasksOutput($"/Query /TN \"{TaskName}\" /XML")).Sid ?? CurrentUserSid();
            return ExeFolder.MayHoldTasks(exe) && CreateScheduledTask(exe, sid);
        }

        /// <summary>The program a task's definition runs: its first Exec
        /// action's Command, without quotes. Null when there is none.</summary>
        internal static string TaskExe(string definition)
        {
            string command = FirstOf(definition, "Command");
            if (command == null) return null;
            command = command.Trim().Trim('"');
            return command.Length == 0 ? null : Environment.ExpandEnvironmentVariables(command);
        }

        private static readonly Regex LogonRun = new(
            "\"(?<exe>[^\"]+)\" " + LogonStart.Switch + " (?<sid>S-1-[0-9-]+)", RegexOptions.CultureInvariant);

        /// <summary>
        /// The PadForge a logon task starts and the user it is for. The
        /// current form names both in its action. The form before #597 runs
        /// the exe as its Command, for the user its principal names, by SID or
        /// by account name. Either is null when the definition does not say.
        /// </summary>
        internal static (string Exe, string Sid) ReadTask(string definition)
        {
            string arguments = FirstOf(definition, "Arguments");
            var run = arguments == null ? Match.Empty : LogonRun.Match(arguments);
            if (run.Success) return (run.Groups["exe"].Value, run.Groups["sid"].Value);
            string exe = TaskExe(definition);
            if (exe != null && Path.GetFileName(exe).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)) exe = null;
            return (exe, SidOf(FirstOf(definition, "UserId")));
        }

        /// <summary>Whether a definition is the one <see cref="TaskDefinition"/>
        /// makes for these: the same action, trigger user, account and
        /// settings, compared as text.</summary>
        internal static bool IsCurrent(string definition, string exe, string sid, long size, string natives)
        {
            string want = TaskDefinition(exe, sid, size, natives);
            if (want == null || string.IsNullOrWhiteSpace(definition)) return false;
            foreach (string name in new[] { "Command", "Arguments", "ExecutionTimeLimit", "Priority", "MultipleInstancesPolicy",
                         "DisallowStartIfOnBatteries", "StopIfGoingOnBatteries", "StopOnIdleEnd", "RunOnlyIfIdle", "RunLevel" })
                if (!string.Equals(FirstOf(definition, name)?.Trim(), FirstOf(want, name)?.Trim(), StringComparison.Ordinal)) return false;
            return string.Equals(UserIdsOf(definition), UserIdsOf(want), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The task's definition for an exe and a user, or null when
        /// the action cannot be written (<see cref="LogonStart.Action"/>).</summary>
        internal static string TaskDefinition(string exe, string sid, long size, string natives)
        {
            string action = LogonStart.Action(exe, sid, natives, size);
            return action == null ? null : LogonStart.TaskXml(sid, action, Environment.GetFolderPath(Environment.SpecialFolder.System));
        }

        /// <summary>The text of a definition's first element of a name, or
        /// null.</summary>
        private static string FirstOf(string definition, string name)
        {
            if (string.IsNullOrWhiteSpace(definition)) return null;
            try
            {
                int start = definition.IndexOf('<');
                var doc = XDocument.Parse(start > 0 ? definition.Substring(start) : definition);
                foreach (var element in doc.Descendants())
                    if (element.Name.LocalName == name) return element.Value;
            }
            catch
            {
            }
            return null;
        }

        /// <summary>Every UserId a definition names, the trigger's and the
        /// principal's, in their order.</summary>
        private static string UserIdsOf(string definition)
        {
            try
            {
                int start = definition.IndexOf('<');
                var doc = XDocument.Parse(start > 0 ? definition.Substring(start) : definition);
                var ids = new StringBuilder();
                foreach (var element in doc.Descendants())
                    if (element.Name.LocalName == "UserId") ids.Append(element.Value.Trim()).Append('|');
                return ids.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>A principal's SID: the text itself when it is one, or the
        /// account's SID. Null when neither.</summary>
        internal static string SidOf(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return null;
            string text = userId.Trim();
            if (LogonStart.LooksLikeSid(text)) return text;
            try
            {
                return new System.Security.Principal.NTAccount(text)
                    .Translate(typeof(System.Security.Principal.SecurityIdentifier)).Value;
            }
            catch
            {
                return null;
            }
        }

        private static string CurrentUserSid()
        {
            try { return System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value; }
            catch { return null; }
        }

        private static long FileLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return -1; }
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
                if (LegacyRunEntryExists() && !ScheduledTaskExists() && ExeFolder.MayHoldTasks(Environment.ProcessPath))
                    CreateScheduledTask(Environment.ProcessPath, CurrentUserSid());
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

        /// <summary>
        /// Makes the task for <paramref name="exePath"/> and the user
        /// <paramref name="sid"/> names, replacing one of the name (/F). It
        /// runs as SYSTEM, so its definition is written to the protected
        /// Work folder, where no program of the user can change it between
        /// the write and schtasks' read, and is deleted afterwards, the
        /// give-back tasks' way (GiveBackTask.Register). /RU names the
        /// account the definition names (Microsoft, schtasks create).
        /// </summary>
        private static bool CreateScheduledTask(string exePath, string sid)
        {
            if (string.IsNullOrEmpty(exePath) || OffLimitsForTest) return false;
            string definition = TaskDefinition(exePath, sid, FileLength(exePath), ProtectedFolder.Natives);
            if (definition == null) return false;
            string folder = ProtectedWork.Folder("Logon");
            if (folder == null) return false;
            string xml = Path.Combine(folder, "PadForge-logon.xml");
            try
            {
                // The encoding the definition declares, with its byte order mark.
                File.WriteAllText(xml, definition, Encoding.Unicode);
                return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xml}\" /RU SYSTEM /F") == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { File.Delete(xml); } catch { }
            }
        }

        private static void DeleteScheduledTask()
        {
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        }

        /// <summary>schtasks by its full path in System32.</summary>
        internal static string SchtasksPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

        /// <summary>What schtasks printed, or null when it failed.</summary>
        private static string RunSchtasksOutput(string arguments)
        {
            if (OffLimitsForTest) return null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = SchtasksPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var p = PadForge.Services.RightsVariables.Start(psi);
                if (p == null) return null;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0 ? output : null;
            }
            catch
            {
                return null;
            }
        }

        private static int RunSchtasks(string arguments)
        {
            if (OffLimitsForTest) return -1;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = SchtasksPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = PadForge.Services.RightsVariables.Start(psi);
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
