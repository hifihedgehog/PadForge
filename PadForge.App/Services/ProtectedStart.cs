using System;
using System.Collections.Generic;
using PadForge.Common;

namespace PadForge.Services
{
    /// <summary>
    /// The first step of every start (#600). A start with rights runs on only
    /// when the host unpacked its native libraries into a folder no program
    /// without rights can change. Otherwise it makes that folder sound and
    /// starts this exe once more, with the same command line and the same
    /// rights, unpacking there, and leaves.
    ///
    /// <para>The single-file host unpacks into
    /// <c>DOTNET_BUNDLE_EXTRACT_BASE_DIR\&lt;exe name&gt;\&lt;bundle ID&gt;</c>, or
    /// under the temp folder when the variable is not set (extractor.cpp,
    /// extraction_dir), and reuses a folder that is already there after
    /// checking only that each file exists (verify_recover_extraction). The
    /// logon task and Windows' prompt both start PadForge with the user's own
    /// temp folder, which every program of the user can write. A library put
    /// there runs with PadForge's rights as soon as it is loaded, and the
    /// runtime looks there for every <c>DllImport</c> before Windows does
    /// (nativelibrary.cpp, LoadNativeLibraryBySearch). Measured on this PC
    /// with a single-file probe: a kernel32.dll copied there was loaded by
    /// the first <c>DllImport</c> of kernel32.dll, and with EventSource on, an
    /// advapi32.dll copied there was loaded before Main. So the build turns
    /// EventSource off (PadForge.App.csproj), and this step runs first in
    /// Main and makes every Windows call through <see cref="System32Calls"/>.
    /// The restart is CreateProcess, which takes no prompt and keeps the
    /// rights, with the exe's own folder as its working folder and the
    /// variables of the allow list every start with rights gets
    /// (<see cref="RightsVariables"/>), DOTNET_BUNDLE_EXTRACT_BASE_DIR set to
    /// <see cref="ProtectedFolder.Natives"/> among them, which the host honors
    /// from the environment (extractor.cpp, pal::getenv).</para>
    ///
    /// <para>The copy started this way answers for the start: it gets the
    /// same window state, the same standard handles and the parent's console,
    /// and its exit code is this process's, so a give-back task, the update
    /// helper's starter and a script that reads a client run's exit code see
    /// what they saw before. Only when it goes on to be the app does this
    /// process leave at once, with <see cref="HandedOver"/>: the copy sets an
    /// event this process made under a name of its own in the private
    /// namespace whose boundary is the Administrators group (InstanceLock's),
    /// and finds that name in <see cref="HandoverVariable"/>. Every program
    /// of the user can set a variable for a start through Windows' prompt
    /// (HKCU\Environment), so the variable carries a name, never a handle:
    /// a name such a program writes names nothing it could make, since only
    /// a token with administrator rights can open that namespace, and the
    /// copy sets and closes only an event it opened itself. Until then the
    /// copy sits in a job
    /// that ends it when this process ends, so stopping this start stops the
    /// copy, as stopping the start stopped the process before.</para>
    ///
    /// <para>Every start, with rights or without, first calls
    /// SetDefaultDllDirectories, so a library loaded by name alone is never
    /// looked for in the working folder or on PATH.</para>
    /// </summary>
    internal static class ProtectedStart
    {
        internal const string BaseVariable = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";

        /// <summary>The handover event's name in the Administrators namespace,
        /// as the restarted copy finds it. Removed from that copy's variables
        /// at once, so nothing it starts sees it.</summary>
        internal const string HandoverVariable = "PADFORGE_HANDOVER_EVENT";

        /// <summary>The namespace the handover event is made in: the one of
        /// the single-instance lock (InstanceLock.NamespaceName), whose
        /// boundary is the Administrators group.</summary>
        internal const string HandoverNamespace = InstanceLock.NamespaceName;

        /// <summary>The start of every handover event's name.</summary>
        internal const string HandoverPrefix = "Handover-";

        /// <summary>The exit code of a start that handed over to a copy that
        /// went on to be the app.</summary>
        internal const int HandedOver = 0x50460001;

        /// <summary>The exit code of a start with rights that found no sound
        /// folder to unpack into and could not make one, or could not start
        /// itself again from one. It does not go on, and says why in a
        /// message box first (<see cref="StartFailure"/>).</summary>
        internal const int NoSoundFolder = 0x50460002;

