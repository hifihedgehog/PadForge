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
using Microsoft.Win32.SafeHandles;
using PadForge.Common;
using PadForge.Services;
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
            var user = new[]
            {
                @"Path=C:\Windows;C:\Tools", @"USERPROFILE=C:\Users\someone", "PADFORGE_PLUGIN_TOKEN=stale",
                "__COMPAT_LAYER=RunAsAdmin", "zeta=last",
            };
            string block = UserVariables.Block(user, new Dictionary<string, string>
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

        // ── The shell's variables ──

        /// <summary>The variables a plugin starts with are the ones Windows
        /// builds for the desktop shell's token: the shell user's own Path
        /// entries are in them, a variable only this process has is not,
        /// <c>__COMPAT_LAYER</c> stays out, and the variables given replace
        /// the shell's.</summary>
        [Fact]
        public void APluginsVariables_AreTheShellTokens_AndNoneThatOnlyThisProcessHas()
        {
            string only = "PADFORGE_TEST_ONLY_HERE_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(only, "1");
            try
            {
                // The same-window control: this process has it.
                Assert.Contains(System32Calls.Variables(), l => l.StartsWith(only + "=", StringComparison.Ordinal));

                using var shell = PluginLauncher.OpenShell();
                Assert.True(shell != null, "no desktop shell runs where the tests run");
                string[] lines = PluginLauncher.ShellVariables(shell, out int error);
                Assert.True(lines != null, "the shell's variables could not be read: error " + error);
                Assert.Equal(0, error);
                var shellMap = UserVariablesTests.Map(lines.Where(l => l.Length > 0 && l[0] != '='));
                Assert.True(shellMap.ContainsKey("USERPROFILE"));
                Assert.True(shellMap.ContainsKey("SystemRoot"));

                string block = UserVariables.Block(lines, new Dictionary<string, string>
                {
                    [PluginManager.TokenVariable] = "KEY",
                    ["USERNAME"] = "given-wins",
                });
                var map = UserVariablesTests.Map(block[..^2].Split('\0'));
                Assert.DoesNotContain(only, map.Keys);
                Assert.DoesNotContain(UserVariables.CompatLayer, map.Keys);
                Assert.Equal("KEY", map[PluginManager.TokenVariable]);
                Assert.Equal("given-wins", map["USERNAME"]);
                foreach (var pair in shellMap)
                {
                    if (string.Equals(pair.Key, UserVariables.CompatLayer, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pair.Key, "USERNAME", StringComparison.OrdinalIgnoreCase)) continue;
                    Assert.Equal(pair.Value, map[pair.Key]);
                }

                // Each of the user's own Path entries, as the profile holds
                // them, is in the shell's Path.
                foreach (string entry in UserVariablesTests.UsersOwnPathEntries())
                    Assert.Contains(entry, shellMap["Path"].Split(';'), StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable(only, null);
            }
        }

        /// <summary>When the shell's variables cannot be read, the plugin is
        /// refused with the Windows error and nothing starts: never a start
        /// with PadForge's own variables.</summary>
        [Fact]
        public void APlugin_IsRefused_WhenTheShellsVariablesCannotBeRead()
        {
            Assert.True(File.Exists(ProbeExe), "the helper was not built beside the tests: " + ProbeExe);
            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.PluginRefused." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string report = Path.Combine(dir, "report.txt");
            int asked = 0;
            try
            {
                var launcher = new PluginLauncher((SafeProcessHandle _, out int error) =>
                {
                    asked++;
                    error = 5;
                    return null;
                });
                var launch = launcher.Start(
                    new PluginCommand(ProbeExe, PluginFile.Quote(ProbeExe) + " rights", Path.GetDirectoryName(ProbeExe)),
                    new Dictionary<string, string> { ["PADFORGE_PROBE_REPORT"] = report });
                Assert.Null(launch.Process);
                Assert.Equal(1, asked);
                Assert.Equal(PluginRefusal.Failed, launch.Refusal);
                Assert.Equal(5, launch.Error);
                Thread.Sleep(1500);
                Assert.False(File.Exists(report), "the helper ran");
                // A refusal the manager tries again, as for a missing shell.
                string manager = ExtrasPageTests.RepoText("PadForge.App", "Services", "Plugins", "PluginManager.cs");
                Assert.Contains("retry: why is PluginRefusal.NoShell or PluginRefusal.Failed or PluginRefusal.None", manager);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }

            // The real reader refuses a handle that may not read the token.
            using var bare = OpenProcess(Synchronize, false, (uint)Environment.ProcessId);
            Assert.False(bare.IsInvalid);
            Assert.Null(PluginLauncher.ShellVariables(bare, out int denied));
            Assert.Equal(5, denied);

            // No variable of PadForge's own process is read for a plugin.
            string source = ExtrasPageTests.RepoText("PadForge.App", "Services", "Plugins", "PluginLauncher.cs");
            Assert.DoesNotContain("GetEnvironmentVariables", source);
            Assert.DoesNotContain("System32Calls.Variables()", source);
            Assert.Contains("if (block == null) return PluginLaunch.Refused(PluginRefusal.Failed, variablesError);", source);
        }

        private const uint Synchronize = 0x00100000;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

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
            // Set in this process only: the plugin's variables are the
            // shell's, so it must not see it. The report's path is given.
            Environment.SetEnvironmentVariable("PADFORGE_PROBE_MINE", mine);
            IPluginProcess process = null;
            try
            {
                Assert.Equal(mine, Environment.GetEnvironmentVariable("PADFORGE_PROBE_MINE"));
                string shellPath;
                using (var shell = PluginLauncher.OpenShell())
                {
                    Assert.True(shell != null, "no desktop shell runs where the tests run");
                    shellPath = UserVariablesTests.Map(PluginLauncher.ShellVariables(shell, out _).Where(l => l.Length > 0 && l[0] != '='))["Path"];
                }
                var launch = new PluginLauncher().Start(
                    new PluginCommand(ProbeExe, PluginFile.Quote(ProbeExe) + " rights", Path.GetDirectoryName(ProbeExe)),
                    new Dictionary<string, string>
                    {
                        [PluginManager.TokenVariable] = "KEY42", [PluginManager.PipeVariable] = "Pipe.For.Test",
                        ["PADFORGE_PROBE_REPORT"] = report,
                    });
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
                Assert.Equal(string.Empty, facts["mine"]);
                Assert.Equal(shellPath, facts["path"]);
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
