using System;
using System.Linq;
using PadForge.Common;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// PadForge's command line (#597). One reader for every start: a switch
    /// means the same whether it starts PadForge or reaches a running copy,
    /// and the line the control pipe takes is the switch without its dashes.
    /// </summary>
    public class CommandLineSwitchesTests
    {
        private static CommandLineSwitches.Request Parse(params string[] args) => CommandLineSwitches.Parse(args);

        private static string[] Lines(CommandLineSwitches.Request request)
            => request.Steps.Select(s => s.Line).ToArray();

        [Fact]
        public void NoWords_AskForNothing()
        {
            foreach (var request in new[] { Parse(), CommandLineSwitches.Parse(null) })
            {
                Assert.Null(request.Error);
                Assert.True(request.Empty);
                Assert.True(request.CanStart);
                Assert.Empty(request.Steps);
                Assert.Null(request.Engine);
                Assert.Equal(CommandLineSwitches.WindowStart.AsSet, request.Window);
            }
        }

        /// <summary>Every switch that reaches a running copy, with the line
        /// it becomes, whether it can start PadForge, and when a starting
        /// copy applies it.</summary>
        [Theory]
        [InlineData(new[] { "--profile", "Racing" }, "activate Racing", true, "AfterEngine")]
        [InlineData(new[] { "--profile", "Mixed Case Name" }, "activate Mixed Case Name", true, "AfterEngine")]
        [InlineData(new[] { "--default-profile" }, "deactivate", true, "AfterEngine")]
        [InlineData(new[] { "--engine", "on" }, "engine on", true, "AtStart")]
        [InlineData(new[] { "--engine", "off" }, "engine off", true, "AtStart")]
        [InlineData(new[] { "--controllers", "on" }, "controllers on", true, "BeforeEngine")]
        [InlineData(new[] { "--controllers", "off" }, "controllers off", true, "BeforeEngine")]
        [InlineData(new[] { "--slot", "1", "on" }, "slot 1 on", true, "BeforeEngine")]
        [InlineData(new[] { "--slot", "16", "off" }, "slot 16 off", true, "BeforeEngine")]
        [InlineData(new[] { "--show" }, "show", true, "AtStart")]
        [InlineData(new[] { "--minimized" }, "minimize", true, "AtStart")]
        [InlineData(new[] { "--tray" }, "hide", true, "AtStart")]
        [InlineData(new[] { "--hide" }, "hide", false, "AfterEngine")]
        [InlineData(new[] { "--exit" }, "exit", false, "AfterEngine")]
        [InlineData(new[] { "--state" }, "state", false, "AfterEngine")]
        [InlineData(new[] { "--snapshot" }, "snapshot", false, "AfterEngine")]
        [InlineData(new[] { "--record", "start" }, "record start", true, "AfterEngine")]
        [InlineData(new[] { "--record", "stop" }, "record stop", false, "AfterEngine")]
        [InlineData(new[] { "--record", "mark" }, "record mark", false, "AfterEngine")]
        [InlineData(new[] { "--record", "mark", "lap", "three" }, "record mark lap three", false, "AfterEngine")]
        public void ASwitch_IsItsLine(string[] args, string line, bool canStart, string phase)
        {
            var request = Parse(args);
            Assert.Null(request.Error);
            var step = Assert.Single(request.Steps);
            Assert.Equal(line, step.Line);
            Assert.Equal(canStart, step.CanStart);
            Assert.Equal(phase, step.Phase.ToString());
            Assert.Equal(canStart, request.CanStart);
            Assert.False(request.Empty);
        }

        /// <summary>The same switches in their second form, a name and its
        /// value in one word, and a name in any case.</summary>
        [Theory]
        [InlineData(new[] { "--profile=Racing" }, "activate Racing")]
        [InlineData(new[] { "--profile=My Halo Profile" }, "activate My Halo Profile")]
        [InlineData(new[] { "--profile=a=b" }, "activate a=b")]
        [InlineData(new[] { "--engine=off" }, "engine off")]
        [InlineData(new[] { "--controllers=ON" }, "controllers on")]
        [InlineData(new[] { "--slot=3", "on" }, "slot 3 on")]
        [InlineData(new[] { "--record=start" }, "record start")]
        [InlineData(new[] { "--record=mark", "lap", "three" }, "record mark lap three")]
        [InlineData(new[] { "--PROFILE", "Racing" }, "activate Racing")]
        [InlineData(new[] { "--Engine", "Off" }, "engine off")]
        [InlineData(new[] { "--RECORD", "STOP" }, "record stop")]
        [InlineData(new[] { "--Default-Profile" }, "deactivate")]
        public void BothForms_AndAnyCase_ReadTheSame(string[] args, string line)
        {
            var request = Parse(args);
            Assert.Null(request.Error);
            Assert.Equal(line, Assert.Single(request.Steps).Line);
        }

        /// <summary>The word after --profile is the name whatever it says,
        /// so a profile named like a switch stays a profile.</summary>
        [Theory]
        [InlineData("--exit")]
        [InlineData("--updated")]
        [InlineData("--wait")]
        [InlineData("--profile")]
        [InlineData("-h")]
        public void AProfileNamedLikeASwitch_StaysAProfile(string name)
        {
            var request = Parse("--profile", name);
            Assert.Null(request.Error);
            Assert.Equal("activate " + name, Assert.Single(request.Steps).Line);
            Assert.False(request.Wait);
            Assert.False(request.Help);
            Assert.True(request.CanStart);

            // And in one word.
            Assert.Equal("activate " + name, Assert.Single(Parse("--profile=" + name).Steps).Line);
        }

        /// <summary>A name cannot carry a line break onto the pipe, where a
        /// line is one command.</summary>
        [Fact]
        public void AValueThatGoesOntoALine_HoldsNoLineBreak()
        {
            // Each break becomes a space.
            Assert.Equal("activate Racing  exit", Assert.Single(Parse("--profile", "Racing\r\nexit").Steps).Line);
            Assert.Equal("record mark a b", Assert.Single(Parse("--record", "mark", "a\nb").Steps).Line);
        }

        [Fact]
        public void TheSwitchesThatReachNoPipe_AreFlags()
        {
            foreach (string help in new[] { "--help", "-h", "-?", "/?", "--HELP" })
            {
                var asked = Parse(help);
                Assert.Null(asked.Error);
                Assert.True(asked.Help);
                Assert.Empty(asked.Steps);
                Assert.False(asked.Empty);
            }
            Assert.True(Parse("--version").Version);
            Assert.True(Parse("--mcp").Mcp);

            var wait = Parse("--wait");
            Assert.True(wait.Wait);
            Assert.False(wait.Empty);
            Assert.True(wait.CanStart);
            Assert.Empty(wait.Steps);

            // The update helper's marker asks for nothing.
            var updated = Parse(UpdateService.UpdatedSwitch);
            Assert.Null(updated.Error);
            Assert.True(updated.Empty);
            Assert.Equal(CommandLineSwitches.UpdatedSwitch, UpdateService.UpdatedSwitch);
        }

        /// <summary>The last of the window switches counts, and the last
        /// --engine, for a copy that starts. A running copy gets each as its
        /// line, in order.</summary>
        [Fact]
        public void TheLastWindowSwitch_AndTheLastEngineSwitch_Count()
        {
            Assert.Equal(CommandLineSwitches.WindowStart.Shown, Parse("--show").Window);
            Assert.Equal(CommandLineSwitches.WindowStart.Minimized, Parse("--minimized").Window);
            Assert.Equal(CommandLineSwitches.WindowStart.Tray, Parse("--tray").Window);
            Assert.Equal(CommandLineSwitches.WindowStart.Tray, Parse("--show", "--tray").Window);
            Assert.Equal(CommandLineSwitches.WindowStart.Shown, Parse("--tray", "--minimized", "--show").Window);
            Assert.Equal(new[] { "hide", "minimize", "show" }, Lines(Parse("--tray", "--minimized", "--show")));

            Assert.True(Parse("--engine", "on").Engine);
            Assert.False(Parse("--engine", "off").Engine);
            Assert.False(Parse("--engine", "on", "--engine=off").Engine);
            Assert.Null(Parse("--profile", "Racing").Engine);
        }

        [Fact]
        public void SeveralSwitches_RunInTheOrderGiven()
        {
            var request = Parse("--profile", "Racing", "--engine", "on", "--slot", "2", "off", "--controllers=on", "--record", "start", "--wait");
            Assert.Null(request.Error);
            Assert.Equal(new[] { "activate Racing", "engine on", "slot 2 off", "controllers on", "record start" }, Lines(request));
            Assert.True(request.Wait);
            Assert.True(request.CanStart);

            // One switch that needs a running copy keeps the whole line from
            // starting one.
            Assert.False(Parse("--profile", "Racing", "--state").CanStart);
            Assert.False(Parse("--record", "start", "--record", "mark").CanStart);
        }

        /// <summary>Every command line PadForge cannot read, with the words
        /// it says. Nothing after the mistake is run.</summary>
        [Theory]
        [InlineData(new[] { "--frobnicate" }, "PadForge does not know the switch --frobnicate.")]
        [InlineData(new[] { "-x" }, "PadForge does not know the switch -x.")]
        [InlineData(new[] { "Racing" }, "PadForge does not know what to do with \"Racing\".")]
        [InlineData(new[] { "" }, "PadForge does not know what to do with \"\".")]
        [InlineData(new[] { "--profile" }, "--profile needs a profile's name or ID.")]
        [InlineData(new[] { "--profile", "  " }, "--profile needs a profile's name or ID.")]
        [InlineData(new[] { "--profile=" }, "--profile needs a profile's name or ID.")]
        [InlineData(new[] { "--engine" }, "--engine needs on or off.")]
        [InlineData(new[] { "--engine", "maybe" }, "--engine needs on or off.")]
        [InlineData(new[] { "--engine=" }, "--engine needs on or off.")]
        [InlineData(new[] { "--controllers" }, "--controllers needs on or off.")]
        [InlineData(new[] { "--controllers", "all" }, "--controllers needs on or off.")]
        [InlineData(new[] { "--slot" }, "--slot needs a number from 1 to 16, then on or off.")]
        [InlineData(new[] { "--slot", "0", "on" }, "--slot needs a number from 1 to 16, then on or off.")]
        [InlineData(new[] { "--slot", "17", "on" }, "--slot needs a number from 1 to 16, then on or off.")]
        [InlineData(new[] { "--slot", "two", "on" }, "--slot needs a number from 1 to 16, then on or off.")]
        [InlineData(new[] { "--slot", "-1", "on" }, "--slot needs a number from 1 to 16, then on or off.")]
        [InlineData(new[] { "--slot", "2" }, "--slot needs a number from 1 to 16, then on or off.")]
        [InlineData(new[] { "--slot", "2", "up" }, "--slot needs a number from 1 to 16, then on or off.")]
        [InlineData(new[] { "--record" }, "--record needs start, stop or mark.")]
        [InlineData(new[] { "--record", "pause" }, "--record needs start, stop or mark.")]
        [InlineData(new[] { "--show=1" }, "--show takes no value.")]
        [InlineData(new[] { "--wait=yes" }, "--wait takes no value.")]
        [InlineData(new[] { "--exit=now" }, "--exit takes no value.")]
        [InlineData(new[] { "--help=me" }, "--help takes no value.")]
        public void AWrongCommandLine_SaysWhatIsWrong(string[] args, string error)
        {
            var request = Parse(args);
            Assert.Equal(error, request.Error);
        }

        [Fact]
        public void AMistake_StopsTheReading()
        {
            var request = Parse("--profile", "Racing", "--frobnicate", "--exit");
            Assert.Equal("PadForge does not know the switch --frobnicate.", request.Error);
            Assert.Equal(new[] { "activate Racing" }, Lines(request));
        }

        /// <summary>The update helper reads its own command line. A
        /// give-back task's never gets this far (Program.Main takes its run
        /// first), so its switch alone is a switch PadForge does not
        /// know.</summary>
        [Fact]
        public void TheUpdateHelpersLine_IsItsOwn()
        {
            var helper = Parse(UpdateService.ApplyUpdateSwitch, @"C:\PadForge\PadForge.exe", "1234", "--frobnicate", "--wait");
            Assert.True(helper.Internal);
            Assert.Null(helper.Error);
            Assert.False(helper.Wait);
            Assert.Empty(helper.Steps);

            Assert.False(GiveBackRun.IsRun(new[] { GiveBackRun.TouchpadSwitch }));
            var stray = Parse(GiveBackRun.TouchpadSwitch);
            Assert.False(stray.Internal);
            Assert.Equal("PadForge does not know the switch " + GiveBackRun.TouchpadSwitch + ".", stray.Error);
        }

        // ── What the copy that becomes the app is handed ──

        [Theory]
        [InlineData(new[] { "--wait" }, new string[0])]
        [InlineData(new[] { "--profile", "Racing", "--wait" }, new[] { "--profile", "Racing" })]
        [InlineData(new[] { "--WAIT", "--tray", "--engine", "off" }, new[] { "--tray", "--engine", "off" })]
        [InlineData(new[] { "--profile", "--wait", "--wait" }, new[] { "--profile", "--wait" })]
        [InlineData(new[] { "--profile=--wait" }, new[] { "--profile=--wait" })]
        [InlineData(new[] { "--slot", "2", "on", "--wait", "--record", "start" }, new[] { "--slot", "2", "on", "--record", "start" })]
        [InlineData(new[] { "--updated", "--minimized" }, new[] { "--updated", "--minimized" })]
        public void WithoutWait_KeepsEveryOtherWord(string[] args, string[] kept)
            => Assert.Equal(kept, CommandLineSwitches.WithoutWait(args));

        [Fact]
        public void WithoutWait_HandsOnWhatNoSwitchTook()
        {
            // The update helper's line, whole, a --wait inside it included.
            var helper = new[] { UpdateService.ApplyUpdateSwitch, @"C:\PadForge\PadForge.exe", "1234", "--wait" };
            Assert.Equal(helper, CommandLineSwitches.WithoutWait(helper));
            // And the rest of a line that could not be read.
            Assert.Equal(new[] { "--tray", "--frobnicate", "--wait" },
                CommandLineSwitches.WithoutWait(new[] { "--wait", "--tray", "--frobnicate", "--wait" }));
            Assert.Empty(CommandLineSwitches.WithoutWait(null));
        }

        // ── What an update's restart repeats ──

        [Theory]
        [InlineData(new[] { "--profile", "Racing" }, new[] { "--profile", "Racing" })]
        [InlineData(new[] { "--profile=Racing", "--default-profile" }, new[] { "--profile=Racing", "--default-profile" })]
        [InlineData(new[] { "--minimized", "--tray", "--show" }, new[] { "--minimized", "--tray", "--show" })]
        [InlineData(new[] { "--engine", "off", "--controllers=off" }, new[] { "--engine", "off", "--controllers=off" })]
        [InlineData(new[] { "--slot", "2", "off", "--slot=3", "on" }, new[] { "--slot", "2", "off", "--slot=3", "on" })]
        // What PadForge was told to do once is dropped.
        [InlineData(new[] { "--record", "start", "--tray" }, new[] { "--tray" })]
        [InlineData(new[] { "--record=start", "--profile", "Racing" }, new[] { "--profile", "Racing" })]
        [InlineData(new[] { "--updated", "--wait", "--tray" }, new[] { "--tray" })]
        // A profile's name is kept whatever it says.
        [InlineData(new[] { "--profile", "--record" }, new[] { "--profile", "--record" })]
        [InlineData(new[] { "--profile", "--updated", "--updated" }, new[] { "--profile", "--updated" })]
        public void StartOptionsOnly_RepeatsHowPadForgeWasStarted(string[] args, string[] kept)
            => Assert.Equal(kept, CommandLineSwitches.StartOptionsOnly(args));

        [Fact]
        public void StartOptionsOnly_OfNothing_IsNothing()
        {
            Assert.Empty(CommandLineSwitches.StartOptionsOnly(null));
            Assert.Empty(CommandLineSwitches.StartOptionsOnly(Array.Empty<string>()));
            // The update helper's own line is no start of the app.
            Assert.Empty(CommandLineSwitches.StartOptionsOnly(new[] { UpdateService.ApplyUpdateSwitch, "x", "1" }));
        }

        /// <summary>The updater strips its marker from a restart's words by
        /// the rule the reader has: only --profile takes any word as its
        /// value. So what StartOptionsOnly hands it is carried as it
        /// is.</summary>
        [Fact]
        public void TheUpdatersMarkerRule_AgreesWithTheReader()
        {
            var start = new[] { "--updated", "--profile", "--updated", "--engine", "off", "--slot", "2", "on", "--tray" };
            var relaunch = CommandLineSwitches.StartOptionsOnly(start);
            Assert.Equal(new[] { "--profile", "--updated", "--engine", "off", "--slot", "2", "on", "--tray" }, relaunch);
            Assert.Equal(relaunch, UpdateService.WithoutUpdatedMarkers(relaunch));
            Assert.False(UpdateService.HasUpdatedMarker(relaunch));
            Assert.True(UpdateService.HasUpdatedMarker(start));
        }

        // ── The help ──

        [Fact]
        public void TheHelp_NamesEverySwitch_AndEveryExitCode()
        {
            string help = CommandLineSwitches.HelpText;
            foreach (string name in new[]
                     {
                         "--profile", "--default-profile", "--engine", "--controllers", "--slot", "--show", "--hide",
                         "--minimized", "--maximized", "--tray", "--exit", "--state", "--snapshot", "--record", "--wait", "--mcp",
                         "--settings-folder",
                         "--tools", "--call", "--json", "--skill", "--describe",
                         "--version", "--help",
                     })
            {
                Assert.Contains(name, help, StringComparison.Ordinal);
                // And each one named is one the reader takes.
                Assert.DoesNotContain("does not know", Parse(name).Error ?? string.Empty, StringComparison.Ordinal);
            }
            foreach (int code in new[] { ClientRun.Done, ClientRun.AnsweredAnError, ClientRun.WrongCommandLine, ClientRun.NotRunning, ClientRun.NotAllowed })
                Assert.Contains(code + " ", help.Substring(help.IndexOf("Exit codes:", StringComparison.Ordinal)), StringComparison.Ordinal);
            // A console's line is 80 wide.
            Assert.All(help.Split(new[] { "\r\n" }, StringSplitOptions.None), line => Assert.True(line.Length <= 80, line));
            Assert.DoesNotContain(";", help, StringComparison.Ordinal);
        }
    }
}
