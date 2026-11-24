using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using PadForge.Services.Automation;
using PadForge.Services.Plugins;

namespace PadForge.Services.Install
{
    /// <summary>What <c>--install</c> puts in place.</summary>
    internal enum InstallWhat
    {
        /// <summary>The skill folder, for an assistant program or into a
        /// folder.</summary>
        Skill,
        /// <summary>The AutoHotkey v2 library, in Documents\AutoHotkey\Lib.</summary>
        AutoHotkey,
        /// <summary>The PowerShell module, in both Modules folders.</summary>
        PowerShell,
        /// <summary>The Python library, in the user site of each Python.</summary>
        Python,
        /// <summary>PadForge's entry in an assistant program's settings.</summary>
        Assistant,
    }

    /// <summary>Whether an install is in place, as this PadForge would write
    /// it.</summary>
    internal enum InstallState
    {
        NotInstalled,
        /// <summary>Byte for byte what this PadForge writes.</summary>
        Current,
        /// <summary>There, and different: written by another PadForge.</summary>
        Older,
    }

    /// <summary>An <c>--install</c> command line: what, for which program or
    /// into which folder or file, and whether it takes the install out or
    /// only looks.</summary>
    internal sealed record InstallRequest(InstallWhat What, string Target, bool Remove = false, bool Check = false)
    {
        /// <summary>A file the run writes what it said into, for the page
        /// that started it through the shell, which gets no output back
        /// (<c>--report &lt;file&gt;</c>). Null for none.</summary>
        public string Report { get; init; }

        /// <summary>The words that ask for it, after the exe.</summary>
        public IReadOnlyList<string> Words()
        {
            var words = new List<string> { "--install", UserInstall.Word(What) };
            if (!string.IsNullOrEmpty(Target)) words.Add(Target);
            if (Remove) words.Add("--remove");
            if (Check) words.Add("--check");
            if (!string.IsNullOrEmpty(Report)) { words.Add("--report"); words.Add(Report); }
            return words;
        }
    }

    /// <summary>What an install started from the page came to: its exit
    /// code, why it failed in words, or null, and what the run said, read
    /// back from its report (null when it said nothing).</summary>
    internal sealed record InstallRun(int Code, string Error, string Said);

    /// <summary>
    /// <c>PadForge.Exe --install &lt;what&gt;</c> (#600): the one way PadForge
    /// writes into the user's own folders and other programs' settings. The
    /// page's buttons start it with the user's own rights through the
    /// desktop shell (<see cref="AsUserAsync"/>, PluginLauncher's route), and
    /// a person or a script runs it as it is. Either way the run has the
    /// user's own token, so the folders are the user's
    /// (<see cref="UserFolders"/>) and every file it writes is the user's to
    /// change. Run from a prompt with rights, it starts itself again through
    /// the shell first (<see cref="RunAsTheUser"/>).
    ///
    /// <para>Each install writes only its own folder or file
    /// (<see cref="OwnFiles"/>) and takes out what a newer copy no longer
    /// has. An assistant's entry goes in through the maker's own add command
    /// where one is documented, and into the settings file otherwise, with
    /// every other key left as it was.</para>
    ///
    /// <para>Exit codes: 0 done (or, with <c>--check</c>, current), 1 it
    /// failed, 2 the command line is wrong, and with <c>--check</c> 6 an
    /// older copy is in place and 7 none is.</para>
    ///
    /// <para>Uses no part of WPF: it runs in the client run before the app
    /// would load.</para>
    /// </summary>
    internal static class UserInstall
    {
        internal const int Done = 0, Failed = 1, WrongCommandLine = 2, IsOlder = 6, IsMissing = 7;

        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app: no program is started, neither an assistant's
        /// add command nor a Python to ask for its user site.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Test seam: runs a program in place of Windows and gives its
        /// exit code and output. Null in production.</summary>
        internal static Func<string, IReadOnlyList<string>, (int Code, string Output)> ToolSeam;

        /// <summary>Test seam: finds a command in place of the PATH. Null in
        /// production.</summary>
        internal static Func<string, string> FindSeam;

        /// <summary>Test seam: the Pythons PEP 514 registers, in place of the
        /// registry. Null in production.</summary>
        internal static Func<IReadOnlyList<string>> PythonsSeam;

        /// <summary>The exe an assistant starts: this PadForge.</summary>
        internal static Func<string> Exe = () => Environment.ProcessPath;

        internal static string Word(InstallWhat what) => what switch
        {
            InstallWhat.AutoHotkey => "autohotkey",
            InstallWhat.PowerShell => "powershell",
            InstallWhat.Python => "python",
            InstallWhat.Assistant => "assistant",
            _ => "skill",
        };

        internal const string Usage =
            "--install takes what to install: skill <program or folder>, autohotkey [folder or file],\r\n" +
            "powershell [folder or file], python [folder or file] or assistant <program>, then --remove or --check if wanted.\r\n" +
            "--install assistant --check with no program checks every program. --report <file> also writes what the run says into the file.\r\n" +
            "The programs: ";

