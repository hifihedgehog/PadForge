using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PadForge.Common
{
    /// <summary>
    /// The one way the app hands the user's shell something: a web page, a
    /// folder or a file to open, the program a Run Program macro names, and
    /// text for the clipboard.
    ///
    /// <para>PadForge runs with administrator rights, and a program it
    /// started with <c>Process.Start</c> got them too, so a Run Program macro
    /// fired by a program at Send Inputs ran its target as administrator
    /// (#597). Everything here starts through the desktop shell's own object
    /// instead, the way PowerToys starts a program without rights
    /// (PowerToys src/common/utils/elevation.h, ShellExecuteFromExplorer):
    /// ShellWindows finds the desktop's window, its document is the desktop
    /// folder's view, whose Application is the shell, and the shell's
    /// ShellExecute starts the program inside explorer.exe, with the signed-in
    /// user's rights. Probed from a process with rights on this PC: the child
    /// ran at medium integrity. When the shell itself runs with rights (UAC
    /// off), so does the program, as before. A target that needs rights gets
    /// Windows' own prompt, as it does from Explorer. When there is no desktop
    /// shell, nothing starts and the caller is told why: a program without
    /// rights can end the user's shell, so falling back to a start with
    /// PadForge's rights would hand them over again.</para>
    ///
    /// <para>In the app every call goes through. The test host hands the
    /// shell nothing (ShellOffLimits in PadForge.Tests): a test that reached
    /// one of these, or a build of the code with a check removed, would open a
    /// browser tab, an Explorer window or a program on the desktop the tests
    /// run on, or replace what its owner had copied.</para>
    /// </summary>
    internal static class ShellLaunch
    {
        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app. False in production, where every call is as it
        /// was.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>What is handed to the desktop shell: the file, its
        /// arguments, its working folder, the verb and the window
        /// state.</summary>
        internal sealed record UserStart(string File, string Arguments, string Folder, string Verb, int Show);

        /// <summary>Test seam: the desktop shell's start, answering null when it
        /// started the program or why it did not. Null in production.</summary>
        internal static Func<UserStart, string> UserShellSeam;

        /// <summary>How long the desktop shell is given to take a
        /// start.</summary>
        internal const int ShellWaitMs = 15000;

        /// <summary>Starts what <paramref name="psi"/> names with the user's
        /// rights, through the desktop shell. The shell gives no process
        /// back, so this always answers null. The test host starts nothing.</summary>
        internal static Process Start(ProcessStartInfo psi)
        {
            if (OffLimitsForTest && UserShellSeam == null) return null;
            AsTheUser(psi, out _);
            return null;
        }

        /// <summary>The same for a program and its arguments.</summary>
        internal static Process Start(string fileName, string arguments)
            => Start(new ProcessStartInfo(fileName, arguments ?? string.Empty));

        /// <summary>Starts <paramref name="psi"/> with the user's rights and
        /// says whether the shell took it. <paramref name="why"/> says why
        /// not.</summary>
        internal static bool AsTheUser(ProcessStartInfo psi, out string why)
        {
            if (OffLimitsForTest && UserShellSeam == null)
            {
                why = "the test host starts nothing";
                return false;
            }
            var start = new UserStart(psi.FileName, ArgumentsOf(psi), psi.WorkingDirectory ?? string.Empty,
                psi.Verb ?? string.Empty, ShowOf(psi.WindowStyle));
            why = UserShellSeam != null ? UserShellSeam(start) : ThroughTheDesktop(start);
            return why == null;
        }

        /// <summary>Starts one of PadForge's own helpers with PadForge's
        /// rights, as the DSX stand-in is. Never a program a person or a
        /// setting names. The test host starts nothing.</summary>
        internal static Process StartOwnHelper(ProcessStartInfo psi)
            => OffLimitsForTest ? null : PadForge.Services.RightsVariables.Start(psi);

        /// <summary>Explorer by its full path in Windows' folder.</summary>
        internal static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        /// <summary>The arguments as one line: the string when it was given,
        /// otherwise the list quoted as the C runtime reads it back
        /// (Microsoft, Parsing C command-line arguments).</summary>
        internal static string ArgumentsOf(ProcessStartInfo psi)
        {
            if (!string.IsNullOrEmpty(psi.Arguments) || psi.ArgumentList.Count == 0) return psi.Arguments ?? string.Empty;
            var line = new StringBuilder();
            foreach (string argument in psi.ArgumentList)
            {
                if (line.Length > 0) line.Append(' ');
                Quote(line, argument);
            }
            return line.ToString();
        }

        private static void Quote(StringBuilder line, string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                line.Append(argument);
                return;
            }
            line.Append('"');
            int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') line.Append('\\', slashes * 2 + 1);
                else line.Append('\\', slashes);
                slashes = 0;
                line.Append(c);
            }
            line.Append('\\', slashes * 2);
            line.Append('"');
        }

        /// <summary>ShellExecute's window state for a ProcessStartInfo's
        /// (Microsoft, ShowWindow: SW_SHOWNORMAL 1, SW_SHOWMINIMIZED 2,
        /// SW_SHOWMAXIMIZED 3, SW_HIDE 0).</summary>
        internal static int ShowOf(ProcessWindowStyle style) => style switch
        {
            ProcessWindowStyle.Hidden => 0,
            ProcessWindowStyle.Minimized => 2,
            ProcessWindowStyle.Maximized => 3,
            _ => 1,
        };

        private const int CsidlDesktop = 0;
        private const int SwcDesktop = 8;
        private const int SwfoNeedDispatch = 1;

        /// <summary>
        /// The start through explorer.exe, on a thread of its own in the
        /// single-threaded apartment the shell's objects expect, waited for
        /// <see cref="ShellWaitMs"/> at most. Null when the shell took it,
        /// otherwise why not.
        /// </summary>
        private static string ThroughTheDesktop(UserStart start)
        {
            string why = "the desktop shell did not answer";
            var thread = new Thread(() =>
            {
                object windows = null, desktop = null, document = null, shell = null;
                try
                {
                    Type type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: false);
                    if (type == null)
                    {
                        why = "Windows has no ShellWindows object";
                        return;
                    }
                    windows = Activator.CreateInstance(type);
                    // FindWindowSW's window handle comes back through its
                    // fourth argument.
                    var byReference = new ParameterModifier(5);
                    byReference[3] = true;
                    desktop = Call(windows, "FindWindowSW", BindingFlags.InvokeMethod,
                        new object[] { CsidlDesktop, null, SwcDesktop, 0, SwfoNeedDispatch }, byReference);
                    if (desktop == null)
                    {
                        why = "no desktop shell is running";
                        return;
                    }
                    document = Call(desktop, "Document", BindingFlags.GetProperty, Array.Empty<object>(), null);
                    shell = document == null ? null : Call(document, "Application", BindingFlags.GetProperty, Array.Empty<object>(), null);
                    if (shell == null)
                    {
                        why = "the desktop shell gave no shell object";
                        return;
                    }
                    Call(shell, "ShellExecute", BindingFlags.InvokeMethod,
                        new object[] { start.File, start.Arguments, start.Folder, start.Verb, start.Show }, null);
                    why = null;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is COMException com)
                {
                    why = "the desktop shell refused the start (0x" + com.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")";
                }
                catch (COMException ex)
                {
                    why = "the desktop shell refused the start (0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")";
                }
                catch (Exception ex)
                {
                    why = "the desktop shell could not be reached: " + ex.GetType().Name;
                }
                finally
                {
                    foreach (object held in new[] { shell, document, desktop, windows })
                        if (held != null && Marshal.IsComObject(held)) Marshal.ReleaseComObject(held);
                }
            })
            { IsBackground = true, Name = "PadForge shell start" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return thread.Join(ShellWaitMs) ? why : "the desktop shell did not answer";
        }

        /// <summary>A call by name through the object's IDispatch, as a
        /// script makes it.</summary>
        private static object Call(object target, string name, BindingFlags kind, object[] arguments, ParameterModifier? byReference)
            => target.GetType().InvokeMember(name, kind, null, target, arguments,
                byReference.HasValue ? new[] { byReference.Value } : null, null, null);

        /// <summary>Puts text on the clipboard. The test host leaves the
        /// clipboard as it is.</summary>
        internal static void SetClipboardText(string text)
        {
            if (OffLimitsForTest) return;
            System.Windows.Clipboard.SetText(text);
        }
    }
}
