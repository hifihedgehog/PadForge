using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace PadForge.Services.Install
{
    /// <summary>
    /// Who an assistant program is, in the terms its connection carries
    /// (#600): the program Windows names as the one that started PadForge.exe
    /// --mcp, and for a program an interpreter runs, its script. An assistant
    /// row finds a program by the file its maker puts on the PATH, which for
    /// a package npm installed is a .cmd shim and for one winget installed is
    /// a link. This reads the shim the way npm's cmd-shim writes it, and
    /// follows a link to the file Windows runs, so the row's Add is consent
    /// for the program that connects and the row sees it connect.
    ///
    /// <para>Uses no part of WPF.</para>
    /// </summary>
    internal static class AssistantIdentity
    {
        /// <summary>One program's identity.</summary>
        /// <param name="Path">The program Windows runs, by its final path:
        /// the interpreter for a script, or null when the shim's interpreter
        /// is not on this PC.</param>
        /// <param name="Script">The script the interpreter runs, or
        /// null.</param>
        /// <param name="PackageFolder">The npm package the program or its
        /// script lies in (node_modules\name or node_modules\@scope\name), or
        /// null. A package whose script starts a native program of its own
        /// (Codex's bin\codex.js, Copilot's npm-loader.js) has that program
        /// in this folder.</param>
        internal readonly record struct Identity(string Path, string Script, string PackageFolder);

        /// <summary>The longest shim read. npm's are under 1 KB.</summary>
        internal const int MostShimBytes = 64 * 1024;

        /// <summary>Test seam: opens a shim for reading. Null opens the
        /// file.</summary>
        internal static Func<string, Stream> ShimOpenSeam;

        /// <summary>Test seam: the desktop shell user's variables. Null reads
        /// them through the shell's token.</summary>
        internal static Func<IReadOnlyList<string>> ShellVariablesSeam;

        /// <summary>The identity of the program an assistant row found, or
        /// the default for none.</summary>
        internal static Identity Of(string program)
        {
            if (string.IsNullOrWhiteSpace(program)) return default;
            string full = Full(program);
            string extension = System.IO.Path.GetExtension(full);
            if ((extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
                && ReadShim(full) is { } shim)
                return shim;
            string final = Final(full);
            return new Identity(final, null, PackageFolderOf(final));
        }

        /// <summary>The consent key of an assistant: its script when an
        /// interpreter runs it, so one answer covers that program whichever
        /// copy of the interpreter runs it and no other program of that
        /// interpreter, else its program's path.</summary>
        internal static string ConsentKey(Identity identity)
            => PadForge.Services.Automation.AutomationConsent.KeyOf("assistant",
                identity.Script != null ? null : identity.Path, identity.Script);

        /// <summary>Whether a connected assistant is this program: its
        /// script, or its program with no script, or a program inside this
        /// program's npm package.</summary>
        /// <param name="identity">The row's program.</param>
        /// <param name="hostPath">The program behind the connection, as
        /// Windows names it.</param>
        /// <param name="hostScript">The script that program runs, or
        /// null.</param>
        internal static bool Matches(Identity identity, string hostPath, string hostScript)
        {
            if (identity.Script != null && hostScript != null && SamePath(identity.Script, hostScript)) return true;
            if (identity.Script == null && hostScript == null && identity.Path != null && hostPath != null && SamePath(identity.Path, hostPath))
                return true;
            return identity.PackageFolder != null && (Inside(hostPath, identity.PackageFolder) || Inside(hostScript, identity.PackageFolder));
        }

        /// <summary>Whether a program lies inside the npm package of a
        /// script.</summary>
        internal static bool InsidePackageOf(string script, string program)
            => PackageFolderOf(script) is { } folder && Inside(program, folder);

        /// <summary>
        /// What an npm shim runs, read as cmd-shim writes it (npm/cmd-shim,
        /// lib/index.js, writeShim_): its last line runs
        /// <c>"%_prog%" args "%dp0%\target" %*</c>, where _prog is
        /// <c>%dp0%\node.exe</c> when that file is there and the shebang's
        /// program (node) from the PATH otherwise. A file with no shebang is
        /// run itself: <c>"%dp0%\target" %*</c>. Older versions wrote
        /// <c>%~dp0</c> in place of <c>%dp0%</c> and named the program on each
        /// branch's line. Null when the file is no such shim.
        /// </summary>
        internal static Identity? ReadShim(string file)
        {
            string text = ReadCapped(file);
            if (text == null) return null;
            string dp0 = System.IO.Path.GetDirectoryName(file) + "\\";
            var programs = new List<string>();
            string target = null;
            bool targetRuns = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.IndexOf("%*", StringComparison.Ordinal) < 0) continue;
                // The command that runs: the part after the last & outside
                // quotes, such as endLocal & ... & "%_prog%" "%dp0%\x.js" %*.
                var words = Words(LastCommand(line));
                int at = words.FindLastIndex(w => w.Contains("%dp0%", StringComparison.OrdinalIgnoreCase)
                    || w.Contains("%~dp0", StringComparison.OrdinalIgnoreCase));
                if (at < 0) continue;
                target = Expand(words[at], dp0);
                if (at == 0)
                {
                    targetRuns = true;
                    continue;
                }
                string program = words[0].TrimStart('@');
                if (program.Equals("%_prog%", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Match set in SetProgram.Matches(text)) programs.Add(Expand(set.Groups[1].Value, dp0));
                }
                else
                {
                    programs.Add(Expand(program, dp0));
                }
            }
            if (target == null) return null;
            string script = Full(target);
            if (targetRuns && programs.Count == 0)
            {
                string runs = Final(script);
                return new Identity(runs, null, PackageFolderOf(runs));
            }
            return new Identity(InterpreterOf(programs), script, PackageFolderOf(script));
        }

        /// <summary>A shim's text, read through one open of the file and no
        /// further than <see cref="MostShimBytes"/>: the folder is the user's,
        /// so the file can grow or be swapped between a read of its size and
        /// a second open. Null for no file, an unreadable one or one past the
        /// cap.</summary>
        private static string ReadCapped(string file)
        {
            try
            {
                using var stream = ShimOpenSeam?.Invoke(file)
                    ?? new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[MostShimBytes + 1];
                int got = 0, n;
                while (got < bytes.Length && (n = stream.Read(bytes, got, bytes.Length - got)) > 0) got += n;
                if (got > MostShimBytes) return null;
                // As File.ReadAllText reads it: UTF-8 unless a byte order mark
                // names another encoding.
                using var reader = new StreamReader(new MemoryStream(bytes, 0, got), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>The first program named that is a file here, else the
        /// first bare name found as its .exe on the PATH of the desktop
        /// shell's user, who starts the assistant, as cmd.exe would run it
        /// for that user. PadForge with rights has the machine's Path alone
        /// (RightsVariables), which holds no Node or Python installed for the
        /// user. This process's own PATH is searched when the shell gives no
        /// variables. Null when none is there.</summary>
        private static string InterpreterOf(List<string> programs)
        {
            foreach (string program in programs)
                if (program.Contains('\\') && File.Exists(program)) return Final(Full(program));
            IReadOnlyList<string> shell = null;
            bool shellRead = false;
            foreach (string program in programs)
            {
                if (program.Contains('\\') || program.Contains('/')) continue;
                string name = program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? program.Substring(0, program.Length - 4) : program;
                if (!shellRead)
                {
                    shell = ShellVariables();
                    shellRead = true;
                }
                string path = ValueOf(shell, "PATH");
                string found = path != null ? UserInstall.FindCommandOn(name, path, ValueOf(shell, "PATHEXT")) : UserInstall.FindCommand(name);
                if (found != null && found.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return Final(found);
            }
            return null;
        }

        /// <summary>The desktop shell user's variables, or null when there is
        /// no shell, its variables cannot be read, or the test host's fence
        /// stands.</summary>
        private static IReadOnlyList<string> ShellVariables()
        {
            if (ShellVariablesSeam == null && UserInstall.OffLimitsForTest) return null;
            var seam = ShellVariablesSeam;
            if (seam != null) return seam();
            using var shell = PadForge.Services.Plugins.PluginLauncher.OpenShell();
            return shell == null ? null : PadForge.Services.Plugins.PluginLauncher.ShellVariables(shell, out _);
        }

        /// <summary>A variable's value in NAME=value lines, its name in any
        /// case, or null.</summary>
        private static string ValueOf(IReadOnlyList<string> variables, string name)
        {
            if (variables == null) return null;
            foreach (string line in variables)
                if (line != null && line.Length > name.Length && line[name.Length] == '='
                    && line.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(name.Length + 1);
            return null;
        }

        private static readonly Regex SetProgram = new("SET \"_prog=([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A command's words: quoted ones without their quotes, the
        /// rest split at spaces.</summary>
        private static List<string> Words(string command)
        {
            var words = new List<string>();
            foreach (Match m in Regex.Matches(command, "\"([^\"]*)\"|(\\S+)"))
                words.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            return words;
        }

        /// <summary>The part of a line after its last &amp; outside
        /// quotes.</summary>
        private static string LastCommand(string line)
        {
            bool quoted = false;
            int start = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') quoted = !quoted;
                else if (line[i] == '&' && !quoted) start = i + 1;
            }
            return line.Substring(start);
        }

        /// <summary>A shim's word with its folder put in for %dp0% and
        /// %~dp0, each of which ends with a backslash.</summary>
        private static string Expand(string word, string dp0)
            => Regex.Replace(word, "%dp0%|%~dp0", dp0.Replace("$", "$$"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The npm package a file lies in: the folder after the last
        /// node_modules on its path, two folders for a scoped name. Null for
        /// a file in no package.</summary>
        internal static string PackageFolderOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string[] parts = Full(path).Split('\\');
            int at = Array.FindLastIndex(parts, p => p.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
            if (at < 0) return null;
            int last = at + 1 < parts.Length && parts[at + 1].StartsWith("@", StringComparison.Ordinal) ? at + 2 : at + 1;
            // The file itself lies below the package's folder.
            if (last >= parts.Length - 1 || parts[last].Length == 0) return null;
            return string.Join("\\", parts, 0, last + 1);
        }

        private static bool Inside(string path, string folder)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
            string full = Full(path);
            if (full.StartsWith(Full(folder) + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            string final = UserInstall.FinalPathOf(folder, directory: true);
            return final != null && full.StartsWith(final + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SamePath(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>The file Windows runs for a path: a link followed to its
        /// target (winget's Links\codex.exe is one), or the path itself when
        /// it cannot be opened.</summary>
        private static string Final(string path) => UserInstall.FinalPathOf(path, directory: false) ?? path;

        private static string Full(string path)
        {
            try { return System.IO.Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        }
    }
}
