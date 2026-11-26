using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PadForge.Services.Automation;
using PadForge.Services.Install;
using PadForge.Services.Plugins;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// What an install started from the Automation page hands back, and how
    /// the page finds the assistant programs (#600 item 9): the report the
    /// run writes into the user's temp folder and the page reads back and
    /// deletes, the one check of every assistant program, the places each
    /// program is found, and the skill's version. Every folder is under a
    /// test's own temporary folder, and no program starts.
    /// </summary>
    [Collection("UserInstallStatics")]
    public sealed class AssistantInstallReportTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Report." + Guid.NewGuid().ToString("N"));
        private const string Exe = @"C:\Apps\PadForge\PadForge.exe";

        public AssistantInstallReportTests()
        {
            Directory.CreateDirectory(_root);
            UserFolders.Seam = f => Path.Combine(_root, f switch
            {
                UserFolder.Documents => @"Profile\Documents",
                UserFolder.RoamingData => @"Profile\AppData\Roaming",
                UserFolder.LocalData => @"Profile\AppData\Local",
                _ => "Profile",
            });
            UserInstall.Exe = () => Exe;
            UserInstall.FindSeam = _ => null;
            UserInstall.ReportFolderSeam = () => _root;
            AssistantPrograms.PackageRootSeam = _ => null;
            AssistantPrograms.ProgramFilesSeam = () => new[] { Path.Combine(_root, "Program Files") };
        }

        public void Dispose()
        {
            UserFolders.Seam = null;
            UserInstall.ToolSeam = null;
            UserInstall.FindSeam = null;
            UserInstall.Launcher = null;
            UserInstall.ReportFolderSeam = null;
            UserInstall.Exe = () => Environment.ProcessPath;
            AssistantPrograms.PackageRootSeam = null;
            AssistantPrograms.ProgramFilesSeam = null;
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        private string At(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

        private string Touch(params string[] parts)
        {
            string file = At(parts);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "x");
            return file;
        }

        private static InstallRequest Parsed(string line)
        {
            var args = ("--install " + line).Split(' ');
            int i = 0;
            Assert.Null(UserInstall.Parse(args, ref i, out var request));
            return request;
        }

        // ── The command line ──

        [Fact]
        public void CheckWithNoProgram_ChecksEveryOne_AndReportTakesAFullPath()
        {
            var all = Parsed("assistant --check");
            Assert.Equal(InstallWhat.Assistant, all.What);
            Assert.Null(all.Target);
            Assert.True(all.Check);
            var reported = Parsed(@"assistant codex --report C:\Temp\r.txt");
            Assert.Equal(@"C:\Temp\r.txt", reported.Report);
            Assert.Equal(new[] { "--install", "assistant", "codex", "--report", @"C:\Temp\r.txt" }, reported.Words());
            // A report with no full path, or none at all, is a mistake of the
            // command line. Adding with no program still is.
            foreach (string wrong in new[] { "assistant codex --report", "assistant codex --report r.txt", "assistant", "assistant --remove" })
            {
                int i = 0;
                Assert.NotNull(UserInstall.Parse(("--install " + wrong).Split(' '), ref i, out _));
            }
            Assert.Contains("--report", UserInstall.UsageText);
        }

        // ── The report ──

        [Fact]
        public void ARunWithAReport_WritesWhatItSaid_AndThePageReadsItBackAndDeletesIt()
        {
            string report = At("said.txt");
            var said = new List<string>();
            int code = UserInstall.Run(new InstallRequest(InstallWhat.Skill, "codex") { Report = report }, said.Add, s => said.Add("error: " + s));
            Assert.Equal(UserInstall.Done, code);
            Assert.True(File.Exists(report));
            Assert.Equal(said.Single(), UserInstall.TakeReport(report));
            Assert.False(File.Exists(report));
            // A failure's reason is in the report too: with no folder of the
            // user's named, the skill has nowhere to go.
            UserFolders.Seam = null;
            code = UserInstall.Run(new InstallRequest(InstallWhat.Skill, "claude-code") { Report = At("nofolder.txt") }, _ => { }, _ => { });
            Assert.Equal(UserInstall.Failed, code);
            Assert.Contains("Claude Code", UserInstall.TakeReport(At("nofolder.txt")));
        }

        [Fact]
        public void AReportAlreadyThere_IsLeftAlone_AndAReadBackIsCleanedAndCapped()
        {
            string report = Touch("planted.txt");
            UserInstall.Run(new InstallRequest(InstallWhat.Skill, "codex") { Report = report }, _ => { }, _ => { });
            Assert.Equal("x", File.ReadAllText(report));

            string noisy = At("noisy.txt");
            File.WriteAllText(noisy, "line one\u202E\u0007\r\nline two\r\n" + new string('y', UserInstall.LongestReport * 2), new UTF8Encoding(false));
            string back = UserInstall.TakeReport(noisy);
            Assert.StartsWith("line one\r\nline two\r\n", back);
            Assert.DoesNotContain('\u202E', back);
            Assert.DoesNotContain('\u0007', back);
            Assert.True(back.Length <= UserInstall.LongestReport);
            Assert.False(File.Exists(noisy));
            Assert.Null(UserInstall.TakeReport(At("none.txt")));
        }

        private sealed class ReportingLauncher : IPluginLauncher
        {
            public string Line;
            public int Code;
            public string Writes;

            public PluginLaunch Start(PluginCommand command, IReadOnlyDictionary<string, string> variables)
            {
                Line = command.CommandLine;
                // The run as the user: it writes the report the line names.
                int at = Line.IndexOf("--report ", StringComparison.Ordinal);
                string file = Line.Substring(at + "--report ".Length).Trim().Trim('"');
                if (Writes != null) File.WriteAllText(file, Writes);
                return new PluginLaunch { Process = new DoneProcess(Code) };
            }
        }

        private sealed class DoneProcess : IPluginProcess
        {
            private readonly int _code;
            public DoneProcess(int code) => _code = code;
            public int Id => 4243;
            public bool Owns(int processId) => processId == Id;
            public event Action<int> Exited
            {
                add => Task.Run(() => value(_code));
                remove { }
            }
            public void Stop() { }
            public void Dispose() { }
        }

        /// <summary>A failed install from the page says why, in the run's own
        /// words, where it once said only its exit code. A check's report
        /// comes back as what it said. The report is gone after.</summary>
        [Fact]
        public async Task ThePagesInstall_SaysWhyItFailed_AndWhatACheckSaid()
        {
            var launcher = new ReportingLauncher { Code = UserInstall.Failed, Writes = "Claude Code's own command did not add PadForge (exit code 3): no auth\r\n" };
            UserInstall.Launcher = launcher;
            var run = await UserInstall.RunAsUserAsync(new InstallRequest(InstallWhat.Assistant, "claude-code"));
            Assert.Equal(UserInstall.Failed, run.Code);
            Assert.Equal("Claude Code's own command did not add PadForge (exit code 3): no auth", run.Error);
            Assert.Contains("--report ", launcher.Line);
            Assert.Empty(Directory.EnumerateFiles(_root, "PadForge-install-*"));

            launcher.Code = UserInstall.Done;
            launcher.Writes = "[{\"key\":\"codex\",\"program\":\"C:\\\\codex.exe\",\"entry\":\"current\",\"skill\":\"older\",\"settings\":null}]";
            run = await UserInstall.RunAsUserAsync(new InstallRequest(InstallWhat.Assistant, null, Check: true));
            Assert.Null(run.Error);
            var check = Assert.Single(UserInstall.ReadChecks(run.Said));
            Assert.Equal(new UserInstall.AssistantCheck("codex", @"C:\codex.exe", "current", "older", null), check);

            // A run that wrote nothing still names its code.
            launcher.Code = UserInstall.Failed;
            launcher.Writes = null;
            run = await UserInstall.RunAsUserAsync(new InstallRequest(InstallWhat.Assistant, "codex"));
            Assert.Equal("The install ended with code 1.", run.Error);
        }

        [Fact]
        public void AReportThatIsNoList_GivesNoRows_AndAnUnknownProgramIsLeftOut()
        {
            Assert.Empty(UserInstall.ReadChecks("not json"));
            Assert.Empty(UserInstall.ReadChecks("{\"key\":\"codex\"}"));
            Assert.Empty(UserInstall.ReadChecks("[{\"key\":\"notepad\",\"program\":\"C:\\\\n.exe\"}]"));
            Assert.Empty(UserInstall.ReadChecks(null));
        }

        // ── Every assistant at once ──

        /// <summary>The check names each program PadForge knows, in order,
        /// with its own file when it is on this PC, PadForge's entry and its
        /// skill, and says one line for each.</summary>
        [Fact]
        public void TheCheck_ReportsEveryProgram_FoundOrNot()
        {
            string codex = Touch("bin", "codex.exe");
            UserInstall.FindSeam = name => name == "codex" ? codex : null;
            // Codex's entry in its TOML file and its skill, as this PadForge writes them.
            string toml = At("Profile", ".codex", "config.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(toml));
            File.WriteAllText(toml, "[mcp_servers.padforge]\ncommand = '" + Exe + "'\nargs = [\"--mcp\"]\n");
            UserInstall.Run(new InstallRequest(InstallWhat.Skill, "codex"), _ => { }, _ => { });
            var said = new List<string>();
            var checks = UserInstall.ReadChecks(UserInstall.CheckAll(said.Add));
            Assert.Equal(AssistantHosts.All.Select(h => h.Key), checks.Select(c => c.Key));
            Assert.Equal(AssistantHosts.All.Count, said.Count);
            var c = checks.Single(x => x.Key == "codex");
            Assert.Equal(codex, c.Program);
            Assert.Equal("current", c.Entry);
            Assert.Equal("current", c.Skill);
            Assert.Equal(toml, c.Settings);
            Assert.All(checks.Where(x => x.Key != "codex"), x => Assert.Null(x.Program));
            Assert.Equal("upload", checks.Single(x => x.Key == "claude-desktop").Skill);
            Assert.Equal("missing", checks.Single(x => x.Key == "gemini-cli").Entry);
        }

        /// <summary>A program whose settings file PadForge cannot read keeps
        /// its row, and its entry reads unreadable, never missing.</summary>
        [Fact]
        public void ASettingsFilePadForgeCannotRead_KeepsItsProgram_AndReadsUnreadable()
        {
            string gemini = Touch("bin", "gemini.exe");
            UserInstall.FindSeam = name => name == "gemini" ? gemini : null;
            string json = At("Profile", ".gemini", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(json));
            File.WriteAllText(json, "{ \"mcpServers\": { \"padforge\": {");
            var said = new List<string>();
            var c = UserInstall.ReadChecks(UserInstall.CheckAll(said.Add)).Single(x => x.Key == "gemini-cli");
            Assert.Equal(gemini, c.Program);
            Assert.Equal("unreadable", c.Entry);
            Assert.Equal(json, c.Settings);
            Assert.Contains(said, l => l.StartsWith("gemini-cli: " + gemini + ", entry unreadable", StringComparison.Ordinal));
        }

        /// <summary>A skill whose SKILL.md names a newer version than this
        /// PadForge's is newer, not older, and gets no Update.</summary>
        [Fact]
        public void ASkillFromANewerPadForge_IsSaidToBeNewer()
        {
            Assert.Equal("6.2.0", UserInstall.SkillVersion(Encoding.UTF8.GetBytes("---\nname: padforge\nversion: \"6.2.0\"\n---\n# x\n")));
            Assert.Null(UserInstall.SkillVersion(Encoding.UTF8.GetBytes("# no front matter\nversion: 1.0\n")));
            Assert.Null(UserInstall.SkillVersion(Encoding.UTF8.GetBytes("---\nname: padforge\n---\nversion: 9.9\n")));
            Assert.Null(UserInstall.SkillVersion(null));

            UserInstall.Run(new InstallRequest(InstallWhat.Skill, "codex"), _ => { }, _ => { });
            string skill = At("Profile", ".agents", "skills", "padforge", "SKILL.md");
            string text = File.ReadAllText(skill);
            string ours = UserInstall.SkillVersion(Encoding.UTF8.GetBytes(text));
            // This PadForge's own skill names its version, or none yet: a
            // higher one is newer either way only when both name one.
            File.WriteAllText(skill, text.Replace("---\nname:", "---\nversion: 999.0.0\nname:").Replace("---\r\nname:", "---\r\nversion: 999.0.0\r\nname:"));
            var check = UserInstall.ReadChecks(UserInstall.CheckAll(_ => { })).Single(c => c.Key == "codex");
            Assert.Equal(ours == null ? "older" : "newer", check.Skill);
            File.WriteAllText(skill, text + "\nchanged\n");
            check = UserInstall.ReadChecks(UserInstall.CheckAll(_ => { })).Single(c => c.Key == "codex");
            Assert.Equal("older", check.Skill);
        }

        // ── Where each program is found ──

        [Fact]
        public void ThePackagedClaudeApp_IsFoundInItsPackagesFolder()
        {
            string exe = Touch("WindowsApps", "Claude_6.28899.6.0_x64__pzs8sxrjxfjjc", "app", "claude.exe");
            AssistantPrograms.PackageRootSeam = family => family == AssistantPrograms.ClaudeFamily
                ? At("WindowsApps", "Claude_6.28899.6.0_x64__pzs8sxrjxfjjc") : null;
            Assert.Equal(exe, AssistantPrograms.Find("claude-desktop"));
            AssistantPrograms.PackageRootSeam = _ => null;
            Assert.Null(AssistantPrograms.Find("claude-desktop"));
        }

        /// <summary>Claude Code through VS Code's extension only, with no
        /// claude on the PATH: its own claude.exe in the newest extension
        /// folder is found and is the command Add runs.</summary>
        [Fact]
        public void ClaudeCode_FromItsVsCodeExtension_IsFound_AndAddRunsIt()
        {
            Touch("Profile", ".vscode", "extensions", "anthropic.claude-code-2.1.330-win32-x64", "resources", "native-binary", "claude.exe");
            string newest = Touch("Profile", ".vscode", "extensions", "anthropic.claude-code-2.1.331-win32-x64", "resources", "native-binary", "claude.exe");
            Directory.CreateDirectory(At("Profile", ".vscode", "extensions", "anthropic.claude-code-junk"));
            Assert.Equal(newest, AssistantPrograms.Find("claude-code"));
            Assert.Equal(newest, AssistantPrograms.CommandOf("claude-code"));
            Assert.Null(AssistantPrograms.CommandOf("codex"));

            var runs = new List<(string Exe, IReadOnlyList<string> Words)>();
            UserInstall.ToolSeam = (exe, words) => { runs.Add((exe, words)); return (0, "Added"); };
            Assert.Equal(UserInstall.Done, UserInstall.Run(new InstallRequest(InstallWhat.Assistant, "claude-code"), _ => { }, _ => { }));
            Assert.Equal(newest, runs.Last().Exe);
            Assert.Equal(new[] { "mcp", "add", "--scope", "user", "padforge", "--", Exe, "--mcp" }, runs.Last().Words);

            // The native installer's own place wins.
            string native = Touch("Profile", ".local", "bin", "claude.exe");
            Assert.Equal(native, AssistantPrograms.CommandOf("claude-code"));
        }

        [Fact]
        public void VsCode_VisualStudio_AndZed_AreFoundWhereTheirInstallersPutThem()
        {
            string code = Touch("Program Files", "Microsoft VS Code", "Code.exe");
            string cmd = Touch("Program Files", "Microsoft VS Code", "bin", "code.cmd");
            UserInstall.FindSeam = name => name == "code" ? cmd : null;
            Assert.Equal(code, AssistantPrograms.Find("vscode"));
            UserInstall.FindSeam = _ => null;
            Assert.Equal(code, AssistantPrograms.Find("vscode"));
            string user = Touch("Profile", "AppData", "Local", "Programs", "Microsoft VS Code", "Code.exe");
            Assert.Equal(user, AssistantPrograms.Find("vscode"));

            Assert.Null(AssistantPrograms.Find("visual-studio"));
            Touch("Program Files", "Microsoft Visual Studio", "2022", "Community", "Common7", "IDE", "devenv.exe");
            string vs18 = Touch("Program Files", "Microsoft Visual Studio", "18", "Community", "Common7", "IDE", "devenv.exe");
            Assert.Equal(vs18, AssistantPrograms.Find("visual-studio"));

            string zed = Touch("Profile", "AppData", "Local", "Programs", "Zed", "Zed.exe");
            Assert.Equal(zed, AssistantPrograms.Find("zed"));
            Assert.Null(AssistantPrograms.Find("junie"));
            Assert.Null(AssistantPrograms.Find("nothing"));
        }

        /// <summary>VS Code's copilot stand-in on the PATH is no Copilot CLI.</summary>
        [Fact]
        public void VsCodesCopilotShim_IsNoCommand()
        {
            Assert.True(UserInstall.IsStandIn(@"C:\Program Files\Microsoft VS Code\bin\copilot-shim"));
            Assert.True(UserInstall.IsStandIn(@"C:\Program Files\Microsoft VS Code\bin\copilot-shim\"));
            Assert.False(UserInstall.IsStandIn(@"C:\Users\me\AppData\Roaming\npm"));
            Assert.False(UserInstall.IsStandIn(null));
        }

        /// <summary>A connected assistant is told by the name its client gave
        /// in its initialize, as its --mcp copy's hello carries it, then its
        /// host's image in brackets. Only names read at their source are
        /// known.</summary>
        [Fact]
        public void AConnectedAssistant_IsKnownByItsClientsName()
        {
            Assert.Equal("claude-code", AssistantPrograms.KeyOfClient("claude-code (claude.exe)"));
            Assert.Equal("claude-code", AssistantPrograms.KeyOfClient("claude-code"));
            Assert.Equal("codex", AssistantPrograms.KeyOfClient("codex-mcp-client (codex.exe)"));
            Assert.Null(AssistantPrograms.KeyOfClient("Visual Studio Code (Code.exe)"));
            Assert.Null(AssistantPrograms.KeyOfClient("claude-code-imposter (x.exe)"));
            Assert.Null(AssistantPrograms.KeyOfClient("assistant (claude.exe)"));
            Assert.Null(AssistantPrograms.KeyOfClient(null));
            Assert.All(AssistantPrograms.ClientNames.Keys, key => Assert.NotNull(AssistantHosts.Find(key)));
        }

        /// <summary>CLAUDE_CONFIG_DIR moves Claude Code's settings file and
        /// skills, as its own add command follows it.</summary>
        [Fact]
        public void ClaudeCodesFiles_FollowItsConfigFolderVariable_OutsideTheTestHost()
        {
            var host = AssistantHosts.Find("claude-code");
            // In the test host a variable is never read: the seam's folders
            // stand.
            Assert.Equal(At("Profile", ".claude.json"), host.SettingsFile());
            Assert.Equal(At("Profile", ".claude", "skills"), host.SkillsFolder());
            string code = File.ReadAllText(Path.Combine(OffLimitsSource.RepoRoot(), "PadForge.App", "Services", "Install", "AssistantHosts.cs"));
            Assert.Contains("Variable(\"CLAUDE_CONFIG_DIR\") is { } home ? Path.Combine(home, \".claude.json\")", code);
            Assert.Contains("Variable(\"CLAUDE_CONFIG_DIR\") is { } home ? Path.Combine(home, \"skills\")", code);
        }

        /// <summary>A page's install in the test host starts nothing without
        /// a test's own launcher, and writes no report.</summary>
        [Fact]
        public async Task TheTestHost_StartsNoInstall_WithoutALaunchersOwn()
        {
            UserInstall.Launcher = null;
            var run = await UserInstall.RunAsUserAsync(new InstallRequest(InstallWhat.AutoHotkey, null));
            Assert.Equal(UserInstall.Failed, run.Code);
            Assert.Equal("The test host starts no install.", run.Error);
            Assert.Empty(Directory.EnumerateFiles(_root, "PadForge-install-*"));
            Assert.Null(UserInstall.TakeReport("relative.txt"));
        }

        [Fact]
        public void TheTestHost_FindsNoProgramOfTheBench()
        {
            UserFolders.Seam = null;
            UserInstall.FindSeam = null;
            AssistantPrograms.PackageRootSeam = null;
            AssistantPrograms.ProgramFilesSeam = null;
            foreach (var host in AssistantHosts.All) Assert.Null(AssistantPrograms.Find(host.Key));
            Assert.Null(AssistantPrograms.PackageRoot(AssistantPrograms.ClaudeFamily));
        }
    }
}
