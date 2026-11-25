using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PadForge.Common;
using PadForge.Services;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The command line of #600 item 44: <c>/name</c> and <c>-name</c> read
    /// as <c>--name</c>, <c>--maximized</c>, <c>--settings-folder</c>, the
    /// switches that run alone, and <c>--json</c> on the refusals of the
    /// switches that reach the running copy.
    /// </summary>
    public class CommandLineFormsTests
    {
        private static CommandLineSwitches.Request Parse(params string[] args) => CommandLineSwitches.Parse(args);

        private static string[] Lines(CommandLineSwitches.Request r) => r.Steps.Select(s => s.Line).ToArray();

        /// <summary>Every row of the parser, read from its own source: each
        /// <c>case "--name":</c>. A row merged later is read the same way,
        /// so this list needs no keeping.</summary>
        internal static string[] Rows()
        {
            string source = ExtrasPageTests.RepoText("PadForge.App", "Common", "CommandLineSwitches.cs");
            var rows = Regex.Matches(source, "^ *case \"(--[a-z?-]+)\":", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
            rows.Add(CommandLineSwitches.ProfileSwitch);
            rows.Add(CommandLineSwitches.UpdatedSwitch);
            return rows.Distinct().ToArray();
        }

        /// <summary>A row's words as the parser reads them: what it asks
        /// for, or what is wrong.</summary>
        private static string Reading(CommandLineSwitches.Request r)
            => string.Join("|", r.Error ?? "-", string.Join(",", Lines(r)), r.Help, r.Version, r.Mcp, r.Wait, r.Json,
                r.Tools, r.CallTool ?? "-", r.Skill ?? "-", r.Describe ?? "-", r.Summarize ?? "-", r.PluginKit ?? "-",
                r.SettingsFolder, r.Window, r.Engine?.ToString() ?? "-", r.Install?.What.ToString() ?? "-");

        [Fact]
        public void EveryRow_TakesEachPrefix_AndAValueAfterAnEqualsSign()
        {
            var rows = Rows();
            // The control: the rows the source holds today are all here.
            foreach (string known in new[] { "--profile", "--engine", "--maximized", "--settings-folder", "--state", "--install", "--tools", "--json" })
                Assert.Contains(known, rows);

            foreach (string row in rows)
            {
                string bare = row.Substring(2);
                foreach (var tail in new[] { new string[0], new[] { "on" }, new[] { "2", "on" }, new[] { "start" } })
                {
                    string expected = Reading(Parse(new[] { row }.Concat(tail).ToArray()));
                    foreach (string form in new[] { "-" + bare, "/" + bare, "--" + bare.ToUpperInvariant(), "/" + bare.ToUpperInvariant() })
                        Assert.Equal(expected, Reading(Parse(new[] { form }.Concat(tail).ToArray())));
                }
                if (tail1(row) is { } value)
                {
                    string expected = Reading(Parse(row, value));
                    foreach (string form in new[] { "--" + bare + "=" + value, "-" + bare + "=" + value, "/" + bare + "=" + value })
                        Assert.Equal(expected, Reading(Parse(form)));
                }
            }

            static string tail1(string row) => row switch
            {
                "--profile" => "Racing",
                "--engine" or "--controllers" => "off",
                "--record" => "stop",
                "--summarize" => @"C:\r.zip",
                "--plugin-kit" or "--describe" => @"C:\kit",
                _ => null,
            };
        }

        [Theory]
        [InlineData(new[] { "/exit" }, "exit")]
        [InlineData(new[] { "-EXIT" }, "exit")]
        [InlineData(new[] { "/Engine=off" }, "engine off")]
        [InlineData(new[] { "-slot", "2", "on" }, "slot 2 on")]
        [InlineData(new[] { "/profile", "Racing" }, "activate Racing")]
        [InlineData(new[] { "/record", "mark", "lap", "/exit" }, "record mark lap,exit")]
        [InlineData(new[] { "--record", "mark", "see", "/tmp" }, "record mark see /tmp")]
        [InlineData(new[] { "--profile", "/Racing" }, "activate /Racing")]
        [InlineData(new[] { "--profile", "/exit" }, "activate /exit")]
        public void ASlashOrADash_ReadsAsTwoDashes_AndASlashValueStaysAValue(string[] args, string lines)
        {
            var r = Parse(args);
            Assert.Null(r.Error);
            Assert.Equal(lines, string.Join(",", Lines(r)));
        }

        [Fact]
        public void TheHelpsShortForms_AreHelp_AndAnUnknownSwitchIsNamedAsGiven()
        {
            foreach (string help in new[] { "-h", "-?", "/?", "/h", "/H", "-help", "/help" })
                Assert.True(Parse(help).Help, help);
            Assert.Equal("PadForge does not know the switch /frobnicate.", Parse("/frobnicate").Error);
            Assert.Equal("PadForge does not know the switch -frobnicate.", Parse("-frobnicate").Error);
            Assert.Equal("--exit takes no value.", Parse("/exit=now").Error);
        }

        [Fact]
        public void AKnownSwitch_IsKnownByItsRows()
        {
            foreach (string row in Rows())
            {
                string bare = row.Substring(2);
                Assert.True(CommandLineSwitches.IsKnownSwitch("/" + bare), row);
                Assert.True(CommandLineSwitches.IsKnownSwitch("-" + bare), row);
                Assert.True(CommandLineSwitches.IsKnownSwitch(row + "=x"), row);
            }
            foreach (string value in new[] { "/tmp", "/c/Users/me", "/Racing", "-frobnicate", "Racing", "/", "", null })
                Assert.False(CommandLineSwitches.IsKnownSwitch(value), value ?? "null");
        }

        // ── --maximized ──

        [Fact]
        public void Maximized_IsAStartOption_AndTheLineMaximize()
        {
            var r = Parse("--maximized");
            Assert.Null(r.Error);
            Assert.Equal(CommandLineSwitches.WindowStart.Maximized, r.Window);
            var step = Assert.Single(r.Steps);
            Assert.Equal("maximize", step.Line);
            Assert.True(step.CanStart);
            Assert.Equal(CommandLineSwitches.StartPhase.AtStart, step.Phase);
            Assert.Equal(new[] { "--maximized" }, CommandLineSwitches.StartOptionsOnly(new[] { "--maximized", "--state" }));
            Assert.Equal(CommandLineSwitches.WindowStart.Minimized, Parse("--maximized", "--minimized").Window);
            Assert.Equal(CommandLineSwitches.WindowStart.Maximized, Parse("--tray", "/maximized").Window);

            // Whatever the Start Minimized settings say.
            foreach (bool minimized in new[] { false, true })
                foreach (bool tray in new[] { false, true })
                    Assert.Equal(CommandLineSwitches.WindowStart.Maximized,
                        MainWindow.FirstWindowState(CommandLineSwitches.WindowStart.Maximized, minimized, tray));
        }

        [Fact]
        public void TheRunningCopy_TakesMaximize_AsACommand()
        {
            var target = new RecordingAutomationTarget();
            // A host that serves no pipe: the line is run as a starting
            // copy runs its own.
            using var host = new AutomationHost(target, new InlineAutomationUi(), "PadForge.Tests.Maximize." + Guid.NewGuid().ToString("N"));
            Assert.Equal("ok", host.Commands.RunOwn("maximize"));
            Assert.Equal(new[] { "maximize" }, target.Calls);
            Assert.Equal("error bad-argument", host.Commands.RunOwn("maximize now"));
            Assert.Equal(AutomationAskKind.MaximizeWindow, AutomationCommands.Describe("maximize").Kind);
            // Appended: the kinds before it keep their numbers.
            Assert.True(AutomationAskKind.MaximizeWindow > AutomationAskKind.Rest);
        }

        // ── Runs alone ──

        [Theory]
        [InlineData(new[] { "--settings-folder", "--state" }, "--settings-folder runs alone, and --state was given with it. Give each in a call of its own.")]
        [InlineData(new[] { "--tools", "/skill" }, "--tools runs alone, and --skill was given with it. Give each in a call of its own.")]
        [InlineData(new[] { "--profile", "Racing", "--summarize", @"C:\r.zip" }, "--summarize runs alone, and --profile was given with it. Give each in a call of its own.")]
        [InlineData(new[] { "--mcp", "--minimized" }, "--mcp runs alone, and --minimized was given with it. Give each in a call of its own.")]
        [InlineData(new[] { "--install", "python", "--exit" }, "--install runs alone, and --exit was given with it. Give each in a call of its own.")]
        public void ASwitchThatRunsAlone_IsRefusedBesideAnother(string[] args, string error)
            => Assert.Equal(error, Parse(args).Error);

        [Theory]
        [InlineData("--settings-folder")]
        [InlineData("--tools --json")]
        [InlineData("--call status {} --json")]
        [InlineData("--summarize C:\\r.zip --wait")]
        [InlineData("--plugin-kit C:\\kit")]
        [InlineData("--install skill claude-code --check")]
        [InlineData("--record save C:\\a.zip C:\\b.zip")]
        [InlineData("--help --tools")]
        public void ASwitchThatRunsAlone_GoesWithJsonAndWait(string line)
            => Assert.Null(Parse(line.Split(' ')).Error);

        // ── --settings-folder ──

        [Fact]
        public void SettingsFolder_IsAFlag_ThatNeedsNoCopy()
        {
            var r = Parse("/settings-folder");
            Assert.True(r.SettingsFolder);
            Assert.False(r.Empty);
            Assert.Empty(r.Steps);
            Assert.Equal("--settings-folder takes no value.", Parse("--settings-folder=C:\\x").Error);

            int opened = 0;
            var said = new List<string>();
            var seams = new ClientRun.Seams
            {
                CopyRuns = () => throw new InvalidOperationException("no copy is looked for"),
                Exchange = line => throw new InvalidOperationException("nothing is sent"),
                Say = said.Add, SayError = said.Add, Tell = said.Add, TellError = said.Add,
                SettingsFolder = sayError => { opened++; return ClientRun.Done; },
            };
            Assert.True(ClientRun.TryRun(new[] { "--settings-folder" }, out int code, seams));
            Assert.Equal(ClientRun.Done, code);
            Assert.Equal(1, opened);
            // The path is the user's alone: nothing is printed.
            Assert.Empty(said);

            seams.SettingsFolder = sayError => { sayError("Explorer did not open the settings folder."); return ClientRun.AnsweredAnError; };
            Assert.True(ClientRun.TryRun(new[] { "--settings-folder" }, out code, seams));
            Assert.Equal(ClientRun.AnsweredAnError, code);
        }

        [Fact]
        public void SettingsFolder_SelectsTheFile_OrOpensTheFolderBeforeTheFirstSave()
        {
            const string file = @"C:\Tools\PadForge\PadForge.xml";
            Assert.Equal("/select,\"C:\\Tools\\PadForge\\PadForge.xml\"", SettingsFolderRun.ExplorerArguments(file, _ => true));
            Assert.Equal("\"C:\\Tools\\PadForge\"", SettingsFolderRun.ExplorerArguments(file, _ => false));
        }

        /// <summary>The fence: in the test host the run hands the shell
        /// nothing (ShellLaunch.OffLimitsForTest), and says so.</summary>
        [Fact]
        public void SettingsFolder_InTheTestHost_StartsNothing()
        {
            Assert.True(ShellLaunch.OffLimitsForTest);
            Assert.Null(ShellLaunch.UserShellSeam);
            var errors = new List<string>();
            Assert.Equal(ClientRun.AnsweredAnError, SettingsFolderRun.Run(errors.Add));
            Assert.Equal("Explorer did not open the settings folder: the test host starts nothing", Assert.Single(errors));
        }

        // ── --json on refusals ──

        private sealed class Pipe
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Said = new List<string>();
            public int Lent;
            public bool Copy = true, Stands = true;
            public Func<string, string> Answer = line => "ok";
            public ClientRun.Seams Seams => new ClientRun.Seams
            {
                CopyRuns = () => Copy,
                Exchange = line => Stands ? Answer(line) : null,
                PipeStands = () => Stands,
                Settle = () => RunningCopy.Settled.Ready,
                ShowWindow = () => false,
                LendForeground = () => Lent++,
                Watch = () => null,
                Say = Said.Add, SayError = Errors.Add, Tell = Said.Add, TellError = Errors.Add,
            };
        }

        [Fact]
        public void WithJson_ARefusalIsOneLineOfJson_InTheShapeCallGives()
        {
            var pipe = new Pipe { Answer = line => "error not-allowed" };
            Assert.True(ClientRun.TryRun(new[] { "--state", "--json" }, out int code, pipe.Seams));
            Assert.Equal(ClientRun.NotAllowed, code);
            var line = JsonNode.Parse(Assert.Single(pipe.Errors)).AsObject();
            Assert.Equal("not-allowed", (string)line["error"]);
            Assert.False(string.IsNullOrEmpty((string)line["message"]));

            pipe = new Pipe { Stands = false };
            Assert.True(ClientRun.TryRun(new[] { "--state", "--json" }, out code, pipe.Seams));
            Assert.Equal(ClientRun.NotAllowed, code);
            Assert.Equal(ClientRun.JsonRefusal("not-allowed", ClientRun.AutomationOffText), Assert.Single(pipe.Errors));

            pipe = new Pipe { Copy = false };
            Assert.True(ClientRun.TryRun(new[] { "--state", "--json" }, out code, pipe.Seams));
            Assert.Equal(ClientRun.NotRunning, code);
            Assert.Equal(ClientRun.JsonRefusal("not-running", ClientRun.NotRunningText), Assert.Single(pipe.Errors));

            pipe = new Pipe { Answer = line => "error unknown-slot No controller has that number." };
            Assert.True(ClientRun.TryRun(new[] { "--slot", "9", "on", "--json" }, out code, pipe.Seams));
            Assert.Equal(ClientRun.AnsweredAnError, code);
            Assert.Equal(ClientRun.JsonRefusal("unknown-slot", "No controller has that number."), Assert.Single(pipe.Errors));

            // Without --json the words stay as the pipe gave them.
            pipe = new Pipe { Answer = line => "error not-allowed" };
            Assert.True(ClientRun.TryRun(new[] { "--state" }, out code, pipe.Seams));
            Assert.Equal("error not-allowed", Assert.Single(pipe.Errors));
        }

        [Fact]
        public void State_PrintsOneLineOfJson_WithOrWithoutJson()
        {
            foreach (var args in new[] { new[] { "--state" }, new[] { "--state", "--json" }, new[] { "/state", "-json" } })
            {
                var pipe = new Pipe { Answer = line => "ok {\"engine\":\"running\"}" };
                Assert.True(ClientRun.TryRun(args, out int code, pipe.Seams));
                Assert.Equal(ClientRun.Done, code);
                Assert.Equal("{\"engine\":\"running\"}", Assert.Single(pipe.Said));
            }
        }

        [Fact]
        public void Maximize_LendsTheFront_AndKeepsTheAutomationGate()
        {
            var pipe = new Pipe();
            Assert.True(ClientRun.TryRun(new[] { "--maximized" }, out int code, pipe.Seams));
            Assert.Equal(ClientRun.Done, code);
            Assert.Equal(1, pipe.Lent);

            // With Automation off it is refused with 5, and the window is
            // not shown by another way.
            pipe = new Pipe { Stands = false };
            Assert.True(ClientRun.TryRun(new[] { "/maximized" }, out code, pipe.Seams));
            Assert.Equal(ClientRun.NotAllowed, code);
            Assert.Equal(ClientRun.AutomationOffText, Assert.Single(pipe.Errors));

            // With no copy, it starts PadForge maximized.
            pipe = new Pipe { Copy = false };
            Assert.False(ClientRun.TryRun(new[] { "--maximized" }, out code, pipe.Seams));
        }
    }
}