        /// <summary>
        /// The variables that name a library SDL loads into the process:
        /// SDL3_DYNAMIC_API, which SDL loads at its first call
        /// (SDL_dynapi.c, SDL_InitDynamicAPI), and the OpenGL, Vulkan and EGL
        /// libraries its hints name (SDL_windowsopengl.c, SDL_windowsvulkan.c,
        /// SDL_egl.c), which SDL reads from the environment when no hint is
        /// set (SDL_GetHint). Every program of the user can set them
        /// (HKCU\Environment), so a process with rights removes them from its
        /// own variables first, and a copy it starts never has them.
        /// </summary>
        internal static readonly string[] LibraryVariables =
            { "SDL3_DYNAMIC_API", "SDL_OPENGL_LIBRARY", "SDL_VULKAN_LIBRARY", "SDL_EGL_LIBRARY" };

        /// <summary>The handover event this process was handed, or
        /// zero.</summary>
        private static IntPtr s_handover;

        internal enum Decision
        {
            /// <summary>Go on: no rights, or the libraries came from a sound
            /// folder.</summary>
            GoOn,

            /// <summary>Start once more from a sound folder.</summary>
            Restart,
        }

        /// <summary>
        /// What a start does. <paramref name="baseVariable"/> is the full form
        /// of DOTNET_BUNDLE_EXTRACT_BASE_DIR, or null. A start with rights goes
        /// on only when that is <paramref name="natives"/> and the folders are
        /// sound now.
        /// </summary>
        internal static Decision Decide(bool privileged, string baseVariable, string natives, Func<string, string, bool> sameName,
            Func<bool> nativesAreSound)
        {
            if (!privileged) return Decision.GoOn;
            if (string.IsNullOrEmpty(natives) || string.IsNullOrEmpty(baseVariable)) return Decision.Restart;
            if (!sameName(baseVariable.TrimEnd('\\'), natives)) return Decision.Restart;
            return nativesAreSound() ? Decision.GoOn : Decision.Restart;
        }

        /// <summary>
        /// Main's first call. Returns only when the process is to go on. A
        /// start it restarted, or one that cannot go on, ends here through
        /// ExitProcess, so none of the runtime's own steps at exit runs in a
        /// process that may not trust its libraries.
        /// </summary>
        internal static void Run()
        {
            System32Calls.SetDefaultDllDirectories(System32Calls.SearchDefaultDirs);
            TakeHandover(System32Calls.GetVariable(HandoverVariable), HandoverNamespace);

            // Without System32's calls nothing can be checked. A start without
            // rights loses nothing by going on.
            if (!System32Calls.Ready) return;
            if (!System32Calls.IsPrivileged()) return;
            foreach (string name in LibraryVariables) System32Calls.SetVariable(name, null);

            string natives = ProtectedFolder.Natives;
            string variable = System32Calls.GetVariable(BaseVariable);
            string full = string.IsNullOrEmpty(variable) ? null : System32Calls.FullPath(variable);
            // A start that goes on as it is may have been steered there: every
            // program of the user can name the protected folder in its
            // variables (HKCU\Environment), and Windows builds those of the
            // copy its prompt starts from them. So it keeps the allow list's
            // variables only, as every start PadForge makes with rights gets
            // them, and one whose variables cannot be read starts again.
            if (Decide(true, full, natives, System32Calls.SameName, ProtectedFolder.NativesAreSound) == Decision.GoOn
                && KeepAllowListOnly(natives))
                return;

            if (!ProtectedFolder.MakeNativesSound())
            {
                StartFailure.Show(StartFailure.Kind.NoFolder);
                System32Calls.ExitNow(NoSoundFolder);
            }
            System32Calls.ExitNow(Restart(natives));
        }

        /// <summary>
        /// Makes this process's variables the allow list's
        /// (<see cref="AllowListOnly"/>), before anything past Main reads one.
        /// False when its variables cannot be read.
        /// </summary>
        private static bool KeepAllowListOnly(string natives)
        {
            string[] own = System32Calls.Variables();
            if (own == null) return false;
            var (drop, set) = AllowListOnly(own, System32Calls.MachineVariables(), natives);
            foreach (string name in drop) System32Calls.SetVariable(name, null);
            foreach (string line in set)
            {
                string name = NameOf(line);
                System32Calls.SetVariable(name, line.Substring(name.Length + 1));
            }
            return true;
        }

