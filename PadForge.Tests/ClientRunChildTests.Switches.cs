using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using PadForge.Common;
using PadForge.Engine.Recording;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using Xunit;

namespace PadForge.Tests
{
    public partial class ClientRunChildTests
    {
        // ─────────────────────────────────────────────
        //  Every switch of the client run
        // ─────────────────────────────────────────────

        /// <summary>One run of the table below: its folder, the recording
        /// it may name, the rig standing for PadForge when a copy runs, and
        /// the child.</summary>
        internal sealed record SwitchRun(string Folder, AssistantRig Rig, Child Child);

        /// <summary>
        /// A row of the client run's switches: its words, with
        /// <c>{folder}</c> and <c>{recording}</c> put in, and whether a copy
        /// runs, which is a rig serving the child's pipe. The run's code and
        /// words are what <see cref="ClientRun.TryRun"/> gives in this
        /// process through the seams a test's child takes, unless the row
        /// names them: the <c>--web</c> helper's own words go to standard
        /// error past the seams. A row that changes PadForge's side compares
        /// its code alone and checks the change.
        /// </summary>
        internal sealed record SwitchRow(string[] Words, bool CopyRuns = false, bool CompareWords = true, int? Code = null, string Error = null,
            Action<SwitchRun> After = null);

        /// <summary>
        /// Every switch of the client run (ClientRun.TryRun and the rows of
        /// CommandLineSwitches.Parse), each in a row of its own, with the
        /// prefix forms and the command lines with no switch.
        /// <c>--mcp</c> is the one switch not here: its runs are the tests
        /// above that drive it through the SDK's client, and its run here
        /// would serve this test host's own standard input. The test
        /// <see cref="EverySwitchTheParserKnows_HasARowOfTheTable"/> fails
        /// on a switch that has no row.
        /// </summary>
        internal static readonly Dictionary<string, SwitchRow> SwitchRows = new()
        {
            ["--version"] = new(new[] { "--version" }),
            ["/version"] = new(new[] { "/version" }),
            ["--help"] = new(new[] { "--help" }),
            ["-h"] = new(new[] { "-h" }),
            ["/?"] = new(new[] { "/?" }),
            ["a switch PadForge does not know"] = new(new[] { "--no-such-switch" }),
            ["--version=1, a value for a switch that takes none"] = new(new[] { "--version=1" }),
            ["--install skill <folder>"] = new(new[] { "--install", "skill", "{folder}" }),
            ["--install assistant claude-code --check"] = new(new[] { "--install", "assistant", "claude-code", "--check" }),
            ["--summarize"] = new(new[] { "--summarize", "{recording}" }),
            ["--record save"] = new(new[] { "--record", "save", "{recording}", "{folder}\\copy.zip" }),
            ["--plugin-kit"] = new(new[] { "--plugin-kit", "{folder}\\kit" },
                After: run => Assert.True(File.Exists(Path.Combine(run.Folder, "kit", "README.md")), "the child wrote no kit")),
            ["--settings-folder"] = new(new[] { "--settings-folder" }),
            ["--web, no copy"] = new(new[] { "--web" }),
            ["--web, a copy, no key"] = new(new[] { "--web" }, CopyRuns: true, CompareWords: false, Code: WebHelper.Refused,
                Error: "PadForge starts its web helper itself"),
            ["--tools"] = new(new[] { "--tools" }, CopyRuns: true),
            ["--tools --json"] = new(new[] { "--tools", "--json" }, CopyRuns: true),
            ["--call list_controllers --json"] = new(new[] { "--call", "list_controllers", "--json" }, CopyRuns: true),
            ["--call set, then the value read back"] = new(new[] { "--call", "set", "{\"id\":\"pad/Pad_Deadzone@c1\",\"value\":35}", "--json" }, CopyRuns: true,
                CompareWords: false, After: run =>
                {
                    Assert.Equal(35, run.Rig.Settings.Number);
                    var read = AssistantCommandLineRun(run.Rig, "--call", "read", "{\"id\":\"pad/Pad_Deadzone@c1\"}", "--json");
                    Assert.Equal(35, System.Text.Json.JsonDocument.Parse(read).RootElement.GetProperty("place").GetProperty("value").GetDouble());
                }),
            ["--call, a tool PadForge does not have"] = new(new[] { "--call", "no_such_tool" }, CopyRuns: true),
            ["--skill"] = new(new[] { "--skill" }),
            ["--skill tools"] = new(new[] { "--skill", "tools" }),
            ["--skill, a file the skill does not have"] = new(new[] { "--skill", "no-such-file" }),
            ["--describe"] = new(new[] { "--describe", "{folder}\\pages" },
                After: run => Assert.True(File.Exists(Path.Combine(run.Folder, "pages", "pages.md")) && File.Exists(Path.Combine(run.Folder, "pages", "pages.json")),
                    "the child wrote no page description")),
            ["--state"] = new(new[] { "--state" }, CopyRuns: true),
            ["--json --state"] = new(new[] { "--json", "--state" }, CopyRuns: true),
            ["--json"] = new(new[] { "--json" }, CopyRuns: true),
            ["--wait --state"] = new(new[] { "--wait", "--state" }, CopyRuns: true),
            ["--profile Racing"] = new(new[] { "--profile", "Racing" }, CopyRuns: true),
            ["--default-profile"] = new(new[] { "--default-profile" }, CopyRuns: true),
            ["--engine on"] = new(new[] { "--engine", "on" }, CopyRuns: true),
            ["--controllers off"] = new(new[] { "--controllers", "off" }, CopyRuns: true),
            ["--slot 2 on"] = new(new[] { "--slot", "2", "on" }, CopyRuns: true),
            ["--minimized"] = new(new[] { "--minimized" }, CopyRuns: true),
            ["--tray"] = new(new[] { "--tray" }, CopyRuns: true),
            ["--maximized"] = new(new[] { "--maximized" }, CopyRuns: true),
            ["--show"] = new(new[] { "--show" }, CopyRuns: true),
            ["--hide"] = new(new[] { "--hide" }, CopyRuns: true),
            ["--exit"] = new(new[] { "--exit" }, CopyRuns: true),
            ["--snapshot"] = new(new[] { "--snapshot" }, CopyRuns: true),
            ["--record start"] = new(new[] { "--record", "start" }, CopyRuns: true),
            ["--record stop"] = new(new[] { "--record", "stop" }, CopyRuns: true),
            ["--record mark"] = new(new[] { "--record", "mark", "the", "stick", "drifts" }, CopyRuns: true),
            ["--updated"] = new(new[] { CommandLineSwitches.UpdatedSwitch }, CopyRuns: true),
            ["the update helper's line"] = new(new[] { UpdateService.ApplyUpdateSwitch, "{folder}\\PadForge.exe", "1234" }),
            ["no switch, a copy running"] = new(Array.Empty<string>(), CopyRuns: true),
            ["no switch, no copy"] = new(Array.Empty<string>()),
        };