        internal static string UsageText => Usage + string.Join(", ", AssistantHosts.All.Select(h => h.Key)) + ".";

        /// <summary>Reads the words after <c>--install</c>, from
        /// <paramref name="i"/>, which it leaves on the last word it took.
        /// Null and the request, or the mistake in words.</summary>
        internal static string Parse(IReadOnlyList<string> args, ref int i, out InstallRequest request)
        {
            request = null;
            string word = i + 1 < args.Count ? args[++i]?.Trim().ToLowerInvariant() : null;
            InstallWhat what;
            switch (word)
            {
                case "skill": what = InstallWhat.Skill; break;
                case "autohotkey": case "ahk": what = InstallWhat.AutoHotkey; break;
                case "powershell": what = InstallWhat.PowerShell; break;
                case "python": what = InstallWhat.Python; break;
                case "assistant": what = InstallWhat.Assistant; break;
                default: return UsageText;
            }
            string target = null, report = null;
            bool remove = false, check = false;
            while (i + 1 < args.Count)
            {
                string next = args[i + 1] ?? string.Empty;
                string lower = next.ToLowerInvariant();
                if (lower is "--remove" or "/remove" or "-remove") { remove = true; i++; continue; }
                if (lower is "--check" or "/check" or "-check") { check = true; i++; continue; }
                if (lower is "--report" or "/report" or "-report")
                {
                    if (i + 2 >= args.Count || !Path.IsPathFullyQualified(args[i + 2] ?? string.Empty))
                        return "--report takes a full path for the file it writes.";
                    report = args[i + 2];
                    i += 2;
                    continue;
                }
                if (target == null && !next.StartsWith("-", StringComparison.Ordinal)) { target = next.Trim(); i++; continue; }
                break;
            }
            if (remove && check) return "--install takes --remove or --check, not both.";
            if (what == InstallWhat.Assistant && check && string.IsNullOrEmpty(target))
            {
                // Every program at once, for the Automation page's rows.
                request = new InstallRequest(what, null, false, true) { Report = report };
                return null;
            }
            if (what is InstallWhat.Skill or InstallWhat.Assistant && string.IsNullOrEmpty(target))
                return "--install " + Word(what) + " needs a program (" + string.Join(", ", AssistantHosts.All.Select(h => h.Key)) + ")"
                       + (what == InstallWhat.Skill ? " or a folder." : ".");
            if (what == InstallWhat.Assistant && AssistantHosts.Find(target) == null)
                return "PadForge knows no assistant program named " + target + ". " + UsageText;
            if (what == InstallWhat.Skill && AssistantHosts.Find(target) is { } host && host.SkillsFolder == null)
                return host.Name + " takes skills only as an upload, so there is no folder to install into.";
            request = new InstallRequest(what, target, remove, check) { Report = report };
            return null;
        }

        // ── The run ──

        /// <summary>
        /// The client run's <c>--install</c>: with rights, it starts itself
        /// again through the desktop shell, so the install is the user's.
        /// When the shell runs with rights too (UAC is off), every program of
        /// the user does, and the install runs here.
        /// </summary>
        internal static int RunAsTheUser(InstallRequest request, Action<string> say, Action<string> sayError)
        {
            if (HasRights() && !ShellHasRights())
            {
                // The run as the user writes what it says into a report,
                // which this run reads back and says, so a person at a
                // prompt with rights sees what a prompt without them would.
                var started = RunAsUserAsync(request).GetAwaiter().GetResult();
                if (started.Error != null) sayError(started.Error);
                else if (!string.IsNullOrEmpty(started.Said) && request.Report == null) say(started.Said.TrimEnd());
                return started.Code;
            }
            return Run(request, say, sayError);
        }

        /// <summary>Test seam: whether this process has administrator
        /// rights. Null reads the process's own token.</summary>
        internal static Func<bool> HasRightsSeam;

        private static bool HasRights()
        {
            var seam = HasRightsSeam;
            if (seam != null) return seam();
            using var self = Process.GetCurrentProcess();
            return PluginLauncher.HasAdministratorRights(self.SafeHandle) != false;
        }

        /// <summary>Test seam: whether the desktop shell has administrator
        /// rights. Null reads the shell's own token.</summary>
        internal static Func<bool> ShellHasRightsSeam;

        private static bool ShellHasRights()
        {
            var seam = ShellHasRightsSeam;
            if (seam != null) return seam();
            using var shell = PluginLauncher.OpenShell();
            return shell != null && PluginLauncher.HasAdministratorRights(shell) != false;
        }

        /// <summary>Runs an install here, in this process, with its rights.
        /// Gives the exit code.</summary>
        internal static int Run(InstallRequest request, Action<string> say, Action<string> sayError)
        {
            if (string.IsNullOrEmpty(request.Report)) return RunHere(request, say, sayError, out _);
            var told = new StringBuilder();
            int code = RunHere(request, t => { told.Append(t).Append("\r\n"); say(t); }, t => { told.Append(t).Append("\r\n"); sayError(t); }, out string report);
            WriteReport(request.Report, report ?? told.ToString());
            return code;
        }