        /// <summary>
        /// What makes a process's variables (<paramref name="own"/>, as
        /// <c>name=value</c> lines) those of the allow list every start with
        /// rights gets (<see cref="RightsVariables.Build"/>): the names to
        /// remove, and the lines to set. The drive lines Windows keeps for each
        /// process (<c>=C:=C:\</c>) stay.
        /// </summary>
        internal static (List<string> Drop, List<string> Set) AllowListOnly(IReadOnlyList<string> own, IReadOnlyList<string> machine,
            string natives)
        {
            var set = RightsVariables.Build(machine, own, natives);
            var drop = new List<string>();
            foreach (string line in own ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(line) || line[0] == '=') continue;
                string name = NameOf(line);
                bool kept = false;
                foreach (string keep in set)
                    if (CompareNames(NameOf(keep), name) == 0) { kept = true; break; }
                if (!kept) drop.Add(name);
            }
            return (drop, set);
        }

        /// <summary>Takes the handover event a restarting start named for
        /// this process in <paramref name="value"/>, the variable's, if any:
        /// the variable is removed, and an event of that name in the
        /// Administrators namespace <paramref name="namespaceName"/> is opened
        /// by this process itself, so a value a program without rights wrote
        /// names nothing this process sets or closes but an event it opened.
        /// Only a token with administrator rights opens the namespace.</summary>
        internal static void TakeHandover(string value, string namespaceName)
        {
            if (value == null) return;
            System32Calls.SetVariable(HandoverVariable, null);
            if (!IsHandoverName(value)) return;
            IntPtr ns = System32Calls.OpenAdministratorsNamespace(namespaceName, create: false, out IntPtr boundary);
            if (ns == IntPtr.Zero) return;
            try
            {
                IntPtr opened = System32Calls.OpenNamedEvent(namespaceName + "\\" + value);
                if (opened == IntPtr.Zero) return;
                System32Calls.Close(s_handover);
                s_handover = opened;
            }
            finally
            {
                System32Calls.CloseNamespace(ns, boundary);
            }
        }