        public static IEnumerable<object[]> SwitchRowNames() => SwitchRows.Keys.Select(name => new object[] { name });

        /// <summary>
        /// Each switch of the client run, run as a fenced child of this
        /// process with this process as its debugger (AS-131): it ends with
        /// the code and the words the run gives in this process through the
        /// same seams, and from its first instruction to its last it loads
        /// no part of WPF, by every image Windows reported it mapped and by
        /// everything mapped at its exit event, and shows no window. The
        /// runtime, coreclr.dll and PadForge.dll in those listings show they
        /// read the child.
        /// </summary>
        [UnelevatedTheory]
        [MemberData(nameof(SwitchRowNames))]
        public void EverySwitchOfTheClientRun_EndsAsTheRunHereDoes_AndLoadsNoWpfFromItsStartToItsEnd(string name)
        {
            var row = SwitchRows[name];
            string exe = BuiltExe();
            string folder = TempFolder("switch");
            try
            {
                string recording = MakeRecording(folder);
                string[] words = row.Words.Select(w => w.Replace("{folder}", folder).Replace("{recording}", recording)).ToArray();
                using var rig = row.CopyRuns ? new AssistantRig(AutomationLevel.ChangeSettings) : null;
                string pipe = rig?.PipeName ?? "PadForge.Tests.Nobody." + Guid.NewGuid().ToString("N");

                // The run in this process, through the seams of a test's
                // child: the code and the words the child must give.
                var (code, said, errors) = RunHere(words, pipe);
                // What it wrote goes, and PadForge's side is as it was.
                ClearAllBut(folder, recording);
                if (rig != null)
                {
                    rig.Settings.Number = 8;
                    rig.Settings.Switch = false;
                    rig.Settings.Choice = "Low";
                }

                using var watch = new WindowWatch();
                using var child = new Child(exe, Words(words), new Dictionary<string, string> { [WebHelper.PipeVariable] = pipe }, watch, debugged: true);
                Assert.True(child.Ends(60000), name + " did not end. Standard error: " + child.StandardError);

                int expected = row.Code ?? code;
                Assert.True(expected == child.ExitCode, name + " ended with " + child.ExitCode + " where the run here gave " + expected
                    + ". Standard output: " + child.StandardOutputText + " Standard error: " + child.StandardError);
                if (row.CompareWords)
                {
                    Assert.Equal(Lines(said), Lf(child.StandardOutputText));
                    Assert.Equal(Lines(errors), Lf(child.StandardError));
                }
                if (row.Error != null) Assert.Contains(row.Error, child.StandardError);
                row.After?.Invoke(new SwitchRun(folder, rig, child));

                // No part of WPF from its first instruction to its last.
                Assert.True(child.MappedAtExit != null, "no listing at the exit event: " + child.ExitListingFailure);
                Assert.Contains("System.Private.CoreLib.dll", child.MappedAtExit);
                Assert.Contains("PadForge.dll", child.MappedAtExit);
                Assert.Contains(child.LoadedImages, f => string.Equals(f, "coreclr.dll", StringComparison.OrdinalIgnoreCase));
                NoWpf(child.MappedAtExit, "at its exit event", child);
                NoWpf(child.LoadedImages, "among the images Windows reported it mapped", child);

                // No window from its start to its end.
                watch.Drain();
                Assert.True(watch.Shown(child.Id).Count == 0, name + " showed a window: " + Events(watch, child.Id));
            }
            finally
            {
                RemoveFolder(folder);
            }
        }

