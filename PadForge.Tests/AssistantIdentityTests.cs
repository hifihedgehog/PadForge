using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using PadForge.Services.Automation;
using PadForge.Services.Install;
using PadForge.ViewModels;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// An assistant row and the assistant's connection agree on who the
    /// program is (#600 items 5 and 9). A row finds an npm-installed assistant
    /// by the .cmd shim npm puts on the PATH, and a winget-installed one by a
    /// link, while its connection carries the program Windows ran: node.exe
    /// and its script, or the native program the package's launcher starts.
    /// The shims here are planted in a temp folder, written as npm's cmd-shim
    /// writes them (its tap snapshots, test/basic.js), and the clients are
    /// planted through the pipe's seams. Nothing is started but cmd.exe's
    /// mklink for a junction, and nothing outside the temp folder is read.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class AssistantIdentityTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Identity." + Guid.NewGuid().ToString("N"));
        private readonly string _npm;

        public AssistantIdentityTests()
        {
            _npm = Path.Combine(_root, "npm");
            Directory.CreateDirectory(_npm);
        }

        public void Dispose()
        {
            UserInstall.FindSeam = null;
            AutomationConsent.CommandLineSeam = null;
            AutomationConsent.ParentSeam = null;
            AutomationConsent.ParentPathSeam = null;
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>cmd-shim's head, the same in every shim it writes.</summary>
        private const string Head = "@ECHO off\r\nGOTO start\r\n:find_dp0\r\nSET dp0=%~dp0\r\nEXIT /b\r\n:start\r\nSETLOCAL\r\nCALL :find_dp0\r\n";

        /// <summary>A shim for a script with a node shebang, as cmd-shim 7
        /// writes it ("env shebang").</summary>
        private string NodeShim(string name, string target, string args = "")
        {
            string file = Path.Combine(_npm, name + ".cmd");
            File.WriteAllText(file, Head + "\r\n"
                + "IF EXIST \"%dp0%\\node.exe\" (\r\n"
                + "  SET \"_prog=%dp0%\\node.exe\"\r\n"
                + ") ELSE (\r\n"
                + "  SET \"_prog=node\"\r\n"
                + ")\r\n\r\n"
                + "endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & set PATHEXT=%PATHEXT:;.JS;=;% & \"%_prog%\" " + args + " \"%dp0%\\" + target + "\" %*\r\n");
            Plant(Path.Combine(_npm, target));
            return file;
        }

        private static string Plant(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            if (!File.Exists(file)) File.WriteAllText(file, "x");
            return file;
        }

        private static string Final(string path) => UserInstall.FinalPathOf(path, directory: false);

        [Fact]
        public void AnNpmShim_IsReadForTheInterpreterAndTheScriptItRuns()
        {
            // node.exe beside the shim is the one it runs.
            string node = Plant(Path.Combine(_npm, "node.exe"));
            string codex = NodeShim("codex", @"node_modules\@openai\codex\bin\codex.js");
            var id = AssistantIdentity.Of(codex);
            Assert.Equal(Final(node), id.Path, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(_npm, @"node_modules\@openai\codex\bin\codex.js"), id.Script, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(_npm, @"node_modules\@openai\codex"), id.PackageFolder, StringComparer.OrdinalIgnoreCase);
            File.Delete(node);

            // With none beside it, node is the PATH's, and a shebang's
            // arguments stand between it and the script.
            string pathNode = Plant(Path.Combine(_root, "nodejs", "node.exe"));
            UserInstall.FindSeam = name => name == "node" ? pathNode : null;
            string gemini = NodeShim("gemini", @"node_modules\@google\gemini-cli\bundle\gemini.js", "--no-warnings=DEP0040");
            id = AssistantIdentity.Of(gemini);
            Assert.Equal(Final(pathNode), id.Path, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(_npm, @"node_modules\@google\gemini-cli\bundle\gemini.js"), id.Script, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(_npm, @"node_modules\@google\gemini-cli"), id.PackageFolder, StringComparer.OrdinalIgnoreCase);

            // No node anywhere: the script still names the program.
            UserInstall.FindSeam = _ => null;
            id = AssistantIdentity.Of(gemini);
            Assert.Null(id.Path);
            Assert.Equal(Path.Combine(_npm, @"node_modules\@google\gemini-cli\bundle\gemini.js"), id.Script, StringComparer.OrdinalIgnoreCase);

            // A file with no shebang is run itself ("no shebang").
            string tool = Path.Combine(_npm, "tool.cmd");
            File.WriteAllText(tool, Head + "\"%dp0%\\node_modules\\tool\\bin\\tool.exe\"   %*\r\n");
            string toolExe = Plant(Path.Combine(_npm, @"node_modules\tool\bin\tool.exe"));
            id = AssistantIdentity.Of(tool);
            Assert.Equal(Final(toolExe), id.Path, StringComparer.OrdinalIgnoreCase);
            Assert.Null(id.Script);

            // Older npm wrote %~dp0 and named the program on each branch.
            string old = Path.Combine(_npm, "old.cmd");
            string oldNode = Plant(Path.Combine(_npm, "node.exe"));
            File.WriteAllText(old, "@IF EXIST \"%~dp0\\node.exe\" (\r\n"
                + "  \"%~dp0\\node.exe\"  \"%~dp0\\node_modules\\old-cli\\cli.js\" %*\r\n"
                + ") ELSE (\r\n  @SETLOCAL\r\n  @SET PATHEXT=%PATHEXT:;.JS;=;%\r\n"
                + "  node  \"%~dp0\\node_modules\\old-cli\\cli.js\" %*\r\n)\r\n");
            id = AssistantIdentity.Of(old);
            Assert.Equal(Final(oldNode), id.Path, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(_npm, @"node_modules\old-cli\cli.js"), id.Script, StringComparer.OrdinalIgnoreCase);

            // A batch file that runs nothing with %* is the file itself.
            string plain = Path.Combine(_root, "plain.cmd");
            File.WriteAllText(plain, "@echo hi\r\n");
            id = AssistantIdentity.Of(plain);
            Assert.Equal(Final(plain), id.Path, StringComparer.OrdinalIgnoreCase);
            Assert.Null(id.Script);
        }

        /// <summary>winget puts a link on the PATH, and Windows names the
        /// file it points to as the running program (QueryFullProcessImageName
        /// on rg.exe started through WinGet\Links gave its Packages path, read
        /// on this PC on 2026-11-24). A junction stands for the link here,
        /// since a file link needs rights a test host may lack.</summary>
        [Fact]
        public void ALinkedProgram_IsTheFileWindowsRuns()
        {
            string real = Plant(Path.Combine(_root, "Packages", "OpenAI.Codex", "codex-x86_64-pc-windows-msvc.exe"));
            string links = Path.Combine(_root, "Links");
            using (var mklink = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                       "/c mklink /J \"" + links + "\" \"" + Path.GetDirectoryName(real) + "\"")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
            {
                mklink.StandardOutput.ReadToEnd();
                Assert.True(mklink.WaitForExit(10000) && mklink.ExitCode == 0, "the junction was made");
            }
            var id = AssistantIdentity.Of(Path.Combine(links, "codex-x86_64-pc-windows-msvc.exe"));
            Assert.Equal(Final(real), id.Path, StringComparer.OrdinalIgnoreCase);
            Assert.StartsWith(Path.Combine(_root, "Packages"), id.Path, StringComparison.OrdinalIgnoreCase);
            // Its connection, by the path Windows gives, is the row's.
            Assert.True(AssistantIdentity.Matches(id, real, null));
            Assert.Equal(AutomationConsent.KeyOf("assistant", real, null),
                AutomationConsent.AssistantKeyOf(Path.Combine(links, "codex-x86_64-pc-windows-msvc.exe")));
        }

        // ── The round trip: Add on the row, the connection, the row's line ──

        private sealed class Checks
        {
            public readonly List<UserInstall.AssistantCheck> List = new();

            public Task<InstallRun> Run(InstallRequest request)
            {
                if (request.What == InstallWhat.Assistant && request.Target == null && request.Check)
                {
                    string json = System.Text.Json.JsonSerializer.Serialize(List.Select(c => new
                    {
                        key = c.Key, program = c.Program, entry = c.Entry, skill = c.Skill, settings = c.Settings,
                    }));
                    return Task.FromResult(new InstallRun(UserInstall.Done, null, json));
                }
                return Task.FromResult(new InstallRun(UserInstall.Done, null, "done"));
            }
        }

        /// <summary>An assistant's copy of PadForge.exe connects at Change
        /// Settings, started by <paramref name="parent"/> whose command line
        /// is <paramref name="commandLine"/>. Gives the hello's answer.</summary>
        private static async Task<AutomationClient> ConnectAssistant(AutomationPipeRig rig, string parent, string commandLine, string name)
        {
            rig.Host.AutomationPipe.ProcessOf = _ => AutomationClientProcess.ForTest(424500, Environment.ProcessPath);
            AutomationConsent.ParentSeam = _ => 4245;
            AutomationConsent.ParentPathSeam = _ => parent;
            AutomationConsent.CommandLineSeam = _ => commandLine;
            var opened = new TaskCompletionSource<bool>();
            rig.Host.McpSession = async (peer, stream, stopping) =>
            {
                opened.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, stopping);
            };
            var client = rig.Connect();
            Assert.StartsWith("ok padforge=", client.Final("hello kind=mcp name=" + name + " host=" + Path.GetFileName(parent)));
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await AutomationPipeRig.Until(() => rig.Host.Clients.Snapshot().Any(c => c.Kind == AutomationClientKind.Assistant));
            return rig.Host.Clients.Snapshot().Single(c => c.Kind == AutomationClientKind.Assistant);
        }

        private static async Task<AssistantRowItem> RowFor(AutomationHost host, string key, string program)
        {
            var checks = new Checks();
            checks.List.Add(new UserInstall.AssistantCheck(key, program, "current", "current", null));
            var vm = new DashboardViewModel { AssistantInstallSeam = checks.Run, AssistantIconSeam = _ => null };
            vm.AttachAutomation(host, Dispatcher.CurrentDispatcher);
            vm.EnableAutomation = true;
            await vm.RefreshAssistantRowsAsync();
            return vm.AssistantRows.Single(r => r.Key == key);
        }

        /// <summary>Gemini CLI added through its npm shim, then started: its
        /// node.exe runs the shim's script (a node.exe other than the one the
        /// shim would pick, as a version manager's), with the path as cmd.exe
        /// expands %dp0%\ (a doubled backslash). It asks nothing, its row says
        /// Connected now, and Allowed Programs lists it as connected.</summary>
        [Fact]
        public async Task AnAssistantAddedThroughItsShim_ConnectsWithNoQuestion_AndItsRowSaysConnectedNow()
        {
            Plant(Path.Combine(_npm, "node.exe"));
            string shim = NodeShim("gemini", @"node_modules\@google\gemini-cli\bundle\gemini.js");
            using var rig = new AutomationPipeRig(AutomationLevel.ChangeSettings);
            var consent = TestConsent.Empty(rig.Host);
            var page = new AutomationConsentForPage(consent, rig.Host.Sessions, rig.Host.Clients);
            page.AllowAssistant("gemini-cli", "Gemini CLI", shim);

            const string node = @"C:\nvm4w\nodejs\node.exe";
            string asRun = "\"" + node + "\" --max-old-space-size=8192 \"" + _npm + "\\\\node_modules\\@google\\gemini-cli\\bundle\\gemini.js\" --experimental-acp";
            var client = await ConnectAssistant(rig, node, asRun, "gemini-cli");
            await Task.Delay(200);
            Assert.Empty(rig.Host.Approvals.Pending);
            Assert.Equal(node, client.HostPath);
            Assert.Equal(Path.Combine(_npm, @"node_modules\@google\gemini-cli\bundle\gemini.js"), client.HostScript, StringComparer.OrdinalIgnoreCase);
            Assert.True(Assert.Single(page.Allowed).ConnectedNow);

            var row = await RowFor(rig.Host, "gemini-cli", shim);
            Assert.Equal(Strings.Instance.Extras_Automation_State_ConnectedNow, row.Line);

            // The row's Remove takes that consent away.
            page.ForgetAssistant("gemini-cli", shim);
            Assert.Empty(page.Allowed);
        }

        /// <summary>Codex added through its npm shim: its launcher
        /// (codex-cli/bin/codex.js) starts the native codex.exe of its
        /// platform package, inside the package's folder, and that program
        /// starts PadForge.exe. The connection goes by the added assistant's
        /// consent, and the row sees it.</summary>
        [Fact]
        public async Task AnAssistantWhoseLauncherStartsANativeProgram_IsCoveredByItsPackage()
        {
            Plant(Path.Combine(_npm, "node.exe"));
            string shim = NodeShim("codex", @"node_modules\@openai\codex\bin\codex.js");
            string native = Plant(Path.Combine(_npm, @"node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe"));
            using var rig = new AutomationPipeRig(AutomationLevel.ChangeSettings);
            var consent = TestConsent.Empty(rig.Host);
            var page = new AutomationConsentForPage(consent, rig.Host.Sessions, rig.Host.Clients);
            page.AllowAssistant("codex", "Codex", shim);

            var client = await ConnectAssistant(rig, native, "\"" + native + "\" mcp", "codex-mcp-client");
            await Task.Delay(200);
            Assert.Empty(rig.Host.Approvals.Pending);
            Assert.Equal(native, client.HostPath);
            Assert.Null(client.HostScript);
            Assert.True(Assert.Single(page.Allowed).ConnectedNow);
            var row = await RowFor(rig.Host, "codex", shim);
            Assert.Equal(Strings.Instance.Extras_Automation_State_ConnectedNow, row.Line);

            // A program outside the package is not covered: it would ask.
            AutomationConsent.ParentPathSeam = _ => Path.Combine(_root, "elsewhere", "codex.exe");
            var other = consent.Covering(AutomationConsent.Identify(AutomationClientProcess.ForTest(424502, Environment.ProcessPath), "assistant", "x"));
            Assert.Null(consent.Find(other.Key));
        }

        /// <summary>VS Code's and the desktop app's rows name their program,
        /// and a connection whose program is that file is theirs, though their
        /// MCP client names are not listed (AssistantPrograms.ClientNames). A
        /// client the row's program did not start leaves the row Added.</summary>
        [Fact]
        public async Task ARowWhoseProgramStartedTheConnection_SaysConnectedNow_ByItsPath()
        {
            string code = Plant(Path.Combine(_root, "Microsoft VS Code", "Code.exe"));
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            TestConsent.AllowAll(rig.Host);
            var client = await ConnectAssistant(rig, Final(code), "\"" + code + "\" --type=utility", "copilot-harness");
            Assert.Equal(Final(code), client.HostPath, StringComparer.OrdinalIgnoreCase);
            var row = await RowFor(rig.Host, "vscode", code);
            Assert.Equal(Strings.Instance.Extras_Automation_State_ConnectedNow, row.Line);

            string other = Plant(Path.Combine(_root, "Zed", "Zed.exe"));
            var zed = await RowFor(rig.Host, "zed", other);
            Assert.Equal(Strings.Instance.Extras_Automation_State_Added, zed.Line);
        }
    }
}
