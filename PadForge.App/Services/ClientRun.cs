using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using PadForge.Common;
using PadForge.Services.Automation;

namespace PadForge.Services
{
    /// <summary>
    /// A start of PadForge.exe that is not the app: it hands its switches to
    /// the running copy, prints what the copy answers, and exits with a code a
    /// script can read (#597).
    ///
    /// <para>It needs no administrator rights and asks for none, so a prompt,
    /// a launcher or a batch file gets its answer on its own console. A copy
    /// Windows raised through its prompt could not write there, which is why
    /// the exe starts with the caller's rights and only the app raises
    /// itself (<see cref="StartWithRights"/>).</para>
    ///
    /// <para>Runs before WPF loads and uses none of it.</para>
    /// </summary>
    internal static class ClientRun
    {
        internal const int Done = 0;
        internal const int AnsweredAnError = 1;
        internal const int WrongCommandLine = 2;
        internal const int NotRunning = 4;
        internal const int NotAllowed = 5;

        /// <summary>What the pipe answers when Automation is off or the level
        /// in force does not allow a line.</summary>
        internal const string NotAllowedReply = "error not-allowed";

        internal const string NotRunningText = "PadForge is not running.";
        internal const string AutomationOffText =
            "Automation is off in PadForge. Turn it on under Extras, Automation.";
        internal const string NoAnswerText = "PadForge did not answer.";

        /// <summary>The run's ways out to the machine, each a test's to
        /// replace.</summary>
        internal sealed class Seams
        {
            /// <summary>Whether a PadForge runs in this session.</summary>
            public Func<bool> CopyRuns = RunningCopy.Exists;

            /// <summary>Sends one line to the running copy and returns its
            /// answer, or null when the pipe is not there or does not answer.</summary>
            public Func<string, string> Exchange = ControlPipeClient.Exchange;

            /// <summary>Whether the control pipe exists at all. It does not
            /// while Automation is off.</summary>
            public Func<bool> PipeStands = ControlPipeClient.Stands;

            /// <summary>Waits while the running copy is still starting or is
            /// closing, and says how that ended. A copy in either state serves
            /// no pipe, and that says nothing about Automation.</summary>
            public Func<RunningCopy.Settled> Settle = () => RunningCopy.WaitUntilSettled();

            /// <summary>Asks the running copy to show its window. Needs no
            /// pipe.</summary>
            public Func<bool> ShowWindow = () => RunningCopy.Show();

            /// <summary>Hands the running copy this process's right to come
            /// to the front, before a line that shows its window.</summary>
            public Action LendForeground = RunningCopy.LendForeground;

            /// <summary>Takes hold of the running copy and answers with the
            /// wait for its exit, or null when it cannot be watched.</summary>
            public Func<Func<int?>> Watch = () => RunningCopy.Watch();

            /// <summary>An answer. It goes to standard output or the caller's
            /// console, and nowhere when the caller gave neither: a launcher
            /// never gets a window for a command it sent.</summary>
            public Action<string> Say = CallerConsole.Say;
            public Action<string> SayError = CallerConsole.SayError;

            /// <summary>Words the person asked for or has to see: the help,
            /// the version, a command line that cannot be read. With nowhere
            /// to write they come up in a window, so a shortcut with a
            /// mistyped switch does not fail without a word.</summary>
            public Action<string> Tell = CallerConsole.Tell;
            public Action<string> TellError = CallerConsole.TellError;

            public Func<string> VersionLine = () => "PadForge " + BuildIdentity.Display;
            public Func<int> ServeAssistant = AssistantBridge.Run;
        }