        private static int RunHere(InstallRequest request, Action<string> say, Action<string> sayError, out string report)
        {
            report = null;
            try
            {
                if (request.What == InstallWhat.Assistant && request.Check && string.IsNullOrEmpty(request.Target))
                {
                    report = CheckAll(say);
                    return Done;
                }
                if (request.Check)
                {
                    var state = Check(request);
                    say(state switch { InstallState.Current => "current", InstallState.Older => "older", _ => "not installed" });
                    return state switch { InstallState.Current => Done, InstallState.Older => IsOlder, _ => IsMissing };
                }
                string done = request.What switch
                {
                    InstallWhat.Skill => Skill(request),
                    InstallWhat.Assistant => Assistant(request),
                    _ => Library(request),
                };
                say(done);
                return Done;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                sayError(ex.Message);
                return Failed;
            }
        }

        // ── The skill ──

        private static IReadOnlyList<(string Path, byte[] Bytes)> SkillFiles()
            => AssistantSkill.Files.Select(f => (f.Path, f.Bytes)).ToList();

        /// <summary>The folder the skill goes into: the program's skills
        /// folder, or the folder given, each with padforge under it.</summary>
        internal static string SkillFolder(string target)
        {
            if (AssistantHosts.Find(target) is { } host)
            {
                string skills = host.SkillsFolder?.Invoke()
                    ?? throw new InvalidOperationException("PadForge finds no folder of the user's for " + host.Name + "'s skills.");
                return Path.Combine(skills, AssistantSkill.Name);
            }
            if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target))
                throw new ArgumentException("A folder for the skill is a full path, such as C:\\Skills.");
            // The folder given holds the install, never is it: a folder of
            // the user's named for PadForge stays theirs.
            return Path.Combine(target, AssistantSkill.Name);
        }

        private static string Skill(InstallRequest request)
        {
            string folder = SkillFolder(request.Target);
            if (request.Remove)
                return OwnFiles.RemoveFolder(folder, "SKILL.md") ? "Removed " + folder + "." : "No skill was installed in " + folder + ".";
            var (written, removed) = OwnFiles.Mirror(folder, SkillFiles());
            return "The skill is in " + folder + " (" + written + " written, " + removed + " removed).";
        }

        // ── The libraries ──

        /// <summary>Where a library goes: the folder or file given, or each
        /// place its language finds it.</summary>
        internal static IReadOnlyList<string> LibraryFiles(InstallWhat what, string target)
        {
            var library = AutomationLibraries.Of(KindOf(what));
            if (!string.IsNullOrWhiteSpace(target))
            {
                if (!Path.IsPathFullyQualified(target)) throw new ArgumentException("A folder or file for the library is a full path.");
                bool file = string.Equals(Path.GetExtension(target), Path.GetExtension(library.FileName), StringComparison.OrdinalIgnoreCase);
                return new[] { file ? target : Path.Combine(target, library.FileName) };
            }
            switch (what)
            {
                case InstallWhat.AutoHotkey:
                {
                    // AutoHotkey v2 reads #Include <Name> from the user
                    // library, A_MyDocuments\AutoHotkey\Lib.
                    string documents = UserFolders.Of(UserFolder.Documents) ?? throw new InvalidOperationException("Windows names no Documents folder.");
                    return new[] { Path.Combine(documents, "AutoHotkey", "Lib", library.FileName) };
                }
                case InstallWhat.PowerShell:
                {
                    // PowerShell 7 reads Documents\PowerShell\Modules, Windows
                    // PowerShell Documents\WindowsPowerShell\Modules. A module
                    // is a folder of its own name.
                    string documents = UserFolders.Of(UserFolder.Documents) ?? throw new InvalidOperationException("Windows names no Documents folder.");
                    return new[]
                    {
                        Path.Combine(documents, "PowerShell", "Modules", "PadForge", library.FileName),
                        Path.Combine(documents, "WindowsPowerShell", "Modules", "PadForge", library.FileName),
                    };
                }
                default:
                {
                    var sites = UserSites();
                    if (sites.Count == 0) throw new InvalidOperationException("PadForge finds no Python on this PC.");
                    return sites.Select(site => Path.Combine(site, library.FileName)).ToList();
                }
            }
        }

        private static AutomationLibraryKind KindOf(InstallWhat what) => what switch
        {
            InstallWhat.Python => AutomationLibraryKind.Python,
            InstallWhat.PowerShell => AutomationLibraryKind.PowerShell,
            _ => AutomationLibraryKind.AutoHotkey,
        };

        private static string Library(InstallRequest request)
        {
            byte[] bytes = AutomationLibraries.Read(KindOf(request.What));
            var files = LibraryFiles(request.What, request.Target);
            var said = new StringBuilder();
            foreach (string file in files)
            {
                // The module's own folder holds the module alone.
                string folder = Path.GetDirectoryName(file);
                bool ownFolder = request.What == InstallWhat.PowerShell && string.IsNullOrEmpty(request.Target);
                if (request.Remove)
                {
                    bool gone = ownFolder ? OwnFiles.RemoveFolder(folder, Path.GetFileName(file)) : DeleteIfThere(file, bytes);
                    said.Append(gone ? "Removed " : "Nothing to remove in ").Append(ownFolder ? folder : file).Append(". ");
                    continue;
                }
                if (ownFolder) OwnFiles.Mirror(folder, new[] { (Path.GetFileName(file), bytes) });
                else OwnFiles.WriteIfDifferent(file, bytes);
                said.Append("Installed ").Append(file).Append(". ");
            }
            return said.ToString().TrimEnd();
        }

        /// <summary>Removes a library's file when it is one PadForge wrote:
        /// its first line is the library's own. Another file of that name
        /// stays.</summary>
        private static bool DeleteIfThere(string file, byte[] library)
        {
            if (!File.Exists(file)) return false;
            string first = File.ReadLines(file).FirstOrDefault() ?? string.Empty;
            string ours = new StreamReader(new MemoryStream(library)).ReadLine() ?? string.Empty;
            if (!string.Equals(first.Trim(), ours.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException(file + " is not a library PadForge wrote, so PadForge leaves it as it is.");
            File.Delete(file);
            return true;
        }

        /// <summary>The user site of each Python PEP 514 registers, as that
        /// Python names it (site.getusersitepackages), so a Store Python's
        /// own folder and a PYTHONUSERBASE the user set are both followed.
        /// A Python that does not answer is left out.</summary>
        internal static IReadOnlyList<string> UserSites()
        {
            var sites = new List<string>();
            foreach (string python in Pythons())
            {
                var (code, output) = RunTool(python, new[] { "-c", "import site, sys; sys.stdout.write(site.getusersitepackages())" }, 10000);
                string site = output?.Trim();
                if (code == 0 && !string.IsNullOrEmpty(site) && Path.IsPathFullyQualified(site)
                    && !sites.Contains(site, StringComparer.OrdinalIgnoreCase))
                    sites.Add(site);
            }
            return sites;
        }

        /// <summary>The python.exe of each Python registered under
        /// HKEY_CURRENT_USER and HKEY_LOCAL_MACHINE\Software\Python (PEP 514),
        /// the launcher's own key left out, each once.</summary>
        internal static IReadOnlyList<string> Pythons()
        {
            if (PythonsSeam == null && OffLimitsForTest) return Array.Empty<string>();
            var seam = PythonsSeam;
            if (seam != null) return seam();
            var found = new List<string>();
            foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"Software\Python");
                    if (root == null) continue;
                    foreach (string company in root.GetSubKeyNames())
                    {
                        if (string.Equals(company, "PyLauncher", StringComparison.OrdinalIgnoreCase)) continue;
                        using var companyKey = root.OpenSubKey(company);
                        if (companyKey == null) continue;
                        foreach (string tag in companyKey.GetSubKeyNames())
                        {
                            using var install = companyKey.OpenSubKey(tag + @"\InstallPath");
                            if (install == null) continue;
                            string exe = install.GetValue("ExecutablePath") as string;
                            if (string.IsNullOrEmpty(exe) && install.GetValue(null) is string folder) exe = Path.Combine(folder, "python.exe");
                            if (!string.IsNullOrEmpty(exe) && File.Exists(exe) && !found.Contains(exe, StringComparer.OrdinalIgnoreCase))
                                found.Add(exe);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // A key that cannot be read registers no Python.
                }
            }
            return found;
        }

        // ── An assistant's entry ──

        private static string Assistant(InstallRequest request)
        {
            var host = AssistantHosts.Find(request.Target);
            string exe = Exe();
            // The command on the PATH, or the program's own copy where it
            // keeps one off the PATH, such as the one an editor's extension
            // carries (AssistantPrograms.CommandOf).
            string command = host.Command == null ? null : FindCommand(host.Command) ?? AssistantPrograms.CommandOf(host.Key);
            if (command != null)
            {
                // An entry already there, this PadForge's or another's, is
                // taken out first, so Update adds over it whether or not the
                // host's add command replaces an entry of the same name.
                if (!request.Remove && AssistantState(host) != InstallState.NotInstalled)
                    RunTool(command, host.RemoveWords, 60000);
                var words = request.Remove ? host.RemoveWords : host.AddWords(exe);
                var (code, output) = RunTool(command, words, 60000);
                if (code != 0)
                    throw new InvalidOperationException(host.Name + "'s own command did not " + (request.Remove ? "take PadForge out" : "add PadForge")
                                                        + " (exit code " + code + "): " + (output ?? string.Empty).Trim());
                return (request.Remove ? "Took PadForge out of " : "Added PadForge to ") + host.Name + " through its own command.";
            }
            if (host.Command != null && !host.FileWhenNoCommand)
                throw new InvalidOperationException(host.Name + "'s command " + host.Command + " is not on this PC.");
            string file = host.SettingsFile?.Invoke() ?? throw new InvalidOperationException("PadForge finds no folder of the user's for " + host.Name + "'s settings.");
            if (request.Remove)
                return OwnFiles.RemoveJsonEntry(file, host.Container, AssistantHosts.EntryName)
                    ? "Took PadForge out of " + file + "." : "PadForge was not in " + file + ".";
            OwnFiles.SetJsonEntry(file, host.Container, AssistantHosts.EntryName, host.Entry(exe));
            return "Added PadForge to " + file + ". Close " + host.Name + " and open it again to see it.";
        }

        // ── Looking ──

        /// <summary>Whether an install is in place as this PadForge writes
        /// it. Reads only, so the app may ask it with its own rights for the
        /// folders of its own user.</summary>
        internal static InstallState Check(InstallRequest request)
        {
            switch (request.What)
            {
                case InstallWhat.Skill:
                    return Matches(OwnFiles.Matches(SkillFolder(request.Target), SkillFiles()));
                case InstallWhat.Assistant:
                    return AssistantState(AssistantHosts.Find(request.Target));
                default:
                {
                    byte[] bytes = AutomationLibraries.Read(KindOf(request.What));
                    var states = LibraryFiles(request.What, request.Target)
                        .Select(f => File.Exists(f) ? (File.ReadAllBytes(f).AsSpan().SequenceEqual(bytes) ? InstallState.Current : InstallState.Older) : InstallState.NotInstalled)
                        .ToList();
                    if (states.All(s => s == InstallState.Current)) return InstallState.Current;
                    return states.Any(s => s != InstallState.NotInstalled) ? InstallState.Older : InstallState.NotInstalled;
                }
            }
        }

        private static InstallState Matches(bool? matches)
            => matches == null ? InstallState.NotInstalled : matches.Value ? InstallState.Current : InstallState.Older;

        /// <summary>Whether a host's settings name PadForge, and this
        /// PadForge's exe with --mcp.</summary>
        internal static InstallState AssistantState(AssistantHost host)
        {
            string file = host?.SettingsFile?.Invoke();
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return InstallState.NotInstalled;
            string exe = Exe();
            if (host.Container == null)
            {
                // A TOML file: PadForge's own table and its command line.
                string text;
                try { text = File.ReadAllText(file); }
                catch (IOException) { return InstallState.NotInstalled; }
                int at = text.IndexOf("[mcp_servers." + AssistantHosts.EntryName + "]", StringComparison.Ordinal);
                if (at < 0) return InstallState.NotInstalled;
                int end = text.IndexOf("\n[", at + 1, StringComparison.Ordinal);
                string table = end < 0 ? text.Substring(at) : text.Substring(at, end - at);
                string quoted = "\"" + exe.Replace("\\", "\\\\") + "\"";
                string literal = "'" + exe + "'";
                return (table.Contains(quoted, StringComparison.OrdinalIgnoreCase) || table.Contains(literal, StringComparison.OrdinalIgnoreCase))
                       && table.Contains("--mcp", StringComparison.Ordinal) ? InstallState.Current : InstallState.Older;
            }
            var entry = OwnFiles.JsonEntry(file, host.Container, AssistantHosts.EntryName);
            if (entry == null) return InstallState.NotInstalled;
            bool sameExe = string.Equals(entry["command"]?.GetValue<string>(), exe, StringComparison.OrdinalIgnoreCase);
            bool mcp = entry["args"] is System.Text.Json.Nodes.JsonArray args && args.Any(a => a?.GetValue<string>() == "--mcp");
            return sameExe && mcp ? InstallState.Current : InstallState.Older;
        }

        // ── Every assistant at once ──

        /// <summary>What <c>--install assistant --check</c> with no program
        /// reports of each: its key, the program's own file when it is on
        /// this PC (null when not), PadForge's entry in its settings
        /// (current, older, missing), its skill (current, older, newer,
        /// missing, or upload when it takes skills only as an upload) and
        /// its settings file.</summary>
        internal sealed record AssistantCheck(string Key, string Program, string Entry, string Skill, string Settings);

        /// <summary>Checks every assistant program, says one line for each and
        /// gives the report the page reads: a JSON array of
        /// <see cref="AssistantCheck"/>. A program whose check fails is
        /// reported as not found, so one bad settings file costs one
        /// row.</summary>
        internal static string CheckAll(Action<string> say)
        {
            var all = new System.Text.Json.Nodes.JsonArray();
            foreach (var host in AssistantHosts.All)
            {
                string program = null, entry = "missing", skill = "missing", settings = null;
                try
                {
                    program = AssistantPrograms.Find(host.Key);
                    settings = host.SettingsFile?.Invoke();
                    entry = Word(AssistantState(host));
                    skill = host.SkillsFolder == null ? "upload" : SkillWord(host);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                    or ArgumentException or NotSupportedException or System.Security.SecurityException)
                {
                    program = null;
                }
                say(host.Key + ": " + (program == null ? "not on this PC" : program) + ", entry " + entry + ", skill " + skill + ".");
                all.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["key"] = host.Key,
                    ["program"] = program,
                    ["entry"] = entry,
                    ["skill"] = skill,
                    ["settings"] = settings,
                });
            }
            return all.ToJsonString();
        }

        /// <summary>Reads <see cref="CheckAll"/>'s report back. Entries it
        /// cannot read are left out, and a report that is no JSON array gives
        /// an empty list.</summary>
        internal static IReadOnlyList<AssistantCheck> ReadChecks(string report)
        {
            var list = new List<AssistantCheck>();
            if (string.IsNullOrWhiteSpace(report)) return list;
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(report) is not System.Text.Json.Nodes.JsonArray all) return list;
                foreach (var node in all)
                {
                    if (node is not System.Text.Json.Nodes.JsonObject o) continue;
                    string Text(string name) => o[name] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string s) ? s : null;
                    string key = Text("key");
                    if (AssistantHosts.Find(key) == null) continue;
                    list.Add(new AssistantCheck(key, Text("program"), Text("entry") ?? "missing", Text("skill") ?? "missing", Text("settings")));
                }
            }
            catch (System.Text.Json.JsonException)
            {
                list.Clear();
            }
            return list;
        }

        private static string Word(InstallState state) => state switch
        {
            InstallState.Current => "current",
            InstallState.Older => "older",
            _ => "missing",
        };

        /// <summary>The skill in a program's skills folder: missing, current,
        /// older, or newer when its SKILL.md names a version above this
        /// PadForge's own.</summary>
        private static string SkillWord(AssistantHost host)
        {
            string folder = SkillFolder(host.Key);
            var state = Matches(OwnFiles.Matches(folder, SkillFiles()));
            if (state != InstallState.Older) return Word(state);
            string installed = SkillVersion(ReadSkill(Path.Combine(folder, "SKILL.md")));
            string ours = SkillVersion(AssistantSkill.Files.FirstOrDefault(f => f.Path == "SKILL.md")?.Bytes);
            return Version.TryParse(installed, out var a) && Version.TryParse(ours, out var b) && a > b ? "newer" : "older";
        }

        private static byte[] ReadSkill(string file)
        {
            try { return File.Exists(file) ? File.ReadAllBytes(file) : null; }
            catch (IOException) { return null; }
        }

        /// <summary>The version a SKILL.md's front matter names on a line of
        /// its own, <c>version: 6.1.0</c>, or null.</summary>
        internal static string SkillVersion(byte[] skill)
        {
            if (skill == null) return null;
            using var reader = new StreamReader(new MemoryStream(skill));
            if (reader.ReadLine()?.Trim() != "---") return null;
            for (string line = reader.ReadLine(); line != null && line.Trim() != "---"; line = reader.ReadLine())
            {
                string t = line.Trim();
                if (!t.StartsWith("version:", StringComparison.Ordinal)) continue;
                string value = t.Substring("version:".Length).Trim().Trim('"', '\'');
                return value.Length > 0 ? value : null;
            }
            return null;
        }

        // ── The report ──

        /// <summary>The longest report read back, in characters.</summary>
        internal const int LongestReport = 64 * 1024;

        /// <summary>Writes the report as a new file: a file already at that
        /// path, a link included, is left alone and nothing is written.</summary>
        private static void WriteReport(string file, string text)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] bytes = new UTF8Encoding(false).GetBytes(text ?? string.Empty);
                stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A report that cannot be written leaves the exit code to
                // say how the run went.
            }
        }

        /// <summary>Test seam: the folder a page's install writes its report
        /// into. Null reads the user's temp folder from the desktop shell's
        /// variables, and this process's own when the shell gives none.</summary>
        internal static Func<string> ReportFolderSeam;

        private static string NewReportFile()
        {
            string folder = ReportFolderSeam?.Invoke();
            if (folder == null)
            {
                var shell = Launcher == null ? ShellEnvironment() : null;
                string temp = shell?.FirstOrDefault(v => v.StartsWith("TEMP=", StringComparison.OrdinalIgnoreCase))?.Substring(5);
                folder = !string.IsNullOrEmpty(temp) && Path.IsPathFullyQualified(temp) ? temp : Path.GetTempPath();
            }
            // The folder as Windows resolves it now, so a folder moved by a
            // link after this point is told apart when the report is read
            // (TakeReport).
            folder = FinalPathOf(folder, directory: true) ?? folder;
            return Path.Combine(folder, "PadForge-install-" + Guid.NewGuid().ToString("N") + ".txt");
        }

        /// <summary>
        /// Reads a report the run wrote and deletes it. The run had no rights
        /// and wrote into a folder the user's programs can change, and this
        /// may run with rights, so the file is opened once, without following
        /// a link at its own name, and read and deleted through that one
        /// handle only when Windows' final path of it is the path asked: a
        /// file reached through a folder redirected since is left alone.
        /// What it holds is read as text only, capped, and every line is
        /// cleaned of characters that would make it read as something else.
        /// Null when there is none.
        /// </summary>
        internal static string TakeReport(string file)
        {
            if (string.IsNullOrEmpty(file) || !Path.IsPathFullyQualified(file)) return null;
            using var handle = CreateFileW(file, GenericRead | DeleteAccess, FileShareRead | FileShareDelete, IntPtr.Zero,
                OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            if (!string.Equals(FinalPathOf(handle), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)) return null;
            if (!GetFileInformationByHandle(handle, out var info)) return null;
            bool link = (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0;
            string text = null;
            if (!link)
            {
                try
                {
                    // Read through the handle without handing it over, so the
                    // same handle deletes the file below.
                    var bytes = new byte[LongestReport * 4];
                    int got = 0, n;
                    while (got < bytes.Length && (n = RandomAccess.Read(handle, bytes.AsSpan(got), got)) > 0) got += n;
                    text = new UTF8Encoding(false).GetString(bytes, 0, got);
                    if (text.Length > LongestReport) text = text.Substring(0, LongestReport);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    text = null;
                }
            }
            // The handle's own file goes, a link itself and never what it
            // names.
            var delete = new FileDispositionInfo { DeleteFile = 1 };
            SetFileInformationByHandle(handle, FileDispositionInfoClass, ref delete, (uint)Marshal.SizeOf<FileDispositionInfo>());
            if (string.IsNullOrEmpty(text)) return null;
            var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => PadForge.Engine.Common.ShownText.Clean(l));
            string clean = string.Join("\r\n", lines).Trim();
            return clean.Length == 0 ? null : clean;
        }

        /// <summary>Windows' final path of a file or folder, without the
        /// \\?\ in front, or null when it cannot be opened.</summary>
        private static string FinalPathOf(string path, bool directory)
        {
            if (string.IsNullOrEmpty(path)) return null;
            using var handle = CreateFileW(path, 0, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting,
                directory ? FileFlagBackupSemantics : 0, IntPtr.Zero);
            return handle.IsInvalid ? null : FinalPathOf(handle);
        }

        private static string FinalPathOf(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
        {
            var text = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandleW(handle, text, (uint)text.Capacity, 0);
            if (n == 0 || n >= text.Capacity) return null;
            string path = text.ToString();
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path.Substring(8);
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
        }

        private const uint GenericRead = 0x80000000, DeleteAccess = 0x00010000;
        private const uint FileShareRead = 1, FileShareWrite = 2, FileShareDelete = 4, OpenExisting = 3;
        private const uint FileFlagOpenReparsePoint = 0x00200000, FileFlagBackupSemantics = 0x02000000;
        private const int FileDispositionInfoClass = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfo { public byte DeleteFile; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle handle, StringBuilder path, uint length, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out ByHandleFileInformation info);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int infoClass,
            ref FileDispositionInfo info, uint size);

        // ── Programs ──

        /// <summary>The full path of a command on the PATH, with the
        /// extensions PATHEXT lists, or null. A command is never looked for
        /// in the current folder.</summary>
        internal static string FindCommand(string name)
        {
            if (FindSeam == null && OffLimitsForTest) return null;
            var seam = FindSeam;
            if (seam != null) return seam(name);
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (string folder in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Path.IsPathFullyQualified(folder)) continue;
                if (IsStandIn(folder)) continue;
                foreach (string extension in new[] { ".exe", ".cmd", ".bat" })
                {
                    if (!extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;
                    string candidate = Path.Combine(folder, name + extension);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        /// <summary>Whether a PATH folder holds a stand-in rather than a
        /// program: VS Code puts a copilot of its own on the PATH, in a
        /// folder named copilot-shim, which says the Copilot CLI is not there
        /// and ends with code 0 (run on this PC on 2026-11-24). A command
        /// found there is no command.</summary>
        internal static bool IsStandIn(string folder)
            => string.Equals(Path.GetFileName((folder ?? string.Empty).TrimEnd('\\', '/')), "copilot-shim", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Runs one of the user's own programs (an assistant's add command or
        /// a Python) with this process's rights and variables, which in an
        /// install are the user's, and gives its exit code and what it
        /// wrote. A batch file runs through cmd.exe, and a path cmd.exe would
        /// read as more than a path is refused. -1 with no output when the
        /// program could not run or did not end in <paramref name="timeoutMs"/>.
        /// </summary>
        internal static (int Code, string Output) RunTool(string exe, IReadOnlyList<string> words, int timeoutMs, IReadOnlyList<string> variables = null)
        {
            if (ToolSeam == null && OffLimitsForTest) return (-1, null);
            if (ToolSeam != null) return ToolSeam(exe, words);
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            if (variables != null)
            {
                // The desktop shell's own variables, for a run started for
                // the user from PadForge with UAC off.
                psi.Environment.Clear();
                foreach (string line in variables)
                {
                    int eq = line.IndexOf('=', 1);
                    if (eq > 0) psi.Environment[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            string extension = Path.GetExtension(exe);
            if (string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
            {
                var all = new[] { exe }.Concat(words).ToList();
                if (all.Any(w => w.IndexOfAny(new[] { '"', '%', '^', '&', '|', '<', '>', '!', '\r', '\n' }) >= 0))
                    return (-1, "A word of the command holds a character cmd.exe would read: " + string.Join(" ", all));
                psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                psi.Arguments = "/d /s /c \"" + string.Join(" ", all.Select(w => "\"" + w + "\"")) + "\"";
            }
            else
            {
                psi.FileName = exe;
                foreach (string word in words) psi.ArgumentList.Add(word);
            }
            try
            {
                using var process = Process.Start(psi);
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    return (-1, "It did not end in " + timeoutMs / 1000 + " seconds.");
                }
                return (process.ExitCode, output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult());
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return (-1, ex.Message);
            }
        }

        // ── From the app ──

        /// <summary>Test seam: the launcher the app starts an install with.
        /// Null uses the plugins' launcher.</summary>
        internal static IPluginLauncher Launcher;

        /// <summary>How long an install started from the page may take.</summary>
        internal const int AsUserMs = 120000;

        /// <summary>
        /// Starts <c>PadForge.exe --install</c> with the user's own rights,
        /// through the desktop shell as a plugin starts (PluginLauncher), and
        /// waits for its exit code. When the shell itself runs with rights
        /// (UAC is off), the user's every program does, and the install runs
        /// in this process instead. Error is null when the code is the
        /// install's own.
        /// </summary>
        internal static async Task<(int Code, string Error)> AsUserAsync(InstallRequest request)
        {
            var run = await RunAsUserAsync(request).ConfigureAwait(false);
            return (run.Code, run.Error);
        }

        /// <summary>
        /// <see cref="AsUserAsync"/>, and what the run said. The run writes
        /// what it says into a report in the user's own temp folder
        /// (<c>--report</c>), since a start through the shell hands back its
        /// exit code alone. This reads the report back and deletes it
        /// (<see cref="TakeReport"/>), so a failed install names its reason
        /// and a check of every assistant gives its rows.
        /// </summary>
        internal static async Task<InstallRun> RunAsUserAsync(InstallRequest request)
        {
            if (Launcher == null && OffLimitsForTest) return new InstallRun(Failed, "The test host starts no install.", null);
            string exe = Exe();
            string report = string.IsNullOrEmpty(request.Report) ? NewReportFile() : null;
            if (report != null) request = request with { Report = report };
            try
            {
                var launcher = Launcher ?? new PluginLauncher();
                string line = "\"" + exe + "\" " + string.Join(" ", request.Words().Select(Quote));
                var launch = launcher.Start(new PluginCommand(exe, line, Path.GetDirectoryName(exe)), new Dictionary<string, string>());
                if (launch.Refusal == PluginRefusal.ShellElevated)
                {
                    // UAC is off: every program of the user has full rights,
                    // the shell included. The install runs as its own
                    // PadForge.exe with the shell's variables, so the user's
                    // PATH finds the assistants' commands, which PadForge's
                    // own allow list of variables would not.
                    var (code, output) = await Task.Run(() => RunTool(exe, request.Words(), AsUserMs, ShellEnvironment())).ConfigureAwait(false);
                    string said = report != null ? TakeReport(report) : null;
                    said ??= string.IsNullOrWhiteSpace(output) ? null : PadForge.Engine.Common.ShownText.Clean(output.Trim());
                    return new InstallRun(code, IsAnswer(code) ? null : said ?? "The install ended with code " + code + ".", said);
                }
                if (launch.Process == null)
                    return new InstallRun(Failed, "Windows did not start the install with the user's rights (" + launch.Refusal
                        + (launch.Error != 0 ? ", error " + launch.Error : string.Empty) + ").", null);
                using var process = launch.Process;
                var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += c => exited.TrySetResult(c);
                var done = await Task.WhenAny(exited.Task, Task.Delay(AsUserMs)).ConfigureAwait(false);
                if (done != exited.Task)
                {
                    process.Stop();
                    return new InstallRun(Failed, "The install did not end in " + AsUserMs / 1000 + " seconds.", null);
                }
                int exit = exited.Task.Result;
                string told = report != null ? TakeReport(report) : null;
                return new InstallRun(exit, IsAnswer(exit) ? null : told ?? "The install ended with code " + exit + ".", told);
            }
            finally
            {
                // A report the run left behind, when this read none.
                if (report != null) TakeReport(report);
            }
        }

        /// <summary>An exit code that is an answer, not a failure.</summary>
        private static bool IsAnswer(int code) => code is Done or IsOlder or IsMissing;

        /// <summary>Test seam: the desktop shell's variables. Null reads them
        /// through the shell's token.</summary>
        internal static Func<IReadOnlyList<string>> ShellEnvironmentSeam;

        private static IReadOnlyList<string> ShellEnvironment()
        {
            var seam = ShellEnvironmentSeam;
            if (seam != null) return seam();
            using var shell = PluginLauncher.OpenShell();
            return shell == null ? null : PluginLauncher.ShellVariables(shell, out _);
        }

        /// <summary>A word quoted for a command line as the C runtime reads
        /// it.</summary>
        internal static string Quote(string word)
        {
            if (word.Length > 0 && word.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return word;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in word)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') sb.Append('\\', slashes * 2 + 1);
                else sb.Append('\\', slashes);
                slashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }
    }
}