        /// <summary>Whether a value has the shape of a handover event's name:
        /// <see cref="HandoverPrefix"/> and two numbers in hex digits, the
        /// restarting start's process and a tick of its clock.</summary>
        internal static bool IsHandoverName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > HandoverPrefix.Length + 33
                || !value.StartsWith(HandoverPrefix, StringComparison.Ordinal))
                return false;
            string rest = value.Substring(HandoverPrefix.Length);
            int dash = rest.IndexOf('-');
            return dash > 0 && dash < rest.Length - 1 && rest.IndexOf('-', dash + 1) < 0
                && TryParseHex(rest.Substring(0, dash), out _) && TryParseHex(rest.Substring(dash + 1), out _);
        }

        /// <summary>The handover event's name for this start: its process
        /// and a tick of its clock.</summary>
        internal static string HandoverName()
            => HandoverPrefix + ProtectedFolder.Hex(System32Calls.ProcessId()) + "-" + ProtectedFolder.Hex(System32Calls.TickCount());

        /// <summary>Called by the copy that is the app, once it holds the
        /// single-instance lock (App.OnStartup): the start that restarted it
        /// leaves now. Every other kind of run keeps its start waiting for its
        /// exit code.</summary>
        internal static void HandOver()
        {
            IntPtr handle = s_handover;
            s_handover = IntPtr.Zero;
            if (handle == IntPtr.Zero) return;
            System32Calls.SetEvent(handle);
            System32Calls.Close(handle);
        }

        /// <summary>Starts this exe again from the sound folder and answers
        /// with the exit code this process leaves with. A copy that could not
        /// be started is said in a message box.</summary>
        private static int Restart(string natives)
        {
            int code = StartCopy(natives, out bool started);
            if (!started) StartFailure.Show(StartFailure.Kind.NoRestart);
            return code;
        }

        private static int StartCopy(string natives, out bool started)
        {
            started = false;
            string exe = System32Calls.OwnExe();
            string commandLine = System32Calls.CommandLine();
            string[] variables = System32Calls.Variables();
            if (exe == null || commandLine == null || variables == null) return NoSoundFolder;
            int slash = exe.LastIndexOf('\\');
            if (slash <= 0) return NoSoundFolder;
            string folder = exe.Substring(0, slash + 1);

            // The handover event, under a name of this start's own in the
            // Administrators namespace, held open by this process until the
            // copy hands over or ends.
            IntPtr ns = System32Calls.OpenAdministratorsNamespace(HandoverNamespace, create: true, out IntPtr boundary);
            if (ns == IntPtr.Zero) return NoSoundFolder;
            string name = HandoverName();
            IntPtr handover = System32Calls.NewNamedEvent(HandoverNamespace + "\\" + name);
            if (handover == IntPtr.Zero)
            {
                System32Calls.CloseNamespace(ns, boundary);
                return NoSoundFolder;
            }
            IntPtr job = IntPtr.Zero;
            try
            {
                string block = CopyBlock(System32Calls.MachineVariables(), variables, natives, name);

                // A client run writes to the console of the process that
                // started it, and the copy's parent is this process.
                System32Calls.AttachToParentConsole();
                System32Calls.GetStartupInfo(out var startup);
                startup.lpReserved = IntPtr.Zero;
                startup.cbReserved2 = 0;
                startup.lpReserved2 = IntPtr.Zero;

                if (!System32Calls.CreateProcess(exe, commandLine, block, folder, ref startup, out var child)) return NoSoundFolder;
                // The copy lives no longer than this start until it hands
                // over: whoever stops this process, as the update's starter
                // stops a helper that never reported ready, stops the copy
                // too. A job that cannot be made leaves the copy on its own.
                job = System32Calls.NewKillOnCloseJob();
                if (job != IntPtr.Zero && !System32Calls.AssignToJob(job, child.hProcess))
                {
                    System32Calls.Close(job);
                    job = IntPtr.Zero;
                }
                if (!System32Calls.Resume(child.hThread))
                {
                    System32Calls.Terminate(child.hProcess, unchecked((uint)NoSoundFolder));
                    System32Calls.Close(child.hThread);
                    System32Calls.Close(child.hProcess);
                    return NoSoundFolder;
                }
                started = true;
                System32Calls.Close(child.hThread);
                try
                {
                    uint first = System32Calls.WaitForFirst(child.hProcess, handover);
                    if (first == System32Calls.WaitObject0 + 1)
                    {
                        // The app outlives this start.
                        if (job != IntPtr.Zero) System32Calls.SetKillOnClose(job, false);
                        return HandedOver;
                    }
                    return System32Calls.GetExitCode(child.hProcess, out uint code) ? unchecked((int)code) : NoSoundFolder;
                }
                finally
                {
                    System32Calls.Close(child.hProcess);
                }
            }
            finally
            {
                System32Calls.Close(job);
                System32Calls.Close(handover);
                System32Calls.CloseNamespace(ns, boundary);
            }
        }

        /// <summary>
        /// The restarted copy's variables: those every start with rights gets
        /// (<see cref="RightsVariables.Build"/>), the system's from
        /// <paramref name="machine"/> and the user's and PadForge's from
        /// <paramref name="own"/>, this process's, with
        /// DOTNET_BUNDLE_EXTRACT_BASE_DIR set to <paramref name="natives"/>
        /// and the handover event's name in <see cref="HandoverVariable"/>.
        /// No variable that loads code into the copy is handed on, whatever
        /// this process was started with. The block ends in two null
        /// characters.
        /// </summary>
        internal static string CopyBlock(IReadOnlyList<string> machine, IReadOnlyList<string> own, string natives, string handoverName)
            => RightsVariables.ToBlock(RightsVariables.Build(machine, own, natives, HandoverVariable + "=" + handoverName));

        /// <summary>A line's name. The drive lines Windows keeps start with
        /// '=' (<c>=C:=C:\</c>), whose name runs to the second '='.</summary>
        internal static string NameOf(string line)
        {
            int equals = line.IndexOf('=', line.StartsWith('=') ? 1 : 0);
            return equals < 0 ? line : line.Substring(0, equals);
        }

        /// <summary>Names compared by their characters, ASCII letters as
        /// upper case, with no reading of the user's culture.</summary>
        internal static int CompareNames(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                char x = Upper(a[i]), y = Upper(b[i]);
                if (x != y) return x < y ? -1 : 1;
            }
            return a.Length.CompareTo(b.Length);
        }

        private static char Upper(char c) => c >= 'a' && c <= 'z' ? (char)(c - 32) : c;

        internal static bool TryParseHex(string text, out ulong value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text) || text.Length > 16) return false;
            foreach (char c in text)
            {
                int d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                if (d < 0) return false;
                value = (value << 4) | (uint)d;
            }
            return true;
        }
    }
}
