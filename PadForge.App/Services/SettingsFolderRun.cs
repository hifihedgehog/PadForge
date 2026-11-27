using System;
using System.Diagnostics;
using System.IO;
using PadForge.Common;
using PadForge.Services.Automation;

namespace PadForge.Services
{
    /// <summary>
    /// <c>--settings-folder</c> (#600 item 44): Explorer opens on the folder
    /// of PadForge.xml with the file selected, or on the folder alone before
    /// the first save. It needs no running copy and starts nothing else.
    ///
    /// <para>Explorer is started through <see cref="ShellLaunch.TryStart"/>,
    /// as the review's Show in Explorer is. The test host hands the shell nothing
    /// (ShellLaunch.OffLimitsForTest). Nothing is printed on success: the
    /// settings file's path is the user's alone, and a script that asked
    /// for the folder has it open in front of the user.</para>
    /// </summary>
    internal static class SettingsFolderRun
    {
        /// <summary>The words Explorer is given: the file selected when it
        /// exists, else its folder.</summary>
        internal static string ExplorerArguments(string settingsFile, Func<string, bool> fileExists)
        {
            if (fileExists(settingsFile)) return "/select,\"" + settingsFile + "\"";
            return "\"" + Path.GetDirectoryName(settingsFile) + "\"";
        }

        /// <summary>Opens the folder and gives the run's exit code: 0 when
        /// Explorer started, 1 when it did not, with why.</summary>
        internal static int Run(Action<string> sayError)
        {
            string file = AutomationFileSettings.DefaultPath();
            var start = new ProcessStartInfo(ShellLaunch.Explorer, ExplorerArguments(file, File.Exists));
            if (ShellLaunch.TryStart(start, out string why)) return ClientRun.Done;
            sayError("Explorer did not open the settings folder" + (string.IsNullOrEmpty(why) ? "." : ": " + why));
            return ClientRun.AnsweredAnError;
        }
    }
}