        /// <summary>
        /// Runs the command line as a client when it is one. True when this
        /// process is done and exits with <paramref name="exitCode"/>. False
        /// when it is to go on and start PadForge as the app.
        /// </summary>
        internal static bool TryRun(IReadOnlyList<string> args, out int exitCode, Seams seams = null)
        {
            seams ??= new Seams();
            exitCode = Done;
            var request = CommandLineSwitches.Parse(args);

            // The update helper's command line is its own to read.
            if (request.Internal) return false;

            if (request.Error != null)
            {
                seams.TellError(request.Error + "\r\n\r\n" + CommandLineSwitches.HelpText);
                exitCode = WrongCommandLine;
                return true;
            }
            if (request.Help)
            {
                seams.Tell(CommandLineSwitches.HelpText);
                return true;
            }
            if (request.Version)
            {
                seams.Tell(seams.VersionLine());
                return true;
            }
            if (request.Mcp)
            {
                // The assistant's side of the door. It serves whether or not
                // PadForge runs, and says which.
                exitCode = seams.ServeAssistant();
                return true;
            }

            if (!seams.CopyRuns()) return NoCopy(request, seams, out exitCode);

            // A copy runs. Started again with no switch, PadForge shows it.
            if (request.Empty)
            {
                if (seams.ShowWindow()) return true;
                seams.SayError(NoAnswerText);
                exitCode = AnsweredAnError;
                return true;
            }

            // Taken before any line goes out, so a copy told to exit is
            // still the one waited for.
            var wait = request.Wait ? seams.Watch() : null;

            for (int i = 0; i < request.Steps.Count; i++)
            {
                int code = RunLine(request.Steps[i].Line, seams, out bool gone);
                if (gone)
                {
                    // The copy closed before it took a line. With nothing
                    // sent yet this is a start like any other with no copy.
                    if (i == 0) return NoCopy(request, seams, out exitCode);
                    seams.SayError(NotRunningText);
                    exitCode = NotRunning;
                    return true;
                }
                if (code != Done)
                {
                    exitCode = code;
                    return true;
                }
            }

            if (wait != null)
            {
                int? copyExit = wait();
                if (copyExit.HasValue) exitCode = copyExit.Value;
            }
            return true;
        }

        /// <summary>No copy runs. A command line that can start PadForge goes
        /// on to become the app, which reads the same switches. One that
        /// cannot says so.</summary>
        private static bool NoCopy(CommandLineSwitches.Request request, Seams seams, out int exitCode)
        {
            exitCode = Done;
            if (request.CanStart) return false;
            seams.SayError(NotRunningText);
            exitCode = NotRunning;
            return true;
        }

        private static int RunLine(string line, Seams seams, out bool gone)
        {
            gone = false;
            // A click on the tray icon shows the window with PadForge's right
            // to the front. A line cannot carry that right, so it is lent
            // first.
            if (line == "show") seams.LendForeground();

            string reply = seams.Exchange(line);
            if (reply == null && !seams.PipeStands())
            {
                // No pipe: Automation is off, or the copy is not serving yet,
                // or not any more.
                switch (seams.Settle())
                {
                    case RunningCopy.Settled.Gone:
                        gone = true;
                        return NotRunning;
                    case RunningCopy.Settled.Ready:
                        reply = seams.Exchange(line);
                        break;
                }
            }

            if (reply == null)
            {
                // Showing the window is what a click on the tray icon does and
                // needs no Automation.
                if (line == "show" && seams.ShowWindow())
                {
                    seams.Say("ok");
                    return Done;
                }
                if (!seams.PipeStands())
                {
                    seams.SayError(AutomationOffText);
                    return NotAllowed;
                }
                seams.SayError(NoAnswerText);
                return AnsweredAnError;
            }

            if (reply.StartsWith("ok", StringComparison.Ordinal))
            {
                // The state is JSON for a script to read, so it is printed
                // alone. Every other answer is printed as the pipe gave it.
                if (line == "state" && reply.Length > 3)
                    seams.Say(reply.Substring(3));
                else
                    seams.Say(reply);
                return Done;
            }

            // Automation serves plugins alone, or its level is too low. The
            // window still shows, as it does with Automation off.
            if (line == "show" && reply.StartsWith(NotAllowedReply, StringComparison.Ordinal) && seams.ShowWindow())
            {
                seams.Say("ok");
                return Done;
            }

            seams.SayError(reply);
            return reply.StartsWith(NotAllowedReply, StringComparison.Ordinal) ? NotAllowed : AnsweredAnError;
        }
    }

    /// <summary>The client end of <c>PadForge.Control</c>: one line out, one
    /// line back, one connection each.</summary>
    internal static class ControlPipeClient
    {
        /// <summary>The longest answer read. The state line is the longest the
        /// app writes, and sixteen controllers stay well under this.</summary>
        internal const int LongestReply = 256 * 1024;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WaitNamedPipeW(string name, uint timeoutMs);

        private const int ERROR_FILE_NOT_FOUND = 2;