        /// <summary>The run in this process, through the seams a test's child
        /// takes (ClientRun.Seams.ForThisRun), with what it says caught. It
        /// serves no assistant and no web helper here: those run in the
        /// child alone.</summary>
        private static (int Code, List<string> Said, List<string> Errors) RunHere(string[] words, string pipe)
        {
            var seams = ClientRun.Seams.ForThisRun(ChildVariables(pipe));
            Assert.True(seams.TestsChild);
            var said = new List<string>();
            var errors = new List<string>();
            seams.Say = said.Add;
            seams.Tell = said.Add;
            seams.SayError = errors.Add;
            seams.TellError = errors.Add;
            seams.ServeAssistant = () => throw new InvalidOperationException("--mcp runs in the tests that drive it through the SDK's client");
            seams.ServeWeb = () => WebHelper.Refused;
            Assert.True(ClientRun.TryRun(words, out int code, seams), "a test's run went on to become the app");
            return (code, said, errors);
        }

        /// <summary>A command line run here against a rig, for a row's check
        /// of what the child changed: its standard output.</summary>
        private static string AssistantCommandLineRun(AssistantRig rig, params string[] words)
        {
            var (code, said, errors) = RunHere(words, rig.PipeName);
            Assert.True(code == ClientRun.Done, string.Join(" ", words) + " gave " + code + ": " + string.Join(" ", errors));
            return string.Join("\n", said);
        }

