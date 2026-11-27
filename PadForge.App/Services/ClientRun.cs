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
    /// script can read (#600).
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
        internal const string DidNotCloseText = "PadForge did not close.";
        internal const string NotPadForgeText =
            "Another program holds the pipe PadForge.Control. It is not PadForge, so nothing was sent to it.";

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
            /// hold, whose wait for its exit gives its code, or null when it
            /// cannot be watched.</summary>
            public Func<RunningCopy.Hold<int?>> Watch = () => RunningCopy.Watch();

            /// <summary>Asks the running copy to close over its signal pipe,
            /// or its signal window for a caller with rights, and waits for its
            /// end (#600 item 44). Needs no Automation.</summary>
            public Func<RunningCopy.Closing> ExitCopy = () => RunningCopy.Exit();

            /// <summary>Takes hold of the running copy before <c>exit</c>
            /// goes down the pipe, and answers with the hold, whose wait is
            /// for its end, or null when it cannot be held.</summary>
            public Func<RunningCopy.Hold<bool>> HoldForClose = RunningCopy.HoldForClose;

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
            public Func<int> ServeAssistant = AssistantMirror.Run;

            /// <summary>Runs the web helper (#600 item 11).</summary>
            public Func<int> ServeWeb = PadForge.Services.Automation.Web.WebHelper.Run;

            /// <summary>Runs <c>--install</c> (#600) and gives its exit
            /// code. Its words go where the run's answers go.</summary>
            public Func<Install.InstallRequest, Action<string>, Action<string>, int> Install = PadForge.Services.Install.UserInstall.Run;

            /// <summary>The Automation pipe <c>--call</c> opens (#600). A test
            /// gives one of its own.</summary>
            public string AutomationPipeName = AutomationPipe.PipeName;

            /// <summary>Writes a file of <c>--describe</c> with the user's own
            /// rights, the run's own (#600).</summary>
            public Action<string, byte[]> WriteFile = (path, bytes) => File.WriteAllBytes(path, bytes);

            /// <summary>Runs <c>--summarize</c> (#600) and gives its exit
            /// code.</summary>
            public Func<string, Action<string>, Action<string>, int> Summarize = PadForge.Common.Recording.RecordingSummaryPrint.Run;

            /// <summary>Runs <c>--plugin-kit</c> (#600 item 20) and gives its
            /// exit code.</summary>
            public Func<string, Action<string>, Action<string>, int> PluginKit = PadForge.Services.Plugins.PluginKit.RunFromCommandLine;

            /// <summary>Runs <c>--settings-folder</c> (#600 item 44): Explorer
            /// on the folder of PadForge.xml.</summary>
            public Func<Action<string>, int> SettingsFolder = SettingsFolderRun.Run;

            /// <summary>Whether this run is a test's child (#600 item 52),
            /// which never goes on to become the app.</summary>
            public bool TestsChild;

            /// <summary>The same seams, for a run that says some things
            /// another way.</summary>
            internal Seams Copy() => (Seams)MemberwiseClone();

            /// <summary>
            /// The seams of the run <see cref="Program"/> starts (#600 item
            /// 52). The variable that names the Automation pipe for the web
            /// helper PadForge starts (WebHelper.PipeVariable) names
            /// PadForge's own pipe there. A name that is not one of PadForge's
            /// own is a test's (PipeOwner), and a run given one is that
            /// test's child: it reaches that pipe alone, so a test that starts
            /// PadForge.exe never reaches a PadForge that runs on the PC. Its
            /// running copy is whatever serves that pipe, it sends no line
            /// to a control pipe, looks up no window, lock or exit of the
            /// PC's copy, installs nothing in the PC's folders, saves nothing
            /// from the recordings folder the PC's copy keeps, opens nothing
            /// through the shell, shows no box and never becomes the app.
            /// </summary>
            internal static Seams ForThisRun(Func<string, string> variable)
            {
                var seams = new Seams();
                string pipe = variable?.Invoke(PadForge.Services.Automation.Web.WebHelper.PipeVariable);
                if (string.IsNullOrWhiteSpace(pipe) || PipeOwner.IsOwnName(pipe)) return seams;
                seams.TestsChild = true;
                seams.AutomationPipeName = pipe;
                seams.CopyRuns = () => ControlPipeClient.Stands(pipe);
                seams.Exchange = _ => null;
                seams.PipeStands = () => false;
                seams.Settle = () => RunningCopy.Settled.Gone;
                seams.ShowWindow = () => false;
                seams.LendForeground = () => { };
                seams.Watch = () => null;
                seams.ExitCopy = () => RunningCopy.Closing.NotRunning;
                seams.HoldForClose = () => null;
                seams.Tell = seams.Say;
                seams.TellError = seams.SayError;
                seams.ServeAssistant = () => AssistantMirror.Run(pipe);
                seams.Install = (request, say, sayError) =>
                {
                    sayError(TestsChildInstallsNothing);
                    return PadForge.Services.Install.UserInstall.Failed;
                };
                seams.SettingsFolder = sayError =>
                {
                    sayError(TestsChildOpensNothing);
                    return AnsweredAnError;
                };
                return seams;
            }
        }

        internal const string TestsChildInstallsNothing = "A test's run of PadForge installs nothing.";
        internal const string TestsChildOpensNothing = "A test's run of PadForge opens nothing.";

        /// <summary>
        /// Runs the command line as a client when it is one. True when this
        /// process is done and exits with <paramref name="exitCode"/>. False
        /// when it is to go on and start PadForge as the app.
        /// </summary>
        internal static bool TryRun(IReadOnlyList<string> args, out int exitCode, Seams seams = null)
        {
            seams ??= Seams.ForThisRun(Environment.GetEnvironmentVariable);
            exitCode = Done;
            var request = CommandLineSwitches.Parse(args);

            // The update helper's command line is its own to read. A test's
            // child never goes on.
            if (request.Internal) return seams.TestsChild ? NoApp(seams, out exitCode) : false;

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
            if (request.Install != null)
            {
                // An install is the user's own and needs no running copy:
                // it writes the user's folders with the user's rights.
                exitCode = seams.Install(request.Install, seams.Say, seams.SayError);
                return true;
            }
            if (request.Summarize != null)
            {
                // A recording's summary needs no running copy (#600).
                exitCode = seams.Summarize(request.Summarize, seams.Say, seams.SayError);
                return true;
            }
            if (request.PluginKit != null)
            {
                // The kit is written where the user said, and needs no
                // running copy.
                exitCode = seams.PluginKit(request.PluginKit, seams.Say, seams.SayError);
                return true;
            }
            if (request.SettingsFolder)
            {
                // The settings file's folder, which needs no running copy
                // (#600 item 44).
                exitCode = seams.SettingsFolder(seams.SayError);
                return true;
            }
            if (request.Mcp)
            {
                // The assistant's side of the door. It serves whether or not
                // PadForge runs, and says which.
                exitCode = seams.ServeAssistant();
                return true;
            }
            if (request.Web)
            {
                // The web helper reaches the running copy over its Automation
                // pipe, whose owner it checks, with the key that copy handed
                // it, and gives 4 when no pipe stands. It runs as the
                // signed-in user, who need not be the user PadForge runs as,
                // and such a user may not read PadForge's process to find its
                // window, so the window is not looked for.
                exitCode = seams.ServeWeb();
                return true;
            }
            if (request.AssistantRun)
            {
                // The assistant tools and the skill on the command line (#600).
                exitCode = AssistantCommandLine.Run(request, seams);
                return true;
            }

            if (!seams.CopyRuns()) return NoCopy(request, seams, out exitCode);

            // A copy runs. Started again with no switch, PadForge shows it. A
            // copy that is closing takes no show, so this start waits for its
            // end and becomes the app in its place, as a switch's line does
            // when the copy closes first (2026-11-26: the show went to the
            // closing copy and no PadForge was left running). A copy still
            // starting shows its window once it is ready.
            if (request.Empty)
            {
                if (seams.Settle() == RunningCopy.Settled.Gone) return NoCopy(request, seams, out exitCode);
                if (seams.ShowWindow()) return true;
                seams.SayError(NoAnswerText);
                exitCode = AnsweredAnError;
                return true;
            }

            // Taken before any line goes out, so a copy told to exit is
            // still the one waited for. Let go on every way out, so a start
            // that goes on to become the app holds no handle on the old copy.
            using var wait = request.Wait ? seams.Watch() : null;

            for (int i = 0; i < request.Steps.Count; i++)
            {
                int code = RunLine(request.Steps[i].Line, JsonRefusals(seams, request.Json), out bool gone);
                if (gone)
                {
                    // The copy closed before it took a line. With nothing
                    // sent yet this is a start like any other with no copy.
                    if (i == 0) return NoCopy(request, seams, out exitCode);
                    Refuse(seams, request.Json, "not-running", NotRunningText);
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
                int? copyExit = wait.Wait();
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
            if (request.CanStart && !seams.TestsChild) return false;
            Refuse(seams, request.Json, "not-running", NotRunningText);
            exitCode = NotRunning;
            return true;
        }

        /// <summary>A test's child ends here, where a run would go on to
        /// become the app (#600 item 52): as with no copy, 4.</summary>
        private static bool NoApp(Seams seams, out int exitCode)
        {
            seams.SayError(NotRunningText);
            exitCode = NotRunning;
            return true;
        }

        /// <summary>A refusal, in words or, with <c>--json</c>, as the one
        /// line <c>--call</c> gives (#600 item 44):
        /// <c>{"error":"code","message":"words"}</c>.</summary>
        private static void Refuse(Seams seams, bool json, string code, string words)
            => seams.SayError(json ? JsonRefusal(code, words) : words);

        internal static string JsonRefusal(string code, string words)
            => new System.Text.Json.Nodes.JsonObject { ["error"] = code, ["message"] = words }
                .ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        /// <summary>The seams of a run whose refusals are one line of JSON
        /// each with <c>--json</c>: the words the run would say, under the
        /// code that names them. A pipe's own refusal (<c>error code
        /// words</c>) keeps its code.</summary>
        private static Seams JsonRefusals(Seams seams, bool json)
        {
            if (!json) return seams;
            var copy = seams.Copy();
            copy.SayError = words => seams.SayError(JsonRefusal(CodeOf(words), MessageOf(words)));
            return copy;
        }

        /// <summary>The code of a refusal the run says: the pipe's own after
        /// <c>error</c>, or the one that names the run's words.</summary>
        internal static string CodeOf(string words)
        {
            if (words != null && words.StartsWith("error ", StringComparison.Ordinal))
            {
                string rest = words.Substring(6);
                int space = rest.IndexOf(' ');
                return space < 0 ? rest : rest.Substring(0, space);
            }
            return words switch
            {
                NotRunningText => "not-running",
                AutomationOffText => "not-allowed",
                NotPadForgeText => "not-padforge",
                DidNotCloseText => "not-closed",
                _ => "no-answer",
            };
        }

        private static string MessageOf(string words)
        {
            if (words == null || !words.StartsWith("error ", StringComparison.Ordinal)) return words;
            string rest = words.Substring(6);
            int space = rest.IndexOf(' ');
            return space < 0 ? AutomationLine.WordsFor(rest) : rest.Substring(space + 1);
        }

        private static int RunLine(string line, Seams seams, out bool gone)
        {
            gone = false;
            // A click on the tray icon shows the window with PadForge's right
            // to the front. A line cannot carry that right, so it is lent
            // first. --maximized shows the window too (#600 item 44).
            if (line is "show" or "maximize") seams.LendForeground();
            // The copy told to exit is the one waited for (#600 item 44). Let
            // go on every way out, waited for or not.
            using var closing = line == "exit" ? seams.HoldForClose() : null;

            string reply = seams.Exchange(line);
            if (reply == ControlPipeClient.NotPadForge) return NotPadForge(line, seams);
            bool unsettled = false;
            if (reply == null && !seams.PipeStands())
            {
                // No pipe: Automation is off, or the copy is not serving yet,
                // or not any more.
                var settled = seams.Settle();
                unsettled = settled == RunningCopy.Settled.Starting;
                switch (settled)
                {
                    case RunningCopy.Settled.Gone:
                        gone = true;
                        return NotRunning;
                    case RunningCopy.Settled.Ready:
                        reply = seams.Exchange(line);
                        if (reply == ControlPipeClient.NotPadForge) return NotPadForge(line, seams);
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
                // So is closing, which the tray icon's Exit does (#600 item
                // 44).
                if (line == "exit") return ExitOverTheSignal(seams);
                // A copy still starting or closing at the end of the wait
                // serves no pipe, which says nothing about Automation. Only a
                // ready copy with no pipe has it off.
                if (!unsettled && !seams.PipeStands())
                {
                    seams.SayError(AutomationOffText);
                    return NotAllowed;
                }
                seams.SayError(NoAnswerText);
                return AnsweredAnError;
            }

            if (reply.StartsWith("ok", StringComparison.Ordinal))
            {
                // --exit returns once PadForge has closed, as it does over
                // the signal (ExitOverTheSignal).
                if (closing != null && !closing.Wait())
                {
                    seams.SayError(DidNotCloseText);
                    return AnsweredAnError;
                }
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
            if (line == "exit" && reply.StartsWith(NotAllowedReply, StringComparison.Ordinal)) return ExitOverTheSignal(seams);

            seams.SayError(reply);
            return reply.StartsWith(NotAllowedReply, StringComparison.Ordinal) ? NotAllowed : AnsweredAnError;
        }

        /// <summary>The pipe's server is not PadForge: it is treated as no
        /// PadForge, and the caller hears that another program holds the
        /// name. Showing the window and closing need no control pipe and still
        /// go to the running copy, which is found by its own checked window
        /// and asked over its signal pipe.</summary>
        private static int NotPadForge(string line, Seams seams)
        {
            if (line == "show" && seams.ShowWindow())
            {
                seams.Say("ok");
                return Done;
            }
            if (line == "exit") return ExitOverTheSignal(seams);
            seams.SayError(NotPadForgeText);
            return NotRunning;
        }

        /// <summary><c>exit</c> over the signal (#600 item 44): ok
        /// once the copy's process ended, 1 when it still runs at the end of
        /// the wait, 4 when no copy runs.</summary>
        private static int ExitOverTheSignal(Seams seams)
        {
            switch (seams.ExitCopy())
            {
                case RunningCopy.Closing.Closed:
                    seams.Say("ok");
                    return Done;
                case RunningCopy.Closing.NotRunning:
                    seams.SayError(NotRunningText);
                    return NotRunning;
                default:
                    seams.SayError(DidNotCloseText);
                    return AnsweredAnError;
            }
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

        /// <summary>What <see cref="Exchange(string)"/> gives for a pipe
        /// whose server is not PadForge: nothing was sent to it. No answer
        /// of a pipe can be this, since a line holds no NUL.</summary>
        internal const string NotPadForge = "\0not-padforge";

        internal static string Exchange(string line) => Exchange(ExternalControlService.PipeName, line, 5000);

        /// <summary>Sends <paramref name="line"/> and returns the answer, or
        /// null when the pipe is not there, is not taken in time, does not
        /// answer in <paramref name="answerMs"/> or closes without a
        /// word, or <see cref="NotPadForge"/> when its server is another
        /// program (PipeOwner). The pipe is opened at the identification
        /// level, so a server that is not PadForge cannot act as the caller.
        /// A pipe of another name than PadForge's own, which a test serves,
        /// is taken from the caller's own user too: the one rule of
        /// PipeOwner.IsServer.</summary>
        internal static string Exchange(string pipeName, string line, int connectMs, int answerMs = AnswerMs)
        {
            // The test host connects to no pipe under the app's own name.
            if (ExternalControlService.OffLimitsForTest && IsTheAppsOwn(pipeName)) return null;
            if (!Stands(pipeName)) return null;
            try
            {
                // Asynchronous, so the write and the reads can be given up.
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                    System.Security.Principal.TokenImpersonationLevel.Identification);
                client.Connect(connectMs);
                // Nothing goes to a server that is not PadForge.
                if (!PipeOwner.IsServer(client.SafePipeHandle, pipeName))
                    return NotPadForge;
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