        /// <summary>Whether a pipe of that name exists, without connecting to
        /// it. A busy pipe exists.</summary>
        internal static bool Stands() => Stands(ExternalControlService.PipeName);

        internal static bool Stands(string pipeName)
        {
            if (ExternalControlService.OffLimitsForTest && IsTheAppsOwn(pipeName)) return false;
            if (WaitNamedPipeW(@"\\.\pipe\" + pipeName, 1)) return true;
            return Marshal.GetLastWin32Error() != ERROR_FILE_NOT_FOUND;
        }

        /// <summary>How long the running copy has to take a line and answer
        /// it. Starting the engine is the longest a line takes, and a line
        /// waits its turn behind another program's. Past this the copy did
        /// not answer, and the caller is not left waiting on a PadForge that
        /// hangs.</summary>
        internal const int AnswerMs = 60000;

        internal static string Exchange(string line) => Exchange(ExternalControlService.PipeName, line, 5000);

        /// <summary>Sends <paramref name="line"/> and returns the answer, or
        /// null when the pipe is not there, is not taken in time, does not
        /// answer in <paramref name="answerMs"/> or closes without a
        /// word.</summary>
        internal static string Exchange(string pipeName, string line, int connectMs, int answerMs = AnswerMs)
        {
            // The test host connects to no pipe under the app's own name.
            if (ExternalControlService.OffLimitsForTest && IsTheAppsOwn(pipeName)) return null;
            if (!Stands(pipeName)) return null;
            try
            {
                // Asynchronous, so the write and the reads can be given up.
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                client.Connect(connectMs);
                using var deadline = new CancellationTokenSource(answerMs);
                byte[] request = Encoding.UTF8.GetBytes(line + "\n");
                client.WriteAsync(request, 0, request.Length, deadline.Token).GetAwaiter().GetResult();

                // The answer is one UTF-8 line, the framing the server writes.
                using var reply = new MemoryStream();
                var chunk = new byte[4096];
                while (reply.Length < LongestReply)
                {
                    int n = client.ReadAsync(chunk, 0, chunk.Length, deadline.Token).GetAwaiter().GetResult();
                    if (n == 0) break;
                    int end = Array.IndexOf(chunk, (byte)'\n', 0, n);
                    if (end >= 0)
                    {
                        reply.Write(chunk, 0, end);
                        break;
                    }
                    reply.Write(chunk, 0, n);
                }
                if (reply.Length == 0) return null;
                return Encoding.UTF8.GetString(reply.GetBuffer(), 0, (int)reply.Length).TrimEnd('\r');
            }
            catch
            {
                return null;
            }
        }

        private static bool IsTheAppsOwn(string pipeName)
            => string.Equals(pipeName, ExternalControlService.PipeName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes to whoever started this process, one stream at a time: standard
    /// output and standard error are each a pipe, a file, a console or
    /// nothing, whatever the other is.
    ///
    /// <para>A pipe or a file is what a script reads, and gets UTF-8, the
    /// encoding of the control pipe. A console is written in Unicode, so a
    /// profile's name shows right whatever code page the console is on. A
    /// windowed program started from a prompt is handed no console, so it
    /// attaches to its caller's first.</para>
    /// </summary>
    internal static class CallerConsole
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int which);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFileType(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteConsoleW(IntPtr handle, string text, uint length, out uint written, IntPtr reserved);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetConsoleScreenBufferInfo(IntPtr handle, out ScreenBufferInfo info);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteFile(IntPtr handle, ref byte bytes, uint count, out uint written, IntPtr overlapped);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

        // wincon.h: CONSOLE_SCREEN_BUFFER_INFO, of which only the cursor's
        // column is read.
        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenBufferInfo
        {
            public short SizeX, SizeY;
            public short CursorX, CursorY;
            public ushort Attributes;
            public short WindowLeft, WindowTop, WindowRight, WindowBottom;
            public short MaximumX, MaximumY;
        }

        private const int ATTACH_PARENT_PROCESS = -1;
        private const int STD_OUTPUT_HANDLE = -11;
        private const int STD_ERROR_HANDLE = -12;
        private const uint FILE_TYPE_DISK = 1;
        private const uint FILE_TYPE_CHAR = 2;
        private const uint FILE_TYPE_PIPE = 3;
        private const int ERROR_BROKEN_PIPE = 109;
        private const int ERROR_NO_DATA = 232;
        private const int ERROR_PIPE_NOT_CONNECTED = 233;
        private const uint MB_ICONINFORMATION = 0x40;
        private const uint MB_ICONWARNING = 0x30;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        /// <summary>Set by the test assembly for the life of its process
        /// (PadForge.Tests CallerConsoleOffLimits), never by the app: no
        /// console is attached, nothing is written and no box is shown.</summary>
        internal static bool OffLimitsForTest;

        private static bool _attachTried;
        private static bool _attached;

        /// <summary>An answer: written where the caller reads, and dropped
        /// when the caller gave nowhere to write.</summary>
        internal static void Say(string text) => Write(text, error: false, mustBeSeen: false);

        internal static void SayError(string text) => Write(text, error: true, mustBeSeen: false);

        /// <summary>Words that have to be seen: written where the caller
        /// reads, and shown in a window when there is nowhere.</summary>
        internal static void Tell(string text) => Write(text, error: false, mustBeSeen: true);

        internal static void TellError(string text) => Write(text, error: true, mustBeSeen: true);

        private static void Write(string text, bool error, bool mustBeSeen)
        {
            if (OffLimitsForTest) return;
            try
            {
                if (TryWrite(text, error)) return;
            }
            catch
            {
                // Nowhere to write, as far as this stream goes.
            }
            // A session with no desktop, as a task's is, has nobody to press
            // OK, and a box there would hold the process for good.
            if (mustBeSeen && Environment.UserInteractive)
                MessageBoxW(IntPtr.Zero, text, "PadForge", error ? MB_ICONWARNING : MB_ICONINFORMATION);
        }

        private static bool TryWrite(string text, bool error)
        {
            int which = error ? STD_ERROR_HANDLE : STD_OUTPUT_HANDLE;
            IntPtr handle = GetStdHandle(which);
            uint type = Usable(handle) ? GetFileType(handle) : 0;

            // The caller redirected this stream.
            if (type == FILE_TYPE_DISK || type == FILE_TYPE_PIPE) return WriteBytes(handle, text);

            // A console this process is already attached to.
            if (type == FILE_TYPE_CHAR && GetConsoleMode(handle, out _)) return WriteConsole(handle, text);

            // No console yet: the caller's, when it has one.
            if (AttachToCaller())
            {
                IntPtr console = GetStdHandle(which);
                if (Usable(console) && GetConsoleMode(console, out _)) return WriteConsole(console, text);
            }

            // A character device that is no console is the null device: the
            // caller asked for the words to be dropped.
            return type == FILE_TYPE_CHAR && WriteBytes(handle, text);
        }

        private static bool Usable(IntPtr handle) => handle != IntPtr.Zero && handle != InvalidHandle;

        /// <summary>Writes to a pipe, a file or the null device, the way the
        /// runtime's own console stream does: one plain write after another
        /// on the caller's handle, whatever way the caller opened it. A
        /// reader that has gone away is no failure of this process.</summary>
        private static bool WriteBytes(IntPtr handle, string text)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(text + "\r\n");
            int at = 0;
            while (at < bytes.Length)
            {
                if (!WriteFile(handle, ref bytes[at], (uint)(bytes.Length - at), out uint written, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    return error == ERROR_BROKEN_PIPE || error == ERROR_NO_DATA || error == ERROR_PIPE_NOT_CONNECTED;
                }
                if (written == 0) return false;
                at += (int)written;
            }
            return true;
        }

        private static bool WriteConsole(IntPtr console, string text)
        {
            // A caller that did not wait has its prompt back already, with
            // the cursor after it. The words start on a line of their own.
            string lead = GetConsoleScreenBufferInfo(console, out var info) && info.CursorX > 0 ? "\r\n" : string.Empty;
            string all = lead + text + "\r\n";
            int at = 0;
            while (at < all.Length)
            {
                // A console write takes a limited run at a time.
                string run = all.Substring(at, Math.Min(8000, all.Length - at));
                if (!WriteConsoleW(console, run, (uint)run.Length, out uint written, IntPtr.Zero) || written == 0)
                    return at > 0;
                at += (int)written;
            }
            return true;
        }

        private static bool AttachToCaller()
        {
            if (!_attachTried)
            {
                _attachTried = true;
                _attached = AttachConsole(ATTACH_PARENT_PROCESS);
            }
            return _attached;
        }
    }
}
