using System;
using System.Collections.Generic;
using System.Text;
using PadForge.Common;

namespace PadForge.Services
{
    /// <summary>
    /// The variables of every process PadForge starts with its rights (#600),
    /// built from an allow list: the system's variables as the machine has
    /// them, the user's name and folders, and the PadForge variables the
    /// diagnostics read. Every other variable is dropped, CORECLR_*, COR_*,
    /// DOTNET_*, COMPlus_* and SDL3_DYNAMIC_API among them, and
    /// DOTNET_BUNDLE_EXTRACT_BASE_DIR is set to the protected unpack folder.
    ///
    /// <para>Every program of the user can write the user's variables
    /// (HKCU\Environment), and Windows builds a process's variables from them
    /// after the prompt that gives it rights. The .NET runtime loads the
    /// library CORECLR_PROFILER_PATH names before Main when
    /// CORECLR_ENABLE_PROFILING is 1 (profilinghelper.cpp), the .NET
    /// Framework the one COR_PROFILER_PATH names, and other variables of the
    /// same families change what the runtime loads or runs. A process started
    /// with a copy of PadForge's variables would load such a library with
    /// PadForge's rights. So none of them is handed on: the logon start, the
    /// protected start's restart, the update helper, PadForge's own helpers
    /// (the DSX stand-in and the moved copy) and
    /// the Windows tools PadForge runs (schtasks, netsh, pnputil, msiexec,
    /// the installers) all get the block this class builds. A process
    /// PadForge starts as the user without rights (a plugin, the library
    /// host, the install and check copy, a recording's save and the web
    /// helper, each started through <see cref="Plugins.PluginLauncher"/>,
    /// and the failed start's copy) gets the user's own variables instead
    /// (<see cref="UserVariables"/>).</para>
    ///
    /// <para>This part runs in the protected start, before the process may
    /// trust the folder its libraries were unpacked into, so it reads the
    /// machine's variables through <see cref="System32Calls"/> and compares
    /// names by their characters alone, reading no culture.</para>
    /// </summary>
    internal static partial class RightsVariables
    {
        /// <summary>The variables taken from the machine's own block, which no
        /// user value of the same name replaces (Microsoft,
        /// CreateEnvironmentBlock with no token: "system variables
        /// only").</summary>
        internal static readonly string[] SystemVariables =
        {
            "ALLUSERSPROFILE", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "COMPUTERNAME",
            "ComSpec", "DriverData", "NUMBER_OF_PROCESSORS", "OS", "Path", "PATHEXT", "PROCESSOR_ARCHITECTURE",
            "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION", "ProgramData", "ProgramFiles",
            "ProgramFiles(x86)", "ProgramW6432", "PSModulePath", "PUBLIC", "SystemDrive", "SystemRoot", "windir",
        };

        /// <summary>The variables taken from the user's: the user's name,
        /// profile and folders, and the session's name.</summary>
        internal static readonly string[] UserVariables =
        {
            "APPDATA", "CLIENTNAME", "HOMEDRIVE", "HOMEPATH", "HOMESHARE", "LOCALAPPDATA", "LOGONSERVER", "OneDrive",
            "OneDriveCommercial", "OneDriveConsumer", "SESSIONNAME", "TEMP", "TMP", "USERDNSDOMAIN", "USERDOMAIN",
            "USERDOMAIN_ROAMINGPROFILE", "USERNAME", "USERPROFILE",
        };

        /// <summary>The PadForge variables the diagnostics read, taken from
        /// the user's. Each one is a switch or a number, and the one that
        /// names a file, PADFORGE_DIAG, is held to diag.log in the exe's own
        /// folder in a process with rights (SdlDiagLog). A test holds this list to every
        /// PADFORGE_ variable the source reads.</summary>
        internal static readonly string[] PadForgeVariables =
        {
            "PADFORGE_DIAG", "PADFORGE_DS5_WRITE_TRACE", "PADFORGE_MICNOGUARD", "PADFORGE_MICTONE", "PADFORGE_NO_RAWDEDUP",
            "PADFORGE_NO_SWITCH_NFC", "PADFORGE_POLL_AWAKE", "PADFORGE_POLL_HZ", "PADFORGE_VR_CONSUME_SELF",
        };

