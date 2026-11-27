using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PadForge.Services.Install;
using PadForge.Services.Plugins;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// What the install copy may run, and with which rights, and how long it
    /// waits: a copy started for the user never runs with
    /// PadForge's rights while the user has a lower right, the page waits out
    /// both of an Update's commands, and a tool that leaves a child holding
    /// its output ends within its bound. Every folder is the test's own and
    /// no program starts but the tests' own helper.
    /// </summary>
    [Collection("UserInstallStatics")]
    public sealed class PresenceCheckTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Presence." + Guid.NewGuid().ToString("N"));
        private const string Exe = @"C:\Apps\PadForge\PadForge.exe";

        public PresenceCheckTests()
        {
            Directory.CreateDirectory(_root);
            UserFolders.Seam = f => Path.Combine(_root, f.ToString());
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

        private sealed class RefusingLauncher : IPluginLauncher
        {
            private readonly PluginRefusal _refusal;
            public RefusingLauncher(PluginRefusal refusal) => _refusal = refusal;
            public int Starts;

            public PluginLaunch Start(PluginCommand command, IReadOnlyDictionary<string, string> variables)
            {
                Starts++;
                return PluginLaunch.Refused(_refusal);
            }
        }

        /// <summary>A maker's command that runs out of time fails as
        /// TimedOut with its own bound, never as its exit code: the same
        /// defect as the in-place install's timeout, in the install copy's Assistant.</summary>
        [Fact]
        public void AHostCommandThatTimesOut_FailsAsTimedOut()
        {
            UserInstall.FindSeam = name => name == "gemini" ? @"C:\Tools\gemini.exe" : null;
            UserInstall.ToolSeam = (exe, words) => (UserInstall.ToolTimedOut, "It did not end in 60 seconds.");
            string report = Path.Combine(_root, "report.txt");
            var request = new InstallRequest(InstallWhat.Assistant, "gemini-cli") { Report = report };
            Assert.Equal(UserInstall.Failed, UserInstall.Run(request, _ => { }, _ => { }));
            var (_, reason, number, _) = UserInstall.ReadReason(File.ReadAllText(report));
            Assert.Equal(InstallReason.TimedOut, reason);
            Assert.Equal(UserInstall.HostCommandMs / 1000, number);
        }

        /// <summary>The page's bound outlasts an Update's two maker's
        /// commands and the copy's own start, so the copy's own bound and its
        /// own line decide.</summary>
        [Fact]
        public void APagesInstallBound_OutlastsAnUpdatesTwoHostCommands()
            => Assert.True(UserInstall.CopyMs >= 2 * UserInstall.HostCommandMs + 30000,
                "CopyMs " + UserInstall.CopyMs + " against two host commands of " + UserInstall.HostCommandMs);

        /// <summary>A tool that ends at once but leaves a child holding its
        /// output open ends RunTool within its bound, not when the child
        /// ends. The tool is the tests' own helper, the one
        /// program the fence lets RunTool start.</summary>
        [Fact]
        public void AToolThatLeavesAChildHoldingItsOutput_EndsWithinItsBound()
        {
            string probe = PluginLauncherTests.ProbeExe;
            Assert.True(File.Exists(probe), "the helper was not built beside the tests: " + probe);
            var watch = Stopwatch.StartNew();
            var (code, _) = UserInstall.RunTool(probe, new[] { "linger", "8000" }, 2000);
            watch.Stop();
            Assert.True(watch.ElapsedMilliseconds < 5000, "RunTool took " + watch.ElapsedMilliseconds + " ms");
            Assert.Equal(UserInstall.ToolTimedOut, code);
        }

    }
}
