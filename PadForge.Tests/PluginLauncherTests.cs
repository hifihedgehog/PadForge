using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine;
using PadForge.Services.Automation;
using PadForge.Services.Plugins;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// How a plugin is started (#597): its environment block, the command a
    /// file runs as, the fence, and real starts of the tests' own helper
    /// under the desktop shell: it has no administrator rights when the
    /// test host has them, it reads its key and pipe name, it ends when its
    /// job closes, and the kit's plugin connects with its key and declares
    /// its controller.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class PluginLauncherTests
    {
        private readonly ITestOutputHelper _output;

        public PluginLauncherTests(ITestOutputHelper output) => _output = output;

        // ── The environment block ──

        [Fact]
        public void TheEnvironmentBlock_KeepsTheUsersVariables_AndAddsTheKeyAndThePipe()
        {
            var user = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Path"] = @"C:\Windows;C:\Tools",
                ["USERPROFILE"] = @"C:\Users\someone",
                ["PADFORGE_PLUGIN_TOKEN"] = "stale",
                ["__COMPAT_LAYER"] = "RunAsAdmin",
                ["zeta"] = "last",
            };
            string block = PluginLauncher.EnvironmentBlock(user, new Dictionary<string, string>
            {
                [PluginManager.TokenVariable] = "ABC123",
                [PluginManager.PipeVariable] = "PadForge.Automation",
            });
            Assert.EndsWith("\0\0", block);
            var entries = block.TrimEnd('\0').Split('\0');
            Assert.Contains(@"Path=C:\Windows;C:\Tools", entries);
            Assert.Contains(@"USERPROFILE=C:\Users\someone", entries);
            Assert.Contains("PADFORGE_PLUGIN_TOKEN=ABC123", entries);
            Assert.Contains("PADFORGE_PIPE=PadForge.Automation", entries);
            Assert.DoesNotContain(entries, e => e.StartsWith("__COMPAT_LAYER", StringComparison.OrdinalIgnoreCase));
            Assert.Single(entries, e => e.StartsWith("PADFORGE_PLUGIN_TOKEN=", StringComparison.Ordinal));
            // Sorted by name without regard to case, as CreateProcess wants.
            var names = entries.Select(e => e.Substring(0, e.IndexOf('='))).ToList();
            Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), names);
            Assert.Equal("zeta", names[^1]);
        }

        // ── What a file runs as ──

        [Fact]
        public void AProgram_RunsItself_AndEveryFileRunsInItsOwnFolder()
        {
            var command = PluginFile.CommandFor(@"C:\Plugins\My Bridge\bridge.exe");
            Assert.Equal(@"C:\Plugins\My Bridge\bridge.exe", command.Program);
            Assert.Equal("\"C:\\Plugins\\My Bridge\\bridge.exe\"", command.CommandLine);
            Assert.Equal(@"C:\Plugins\My Bridge", command.Folder);
        }

        [Fact]
        public void APowerShellScript_RunsInWindowsPowerShell_WithNoProfile()
        {
            var command = PluginFile.CommandFor(@"C:\Plugins\pad.ps1");
            Assert.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", command.Program, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(" -NoProfile ", command.CommandLine);
            Assert.EndsWith(" -File \"C:\\Plugins\\pad.ps1\"", command.CommandLine);
        }

        [Fact]
        public void AScript_RunsInTheProgramWindowsOpensItsKindWith()
        {
            var command = PluginFile.CommandFor(@"C:\Plugins\pad.py", ext => ext == ".py" ? @"C:\Python\python.exe" : null);
            Assert.Equal(@"C:\Python\python.exe", command.Program);
            Assert.Equal("\"C:\\Python\\python.exe\" \"C:\\Plugins\\pad.py\"", command.CommandLine);
            Assert.Null(PluginFile.CommandFor(@"C:\Plugins\pad.xyz", _ => null));
            // A kind of file whose program is itself has none for a script.
            Assert.Null(PluginFile.CommandFor(@"C:\Plugins\pad.xyz", _ => @"C:\Plugins\pad.xyz"));
            var batch = PluginFile.CommandFor(@"C:\Plugins\pad.cmd");
            Assert.EndsWith(@"\cmd.exe", batch.Program, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(" /d /c \"\"C:\\Plugins\\pad.cmd\"\"", batch.CommandLine);
        }

        [Theory]
        [InlineData(@"C:\a b\c.exe", "\"C:\\a b\\c.exe\"")]
        [InlineData(@"C:\dir\", "\"C:\\dir\\\\\"")]
        [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
        public void AnArgument_IsQuotedAsWindowsReadsItBack(string argument, string quoted)
            => Assert.Equal(quoted, PluginFile.Quote(argument));

        [Fact]
        public void WindowsFindsAProgramForAPowerShellModuleOrNone_AndNeverAScriptForItself()
        {
            // A kind every Windows knows: a .txt file opens in a program.
            string program = PluginFile.AssociatedProgram(".txt");
            if (program != null) Assert.True(File.Exists(program), program);
            Assert.Null(PluginFile.AssociatedProgram(".padforge-no-such-kind"));
        }

        // ── The fence ──

        [Fact]
        public void TheTestHost_StartsNoProgramOutsideItsOwnFolder()
        {
            Assert.True(PluginLauncher.OffLimitsForTest);
            string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            var launch = new PluginLauncher().Start(new PluginCommand(notepad, "\"" + notepad + "\"", Path.GetTempPath()),
                new Dictionary<string, string>());
            Assert.Null(launch.Process);
            Assert.Equal(PluginRefusal.OffLimits, launch.Refusal);
            Assert.Equal(PluginRefusal.OffLimits, new PluginLauncher().Start(null, new Dictionary<string, string>()).Refusal);
        }

        /// <summary>The check that refuses an elevated shell and an
        /// elevated child reads this process's token as the test host's
        /// rights are.</summary>
        [Fact]
        public void TheRightsCheck_ReadsAToken_AsWindowsGivesIt()
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            using var handle = new Microsoft.Win32.SafeHandles.SafeProcessHandle(self.Handle, ownsHandle: false);
            Assert.Equal(Environment.IsPrivilegedProcess, PluginLauncher.HasAdministratorRights(handle));
            // A plugin is never started in its place: the launcher has no
            // other route than the shell's process as the parent.
            string source = ExtrasPageTests.RepoText("PadForge.App", "Services", "Plugins", "PluginLauncher.cs");
            Assert.Contains("if (HasAdministratorRights(shellProcess) != false) return PluginLaunch.Refused(PluginRefusal.ShellElevated);", source);
            Assert.Contains("else if (HasAdministratorRights(process) != false) refusal = \"rights\";", source);
            Assert.Single(Regex.Matches(source, @"\bCreateProcessW\(command"));
            Assert.DoesNotContain("Process.Start", source);
            Assert.DoesNotContain("ShellExecute", source);
        }

        // ── Real starts of the tests' helper ──

        internal static string ProbeExe => Path.Combine(AppContext.BaseDirectory, "PluginProbe", "PadForge.PluginProbe.exe");

        private static bool TestHostIsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        [Fact]
        public async Task TheHelper_StartsUnderTheShell_WithTheUsersRights_ItsKeyAndPipe_AndEndsWhenItsJobCloses()
        {
            Assert.True(File.Exists(ProbeExe), "the helper was not built beside the tests: " + ProbeExe);
            bool elevated = TestHostIsElevated();
            _output.WriteLine(elevated
                ? "The test host has administrator rights, so the helper must not."
                : "The test host has no administrator rights, so the helper's lack of them shows nothing here. The rest is checked.");

            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.PluginStart." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string report = Path.Combine(dir, "report.txt");
            string mine = "mine-" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable("PADFORGE_PROBE_REPORT", report);
            Environment.SetEnvironmentVariable("PADFORGE_PROBE_MINE", mine);
            IPluginProcess process = null;
            try
            {
                var launch = new PluginLauncher().Start(
                    new PluginCommand(ProbeExe, PluginFile.Quote(ProbeExe) + " rights", Path.GetDirectoryName(ProbeExe)),
                    new Dictionary<string, string> { [PluginManager.TokenVariable] = "KEY42", [PluginManager.PipeVariable] = "Pipe.For.Test" });
                Assert.True(launch.Process != null, "not started: " + launch.Refusal + " " + launch.Error);
                process = launch.Process;
                var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += code => exited.TrySetResult(code);

                var clock = Stopwatch.StartNew();
                while (!File.Exists(report) && clock.ElapsedMilliseconds < 20000) Thread.Sleep(50);
                Assert.True(File.Exists(report), "the helper wrote no report");
                var facts = File.ReadAllLines(report).Where(l => l.Contains('='))
                    .ToDictionary(l => l.Substring(0, l.IndexOf('=')), l => l.Substring(l.IndexOf('=') + 1));
                foreach (var pair in facts) _output.WriteLine(pair.Key + "=" + pair.Value);

                Assert.Equal("0", facts["privileged"]);
                Assert.Equal("KEY42", facts["token"]);
                Assert.Equal("Pipe.For.Test", facts["pipe"]);
                Assert.Equal(mine, facts["mine"]);
                Assert.Equal(Path.GetDirectoryName(ProbeExe).TrimEnd('\\'), facts["folder"].TrimEnd('\\'), ignoreCase: true);

                using (var child = Process.GetProcessById(process.Id))
                    Assert.False(child.HasExited);
                Assert.True(process.Owns(process.Id));
                Assert.False(process.Owns(Environment.ProcessId));

                // It waits a minute by itself. Closing its job ends it now.
                Assert.False(exited.Task.IsCompleted);
                process.Stop();
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Throws<ArgumentException>(() => Process.GetProcessById(process.Id).Dispose());
            }
            finally
            {
                Environment.SetEnvironmentVariable("PADFORGE_PROBE_REPORT", null);
                Environment.SetEnvironmentVariable("PADFORGE_PROBE_MINE", null);
                process?.Stop();
                process?.Dispose();
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [Fact]
        public async Task TheKitsPlugin_StartedByTheManager_ConnectsWithItsKey_AndDeclaresItsController()
        {
            Assert.True(File.Exists(ProbeExe), "the helper was not built beside the tests: " + ProbeExe);
            // Automation's lowest level: a plugin's own devices need none.
            using var rig = new AutomationPipeRig(AutomationLevel.Commands);
            using var manager = new PluginManager(new PluginLauncher(), rig.PipeName,
                commandFor: path => new PluginCommand(path, PluginFile.Quote(path) + " kit", Path.GetDirectoryName(path)));
            rig.Host.AcceptPluginToken = manager.AcceptToken;
            var run = manager.Add(ProbeExe, PluginFile.Sha256(ProbeExe));
            manager.SetEngine(true);
            manager.SetPluginsOn(true);
            try
            {
                await AutomationPipeRig.Until(() => run.Status == PluginStatus.Running, 10000);
                ProgramDevices.Entry entry = null;
                await AutomationPipeRig.Until(() => (entry = rig.Host.Sessions.Devices.All().FirstOrDefault(e => e.Key == "kitpad")) != null, 20000);
                Assert.Equal("plugin", entry.Kind);
                Assert.Equal("PadForge.PluginProbe", entry.Program);
                Assert.Equal("Kit Pad", entry.Device.Name);
                Assert.True(entry.Owner.IsPlugin);
                Assert.Equal("Kit Plugin", run.ShownName);

                // It writes to its log, and its stick moves.
                await AutomationPipeRig.Until(() => manager.LogOf(run).Any(l => l.Kind == PluginLogKind.Wrote && l.Text.StartsWith("Declared Kit Pad as ", StringComparison.Ordinal)), 10000);
                int first = entry.Device.PeekState().Axis[0];
                await AutomationPipeRig.Until(() => entry.Device.PeekState().Axis[0] != first, 5000);

                // Its key was taken, once.
                Assert.True(run.TokenTaken);
            }
            finally
            {
                manager.SetPluginsOn(false);
            }
            await AutomationPipeRig.Until(() => manager.LogOf(run).Any(l => l.Kind == PluginLogKind.Exited), 10000);
            Assert.Equal(PluginStatus.Stopped, run.Status);
        }
    }
}
