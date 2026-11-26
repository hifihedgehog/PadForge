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
        /// <summary>A library current in some of its places and missing in
        /// the others, such as a Python added after the install.</summary>
        Partial,
    }

    /// <summary>One place a library goes, and what it is for:
    /// <c>python:&lt;version&gt;</c> for a Python's user site, the version
    /// as that Python names itself (sys.winver: 3.14, 3.14t, 3.14-32,
    /// 3.14-arm64), <c>pwsh</c> for PowerShell 7, <c>powershell</c> for
    /// Windows PowerShell, <c>autohotkey</c> for AutoHotkey v2, or
    /// <c>-</c> for a folder or file the command line named.</summary>
    internal readonly record struct LibraryPlace(string File, string For);

    /// <summary>A Python that answered: its program, its version as it
    /// names itself (sys.winver) and its user site, from one run.</summary>
    internal readonly record struct PythonSite(string Python, string Version, string Site);

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

    /// <summary>Why an install or a check did not finish, as the page
    /// words it in the user's language (<see cref="InstallRun.Reason"/>).</summary>
    internal enum InstallReason
    {
        None,
        /// <summary>Windows did not start the run with the user's rights.</summary>
        NotStarted,
        /// <summary>It did not end within its bound, in Number seconds.</summary>
        TimedOut,
        /// <summary>It ended with code Number and named no reason.</summary>
        ExitCode,
        /// <summary>The settings file Detail is not JSON PadForge can read.</summary>
        SettingsUnreadable,
        /// <summary>Detail is a file or folder PadForge did not write.</summary>
        NotOurs,
        /// <summary>Windows names no Documents folder.</summary>
        NoDocuments,
        /// <summary>No folder of the user's for the program Detail names.</summary>
        NoUserFolder,
        /// <summary>The program Detail's own command ended with code Number.</summary>
        HostCommandFailed,
        /// <summary>The program Detail's command is not on this PC.</summary>
        HostCommandMissing,
        /// <summary>Windows refused a file the run reads or writes.</summary>
        FileRefused,
        /// <summary>No Python is registered, named by the py launcher or on
        /// the PATH.</summary>
        NoPython,
        /// <summary>No Python found answered within Number seconds.</summary>
        NoPythonAnswered,
        /// <summary>Any other reason.</summary>
        Other,
    }

    /// <summary>A failure of an install that carries its reason, the number
    /// and the file or program the reason names. Its message is the English
    /// one the command line prints.</summary>
    internal sealed class InstallProblem : InvalidOperationException
    {
        internal InstallProblem(InstallReason reason, string message, int number = 0, string detail = null, Exception inner = null)
            : base(message, inner)
        {
            Reason = reason;
            Number = number;
            Detail = detail;
        }

        internal InstallReason Reason { get; }

        internal int Number { get; }

        internal string Detail { get; }
    }

    /// <summary>What an install started from the page came to: its exit
    /// code, why it failed in English words for the command line, or null,
    /// and what the run said, read back from its report (null when it said
    /// nothing). A failure also carries its reason, the number and the file
    /// or program the reason names, which the page words in the user's
    /// language (ScriptLibraryRows.ReasonText).</summary>
    internal sealed record InstallRun(int Code, string Error, string Said)
    {
        public InstallReason Reason { get; init; }

        /// <summary>An exit code or a count of seconds, as the reason
        /// says.</summary>
        public int Number { get; init; }

        /// <summary>A file's path or an assistant program's key, as the
        /// reason says, or null.</summary>
        public string Detail { get; init; }
    }

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
    /// older copy is in place, 7 none is, 8 a library is current in some of
    /// its places and missing in the others, 9 PadForge finds no Python, and
    /// 10 no Python it found answered.</para>
    ///
    /// <para>Uses no part of WPF: it runs in the client run before the app
    /// would load.</para>
    /// </summary>
    internal static class UserInstall
    {
        internal const int Done = 0, Failed = 1, WrongCommandLine = 2, IsOlder = 6, IsMissing = 7, IsPartial = 8, NoPython = 9, NoPythonAnswered = 10;

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

        /// <summary>The codes <see cref="RunTool"/> gives in place of an exit
        /// code: the program did not end within its bound, or Windows did
        /// not start it. -1 is a run the test host's fence or cmd.exe's
        /// rules refused.</summary>
        internal const int ToolTimedOut = -2, ToolNotStarted = -3;

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
        /// When the shell's user has no lower right (UAC is off, or the
        /// built-in Administrator's token is not split), every program of the
        /// user runs with full rights, and the install runs here.
        /// </summary>
        internal static int RunAsTheUser(InstallRequest request, Action<string> say, Action<string> sayError)
        {
            if (HasRights() && !NoLowerRight())
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
            // Rights read for certain, and nothing less, start the install
            // again as the user. A copy that cannot read its own token runs
            // the install here, so the run as the user never starts itself
            // again.
            return PluginLauncher.ThisProcessHasAdministratorRights() == true;
        }

        /// <summary>Test seam: whether the desktop shell's user has no lower
        /// right than full rights. Null reads the shell's token
        /// (<see cref="PluginLauncher.ShellUserHasNoLowerRight"/>).</summary>
        internal static Func<bool> NoLowerRightSeam;

        private static bool NoLowerRight()
        {
            var seam = NoLowerRightSeam;
            return seam != null ? seam() : PluginLauncher.ShellUserHasNoLowerRight();
        }

        /// <summary>Runs an install here, in this process, with its rights.
        /// Gives the exit code.</summary>
        internal static int Run(InstallRequest request, Action<string> say, Action<string> sayError)
        {
            if (string.IsNullOrEmpty(request.Report)) return RunHere(request, say, sayError, out _, out _);
            var told = new StringBuilder();
            int code = RunHere(request, t => { told.Append(t).Append("\r\n"); say(t); }, t => { told.Append(t).Append("\r\n"); sayError(t); },
                out string report, out string reason);
            // A failed run's last line names its reason for the page, which
            // words it in the user's language (ReadReason).
            WriteReport(request.Report, (report ?? told.ToString()) + (reason != null ? reason + "\r\n" : string.Empty));
            return code;
        }

        private static int RunHere(InstallRequest request, Action<string> say, Action<string> sayError, out string report, out string reason)
        {
            report = null;
            reason = null;
            try
            {
                if (request.What == InstallWhat.Assistant && request.Check && string.IsNullOrEmpty(request.Target))
                {
                    report = CheckAll(say);
                    return Done;
                }
                if (request.Check)
                {
                    if (request.What is InstallWhat.AutoHotkey or InstallWhat.PowerShell or InstallWhat.Python)
                    {
                        // A library's check names each place it goes, what
                        // the place is for and the state there, which the
                        // Scripts card reads (#600 item 26):
                        // "place current python:3.14 C:\...\padforge.py".
                        // The places are found once, so every Python starts
                        // once and the state and the places agree.
                        byte[] bytes = AutomationLibraries.Read(KindOf(request.What));
                        var places = LibraryPlaces(request.What, request.Target);
                        var states = places.Select(p => StateOf(p.File, bytes)).ToList();
                        var rolled = Rollup(states);
                        say(Said(rolled));
                        for (int i = 0; i < places.Count; i++)
                            say("place " + Word(states[i]) + " " + places[i].For + " " + places[i].File);
                        return CodeOf(rolled);
                    }
                    var state = Check(request);
                    say(Said(state));
                    return CodeOf(state);
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
            catch (InstallProblem problem) when (request.Check && problem.Reason is InstallReason.NoPython or InstallReason.NoPythonAnswered)
            {
                // No Python is an answer of the check, not its failure.
                say(problem.Message);
                return problem.Reason == InstallReason.NoPython ? NoPython : NoPythonAnswered;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                sayError(ex.Message);
                reason = ReasonLine(ex);
                return Failed;
            }
        }

        private static InstallProblem Problem(InstallReason reason, string message, int number = 0, string detail = null)
            => new(reason, message, number, detail);

        /// <summary>The report's reason line, <c>reason &lt;Reason&gt;
        /// &lt;number&gt; &lt;detail&gt;</c>: the problem's own reason, a
        /// file Windows refused, or another.</summary>
        private static string ReasonLine(Exception ex)
        {
            var (reason, number, detail) = ex switch
            {
                InstallProblem p => (p.Reason, p.Number, p.Detail),
                IOException or UnauthorizedAccessException or System.Security.SecurityException => (InstallReason.FileRefused, 0, (string)null),
                _ => (InstallReason.Other, 0, (string)null),
            };
            return "reason " + reason + " " + number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                   + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail.Replace("\r", " ").Replace("\n", " "));
        }

        /// <summary>The longest detail a reason line carries back.</summary>
        private const int LongestDetail = 1024;

        /// <summary>
        /// Splits a report into what the run said and its reason line. The
        /// report lies in a folder the user's programs can write, so only a
        /// reason this PadForge names is taken (any other reads as Other),
        /// its number only as digits and its detail cleaned and capped. It is
        /// only ever shown as text.
        /// </summary>
        internal static (string Said, InstallReason Reason, int Number, string Detail) ReadReason(string report)
        {
            if (string.IsNullOrEmpty(report)) return (report, InstallReason.None, 0, null);
            var kept = new List<string>();
            InstallReason reason = InstallReason.None;
            int number = 0;
            string detail = null;
            foreach (string line in report.Replace("\r\n", "\n").Split('\n'))
            {
                if (!line.StartsWith("reason ", StringComparison.Ordinal))
                {
                    kept.Add(line);
                    continue;
                }
                string[] words = line.Split(' ', 4);
                reason = words.Length > 1 && words[1].Length > 0 && words[1].All(char.IsAsciiLetter)
                         && Enum.TryParse(words[1], ignoreCase: false, out InstallReason named) && named != InstallReason.None
                    ? named : InstallReason.Other;
                number = words.Length > 2 && words[2].Length is > 0 and <= 9 && words[2].All(char.IsAsciiDigit)
                    ? int.Parse(words[2], System.Globalization.CultureInfo.InvariantCulture) : 0;
                detail = words.Length > 3 ? PadForge.Engine.Common.ShownText.Clean(words[3]).Trim() : null;
                if (detail?.Length > LongestDetail) detail = detail.Substring(0, LongestDetail);
                if (string.IsNullOrEmpty(detail)) detail = null;
            }
            string said = string.Join("\r\n", kept).Trim();
            return (said.Length == 0 ? null : said, reason, number, detail);
        }

        /// <summary>The first line a check says for a state.</summary>
        private static string Said(InstallState state) => state switch
        {
            InstallState.Current => "current",
            InstallState.Older => "older",
            InstallState.Partial => "partial",
            _ => "not installed",
        };

        private static int CodeOf(InstallState state) => state switch
        {
            InstallState.Current => Done,
            InstallState.Older => IsOlder,
            InstallState.Partial => IsPartial,
            _ => IsMissing,
        };

        /// <summary>A library's state from the state of each of its places:
        /// current everywhere, older where any copy differs, partial when
        /// current in some places and missing in the others, else not
        /// installed.</summary>
        internal static InstallState Rollup(IReadOnlyList<InstallState> states)
        {
            if (states.Count > 0 && states.All(s => s == InstallState.Current)) return InstallState.Current;
            if (states.Any(s => s == InstallState.Older)) return InstallState.Older;
            return states.Any(s => s == InstallState.Current) ? InstallState.Partial : InstallState.NotInstalled;
        }

        private static InstallState StateOf(string file, byte[] bytes)
            => !File.Exists(file) ? InstallState.NotInstalled
                : File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes) ? InstallState.Current : InstallState.Older;

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
                    ?? throw Problem(InstallReason.NoUserFolder, "PadForge finds no folder of the user's for " + host.Name + "'s skills.", detail: host.Key);
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
        /// place its language finds it, with what each place is for.</summary>
        internal static IReadOnlyList<LibraryPlace> LibraryPlaces(InstallWhat what, string target)
        {
            var library = AutomationLibraries.Of(KindOf(what));
            if (!string.IsNullOrWhiteSpace(target))
            {
                if (!Path.IsPathFullyQualified(target)) throw new ArgumentException("A folder or file for the library is a full path.");
                bool file = string.Equals(Path.GetExtension(target), Path.GetExtension(library.FileName), StringComparison.OrdinalIgnoreCase);
                return new[] { new LibraryPlace(file ? target : Path.Combine(target, library.FileName), "-") };
            }
            switch (what)
            {
                case InstallWhat.AutoHotkey:
                {
                    // AutoHotkey v2 reads #Include <Name> from the user
                    // library, A_MyDocuments\AutoHotkey\Lib.
                    string documents = UserFolders.Of(UserFolder.Documents) ?? throw Problem(InstallReason.NoDocuments, "Windows names no Documents folder.");
                    return new[] { new LibraryPlace(Path.Combine(documents, "AutoHotkey", "Lib", library.FileName), "autohotkey") };
                }
                case InstallWhat.PowerShell:
                {
                    // PowerShell 7 reads the user's modules from the
                    // PSModulePath of its CurrentUser powershell.config.json
                    // when that names one, and from Documents\PowerShell\Modules
                    // otherwise (PowerShell's ModuleIntrinsics.SetModulePath
                    // and GetModulePath, PSConfiguration.GetModulePath).
                    // Windows PowerShell adds Documents\WindowsPowerShell\Modules
                    // only while the user scope has no PSModulePath variable,
                    // and reads that variable's folders when it has one
                    // (about_PSModulePath). A module is a folder of its own
                    // name.
                    string documents = UserFolders.Of(UserFolder.Documents) ?? throw Problem(InstallReason.NoDocuments, "Windows names no Documents folder.");
                    string pwsh = PowerShellConfigModules(documents) ?? Path.Combine(documents, "PowerShell", "Modules");
                    string windows = FirstFolder(UserVariable("PSModulePath")) ?? Path.Combine(documents, "WindowsPowerShell", "Modules");
                    return new[]
                    {
                        new LibraryPlace(Path.Combine(pwsh, "PadForge", library.FileName), "pwsh"),
                        new LibraryPlace(Path.Combine(windows, "PadForge", library.FileName), "powershell"),
                    };
                }
                default:
                {
                    var sites = UserSites(out int asked);
                    if (sites.Count == 0)
                        throw asked == 0
                            ? new InstallProblem(InstallReason.NoPython, "PadForge finds no Python on this PC.")
                            : new InstallProblem(InstallReason.NoPythonAnswered, "No Python PadForge found answered within " + PythonMs / 1000 + " seconds.", PythonMs / 1000);
                    return sites.Select(site => new LibraryPlace(Path.Combine(site.Site, library.FileName), "python:" + site.Version)).ToList();
                }
            }
        }

        /// <summary>Test seam: a variable of the user's own scope, as
        /// HKEY_CURRENT_USER\Environment holds it. Null reads it.</summary>
        internal static Func<string, string> UserVariableSeam;

        private static string UserVariable(string name)
        {
            if (UserVariableSeam == null && OffLimitsForTest) return null;
            var seam = UserVariableSeam;
            if (seam != null) return seam(name);
            try { return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User); }
            catch (System.Security.SecurityException) { return null; }
        }

        /// <summary>The first full path of a list of folders apart by
        /// semicolons, its variables expanded, or null.</summary>
        private static string FirstFolder(string list)
        {
            if (string.IsNullOrWhiteSpace(list)) return null;
            foreach (string part in Environment.ExpandEnvironmentVariables(list).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Path.IsPathFullyQualified(part)) return part;
            return null;
        }

        /// <summary>The user module folder PowerShell 7's CurrentUser
        /// configuration names: PSModulePath in
        /// Documents\PowerShell\powershell.config.json, expanded as
        /// PSConfiguration.GetModulePath expands it, or null when the file
        /// names none or cannot be read.</summary>
        private static string PowerShellConfigModules(string documents)
        {
            string file = Path.Combine(documents, "PowerShell", "powershell.config.json");
            try
            {
                if (!File.Exists(file)) return null;
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file), new System.Text.Json.JsonDocumentOptions
                    { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                       && json.RootElement.TryGetProperty("PSModulePath", out var value)
                       && value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? FirstFolder(value.GetString())
                    : null;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            {
                return null;
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
            var files = LibraryPlaces(request.What, request.Target).Select(p => p.File).ToList();
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
                throw Problem(InstallReason.NotOurs, file + " is not a library PadForge wrote, so PadForge leaves it as it is.", detail: file);
            OwnFiles.DeleteFile(file);
            return true;
        }

        /// <summary>How long a Python may take to answer.</summary>
        internal const int PythonMs = 10000;

        /// <summary>What each Python is asked, in one run: its version as it
        /// names itself, sys.winver (CPython's PCbuild/python.props: 3.14,
        /// with t for a free-threaded build and -32, -arm32 or -arm64 for
        /// another platform), and its user site, site.getusersitepackages,
        /// which is {userbase}\{implementation}{winver without its dots}\site-packages
        /// (Lib/site.py, _get_path), so a Store Python's own folder and a
        /// PYTHONUSERBASE the user set are both followed. A Python without
        /// sys.winver gives its major and minor version. Its last line ends,
        /// so text the Python writes to stderr, which RunTool gives after
        /// stdout, never joins the site. The words hold no
        /// character cmd.exe would read, for a Python that is a batch
        /// file.</summary>
        internal const string SiteScript = "import site, sys; sys.stdout.write(getattr(sys, 'winver', '{0}.{1}'.format(*sys.version_info)) + '\\n' + site.getusersitepackages() + '\\n')";

        private static readonly System.Text.RegularExpressions.Regex VersionWord =
            new(@"^\d+\.\d+[A-Za-z0-9.\-]{0,16}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>The version and user site of each Python
        /// <see cref="Pythons"/> finds, each site once, and in
        /// <paramref name="asked"/> how many Pythons were asked. A Python
        /// that does not answer within <see cref="PythonMs"/>, or answers
        /// something else, is left out.</summary>
        internal static IReadOnlyList<PythonSite> UserSites(out int asked)
        {
            var sites = new List<PythonSite>();
            var pythons = Pythons();
            asked = pythons.Count;
            foreach (string python in pythons)
            {
                var (code, output) = RunTool(python, new[] { "-c", SiteScript }, PythonMs);
                if (code != 0 || output == null) continue;
                string[] lines = output.Replace("\r\n", "\n").Split('\n');
                if (lines.Length < 2) continue;
                string version = lines[0].Trim(), site = lines[1].Trim();
                if (!VersionWord.IsMatch(version) || site.Length == 0 || !Path.IsPathFullyQualified(site)
                    || sites.Any(s => string.Equals(s.Site, site, StringComparison.OrdinalIgnoreCase)))
                    continue;
                sites.Add(new PythonSite(python, version, site));
            }
            return sites;
        }

        /// <summary>
        /// The user's Pythons, each once by the file Windows runs for it:
        /// every Python PEP 514 registers (<see cref="RegisteredPythons"/>:
        /// the python.org installers, the Python install manager, uv, and a
        /// Store package through its declared keys), every one the py
        /// launcher lists (<c>py --list-paths</c>, CPython's PC/launcher2.c,
        /// which searches the registry and then the Store's packages), and
        /// the Microsoft Store's Pythons (<see cref="StorePythons"/>), and
        /// the python on the user's PATH, where pyenv-win's shims and a
        /// Python registered nowhere are found. A python.exe in the PATH's
        /// WindowsApps folder is an App Execution Alias of a packaged app,
        /// which the Store read finds by its package when it is a Python,
        /// so that folder is left out of the PATH search.
        /// </summary>
        internal static IReadOnlyList<string> Pythons()
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // A registered Python's file was seen by RegisteredPythons. A
            // path the launcher or the PATH gives is taken only when it is
            // a file.
            void Add(string exe, bool seenFile)
            {
                if (string.IsNullOrEmpty(exe) || !Path.IsPathFullyQualified(exe) || (!seenFile && !File.Exists(exe))) return;
                if (seen.Add(FinalPathOf(exe, directory: false) ?? Path.GetFullPath(exe))) found.Add(exe);
            }
            foreach (string exe in RegisteredPythons()) Add(exe, seenFile: true);
            if (FindCommand("py") is { } py && py.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var (code, output) = RunTool(py, new[] { "--list-paths" }, PythonMs);
                if (code == 0 && output != null)
                    foreach (System.Text.RegularExpressions.Match m in LauncherPath.Matches(output)) Add(m.Groups[1].Value, seenFile: false);
            }
            foreach (string exe in StorePythons()) Add(exe, seenFile: false);
            Add(FindCommand("python", IsAliasFolder), seenFile: false);
            return found;
        }

        /// <summary>The python.exe alias of each Python the Microsoft Store
        /// installed for this user: %LOCALAPPDATA%\Microsoft\WindowsApps\&lt;package
        /// family&gt;\python.exe, the read the py launcher makes for each
        /// PythonSoftwareFoundation.Python package it knows (CPython's
        /// PC/launcher2.c, appxSearch and APPX_SEARCH), here for every
        /// version of that name.</summary>
        internal static IReadOnlyList<string> StorePythons()
        {
            string local = UserFolders.Of(UserFolder.LocalData);
            if (string.IsNullOrEmpty(local)) return Array.Empty<string>();
            string aliases = Path.Combine(local, "Microsoft", "WindowsApps");
            var found = new List<string>();
            try
            {
                if (!Directory.Exists(aliases)) return found;
                foreach (string package in Directory.EnumerateDirectories(aliases, "PythonSoftwareFoundation.Python.*"))
                {
                    string exe = Path.Combine(package, "python.exe");
                    if (File.Exists(exe)) found.Add(exe);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A folder that cannot be read lists no Python.
            }
            return found;
        }

        /// <summary>A path on a line of <c>py --list-paths</c>, which writes
        /// " -V:&lt;tag&gt; [*] &lt;path&gt; [args]" for each Python
        /// (launcher2.c, _printEnvironment).</summary>
        private static readonly System.Text.RegularExpressions.Regex LauncherPath =
            new(@"(?m)([A-Za-z]:\\[^\r\n]*?\.exe)(?=\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>Whether a PATH folder is the user's WindowsApps folder of
        /// App Execution Aliases.</summary>
        private static bool IsAliasFolder(string folder)
            => string.Equals(Path.GetFileName((folder ?? string.Empty).TrimEnd('\\', '/')), "WindowsApps", StringComparison.OrdinalIgnoreCase);

        /// <summary>The python.exe of each Python registered under
        /// HKEY_CURRENT_USER and HKEY_LOCAL_MACHINE\Software\Python (PEP 514),
        /// the launcher's own key left out, each once.</summary>
        internal static IReadOnlyList<string> RegisteredPythons()
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

        /// <summary>How long one of an assistant maker's own commands may
        /// take. An Update runs two, the remove and the add.</summary>
        internal const int HostCommandMs = 60000;

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
                    RunTool(command, host.RemoveWords, HostCommandMs);
                var words = request.Remove ? host.RemoveWords : host.AddWords(exe);
                var (code, output) = RunTool(command, words, HostCommandMs);
                if (code == ToolTimedOut)
                    throw Problem(InstallReason.TimedOut, host.Name + "'s own command did not end in " + HostCommandMs / 1000 + " seconds.", HostCommandMs / 1000);
                if (code == ToolNotStarted || code == -1)
                    throw Problem(InstallReason.Other, host.Name + "'s own command did not start: " + (output ?? string.Empty).Trim());
                if (code != 0)
                    throw Problem(InstallReason.HostCommandFailed, host.Name + "'s own command did not " + (request.Remove ? "take PadForge out" : "add PadForge")
                                                        + " (exit code " + code + "): " + (output ?? string.Empty).Trim(), code, host.Key);
                return (request.Remove ? "Took PadForge out of " : "Added PadForge to ") + host.Name + " through its own command.";
            }
            if (host.Command != null && !host.FileWhenNoCommand)
                throw Problem(InstallReason.HostCommandMissing, host.Name + "'s command " + host.Command + " is not on this PC.", detail: host.Key);
            string file = host.SettingsFile?.Invoke() ?? throw Problem(InstallReason.NoUserFolder, "PadForge finds no folder of the user's for " + host.Name + "'s settings.", detail: host.Key);
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
                    return Rollup(LibraryPlaces(request.What, request.Target).Select(p => StateOf(p.File, bytes)).ToList());
                }
            }
        }

        private static InstallState Matches(bool? matches)
            => matches == null ? InstallState.NotInstalled : matches.Value ? InstallState.Current : InstallState.Older;

        /// <summary>Whether a host's settings name PadForge, and this
        /// PadForge's exe with --mcp. Throws for a settings file PadForge
        /// cannot read, which says neither.</summary>
        internal static InstallState AssistantState(AssistantHost host)
        {
            string file = host?.SettingsFile?.Invoke();
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return InstallState.NotInstalled;
            string exe = Exe();
            if (host.Container == null)
            {
                // A TOML file: PadForge's own table and its command line. A
                // file that cannot be read is no answer of either kind.
                string text;
                try { text = File.ReadAllText(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InstallProblem(InstallReason.SettingsUnreadable, file + " could not be read: " + ex.Message, detail: file, inner: ex);
                }
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
        /// (current, older, missing, or unreadable when the settings file
        /// cannot be read), its skill (current, older, newer, missing,
        /// unreadable, or upload when it takes skills only as an upload) and
        /// its settings file.</summary>
        internal sealed record AssistantCheck(string Key, string Program, string Entry, string Skill, string Settings);

        /// <summary>Checks every assistant program, says one line for each and
        /// gives the report the page reads: a JSON array of
        /// <see cref="AssistantCheck"/>. Each part is read on its own: a
        /// program that is found keeps its row when its settings file or its
        /// skill folder cannot be read, which reads as unreadable, never as
        /// missing.</summary>
        internal static string CheckAll(Action<string> say)
        {
            var all = new System.Text.Json.Nodes.JsonArray();
            foreach (var host in AssistantHosts.All)
            {
                string program = null, entry = "missing", skill = "missing", settings = null;
                try { program = AssistantPrograms.Find(host.Key); }
                catch (Exception ex) when (IsReadFailure(ex)) { program = null; }
                try
                {
                    settings = host.SettingsFile?.Invoke();
                    entry = Word(AssistantState(host));
                }
                catch (Exception ex) when (IsReadFailure(ex)) { entry = "unreadable"; }
                try { skill = host.SkillsFolder == null ? "upload" : SkillWord(host); }
                catch (InstallProblem problem) when (problem.Reason == InstallReason.NoUserFolder) { skill = "missing"; }
                catch (Exception ex) when (IsReadFailure(ex)) { skill = "unreadable"; }
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

        private static bool IsReadFailure(Exception ex)
            => ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or NotSupportedException or System.Security.SecurityException;

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

        /// <summary>A place's or an entry's state in a check's words:
        /// current, older or missing.</summary>
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
        internal static string FinalPathOf(string path, bool directory)
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
        internal static string FindCommand(string name) => FindCommand(name, null);

        /// <summary><see cref="FindCommand(string)"/>, leaving out each PATH
        /// folder <paramref name="skipFolder"/> names.</summary>
        internal static string FindCommand(string name, Func<string, bool> skipFolder)
        {
            if (FindSeam == null && OffLimitsForTest) return null;
            var seam = FindSeam;
            if (seam != null) return seam(name);
            return FindCommandOn(name, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"), skipFolder);
        }

        /// <summary><see cref="FindCommand"/> on a PATH and PATHEXT of
        /// another process's variables, such as the desktop shell user's. The
        /// PATH is searched with this process's rights, so only a folder on a
        /// drive of this PC is looked in: a share or a device path on the
        /// user's PATH would be reached with PadForge's rights
        /// (AssistantPrograms.IsOnADrive).</summary>
        internal static string FindCommandOn(string name, string path, string pathExt, Func<string, bool> skipFolder = null)
        {
            var extensions = (string.IsNullOrEmpty(pathExt) ? ".COM;.EXE;.BAT;.CMD" : pathExt).Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (string folder in (path ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!AssistantPrograms.IsOnADrive(folder)) continue;
                if (IsStandIn(folder) || skipFolder?.Invoke(folder) == true) continue;
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

        /// <summary>Whether a program lies in the folder the test host runs
        /// from, the one folder its fence lets <see cref="RunTool"/> start a
        /// program from (the tests' own helper), as PluginLauncher's fence
        /// does.</summary>
        private static bool InTestFolder(string exe)
        {
            if (string.IsNullOrEmpty(exe) || !Path.IsPathFullyQualified(exe)) return false;
            return Path.GetFullPath(exe).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Runs one of the user's own programs (an assistant's add command or
        /// a Python) with this process's rights and variables, which in an
        /// install are the user's, and gives its exit code and what it
        /// wrote. A batch file runs through cmd.exe, and a path cmd.exe would
        /// read as more than a path is refused (-1). <see cref="ToolTimedOut"/>
        /// when the program did not end in <paramref name="timeoutMs"/>, and
        /// <see cref="ToolNotStarted"/> when Windows did not start it.
        /// </summary>
        internal static (int Code, string Output) RunTool(string exe, IReadOnlyList<string> words, int timeoutMs, IReadOnlyList<string> variables = null)
        {
            if (ToolSeam == null && OffLimitsForTest && !InTestFolder(exe)) return (-1, null);
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
                // the user from PadForge when the user has no lower right.
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
                var watch = Stopwatch.StartNew();
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                // The bound covers the output too: a reader sees its end only
                // when every holder of the pipe closes it, and a child the
                // tool left behind holds it as long as it runs.
                if (!process.WaitForExit(timeoutMs)
                    || !Ended(new Task[] { output, errors }, (int)Math.Max(0, timeoutMs - watch.ElapsedMilliseconds)))
                {
                    End(process);
                    return (ToolTimedOut, "It did not end in " + timeoutMs / 1000 + " seconds.");
                }
                return (process.ExitCode, TextOf(output) + TextOf(errors));
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return (ToolNotStarted, ex.Message);
            }
        }

        /// <summary>Whether every read ended within the time left, a read
        /// that failed included.</summary>
        private static bool Ended(Task[] reads, int ms)
        {
            try { return Task.WaitAll(reads, ms); }
            catch (AggregateException) { return true; }
        }

        private static string TextOf(Task<string> read) => read.IsCompletedSuccessfully ? read.Result : string.Empty;

        /// <summary>Ends a tool and the children it started. Kill of a whole
        /// tree throws AggregateException when a part of it would not end
        /// (Process.NonUap.cs, Kill(bool)), and InvalidOperationException
        /// when the tool already ended.</summary>
        private static void End(Process process)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }

        // ── From the app ──

        /// <summary>Test seam: the launcher the app starts an install with.
        /// Null uses the plugins' launcher.</summary>
        internal static IPluginLauncher Launcher;

        /// <summary>How long an install started from the page may take: an
        /// Update's two maker's commands at their own bound
        /// (<see cref="HostCommandMs"/>) and half a minute for the copy's own
        /// start and its look for the command, so the copy's own bound ends a
        /// slow command and its own line names it.</summary>
        internal const int AsUserMs = 2 * HostCommandMs + 30000;

        /// <summary>
        /// Starts <c>PadForge.exe --install</c> with the user's own rights,
        /// through the desktop shell as a plugin starts (PluginLauncher), and
        /// waits for its exit code. When the shell's user has no lower right
        /// than full rights (UAC is off, or the built-in Administrator's token
        /// is not split), the user's every program runs with them, and the
        /// install runs as a PadForge.exe this process starts. Error is null
        /// when the code is the install's own.
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
                if (launch.Refusal == PluginRefusal.ShellElevated && NoLowerRight())
                {
                    // The shell's user has no lower right: every program of
                    // the user has full rights, the shell included. The
                    // install runs as its own PadForge.exe with the shell's
                    // variables, so the user's PATH finds the assistants'
                    // commands, which PadForge's own allow list of variables
                    // would not. A shell with rights whose user also has a
                    // lower right (an Explorer started elevated, a token that
                    // could not be read) gets the refusal below.
                    var (code, output) = await Task.Run(() => RunTool(exe, request.Words(), AsUserMs, ShellEnvironment())).ConfigureAwait(false);
                    // Its bound and a start Windows refused are worded as
                    // the launcher's route words them.
                    if (code == ToolTimedOut)
                        return new InstallRun(Failed, "The install did not end in " + AsUserMs / 1000 + " seconds.", null)
                            { Reason = InstallReason.TimedOut, Number = AsUserMs / 1000 };
                    if (code == ToolNotStarted)
                        return new InstallRun(Failed, "Windows did not start the install: " + output, null) { Reason = InstallReason.NotStarted };
                    string said = report != null ? TakeReport(report) : null;
                    said ??= string.IsNullOrWhiteSpace(output) ? null : PadForge.Engine.Common.ShownText.Clean(output.Trim());
                    return Ran(code, said);
                }
                if (launch.Process == null)
                    return new InstallRun(Failed, "Windows did not start the install with the user's rights (" + launch.Refusal
                        + (launch.Error != 0 ? ", error " + launch.Error : string.Empty) + ").", null) { Reason = InstallReason.NotStarted };
                using var process = launch.Process;
                var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += c => exited.TrySetResult(c);
                var done = await Task.WhenAny(exited.Task, Task.Delay(AsUserMs)).ConfigureAwait(false);
                if (done != exited.Task)
                {
                    process.Stop();
                    return new InstallRun(Failed, "The install did not end in " + AsUserMs / 1000 + " seconds.", null)
                        { Reason = InstallReason.TimedOut, Number = AsUserMs / 1000 };
                }
                int exit = exited.Task.Result;
                return Ran(exit, report != null ? TakeReport(report) : null);
            }
            finally
            {
                // A report the run left behind, when this read none.
                if (report != null) TakeReport(report);
            }
        }

        /// <summary>A run that ended with <paramref name="code"/> and wrote
        /// <paramref name="report"/>: what it said, and for a failure its
        /// reason, or its code when it named none.</summary>
        private static InstallRun Ran(int code, string report)
        {
            var (said, reason, number, detail) = ReadReason(report);
            if (IsAnswer(code)) return new InstallRun(code, null, said);
            if (reason == InstallReason.None) (reason, number, detail) = (InstallReason.ExitCode, code, (string)null);
            return new InstallRun(code, said ?? "The install ended with code " + code + ".", said) { Reason = reason, Number = number, Detail = detail };
        }

        /// <summary>An exit code that is an answer, not a failure.</summary>
        internal static bool IsAnswer(int code) => code is Done or IsOlder or IsMissing or IsPartial or NoPython or NoPythonAnswered;

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