        /// <summary>
        /// The variables a process with rights gets, as <c>name=value</c>
        /// lines: each of <see cref="SystemVariables"/> as
        /// <paramref name="machine"/> has it, each of
        /// <see cref="UserVariables"/> and <see cref="PadForgeVariables"/> as
        /// <paramref name="user"/> has it, DOTNET_BUNDLE_EXTRACT_BASE_DIR set
        /// to <paramref name="natives"/> unless that is empty, and the lines
        /// of <paramref name="added"/> in place of any of the same name.
        /// When the machine's block could not be read
        /// (<paramref name="machine"/> null), the system's variables come
        /// from <paramref name="user"/>, still by the list. Every other
        /// variable is dropped, the drive lines Windows keeps (<c>=C:=C:\</c>)
        /// too. Each name is kept once, and the lines are sorted by name,
        /// upper and lower case alike, as Windows keeps a block (Microsoft,
        /// CreateProcessW).
        /// </summary>
        internal static List<string> Build(IReadOnlyList<string> machine, IReadOnlyList<string> user, string natives,
            params string[] added)
        {
            var kept = new List<string>();
            Take(kept, machine ?? user, SystemVariables);
            Take(kept, user, UserVariables);
            Take(kept, user, PadForgeVariables);
            if (!string.IsNullOrEmpty(natives)) Put(kept, ProtectedStart.BaseVariable + "=" + natives);
            if (added != null)
                foreach (string line in added)
                    if (!string.IsNullOrEmpty(line) && line[0] != '=' && line.IndexOf('=') > 0) Put(kept, line);
            kept.Sort((a, b) => ProtectedStart.CompareNames(ProtectedStart.NameOf(a), ProtectedStart.NameOf(b)));
            return kept;
        }

        /// <summary>The block CreateProcess takes: each line and a null
        /// character, and one more at the end.</summary>
        internal static string ToBlock(IEnumerable<string> lines)
        {
            var block = new StringBuilder();
            foreach (string line in lines) block.Append(line).Append('\0');
            block.Append('\0');
            return block.ToString();
        }

        /// <summary>
        /// The variables of a process this one starts with its rights: the
        /// machine's own block for the system's variables, this process's
        /// for the user's and PadForge's, and <paramref name="natives"/> as
        /// the unpack folder. Null when this process's variables cannot be
        /// read.
        /// </summary>
        internal static List<string> ForChild(string natives, params string[] added)
        {
            string[] own = System32Calls.Variables();
            if (own == null) return null;
            return Build(System32Calls.MachineVariables(), own, natives, added);
        }

        /// <summary>Whether <paramref name="name"/> is one a process with
        /// rights may get.</summary>
        internal static bool IsKept(string name)
            => Listed(name, SystemVariables) || Listed(name, UserVariables) || Listed(name, PadForgeVariables)
               || SameName(name, ProtectedStart.BaseVariable);

        private static void Take(List<string> kept, IReadOnlyList<string> lines, string[] allowed)
        {
            if (lines == null) return;
            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line) || line[0] == '=') continue;
                string name = ProtectedStart.NameOf(line);
                if (!Listed(name, allowed) || IndexOf(kept, name) >= 0) continue;
                kept.Add(line);
            }
        }

        /// <summary>Puts a line in place of any of the same name.</summary>
        private static void Put(List<string> kept, string line)
        {
            int at = IndexOf(kept, ProtectedStart.NameOf(line));
            if (at >= 0) kept[at] = line;
            else kept.Add(line);
        }

        private static int IndexOf(List<string> lines, string name)
        {
            for (int i = 0; i < lines.Count; i++)
                if (SameName(ProtectedStart.NameOf(lines[i]), name)) return i;
            return -1;
        }

        private static bool Listed(string name, string[] allowed)
        {
            foreach (string one in allowed)
                if (SameName(one, name)) return true;
            return false;
        }

        /// <summary>Names alike with ASCII letters compared without regard to
        /// case. Every name on the lists is ASCII.</summary>
        private static bool SameName(string a, string b)
            => a != null && b != null && ProtectedStart.CompareNames(a, b) == 0;
    }
}
