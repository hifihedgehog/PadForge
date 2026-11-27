using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.Services.Install;
using PadForge.Services.Plugins;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// <c>PadForge.exe --install</c> (#600): what it installs, where, as the
    /// user, writing only its own folder or entry and taking out what a newer
    /// copy no longer has. Every folder is a test's own (UserFolders.Seam),
    /// and no program starts (UserInstall's seams).
    /// </summary>
    [Collection("UserInstallStatics")]
    public sealed class UserInstallTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Install." + Guid.NewGuid().ToString("N"));
        private const string Exe = @"C:\Apps\PadForge\PadForge.exe";

        public UserInstallTests()
        {
            Directory.CreateDirectory(_root);
            UserFolders.Seam = f => Path.Combine(_root, f switch
            {
                // Documents moved to OneDrive, as Windows names it.
                UserFolder.Documents => @"OneDrive\Documents",
                UserFolder.RoamingData => @"Profile\AppData\Roaming",
                UserFolder.LocalData => @"Profile\AppData\Local",
                _ => "Profile",
            });
            UserInstall.Exe = () => Exe;
        }

        public void Dispose()
        {
            UserFolders.Seam = null;
            UserInstall.ToolSeam = null;
            UserInstall.FindSeam = null;
            UserInstall.PythonsSeam = null;
            UserInstall.Launcher = null;
            UserInstall.Exe = () => Environment.ProcessPath;
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        private string At(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

        private static int Run(InstallRequest request, List<string> said = null)
            => UserInstall.Run(request, s => said?.Add(s), s => said?.Add("error: " + s));

        // ── The command line ──

        [Theory]
        [InlineData("skill claude-code", (int)InstallWhat.Skill, "claude-code", false, false)]
        [InlineData("skill C:\\Skills --remove", (int)InstallWhat.Skill, "C:\\Skills", true, false)]
        [InlineData("autohotkey", (int)InstallWhat.AutoHotkey, null, false, false)]
        [InlineData("ahk --check", (int)InstallWhat.AutoHotkey, null, false, true)]
        [InlineData("powershell", (int)InstallWhat.PowerShell, null, false, false)]
        [InlineData("python /check", (int)InstallWhat.Python, null, false, true)]
        [InlineData("assistant codex", (int)InstallWhat.Assistant, "codex", false, false)]
        public void TheSwitch_ReadsWhatWhereAndHow(string words, int whatNumber, string target, bool remove, bool check)
        {
            var what = (InstallWhat)whatNumber;
            var args = new List<string> { "--install" };
            args.AddRange(words.Split(' '));
            var r = CommandLineSwitches.Parse(args);
            Assert.Null(r.Error);
            Assert.Equal(new InstallRequest(what, target, remove, check), r.Install);
            Assert.False(r.Empty);
        }

        [Theory]
        [InlineData("--install")]
        [InlineData("--install everything")]
        [InlineData("--install skill")]
        [InlineData("--install assistant")]
        [InlineData("--install assistant notepad")]
        [InlineData("--install skill claude-desktop")]
        [InlineData("--install=skill")]
        [InlineData("--install python --remove --check")]
        [InlineData("--install python --install ahk")]
        public void AWrongInstall_IsAMistakeOfTheCommandLine(string line)
        {
            var r = CommandLineSwitches.Parse(line.Split(' '));
            Assert.NotNull(r.Error);
        }

        [Fact]
        public void TheClientRun_RunsTheInstall_AndNeverLooksForACopy()
        {
            InstallRequest seen = null;
            var seams = new ClientRun.Seams
            {
                CopyRuns = () => throw new InvalidOperationException("an install looks for no running copy"),
                Install = (r, say, error) => { seen = r; return UserInstall.IsOlder; },
                Say = _ => { }, SayError = _ => { }, Tell = _ => { }, TellError = _ => { },
            };
            Assert.True(ClientRun.TryRun(new[] { "--install", "python", "--check" }, out int code, seams));
            Assert.Equal(UserInstall.IsOlder, code);
            Assert.Equal(new InstallRequest(InstallWhat.Python, null, false, true), seen);
            Assert.True(ClientRun.TryRun(new[] { "--install", "nothing" }, out code, seams));
            Assert.Equal(ClientRun.WrongCommandLine, code);
        }

        [Fact]
        public void TheHelp_NamesInstall()
            => Assert.Contains("--install <what> [where]", CommandLineSwitches.HelpText);

        // ── The skill ──

        [Fact]
        public void TheSkill_GoesWhereClaudeCodeReadsIt_AndAnOlderCopysLeftoversGo()
        {
            var request = new InstallRequest(InstallWhat.Skill, "claude-code");
            string folder = At("Profile", ".claude", "skills", "padforge");
            Assert.Equal(InstallState.NotInstalled, UserInstall.Check(request));

            // An older copy: a file this copy no longer has, and one that
            // differs.
            Directory.CreateDirectory(Path.Combine(folder, "references"));
            File.WriteAllText(Path.Combine(folder, "references", "gone.md"), "old");
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), "---\nname: padforge\n---\nold");
            Assert.Equal(InstallState.Older, UserInstall.Check(request));

            Assert.Equal(UserInstall.Done, Run(request));
            Assert.False(File.Exists(Path.Combine(folder, "references", "gone.md")));
            foreach (var file in AssistantSkill.Files)
                Assert.Equal(file.Bytes, File.ReadAllBytes(Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal(InstallState.Current, UserInstall.Check(request));

            // A file already right keeps its time.
            string skill = Path.Combine(folder, "SKILL.md");
            var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(skill, stamp);
            Assert.Equal(UserInstall.Done, Run(request));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(skill));

            Assert.Equal(UserInstall.Done, Run(request with { Remove = true }));
            Assert.False(Directory.Exists(folder));
            Assert.True(Directory.Exists(At("Profile", ".claude", "skills")), "only its own folder goes");
        }

        [Fact]
        public void TheSkill_IntoAFolderTheUserPicked_GoesUnderPadforge()
        {
            string picked = At("Picked");
            Directory.CreateDirectory(picked);
            File.WriteAllText(Path.Combine(picked, "mine.txt"), "the user's");
            Assert.Equal(UserInstall.Done, Run(new InstallRequest(InstallWhat.Skill, picked)));
            Assert.True(File.Exists(Path.Combine(picked, "padforge", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(picked, "mine.txt")));
            Assert.Equal(Path.Combine(picked, "padforge"), AssistantSkill.Save(picked));
        }

        [Fact]
        public void EveryHostsSkillFolder_IsInTheUsersOwnProfile()
        {
            foreach (var host in AssistantHosts.All.Where(h => h.SkillsFolder != null))
                Assert.StartsWith(At("Profile"), host.SkillsFolder());
            Assert.Null(AssistantHosts.Find("claude-desktop").SkillsFolder);
        }

        // ── The libraries ──

        [Fact]
        public void AutoHotkey_GoesToDocumentsLib_WhereverDocumentsWasMoved()
        {
            var request = new InstallRequest(InstallWhat.AutoHotkey, null);
            Assert.Equal(UserInstall.Done, Run(request));
            string file = At("OneDrive", "Documents", "AutoHotkey", "Lib", "PadForge.ahk");
            Assert.Equal(AutomationLibraries.Read(AutomationLibraryKind.AutoHotkey), File.ReadAllBytes(file));
            Assert.Equal(InstallState.Current, UserInstall.Check(request));
            File.AppendAllText(file, "; changed");
            Assert.Equal(InstallState.Older, UserInstall.Check(request));
            Assert.Equal(UserInstall.Done, Run(request with { Remove = true }));
            Assert.False(File.Exists(file));
        }

        [Fact]
        public void PowerShell_GoesToBothModulesFolders_AsAModuleFolderOfItsOwn()
        {
            // An older install: its module, and a file this copy no longer has.
            string stale = At("OneDrive", "Documents", "PowerShell", "Modules", "PadForge", "Old.ps1");
            Directory.CreateDirectory(Path.GetDirectoryName(stale));
            File.WriteAllText(stale, "old");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(stale), "PadForge.psm1"), "old");
            var request = new InstallRequest(InstallWhat.PowerShell, null);
            Assert.Equal(InstallState.NotInstalled, UserInstall.Check(request with { Target = At("Elsewhere") }));
            // A failed run names its reason.
            var said = new List<string>();
            Assert.True(Run(request, said) == UserInstall.Done, string.Join(" | ", said));
            byte[] module = AutomationLibraries.Read(AutomationLibraryKind.PowerShell);
            foreach (string modules in new[] { "PowerShell", "WindowsPowerShell" })
                Assert.Equal(module, File.ReadAllBytes(At("OneDrive", "Documents", modules, "Modules", "PadForge", "PadForge.psm1")));
            Assert.False(File.Exists(stale));
            Assert.Equal(InstallState.Current, UserInstall.Check(request));
            Assert.True(Run(request with { Remove = true }, said) == UserInstall.Done, string.Join(" | ", said));
            Assert.False(Directory.Exists(At("OneDrive", "Documents", "PowerShell", "Modules", "PadForge")));
        }

        [Fact]
        public void Python_GoesToTheUserSiteEachPythonNames()
        {
            string a = At("Py", "a", "python.exe"), b = At("Py", "b", "python.exe");
            UserInstall.PythonsSeam = () => new[] { a, b };
            var asked = new List<string>();
            UserInstall.ToolSeam = (exe, words) =>
            {
                asked.Add(exe);
                Assert.Equal("-c", words[0]);
                Assert.Contains("site.getusersitepackages()", words[1]);
                return (0, exe == a ? "3.13\n" + At("Site", "A") : "3.12\n" + At("Store", "LocalCache", "local-packages", "Python312", "site-packages"));
            };
            var request = new InstallRequest(InstallWhat.Python, null);
            Assert.Equal(UserInstall.Done, Run(request));
            Assert.Equal(new[] { a, b }, asked);
            byte[] library = AutomationLibraries.Read(AutomationLibraryKind.Python);
            Assert.Equal(library, File.ReadAllBytes(At("Site", "A", "padforge.py")));
            Assert.Equal(library, File.ReadAllBytes(At("Store", "LocalCache", "local-packages", "Python312", "site-packages", "padforge.py")));
            Assert.Equal(InstallState.Current, UserInstall.Check(request));
        }

        [Fact]
        public void NoPython_IsSaid()
        {
            UserInstall.PythonsSeam = () => Array.Empty<string>();
            var said = new List<string>();
            Assert.Equal(UserInstall.Failed, Run(new InstallRequest(InstallWhat.Python, null), said));
            Assert.Contains(said, s => s.Contains("no Python"));
        }

        [Fact]
        public void ALibrary_IntoAFileTheUserPicked_IsThatFile()
        {
            string file = At("Mine", "pad.py");
            LibraryFiles.Write(AutomationLibraryKind.Python, file);
            Assert.Equal(AutomationLibraries.Read(AutomationLibraryKind.Python), File.ReadAllBytes(file));
        }

        // ── Assistants ──

        [Fact]
        public void AHostWithItsOwnCommand_IsAddedThroughIt_AtUserScope()
        {
            UserInstall.FindSeam = name => name == "claude" ? @"C:\Tools\claude.exe" : null;
            var runs = new List<(string, IReadOnlyList<string>)>();
            UserInstall.ToolSeam = (exe, words) => { runs.Add((exe, words)); return (0, "Added"); };
            Assert.Equal(UserInstall.Done, Run(new InstallRequest(InstallWhat.Assistant, "claude-code")));
            Assert.Equal(@"C:\Tools\claude.exe", runs[0].Item1);
            Assert.Equal(new[] { "mcp", "add", "--scope", "user", "padforge", "--", Exe, "--mcp" }, runs[0].Item2);
            Assert.Equal(UserInstall.Done, Run(new InstallRequest(InstallWhat.Assistant, "claude-code", Remove: true)));
            Assert.Equal(new[] { "mcp", "remove", "--scope", "user", "padforge" }, runs[1].Item2);

            // A command that fails is said, with its own words.
            UserInstall.ToolSeam = (exe, words) => (1, "no such scope");
            var said = new List<string>();
            Assert.Equal(UserInstall.Failed, Run(new InstallRequest(InstallWhat.Assistant, "claude-code"), said));
            Assert.Contains(said, s => s.Contains("no such scope"));

            // And a host only its command may write is not written without it.
            UserInstall.FindSeam = _ => null;
            Assert.Equal(UserInstall.Failed, Run(new InstallRequest(InstallWhat.Assistant, "gemini-cli")));
            Assert.False(File.Exists(At("Profile", ".gemini", "settings.json")));
        }

        [Fact]
        public void GeminiAndCodex_AreAddedAtUserScope()
        {
            UserInstall.FindSeam = name => @"C:\Tools\" + name + ".cmd";
            var runs = new List<IReadOnlyList<string>>();
            UserInstall.ToolSeam = (exe, words) => { runs.Add(words); return (0, string.Empty); };
            Run(new InstallRequest(InstallWhat.Assistant, "gemini-cli"));
            Run(new InstallRequest(InstallWhat.Assistant, "codex"));
            Assert.Equal(new[] { "mcp", "add", "--scope", "user", "padforge", Exe, "--mcp" }, runs[0]);
            Assert.Equal(new[] { "mcp", "add", "padforge", "--", Exe, "--mcp" }, runs[1]);
        }

        [Fact]
        public void ThePackagedClaudeApp_IsWrittenInItsPackagesOwnDataFolder_KeepingEveryOtherKey()
        {
            string packaged = At("Profile", "AppData", "Local", "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(packaged));
            File.WriteAllText(packaged, "{\"theme\":\"dark\",\"mcpServers\":{\"files\":{\"command\":\"npx\",\"args\":[\"files\"]}}}");
            var request = new InstallRequest(InstallWhat.Assistant, "claude-desktop");
            Assert.Equal(InstallState.NotInstalled, UserInstall.Check(request));
            Assert.Equal(UserInstall.Done, Run(request));
            var root = JsonNode.Parse(File.ReadAllText(packaged)).AsObject();
            Assert.Equal("dark", root["theme"].GetValue<string>());
            Assert.Equal("npx", root["mcpServers"]["files"]["command"].GetValue<string>());
            Assert.Equal(Exe, root["mcpServers"]["padforge"]["command"].GetValue<string>());
            Assert.Equal("--mcp", root["mcpServers"]["padforge"]["args"][0].GetValue<string>());
            Assert.False(File.Exists(At("Profile", "AppData", "Roaming", "Claude", "claude_desktop_config.json")));
            Assert.Equal(InstallState.Current, UserInstall.Check(request));

            // Another PadForge's entry reads as older.
            UserInstall.Exe = () => @"D:\Other\PadForge.exe";
            Assert.Equal(InstallState.Older, UserInstall.Check(request));
            UserInstall.Exe = () => Exe;

            Assert.Equal(UserInstall.Done, Run(request with { Remove = true }));
            root = JsonNode.Parse(File.ReadAllText(packaged)).AsObject();
            Assert.Null(root["mcpServers"]["padforge"]);
            Assert.NotNull(root["mcpServers"]["files"]);
        }

        [Fact]
        public void WithNoPackage_ClaudesDocumentedFolderIsWritten()
        {
            Assert.Equal(UserInstall.Done, Run(new InstallRequest(InstallWhat.Assistant, "claude-desktop")));
            Assert.True(File.Exists(At("Profile", "AppData", "Roaming", "Claude", "claude_desktop_config.json")));
        }

        [Fact]
        public void AFilePadForgeCannotRead_IsLeftAsItIs()
        {
            string zed = At("Profile", "AppData", "Roaming", "Zed", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(zed));
            const string text = "// Zed settings\n{ \"theme\": \"One Dark\" \n";
            File.WriteAllText(zed, text);
            var said = new List<string>();
            Assert.Equal(UserInstall.Failed, Run(new InstallRequest(InstallWhat.Assistant, "zed"), said));
            Assert.Equal(text, File.ReadAllText(zed));
            Assert.Contains(said, s => s.Contains("not JSON PadForge can read"));
        }

        [Fact]
        public void CopilotsFile_IsWrittenWhenItsCommandIsNotThere_AndVisualStudiosUnderServers()
        {
            Assert.Equal(UserInstall.Done, Run(new InstallRequest(InstallWhat.Assistant, "copilot-cli")));
            var copilot = JsonNode.Parse(File.ReadAllText(At("Profile", ".copilot", "mcp-config.json")));
            Assert.Equal("stdio", copilot["mcpServers"]["padforge"]["type"].GetValue<string>());
            Assert.Equal(InstallState.Current, UserInstall.Check(new InstallRequest(InstallWhat.Assistant, "vscode")));

            Assert.Equal(UserInstall.Done, Run(new InstallRequest(InstallWhat.Assistant, "visual-studio")));
            var vs = JsonNode.Parse(File.ReadAllText(At("Profile", ".mcp.json")));
            Assert.Equal(Exe, vs["servers"]["padforge"]["command"].GetValue<string>());

            Assert.Equal(UserInstall.Done, Run(new InstallRequest(InstallWhat.Assistant, "junie")));
            Assert.True(File.Exists(At("Profile", ".junie", "mcp", "mcp.json")));
        }

        [Fact]
        public void CodexsTomlEntry_IsRead()
        {
            string toml = At("Profile", ".codex", "config.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(toml));
            var request = new InstallRequest(InstallWhat.Assistant, "codex");
            File.WriteAllText(toml, "model = \"x\"\n\n[mcp_servers.padforge]\ncommand = \"" + Exe.Replace("\\", "\\\\") + "\"\nargs = [\"--mcp\"]\n\n[other]\n");
            Assert.Equal(InstallState.Current, UserInstall.Check(request));
            File.WriteAllText(toml, "[mcp_servers.padforge]\ncommand = 'D:\\Old\\PadForge.exe'\nargs = [\"--mcp\"]\n");
            Assert.Equal(InstallState.Older, UserInstall.Check(request));
            File.WriteAllText(toml, "[mcp_servers.other]\n");
            Assert.Equal(InstallState.NotInstalled, UserInstall.Check(request));
        }

        // ── Writing only its own ──

        [Fact]
        public void OnlyAFolderNamedForPadForge_IsMirroredOrRemoved()
        {
            string other = At("Other");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "keep.txt"), "x");
            Assert.Equal(InstallReason.NotOurs, Assert.Throws<InstallProblem>(() => OwnFiles.Mirror(other, new[] { ("a.txt", new byte[] { 1 }) })).Reason);
            Assert.Equal(InstallReason.NotOurs, Assert.Throws<InstallProblem>(() => OwnFiles.RemoveFolder(other, "a.txt")).Reason);
            Assert.Throws<InvalidOperationException>(() => OwnFiles.Mirror(At("padforge"), new[] { (@"..\out.txt", new byte[] { 1 }) }));
            Assert.True(File.Exists(Path.Combine(other, "keep.txt")));
            Assert.False(File.Exists(At("out.txt")));
        }

        /// <summary>Opens <paramref name="file"/> the way a sync client or a
        /// scanner does right after a write: without the share a swap
        /// needs.</summary>
        private static FileStream Hold(string file) => new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        /// <summary>Holds <paramref name="file"/> and lets it go after
        /// <paramref name="ms"/>, on a thread of its own.</summary>
        private static Thread HoldFor(string file, int ms)
        {
            var stream = Hold(file);
            var thread = new Thread(() => { Thread.Sleep(ms); stream.Dispose(); }) { IsBackground = true };
            thread.Start();
            return thread;
        }

        /// <summary>A file another program opened a moment ago refuses the
        /// swap (access denied or a sharing violation), as a folder OneDrive
        /// syncs did for 46 of 18,000 swaps on the PC this was found on, the
        /// test host's own temp folder among them. The write tries again for
        /// a moment and lands once the file is let go.</summary>
        [Fact]
        public void AFileHeldForAMoment_IsWrittenOnceItIsLetGo()
        {
            string file = At("Profile", ".copilot", "mcp-config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "{}");
            var letGo = HoldFor(file, 60);
            Assert.True(OwnFiles.WriteIfDifferent(file, System.Text.Encoding.UTF8.GetBytes("{\"a\":1}")));
            letGo.Join();
            Assert.Equal("{\"a\":1}", File.ReadAllText(file));
            Assert.False(File.Exists(file + ".padforge-new"));
        }

        /// <summary>A file held past the tries fails the write, keeps its
        /// bytes, and gets no file left beside it.</summary>
        [Fact]
        public void AFileHeldPastTheTries_FailsTheWrite_AndLeavesNothingBesideIt()
        {
            string file = At("Profile", ".copilot", "mcp-config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "{}");
            using (Hold(file))
            {
                var error = Assert.ThrowsAny<Exception>(() => OwnFiles.WriteIfDifferent(file, System.Text.Encoding.UTF8.GetBytes("{\"a\":1}")));
                Assert.True(error is IOException or UnauthorizedAccessException, error.GetType().Name);
            }
            Assert.Equal("{}", File.ReadAllText(file));
            Assert.False(File.Exists(file + ".padforge-new"));
        }

        /// <summary>A file a newer copy no longer has, held by another
        /// program for a moment, refuses its delete as a held file refuses
        /// the swap. The mirror tries again for a moment and takes it out
        /// once it is let go.</summary>
        [Fact]
        public void AStaleFileHeldForAMoment_IsTakenOutOnceItIsLetGo()
        {
            string folder = At("OneDrive", "Documents", "PowerShell", "Modules", "PadForge");
            string stale = Path.Combine(folder, "Old.ps1");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "PadForge.psm1"), "old");
            File.WriteAllText(stale, "old");
            var letGo = HoldFor(stale, 60);
            Assert.Equal((1, 1), OwnFiles.Mirror(folder, new[] { ("PadForge.psm1", System.Text.Encoding.UTF8.GetBytes("new")) }));
            letGo.Join();
            Assert.False(File.Exists(stale));
            Assert.Equal("new", File.ReadAllText(Path.Combine(folder, "PadForge.psm1")));
        }

        /// <summary>A stale file held past the tries fails the mirror and
        /// stays.</summary>
        [Fact]
        public void AStaleFileHeldPastTheTries_FailsTheMirror_AndStays()
        {
            string folder = At("OneDrive", "Documents", "PowerShell", "Modules", "PadForge");
            string stale = Path.Combine(folder, "Old.ps1");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "PadForge.psm1"), "old");
            File.WriteAllText(stale, "old");
            using (Hold(stale))
            {
                var error = Assert.ThrowsAny<Exception>(() => OwnFiles.Mirror(folder, new[] { ("PadForge.psm1", System.Text.Encoding.UTF8.GetBytes("new")) }));
                Assert.True(error is IOException or UnauthorizedAccessException, error.GetType().Name);
            }
            Assert.Equal("old", File.ReadAllText(stale));
        }

        /// <summary>A removal meets the same: a library's file, and a module
        /// folder with a file in it, each held for a moment, are taken out
        /// once let go.</summary>
        [Fact]
        public void ARemovalOfAFileOrFolderHeldForAMoment_EndsOnceItIsLetGo()
        {
            var said = new List<string>();
            var library = new InstallRequest(InstallWhat.AutoHotkey, null);
            Assert.True(Run(library, said) == UserInstall.Done, string.Join(" | ", said));
            string file = At("OneDrive", "Documents", "AutoHotkey", "Lib", "PadForge.ahk");
            var letGo = HoldFor(file, 60);
            Assert.True(Run(library with { Remove = true }, said) == UserInstall.Done, string.Join(" | ", said));
            letGo.Join();
            Assert.False(File.Exists(file));

            var module = new InstallRequest(InstallWhat.PowerShell, null);
            Assert.True(Run(module, said) == UserInstall.Done, string.Join(" | ", said));
            string folder = At("OneDrive", "Documents", "PowerShell", "Modules", "PadForge");
            letGo = HoldFor(Path.Combine(folder, "PadForge.psm1"), 60);
            Assert.True(Run(module with { Remove = true }, said) == UserInstall.Done, string.Join(" | ", said));
            letGo.Join();
            Assert.False(Directory.Exists(folder));
        }

        // ── Starting it as the user ──

        private sealed class FakeLauncher : IPluginLauncher
        {
            public PluginCommand Started;
            public PluginRefusal Refusal;
            public int ExitCode;

            public PluginLaunch Start(PluginCommand command, IReadOnlyDictionary<string, string> variables)
            {
                Started = command;
                if (Refusal != PluginRefusal.None) return PluginLaunch.Refused(Refusal);
                return new PluginLaunch { Process = new FakeProcess(ExitCode) };
            }
        }

        private sealed class FakeProcess : IPluginProcess
        {
            private readonly int _code;
            public FakeProcess(int code) => _code = code;
            public int Id => 4242;
            public bool Owns(int processId) => processId == Id;
            public event Action<int> Exited
            {
                add => Task.Run(() => value(_code));
                remove { }
            }
            public void Stop() { }
            public void Dispose() { }
        }

        [Fact]
        public async Task ThePagesButton_StartsTheInstallThroughTheShell_AndReadsItsExitCode()
        {
            var launcher = new FakeLauncher { ExitCode = 0 };
            UserInstall.Launcher = launcher;
            var (code, error) = await UserInstall.InCopyAsync(new InstallRequest(InstallWhat.Skill, @"C:\My Skills"));
            Assert.Equal(0, code);
            Assert.Null(error);
            Assert.Equal(Exe, launcher.Started.Program);
            // The run writes what it says into a report in the user's temp
            // folder, which the page reads back (#600).
            Assert.StartsWith("\"" + Exe + "\" --install skill \"C:\\My Skills\" --report ", launcher.Started.CommandLine);

            launcher.ExitCode = 1;
            (code, error) = await UserInstall.InCopyAsync(new InstallRequest(InstallWhat.AutoHotkey, null));
            Assert.Equal(1, code);
            Assert.NotNull(error);

            launcher.Refusal = PluginRefusal.Failed;
            (code, error) = await UserInstall.InCopyAsync(new InstallRequest(InstallWhat.AutoHotkey, null));
            Assert.Equal(UserInstall.Failed, code);
            Assert.Contains("Failed", error);
            Assert.False(File.Exists(At("OneDrive", "Documents", "AutoHotkey", "Lib", "PadForge.ahk")));
        }

        /// <summary>A folder of the user's named PadForge, with files of its
        /// own, is never written over or removed.</summary>
        [Fact]
        public void AFolderOfTheUsersNamedPadForge_KeepsEveryFile()
        {
            string mine = At("Work", "PadForge");
            Directory.CreateDirectory(mine);
            File.WriteAllText(Path.Combine(mine, "PadForge.xml"), "<settings/>");
            File.WriteAllText(Path.Combine(mine, "notes.txt"), "mine");
            Assert.Equal(UserInstall.Done, Run(new InstallRequest(InstallWhat.Skill, mine)));
            Assert.Equal(UserInstall.Done, Run(new InstallRequest(InstallWhat.Skill, mine, Remove: true)));
            Assert.True(File.Exists(Path.Combine(mine, "PadForge.xml")));
            Assert.True(File.Exists(Path.Combine(mine, "notes.txt")));

            // A padforge folder that holds no install is refused, whole.
            string other = At("Skills", "padforge");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "keep.md"), "mine");
            Assert.Equal(UserInstall.Failed, Run(new InstallRequest(InstallWhat.Skill, At("Skills"))));
            Assert.Equal(UserInstall.Failed, Run(new InstallRequest(InstallWhat.Skill, At("Skills"), Remove: true)));
            Assert.True(File.Exists(Path.Combine(other, "keep.md")));
            Assert.False(File.Exists(Path.Combine(other, "SKILL.md")));

            // And a library file PadForge did not write is not removed.
            string script = At("Mine", "padforge.py");
            Directory.CreateDirectory(Path.GetDirectoryName(script));
            File.WriteAllText(script, "print('mine')");
            Assert.Equal(UserInstall.Failed, Run(new InstallRequest(InstallWhat.Python, script, Remove: true)));
            Assert.True(File.Exists(script));
        }

        /// <summary>An entry already there is taken out through the host's
        /// own command before it is added again, so Update works whether or
        /// not the add replaces.</summary>
        [Fact]
        public void AnEntryAlreadyThere_IsTakenOutFirst()
        {
            string json = At("Profile", ".gemini", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(json));
            File.WriteAllText(json, "{\"mcpServers\":{\"padforge\":{\"command\":\"D:\\\\Old\\\\PadForge.exe\",\"args\":[\"--mcp\"]}}}");
            UserInstall.FindSeam = _ => @"C:\Tools\gemini.exe";
            var runs = new List<IReadOnlyList<string>>();
            UserInstall.ToolSeam = (exe, words) => { runs.Add(words); return (0, string.Empty); };
            Assert.Equal(UserInstall.Done, Run(new InstallRequest(InstallWhat.Assistant, "gemini-cli")));
            Assert.Equal(new[] { "mcp", "remove", "--scope", "user", "padforge" }, runs[0]);
            Assert.Equal("add", runs[1][1]);
        }

        // ── Settings files with comments ──

        [Theory]
        [InlineData("zed", @"Profile\AppData\Roaming\Zed\settings.json", "context_servers")]
        [InlineData("vscode", @"Profile\.copilot\mcp-config.json", "mcpServers")]
        [InlineData("claude-desktop", @"Profile\AppData\Roaming\Claude\claude_desktop_config.json", "mcpServers")]
        [InlineData("visual-studio", @"Profile\.mcp.json", "servers")]
        public void ACommentedSettingsFile_KeepsItsCommentsAndLayout(string host, string relative, string container)
        {
            string file = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string original = "// The user's own settings\r\n{\r\n    \"theme\": \"One Dark\", // a note\r\n    /* kept */\r\n    \"" + container
                              + "\": {\r\n        \"files\": { \"command\": \"npx\" },\r\n    },\r\n}\r\n";
            File.WriteAllText(file, original);
            var request = new InstallRequest(InstallWhat.Assistant, host);
            // A failed run says why in the assertion.
            var said = new List<string>();
            Assert.True(Run(request, said) == UserInstall.Done, string.Join(" | ", said));
            string added = File.ReadAllText(file);
            Assert.StartsWith("// The user's own settings\r\n{\r\n    \"theme\": \"One Dark\", // a note\r\n    /* kept */\r\n", added);
            Assert.Contains("\"files\": { \"command\": \"npx\" },", added);
            Assert.Equal(InstallState.Current, UserInstall.Check(request));
            Assert.DoesNotContain("\n", added.Replace("\r\n", string.Empty));
            Assert.True(Run(request, said) == UserInstall.Done, string.Join(" | ", said));
            Assert.Equal(added, File.ReadAllText(file));
            Assert.True(Run(request with { Remove = true }, said) == UserInstall.Done, string.Join(" | ", said));
            Assert.Equal(original, File.ReadAllText(file));
        }

        [Fact]
        public void AFileWithNoContainer_GetsOne_AndAnEmptyFileBecomesOne()
        {
            string zed = At("Profile", "AppData", "Roaming", "Zed", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(zed));
            File.WriteAllText(zed, "{\n  // nothing yet\n  \"theme\": \"Light\"\n}\n");
            Assert.Equal(UserInstall.Done, Run(new InstallRequest(InstallWhat.Assistant, "zed")));
            var read = OwnFiles.ReadJson(zed);
            Assert.Equal("Light", read["theme"].GetValue<string>());
            Assert.Equal(Exe, read["context_servers"]["padforge"]["command"].GetValue<string>());
            Assert.Contains("// nothing yet", File.ReadAllText(zed));
        }

        [Theory]
        [InlineData("plain")]
        [InlineData("C:\\My Skills")]
        [InlineData("C:\\Ends\\")]
        [InlineData("say \"hi\"")]
        public void AWord_IsQuotedAsTheRuntimeReadsIt(string word)
        {
            string line = "x.exe " + UserInstall.Quote(word);
            Assert.Equal(new[] { "x.exe", word }, CommandLineToArgs(line));
        }

        private static string[] CommandLineToArgs(string line)
        {
            IntPtr argv = CommandLineToArgvW(line, out int count);
            try
            {
                var args = new string[count];
                for (int i = 0; i < count; i++)
                    args[i] = System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, i * IntPtr.Size));
                return args;
            }
            finally { LocalFree(argv); }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string line, out int count);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        // ── The fences ──

        [Fact]
        public void TheTestHost_NamesNoRealFolder_AndStartsNoProgram()
        {
            UserFolders.Seam = null;
            Assert.Null(UserFolders.Of(UserFolder.Documents));
            Assert.Null(UserInstall.FindCommand("cmd"));
            Assert.Empty(UserInstall.Pythons());
            Assert.Equal(-1, UserInstall.RunTool(Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/c", "exit 0" }, 1000).Code);
        }

        /// <summary>The install runs in the client run, before the app and
        /// with no window: none of its files names WPF.</summary>
        [Fact]
        public void TheInstall_UsesNoPartOfWpf()
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(OffLimitsSource.RepoRoot(), "PadForge.App", "Services", "Install"), "*.cs"))
            {
                string text = File.ReadAllText(file);
                Assert.DoesNotContain("System.Windows", text);
                Assert.DoesNotContain("Dispatcher", text);
            }
        }
    }

    [CollectionDefinition("UserInstallStatics", DisableParallelization = true)]
    public sealed class UserInstallStatics { }
}
