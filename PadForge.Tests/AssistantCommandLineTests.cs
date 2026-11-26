using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PadForge.Common;
using PadForge.Services;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The assistant tools and the skill on the command line (#600):
    /// <c>--tools [--json]</c>, <c>--call &lt;tool&gt; [json] [--json]</c>
    /// through the same gates and exit codes as an assistant's call,
    /// <c>--skill [name]</c> and <c>--describe &lt;folder&gt;</c>. A call
    /// goes over a real Automation pipe of the test's own.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AssistantCommandLineTests
    {
        private sealed class Run
        {
            public readonly List<string> Said = new(), Errors = new(), Told = new(), ToldErrors = new(), Sent = new();
            public readonly Dictionary<string, byte[]> Written = new();
            public bool Copy = true;
            public bool Pipe = true;
            public Func<string, string> Answer = line => "ok";
            public readonly ClientRun.Seams Seams;

            public Run(string automationPipe = null)
            {
                Seams = new ClientRun.Seams
                {
                    CopyRuns = () => Copy,
                    Exchange = line => { Sent.Add(line); return Pipe ? Answer(line) : null; },
                    PipeStands = () => Pipe,
                    Settle = () => RunningCopy.Settled.Ready,
                    ShowWindow = () => true,
                    LendForeground = () => { },
                    Watch = () => null,
                    Say = Said.Add,
                    SayError = Errors.Add,
                    Tell = Told.Add,
                    TellError = ToldErrors.Add,
                    VersionLine = () => "PadForge 9.9.9",
                    ServeAssistant = () => 7,
                    AutomationPipeName = automationPipe ?? "PadForge.Tests.NoSuchPipe." + Guid.NewGuid().ToString("N"),
                    WriteFile = (path, bytes) => Written[path] = bytes,
                };
            }

            public (bool Done, int Code) Go(params string[] args)
            {
                bool done = ClientRun.TryRun(args, out int code, Seams);
                return (done, code);
            }
        }

        [Fact]
        public void TheSwitches_AreRead()
        {
            var r = CommandLineSwitches.Parse(new[] { "--call", "find", "{\"words\":\"deadzone\"}", "--json" });
            Assert.Equal("find", r.CallTool);
            Assert.Equal("{\"words\":\"deadzone\"}", r.CallArguments);
            Assert.True(r.Json);
            Assert.True(r.AssistantRun);
            Assert.Equal("{}", CommandLineSwitches.Parse(new[] { "--call", "status" }).CallArguments);
            Assert.Equal("status", CommandLineSwitches.Parse(new[] { "--call=status" }).CallTool);
            Assert.NotNull(CommandLineSwitches.Parse(new[] { "--call" }).Error);
            Assert.NotNull(CommandLineSwitches.Parse(new[] { "--call", "--json" }).Error);
            Assert.True(CommandLineSwitches.Parse(new[] { "--tools" }).Tools);
            Assert.Equal(string.Empty, CommandLineSwitches.Parse(new[] { "--skill" }).Skill);
            Assert.Equal("tools", CommandLineSwitches.Parse(new[] { "--skill", "tools" }).Skill);
            Assert.Equal(string.Empty, CommandLineSwitches.Parse(new[] { "--skill", "--json" }).Skill);
            Assert.Equal(@"C:\out", CommandLineSwitches.Parse(new[] { "--describe", @"C:\out" }).Describe);
            Assert.NotNull(CommandLineSwitches.Parse(new[] { "--describe" }).Error);
            // None of them starts PadForge.
            Assert.False(CommandLineSwitches.Parse(new[] { "--tools" }).Empty);
            Assert.Contains("--tools", CommandLineSwitches.HelpText);
            Assert.Contains("--call <tool> [json]", CommandLineSwitches.HelpText);
            Assert.Contains("--skill [name]", CommandLineSwitches.HelpText);
            Assert.Contains("--describe <folder>", CommandLineSwitches.HelpText);
        }

        [Fact]
        public void Skill_PrintsTheSkillOrOneFile_TakenWithOrWithoutItsFolderAndMd()
        {
            var run = new Run { Copy = false };
            Assert.Equal((true, 0), run.Go("--skill"));
            Assert.StartsWith("---\nname: padforge", run.Told[0]);
            foreach (string name in new[] { "tools", "references/tools", "tools.md", "references\\tools.md", "TOOLS" })
            {
                var each = new Run { Copy = false };
                Assert.Equal((true, 0), each.Go("--skill", name));
                Assert.StartsWith("# PadForge's tools by level", each.Told[0]);
            }
            var wrong = new Run { Copy = false };
            Assert.Equal((true, 2), wrong.Go("--skill", "nope"));
            Assert.Contains("references/tools.md", wrong.ToldErrors[0]);
            var folder = new Run { Copy = false };
            Assert.Equal((true, 2), folder.Go("--skill", @"C:\Skills"));
            Assert.Contains("--install skill", folder.ToldErrors[0]);
        }

        [Fact]
        public void Describe_WritesThePageDescriptionAsMarkdownAndJson_WithNoCopyRunning()
        {
            var run = new Run { Copy = false };
            string folder = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Describe." + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.Equal((true, 0), run.Go("--describe", folder));
                Assert.Equal(new[] { Path.Combine(folder, "pages.md"), Path.Combine(folder, "pages.json") }, run.Written.Keys);
                Assert.StartsWith("# PadForge's pages", System.Text.Encoding.UTF8.GetString(run.Written[Path.Combine(folder, "pages.md")]));
                using var doc = JsonDocument.Parse(run.Written[Path.Combine(folder, "pages.json")]);
                Assert.True(doc.RootElement.GetArrayLength() > 500);
                Assert.Empty(run.Errors);
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void Tools_NeedARunningCopy_ListEveryToolWithItsLevelAndArguments_AndMarkTheLevelInForce()
        {
            var none = new Run { Copy = false };
            Assert.Equal((true, ClientRun.NotRunning), none.Go("--tools"));

            var off = new Run { Pipe = false };
            Assert.Equal((true, ClientRun.NotAllowed), off.Go("--tools"));

            var run = new Run { Answer = line => line == "state" ? "ok {\"automation\":{\"on\":true,\"level\":\"read\"}}" : "ok" };
            Assert.Equal((true, 0), run.Go("--tools"));
            string text = run.Told.Single();
            Assert.Contains("The level in force is Read.", text);
            Assert.Contains("Send Inputs (above the level in force)", text);
            Assert.Contains("  find (words, kind, most)", text);
            Assert.Contains("  read (id*)", text);
            Assert.Contains(AssistantCommandLine.BatchNote, text);
            // A tool whose call can need more than its level says so.
            var record = AssistantTools.All.First(t => t.Name == "record_problem");
            Assert.Equal("A recording needs Read. With seconds 0 it saves a snapshot at Commands.", AssistantCommandLine.LevelNote(record));
            Assert.Null(AssistantCommandLine.LevelNote(AssistantTools.All.First(t => t.Name == "status")));
            Assert.Contains("      Record a Problem. " + AssistantCommandLine.LevelNote(record) + "\r\n", text);

            var json = new Run { Answer = run.Answer };
            Assert.Equal((true, 0), json.Go("--tools", "--json"));
            using var doc = JsonDocument.Parse(json.Said.Single());
            Assert.Equal("read", doc.RootElement.GetProperty("level").GetString());
            var tools = doc.RootElement.GetProperty("tools").EnumerateArray().ToList();
            Assert.Equal(AssistantTools.All.Count, tools.Count);
            Assert.True(tools.First(t => t.GetProperty("name").GetString() == "find").GetProperty("allowed").GetBoolean());
            Assert.False(tools.First(t => t.GetProperty("name").GetString() == "set").GetProperty("allowed").GetBoolean());
        }

        [Fact]
        public void Call_GoesThroughTheSameGates_WithTheCommandLinesExitCodes()
        {
            using var rig = new AssistantRig(AutomationLevel.Read);
            // A call the level allows: its result, indented, and with --json
            // in one line.
            var ok = new Run(rig.PipeName);
            Assert.Equal((true, 0), ok.Go("--call", "list_controllers"));
            using (var doc = JsonDocument.Parse(ok.Said.Single()))
                Assert.Equal(1, doc.RootElement.GetProperty("controllers")[0].GetProperty("number").GetInt32());
            Assert.Contains("\n", ok.Said.Single());
            var line = new Run(rig.PipeName);
            Assert.Equal((true, 0), line.Go("--call", "list_controllers", "{\"outputs\":false}", "--json"));
            Assert.DoesNotContain("\n", line.Said.Single());
            // Above the level: 5.
            var above = new Run(rig.PipeName);
            Assert.Equal((true, ClientRun.NotAllowed), above.Go("--call", "set", "{\"id\":\"settings/Settings_Switch\",\"value\":true}"));
            Assert.Contains("(not-allowed)", above.Errors.Last());
            // An unknown tool, a bad argument and JSON that does not read: 2.
            Assert.Equal((true, ClientRun.WrongCommandLine), new Run(rig.PipeName).Go("--call", "no_such_tool"));
            Assert.Equal((true, ClientRun.WrongCommandLine), new Run(rig.PipeName).Go("--call", "list_controllers", "{\"outputz\":true}"));
            Assert.Equal((true, ClientRun.WrongCommandLine), new Run(rig.PipeName).Go("--call", "list_controllers", "{oops"));
            // PadForge's error: 1.
            Assert.Equal((true, ClientRun.AnsweredAnError), new Run(rig.PipeName).Go("--call", "get_mappings", "{\"controller\":9}"));
            // Not running: 4. No Automation pipe: 5.
            Assert.Equal((true, ClientRun.NotRunning), new Run(rig.PipeName) { Copy = false }.Go("--call", "status"));
            Assert.Equal((true, ClientRun.NotAllowed), new Run().Go("--call", "status"));
            // Each call is in Activity, under the command line's name.
            Assert.Contains(rig.Host.Activity.Snapshot(), e => e.Asked.Argument == "list_controllers" && e.Program.Name == AssistantCommandLine.ProgramName);
            // A recording asks for Read, its snapshot for Commands.
            var record = AssistantTools.All.First(t => t.Name == "record_problem");
            Assert.Equal(AutomationLevel.Commands, AssistantCommandLine.LevelFor(record, System.Text.Json.Nodes.JsonNode.Parse("{\"seconds\":0}").AsObject()));
            Assert.Equal(AutomationLevel.Read, AssistantCommandLine.LevelFor(record, new System.Text.Json.Nodes.JsonObject()));
        }

        [Fact]
        public async Task Call_FromAParentNeverAllowed_WaitsOnTheCard_AndADontAllowGives5()
        {
            using var rig = new AssistantRig(AutomationLevel.Read);
            TestConsent.Empty(rig.Host);
            var run = new Run(rig.PipeName);
            var call = Task.Run(() => run.Go("--call", "list_controllers"));
            await AssistantRig.Until(() => rig.Host.Approvals.Pending.Count == 1, 10000);
            var pending = rig.Host.Approvals.Pending[0];
            Assert.Equal(ApprovalKind.Consent, pending.Request.Kind);
            Assert.True(rig.Host.Approvals.Answer(pending.Id, false));
            var (done, code) = await call.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(done);
            Assert.Equal(ClientRun.NotAllowed, code);
            Assert.Contains(run.Errors, e => e.Contains("question card", StringComparison.Ordinal));
        }
    }
}