        /// <summary>Text with every line ended by LF, as the run writes each
        /// answer with CRLF after it.</summary>
        private static string Lf(string text) => text.Replace("\r\n", "\n");

        private static string Lines(IEnumerable<string> said) => string.Concat(said.Select(s => Lf(s) + "\n"));

        /// <summary>A finished recording's form, a zip of one .log, holding
        /// one summary line (RecordingReader reads the first line and the
        /// summary part).</summary>
        private static string MakeRecording(string folder)
        {
            string zip = Path.Combine(folder, "PadForge-test-recording.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("PadForge-test-recording.log").Open(), new UTF8Encoding(false)))
            {
                writer.Write(RecordingCodec.FirstLine + "\n" + RecordingCodec.SummaryHeader + "\nA planted line the summary holds.\n");
            }
            return zip;
        }

        private static void ClearAllBut(string folder, string keep)
        {
            foreach (string file in Directory.GetFiles(folder))
                if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
            foreach (string sub in Directory.GetDirectories(folder)) Directory.Delete(sub, true);
        }

        /// <summary>
        /// Every switch the parser has a row for has a row of the table:
        /// each <c>case "--name":</c> and each constant switch of
        /// CommandLineSwitches.Parse, the update helper's switch read before
        /// them, and each verb of <c>--record</c>. <c>--mcp</c> is run by
        /// the tests that drive it through the SDK's client. A switch added
        /// later fails here until its row is.
        /// </summary>
        [Fact]
        public void EverySwitchTheParserKnows_HasARowOfTheTable()
        {
            string source = OffLimitsSource.Read("PadForge.App/Common/CommandLineSwitches.cs");
            var known = new SortedSet<string>(StringComparer.Ordinal);
            // A row of the parser, never a comment that quotes one.
            foreach (Match m in Regex.Matches(source, "^ *case \"(--[^\"]+)\":", RegexOptions.Multiline)) known.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(source, @"case ([A-Z][A-Za-z]*Switch):"))
            {
                var field = typeof(CommandLineSwitches).GetField(m.Groups[1].Value, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.NotNull(field);
                known.Add((string)field.GetRawConstantValue());
            }
            known.Add(UpdateService.ApplyUpdateSwitch);
            Assert.Contains("--version", known);
            Assert.Contains(CommandLineSwitches.ProfileSwitch, known);

            var rowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in SwitchRows.Values)
                foreach (string word in row.Words.Where(w => w.StartsWith("-", StringComparison.Ordinal) || w.StartsWith("/", StringComparison.Ordinal)))
                    rowed.Add(word == UpdateService.ApplyUpdateSwitch ? word : CommandLineSwitches.NameOf(word, out _));
            var elsewhere = new[] { "--mcp" };
            var missing = known.Where(k => !rowed.Contains(k) && !elsewhere.Contains(k)).ToList();
            Assert.True(missing.Count == 0, "switches with no row in the table: " + string.Join(", ", missing));

            // The verbs of --record, as its own switch reads them.
            int start = source.IndexOf("case \"--record\":", StringComparison.Ordinal);
            int end = source.IndexOf("--record needs start, stop or mark.", start, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, "the --record row is not where this test reads it");
            var verbs = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(source.Substring(start, end - start), "case (?:\"([a-z]+)\"|Recording\\.RecordingSave\\.Verb):"))
                verbs.Add(m.Groups[1].Success ? m.Groups[1].Value : PadForge.Common.Recording.RecordingSave.Verb);
            var rowedVerbs = new SortedSet<string>(SwitchRows.Values.Where(r => r.Words.Length > 1 && r.Words[0] == "--record").Select(r => r.Words[1]), StringComparer.Ordinal);
            Assert.Equal(verbs, rowedVerbs);
        }
    }
}
