using System;
using System.Diagnostics;
using System.IO;

namespace PadForge.Common
{
    /// <summary>
    /// The one way the app hands the user's shell something: a web page, a
    /// folder or a file to open, the program a Run Program macro names, and
    /// text for the clipboard. A program started here starts with
    /// Process.Start, with PadForge's rights and a copy of its variables.
    ///
    /// <para>In the app every call goes straight through. The test host
    /// hands the shell nothing (ShellOffLimits in PadForge.Tests): a test
    /// that reached one of these, or a build of the code with a check
    /// removed, would open a browser tab, an Explorer window or a program on
    /// the desktop the tests run on, or replace what its owner had
    /// copied.</para>
    /// </summary>
    internal static class ShellLaunch
    {
        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app. False in production, where every call is as it
        /// was.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Test seam: takes a start in the test host in place of
        /// Windows. Read only while <see cref="OffLimitsForTest"/> is set.</summary>
        internal static Func<ProcessStartInfo, Process> StartSeam;

        /// <summary>Starts what <paramref name="psi"/> names. The test host
        /// starts nothing and is answered no process, as the shell answers a
        /// launch it took over without starting one.</summary>
        internal static Process Start(ProcessStartInfo psi)
            => OffLimitsForTest ? StartSeam?.Invoke(psi) : Process.Start(psi);

        /// <summary>The same for a program and its arguments.</summary>
        internal static Process Start(string fileName, string arguments)
            => OffLimitsForTest ? StartSeam?.Invoke(new ProcessStartInfo(fileName, arguments)) : Process.Start(fileName, arguments);

        /// <summary>Starts what <paramref name="psi"/> names and says whether
        /// it started. <paramref name="why"/> is Windows' reason when it did
        /// not. The test host starts nothing.</summary>
        internal static bool TryStart(ProcessStartInfo psi, out string why)
        {
            if (OffLimitsForTest)
            {
                why = "the test host starts nothing";
                return false;
            }
            try
            {
                using var started = Process.Start(psi);
                why = null;
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                why = ex.Message;
                return false;
            }
        }

        /// <summary>Starts one of PadForge's own helpers, as the DSX
        /// stand-in is. The test host starts nothing.</summary>
        internal static Process StartOwnHelper(ProcessStartInfo psi)
            => OffLimitsForTest ? null : Process.Start(psi);

        /// <summary>Explorer by its full path in Windows' folder.</summary>
        internal static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        /// <summary>Puts text on the clipboard. The test host leaves the
        /// clipboard as it is.</summary>
        internal static void SetClipboardText(string text)
        {
            if (OffLimitsForTest) return;
            System.Windows.Clipboard.SetText(text);
        }
    }
}
