using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using PadForge.Services.Automation;
using PadForge.Services.Plugins;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// The plugin kit (#597): Save the Plugin Kit writes its files and the
    /// libraries into a new folder, each file says how to build or run it
    /// and how to add it, the C# file builds (PadForge.PluginProbe compiles
    /// it, warnings as errors), every script passes its language's syntax
    /// check, and each script plugin runs in its library's plugin mode
    /// against a host on a pipe of the test's own. The libraries' plugin
    /// mode, and the kit's C# plugin, send the key only to a pipe owned by
    /// Administrators or SYSTEM, as PadForge's is.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class PluginKitTests
    {
        private readonly ITestOutputHelper _output;

        public PluginKitTests(ITestOutputHelper output) => _output = output;

        private static string NewFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Kit." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void TheKit_IsSavedIntoANewFolder_WithEveryFileAndTheLibraries_AndWritesOverNothing()
        {
            string parent = NewFolder();
            try
            {
                string first = PluginKit.Save(parent);
                Assert.Equal(Path.Combine(parent, PluginKit.FolderName), first);
                var names = Directory.GetFiles(first).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                Assert.Equal(new[] { "PadForge.ahk", "PadForge.psm1", "PadForgePlugin.cs", "PadForgePlugin.csproj", "PipeOwner.cs", "README.md", "build.ps1",
                        "padforge.py", "plugin.ahk", "plugin.ps1", "plugin.py" }
                    .OrderBy(n => n, StringComparer.Ordinal), names);
                foreach (var (name, _) in PluginKit.Files)
                {
                    Assert.Equal(PluginKit.Read(name), File.ReadAllBytes(Path.Combine(first, name)));
                    string text = Encoding.UTF8.GetString(PluginKit.Read(name));
                    string head = text.Substring(0, Math.Min(1400, text.Length));
                    // Each opens with what it is, how to build or run it and
                    // how to add it on the Plugins page.
                    Assert.Contains("PadForge Plugin Kit", head);
                    Assert.Contains("Plugins", head);
                    Assert.Contains("Add a Plugin", head);
                    // The button's words, with the one-character ellipsis.
                    Assert.DoesNotContain("Add a Plugin...", text);
                    Assert.DoesNotContain("\t", text);
                }
                foreach (var library in AutomationLibraries.All)
                    Assert.Equal(AutomationLibraries.Read(library.Kind), File.ReadAllBytes(Path.Combine(first, library.FileName)));

                // A second save makes a second folder.
                string second = PluginKit.Save(parent);
                Assert.Equal(Path.Combine(parent, PluginKit.FolderName + " 2"), second);
                Assert.Throws<ArgumentException>(() => PluginKit.Save(" "));
            }
            finally { Directory.Delete(parent, true); }
        }

        [Fact]
        public void TheKitsCSharpPlugin_Builds_AsTheConsoleProgramItsCommentDescribes()
        {
            // The tests' helper is that build: dotnet new console's settings,
            // with warnings as errors. Its being here is the proof.
            Assert.True(File.Exists(PluginLauncherTests.ProbeExe), PluginLauncherTests.ProbeExe);
            string project = ExtrasPageTests.RepoText("PadForge.PluginProbe", "PadForge.PluginProbe.csproj");
            Assert.Contains(@"<Compile Include=""..\PadForge.App\Resources\Plugins\PadForgePlugin.cs""", project);
            Assert.Contains(@"<Compile Include=""..\PadForge.App\Services\Automation\PipeOwner.cs""", project);
            Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", project);
            Assert.Contains("<Nullable>enable</Nullable>", project);
            Assert.Contains("<ImplicitUsings>enable</ImplicitUsings>", project);
            string kit = Encoding.UTF8.GetString(PluginKit.Read("PadForgePlugin.cs"));
            Assert.Contains("dotnet new console", kit);
            Assert.Contains("kind=plugin token=", kit);
            Assert.Contains("PADFORGE_PLUGIN_TOKEN", kit);
            Assert.Contains("PADFORGE_PIPE", kit);
            Assert.Contains("TokenImpersonationLevel.Identification", kit);
        }

        // ── Syntax ──

        private static (int Code, string Output) RunTool(string exe, string arguments, int ms = 60000)
        {
            var start = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ms))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail(exe + " did not finish");
            }
            return (process.ExitCode, output.Result + error.Result);
        }

        [InterpreterFact("python")]
        public void ThePythonPlugin_IsPython()
        {
            string dir = PluginKit.Save(NewFolder());
            try
            {
                var (code, output) = RunTool(AutomationLibraryTests.Interpreter("python"),
                    "-I -c \"import ast,sys; [ast.parse(open(p, encoding='utf-8').read(), p) for p in sys.argv[1:]]; print('parsed')\" "
                    + "\"" + Path.Combine(dir, "plugin.py") + "\" \"" + Path.Combine(dir, "padforge.py") + "\"");
                Assert.True(code == 0, output);
                Assert.Contains("parsed", output);
            }
            finally { Directory.Delete(Path.GetDirectoryName(dir), true); }
        }

        [InterpreterFact("powershell")]
        public void ThePowerShellPlugin_IsPowerShell()
        {
            string dir = PluginKit.Save(NewFolder());
            try
            {
                string check = "$e=$null; foreach ($f in $args) { [void][System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$null, [ref]$e); if ($e.Count) { $e; exit 1 } }; 'parsed'";
                string script = Path.Combine(dir, "check.ps1");
                File.WriteAllText(script, check);
                var (code, output) = RunTool(AutomationLibraryTests.Interpreter("powershell"),
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" \"" + Path.Combine(dir, "plugin.ps1") + "\" \"" + Path.Combine(dir, "PadForge.psm1") + "\"");
                Assert.True(code == 0, output);
                Assert.Contains("parsed", output);
            }
            finally { Directory.Delete(Path.GetDirectoryName(dir), true); }
        }

        [InterpreterFact("autohotkey")]
        public void TheAutoHotkeyPlugin_IsAutoHotkeyV2()
        {
            string dir = PluginKit.Save(NewFolder());
            try
            {
                var (code, output) = RunTool(AutomationLibraryTests.Interpreter("autohotkey"),
                    "/ErrorStdOut /Validate \"" + Path.Combine(dir, "plugin.ahk") + "\"");
                Assert.True(code == 0, output);
                // Positive control: a script with an error fails the check.
                string bad = Path.Combine(dir, "bad.ahk");
                File.WriteAllText(bad, "x := (\n");
                Assert.NotEqual(0, RunTool(AutomationLibraryTests.Interpreter("autohotkey"), "/ErrorStdOut /Validate \"" + bad + "\"").Code);
            }
            finally { Directory.Delete(Path.GetDirectoryName(dir), true); }
        }

        // ── Each script plugin, run ──

        /// <summary>A process whose output lines a test reads, ended with
        /// everything it started.</summary>
        private sealed class Plugin : IDisposable
        {
            private readonly Process _process;
            public readonly BlockingCollection<string> Lines = new();

            public Plugin(string exe, string arguments, string folder, string pipe, string token)
            {
                var start = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = folder,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                start.Environment[PluginManager.TokenVariable] = token;
                start.Environment[PluginManager.PipeVariable] = pipe;
                start.Environment["PYTHONIOENCODING"] = "utf-8";
                _process = Process.Start(start);
                _process.OutputDataReceived += (_, e) => { if (e.Data != null) Lines.Add(e.Data); };
                _process.ErrorDataReceived += (_, e) => { if (e.Data != null) Lines.Add("stderr: " + e.Data); };
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }

            public string Seen() => string.Join("\n", Lines);

            public void Dispose()
            {
                try
                {
                    if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
                catch (InvalidOperationException) { }
                _process.Dispose();
            }
        }

        private async Task RunsAgainstAHost(string language, Func<string, string> argumentsFor, bool rumbleIsPrinted)
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            var logged = new ConcurrentQueue<string>();
            // Its declared settings (#600 item 17) go to a set of the test's
            // own.
            var settings = new PluginSettingSet();
            rig.Host.AcceptPluginToken = (token, pid) =>
            {
                if (token != "KITKEY") return null;
                var grant = new AutomationPluginGrant("Kit " + language, logged.Enqueue);
                PluginLines.Bind(grant, settings);
                return grant;
            };
            string dir = PluginKit.Save(NewFolder());
            try
            {
                using var plugin = new Plugin(AutomationLibraryTests.Interpreter(language), argumentsFor(dir), dir, rig.PipeName, "KITKEY");
                ProgramDevices.Entry entry = null;
                try
                {
                    await AutomationPipeRig.Until(() => (entry = rig.Host.Sessions.Devices.All().FirstOrDefault(e => e.Key == "kitpad" && e.Owner != null)) != null, 30000);
                }
                catch (Exception)
                {
                    _output.WriteLine(plugin.Seen());
                    throw;
                }
                Assert.Equal("plugin", entry.Kind);
                Assert.Equal("Kit Pad", entry.Device.Name);
                Assert.True(entry.Owner.IsPlugin);
                await AutomationPipeRig.Until(() => logged.Any(l => l.StartsWith("Declared Kit Pad as " + entry.Id, StringComparison.Ordinal)), 10000);
                int first = entry.Device.PeekState().Axis[0];
                await AutomationPipeRig.Until(() => entry.Device.PeekState().Axis[0] != first, 10000);
                // The same four settings in every language, and a change the
                // user makes reaches the plugin.
                await AutomationPipeRig.Until(() => settings.Find(null, "turns") != null, 10000);
                Assert.Equal(new[] { "speed", "fire", "center", "turns" }, settings.Declarations().Select(d => d.Key));
                Assert.Null(settings.Set(null, "speed", "3"));
                await AutomationPipeRig.Until(() => logged.Contains("speed is 3."), 10000);
                if (rumbleIsPrinted)
                {
                    entry.Device.SetRumble(5, 6);
                    string line = null;
                    var clock = Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < 10000 && (line == null || !line.StartsWith("rumble", StringComparison.Ordinal)))
                        plugin.Lines.TryTake(out line, 200);
                    Assert.True(line == "rumble 5 6", "no rumble line in: " + plugin.Seen());
                }
            }
            finally { Directory.Delete(Path.GetDirectoryName(dir), true); }
        }

        [InterpreterFact("python")]
        public Task ThePythonPlugin_RunsInPluginMode()
            => RunsAgainstAHost("python", dir => "-I \"" + Path.Combine(dir, "plugin.py") + "\"", rumbleIsPrinted: true);

        [InterpreterFact("powershell")]
        public Task ThePowerShellPlugin_RunsInPluginMode()
            => RunsAgainstAHost("powershell", dir => "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + Path.Combine(dir, "plugin.ps1") + "\"", rumbleIsPrinted: true);

        [InterpreterFact("autohotkey")]
        public Task TheAutoHotkeyPlugin_RunsInPluginMode()
            => RunsAgainstAHost("autohotkey", dir => "/ErrorStdOut \"" + Path.Combine(dir, "plugin.ahk") + "\"", rumbleIsPrinted: false);

        // ── Who serves the pipe ──

        private static bool TestHostIsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>The kit's C# plugin and each library's plugin mode take a
        /// pipe owned by Administrators or SYSTEM. The test's pipe is owned
        /// by the test host's user, which they take only for a pipe of
        /// another name than PadForge's own: asked as if it bore
        /// PadForge's name, they refuse it.</summary>
        [Fact]
        public void TheKitsCSharpPlugin_SendsItsKeyOnlyToAPipePadForgeMade()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            var (code, output) = RunTool(PluginLauncherTests.ProbeExe, "owner " + rig.PipeName);
            Assert.True(code == 0, output);
            Assert.Contains(TestHostIsElevated() ? "own=1 padforge=1" : "own=1 padforge=0", output);
        }

        [InterpreterFact("python")]
        public void ThePythonLibrary_SendsAPluginsKeyOnlyToAPipePadForgeMade()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            string lib = Path.GetDirectoryName(PluginKit.Save(NewFolder()));
            try
            {
                string kit = Path.Combine(lib, PluginKit.FolderName);
                string code = "import sys; sys.path.insert(0, sys.argv[1]); import padforge; "
                    + "p = padforge._Pipe(sys.argv[2], 5); "
                    + "print('own=%d padforge=%d' % (padforge._served_by_padforge(p.handle, sys.argv[2]), padforge._served_by_padforge(p.handle, 'PadForge.Automation')))";
                var (exit, output) = RunTool(AutomationLibraryTests.Interpreter("python"), "-I -c \"" + code + "\" \"" + kit + "\" " + rig.PipeName);
                Assert.True(exit == 0, output);
                Assert.Contains(TestHostIsElevated() ? "own=1 padforge=1" : "own=1 padforge=0", output);
            }
            finally { Directory.Delete(lib, true); }
        }

        [InterpreterFact("powershell")]
        public void ThePowerShellLibrary_SendsAPluginsKeyOnlyToAPipePadForgeMade()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            string lib = Path.GetDirectoryName(PluginKit.Save(NewFolder()));
            try
            {
                string kit = Path.Combine(lib, PluginKit.FolderName);
                string script = Path.Combine(kit, "owner.ps1");
                File.WriteAllText(script,
                    "Import-Module (Join-Path $PSScriptRoot 'PadForge.psm1')\n"
                    + "$c = New-Object PadForgeAutomation.Connection($args[0], 5000)\n"
                    + "'own={0} padforge={1}' -f [int]$c.ServerIsPadForge($args[0]), [int]$c.ServerIsPadForge('PadForge.Automation')\n"
                    + "$c.Dispose()\n");
                var (exit, output) = RunTool(AutomationLibraryTests.Interpreter("powershell"),
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" " + rig.PipeName);
                Assert.True(exit == 0, output);
                Assert.Contains(TestHostIsElevated() ? "own=1 padforge=1" : "own=1 padforge=0", output);
            }
            finally { Directory.Delete(lib, true); }
        }

        [InterpreterFact("autohotkey")]
        public void TheAutoHotkeyLibrary_SendsAPluginsKeyOnlyToAPipePadForgeMade()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            string lib = Path.GetDirectoryName(PluginKit.Save(NewFolder()));
            try
            {
                string kit = Path.Combine(lib, PluginKit.FolderName);
                string script = Path.Combine(kit, "owner.ahk");
                string result = Path.Combine(kit, "owner.txt");
                File.WriteAllText(script,
                    "#Requires AutoHotkey v2.0\n#Include PadForge.ahk\n"
                    + "OnError((e, mode) => (FileAppend(\"error \" e.Message \"`n\", A_Args[2]), ExitApp(1)))\n"
                    + "h :=DllCall(\"CreateFile\", \"Str\", \"\\\\.\\pipe\\\" A_Args[1], \"UInt\", 0xC0000000, \"UInt\", 0, \"Ptr\", 0, \"UInt\", 3, \"UInt\", 0x00110000, \"Ptr\", 0, \"Ptr\")\n"
                    + "FileAppend(\"own=\" PadForge._ServedByPadForge(h, A_Args[1]) \" padforge=\" PadForge._ServedByPadForge(h, \"PadForge.Automation\") \"`n\", A_Args[2])\n"
                    + "DllCall(\"CloseHandle\", \"Ptr\", h)\nExitApp(0)\n", new UTF8Encoding(true));
                var (exit, output) = RunTool(AutomationLibraryTests.Interpreter("autohotkey"),
                    "/ErrorStdOut \"" + script + "\" " + rig.PipeName + " \"" + result + "\"");
                Assert.True(exit == 0, output);
                Assert.Contains(TestHostIsElevated() ? "own=1 padforge=1" : "own=1 padforge=0", File.ReadAllText(result));
            }
            finally { Directory.Delete(lib, true); }
        }
    }
}
