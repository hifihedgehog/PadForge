using System;
using System.Collections.Generic;
using PadForge.Services;
using PadForge.Services.Plugins;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The DSX port files are the user's: C:\Temp\DualSenseX, which any
    /// signed-in program may replace with a link, and the user's own
    /// %LOCALAPPDATA%\DSX. PadForge with rights the shell lacks writes them
    /// in a copy started as the desktop user (PluginLauncher, as a
    /// recording's save is), never in its own process. Every way out is a
    /// seam here, so nothing on the PC is written or started.
    /// </summary>
    public class GameModPortFilesAsUserTests
    {
        private sealed class Recorder : IPluginLauncher
        {
            public readonly List<string> Lines = new();
            public PluginLaunch Start(PluginCommand command, IReadOnlyDictionary<string, string> variables)
            {
                Lines.Add(command.CommandLine);
                return PluginLaunch.Refused(PluginRefusal.Failed);
            }
        }

        [Fact]
        public void WithRightsTheShellLacks_TheFilesAreWrittenAsTheUser_NeverHere()
        {
            var launcher = new Recorder();
            int here = 0;
            GameModPortFiles.Write(7000, new GameModPortFiles.Seams
            {
                RealDsxRunning = () => false,
                RightsTheShellLacks = () => true,
                Launcher = launcher,
                Exe = () => @"C:\PadForge\PadForge.exe",
                WriteHere = _ => here++,
            });
            Assert.Equal(0, here);
            string line = Assert.Single(launcher.Lines);
            Assert.EndsWith(" --port-files 7000", line);
        }

        /// <summary>The copy the line starts reads its port, runs alone, and
        /// refuses anything that is no port.</summary>
        [Fact]
        public void TheSwitch_TakesAPort_AndRunsAlone()
        {
            var r = PadForge.Common.CommandLineSwitches.Parse(new[] { "--port-files", "7000" });
            Assert.Null(r.Error);
            Assert.Equal(7000, r.PortFiles);
            Assert.Contains("--port-files", r.Alone);
            Assert.NotNull(PadForge.Common.CommandLineSwitches.Parse(new[] { "--port-files", "70000" }).Error);
            Assert.NotNull(PadForge.Common.CommandLineSwitches.Parse(new[] { "--port-files", "x" }).Error);
        }

        private sealed class ShellElevated : IPluginLauncher
        {
            public int Starts;
            public PluginLaunch Start(PluginCommand command, IReadOnlyDictionary<string, string> variables)
            {
                Starts++;
                return PluginLaunch.Refused(PluginRefusal.ShellElevated);
            }
        }

        /// <summary>With rights, the files are written here only when the
        /// shell's user has no lower right (UAC off, or the built-in
        /// Administrator). A shell with rights whose user also has a lower
        /// right (an Explorer started elevated, a token that cannot be read)
        /// gets nothing written with PadForge's rights, the same rule
        /// the install follows. RightsTheShellLacks stays unset, so the rule
        /// itself runs.</summary>
        [Fact]
        public void WithRights_TheFilesAreWrittenHereOnlyWhenTheShellsUserHasNoLowerRight()
        {
            var launcher = new ShellElevated();
            int here = 0;
            var seams = new GameModPortFiles.Seams
            {
                RealDsxRunning = () => false,
                OwnRights = () => true,
                NoLowerRight = () => false,
                Launcher = launcher,
                Exe = () => @"C:\PadForge\PadForge.exe",
                WriteHere = _ => here++,
            };
            GameModPortFiles.Write(7000, seams);
            Assert.Equal(0, here);
            Assert.Equal(1, launcher.Starts);

            seams.NoLowerRight = () => true;
            GameModPortFiles.Write(7000, seams);
            Assert.Equal(1, here);
            Assert.Equal(1, launcher.Starts);

            // Without rights, here, and no launcher.
            seams.OwnRights = () => false;
            seams.NoLowerRight = () => false;
            GameModPortFiles.Write(7000, seams);
            Assert.Equal(2, here);
            Assert.Equal(1, launcher.Starts);
        }

        [Fact]
        public void WithoutRightsTheShellLacks_TheFilesAreWrittenHere_AsTheUser()
        {
            var launcher = new Recorder();
            int here = 0;
            GameModPortFiles.Write(7000, new GameModPortFiles.Seams
            {
                RealDsxRunning = () => false,
                RightsTheShellLacks = () => false,
                Launcher = launcher,
                Exe = () => @"C:\PadForge\PadForge.exe",
                WriteHere = _ => here++,
            });
            Assert.Equal(1, here);
            Assert.Empty(launcher.Lines);
        }
    }
}
