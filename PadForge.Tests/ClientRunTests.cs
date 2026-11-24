using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Threading.Tasks;
using PadForge.Common;
using PadForge.Services;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A start of PadForge.exe that is not the app (#597): it hands its
    /// switches to the running copy, prints what the copy answers and exits
    /// with a code a script can read. Every way out to the machine is a seam
    /// here, so nothing reaches a PadForge that runs on the PC.
    /// </summary>
    public class ClientRunTests
    {
        /// <summary>A run's seams, recording what was said and sent.</summary>
        private sealed class Run
        {
            public readonly List<string> Sent = new List<string>();
            public readonly List<string> Said = new List<string>();
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Told = new List<string>();
            public readonly List<string> ToldErrors = new List<string>();
            public readonly List<string> Order = new List<string>();
            public bool Copy = true;
            public bool Pipe = true;
            public bool WindowShows = true;
            public int Shown;
            public int Lent;
            public int Served;
            public int Settles;
            public RunningCopy.Settled Settled = RunningCopy.Settled.Ready;
            public Func<string, string> Answer = line => "ok";
            public Func<int?> Wait;
            public readonly ClientRun.Seams Seams;

            public Run()
            {
                Seams = new ClientRun.Seams
                {
                    CopyRuns = () => Copy,
                    Exchange = line =>
                    {
                        Order.Add("send " + line);
                        if (!Pipe) return null;
                        Sent.Add(line);
                        return Answer(line);
                    },
                    PipeStands = () => Pipe,
                    Settle = () => { Settles++; return Settled; },
                    ShowWindow = () => { Shown++; Order.Add("show"); return WindowShows; },
                    LendForeground = () => { Lent++; Order.Add("lend"); },
                    Watch = () => { Order.Add("watch"); return Wait; },
                    Say = Said.Add,
                    SayError = Errors.Add,
                    Tell = Told.Add,
                    TellError = ToldErrors.Add,
                    VersionLine = () => "PadForge 9.9.9",
                    ServeAssistant = () => { Served++; return 7; },
                };
            }

            public (bool Done, int Code) Go(params string[] args)
            {
                bool done = ClientRun.TryRun(args, out int code, Seams);
                return (done, code);
            }
        }

        // ── No copy runs ──

        [Theory]
        [InlineData("")]
        [InlineData("--profile Racing")]
        [InlineData("--tray --engine off")]
        [InlineData("--show")]
        [InlineData("--record start")]
        [InlineData("--wait")]
        [InlineData("--updated --minimized")]
        public void WithNoCopy_AStartingSwitchGoesOnToBecomeTheApp(string line)
        {
            var args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var run = new Run { Copy = false };
            var (done, code) = run.Go(args);
            Assert.False(done);
            Assert.Equal(ClientRun.Done, code);
            Assert.Empty(run.Sent);
            Assert.Empty(run.Order);
            Assert.Empty(run.Said.Concat(run.Errors).Concat(run.Told).Concat(run.ToldErrors));
        }

        [Theory]
        [InlineData("--exit")]
        [InlineData("--hide")]
        [InlineData("--state")]
        [InlineData("--snapshot")]
        [InlineData("--record stop")]
        [InlineData("--record mark")]
        [InlineData("--profile Racing --exit")]
        public void WithNoCopy_ASwitchThatNeedsOne_Gives4(string line)
        {
            var args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var run = new Run { Copy = false };
            var (done, code) = run.Go(args);
            Assert.True(done);
            Assert.Equal(4, code);
            Assert.Equal(ClientRun.NotRunning, code);
            Assert.Equal(new[] { ClientRun.NotRunningText }, run.Errors);
            Assert.Empty(run.Sent);
            // An answer, never a window: a launcher sent this.
            Assert.Empty(run.ToldErrors);
        }

        // ── A copy runs ──

        [Fact]
        public void WithACopy_NoSwitchShowsItsWindow()
        {
            foreach (var args in new[] { Array.Empty<string>(), new[] { "--updated" } })
            {
                var run = new Run();
                var (done, code) = run.Go(args);
                Assert.True(done);
                Assert.Equal(ClientRun.Done, code);
                Assert.Equal(1, run.Shown);
                Assert.Empty(run.Sent);
                Assert.Empty(run.Said.Concat(run.Errors));
            }

            // A copy that never took the message is an error to a script.
            var deaf = new Run { WindowShows = false };
            Assert.Equal((true, ClientRun.AnsweredAnError), deaf.Go());
            Assert.Equal(new[] { ClientRun.NoAnswerText }, deaf.Errors);
        }

        [Fact]
        public void WithACopy_EachSwitchIsSentAsItsLine_AndItsAnswerPrinted()
        {
            var run = new Run { Answer = line => line.StartsWith("activate ", StringComparison.Ordinal) ? "ok Racing" : "ok on" };
            var (done, code) = run.Go("--profile", "racing", "--engine", "on", "--tray", "--minimized");
            Assert.True(done);
            Assert.Equal(0, code);
            Assert.Equal(new[] { "activate racing", "engine on", "hide", "minimize" }, run.Sent);
            Assert.Equal(new[] { "ok Racing", "ok on", "ok on", "ok on" }, run.Said);
            Assert.Empty(run.Errors);
            Assert.Equal(0, run.Shown);
        }

        [Fact]
        public void State_PrintsTheJsonAlone()
        {
            const string json = "{\"version\":\"6.0.1\",\"engine\":\"running\"}";
            var run = new Run { Answer = _ => "ok " + json };
            Assert.Equal((true, 0), run.Go("--state"));
            Assert.Equal(new[] { json }, run.Said);

            // Every other answer is printed as the pipe gave it.
            var other = new Run { Answer = _ => "ok diag-snapshot-20261123-150000.log" };
            other.Go("--snapshot");
            Assert.Equal(new[] { "ok diag-snapshot-20261123-150000.log" }, other.Said);
        }

        [Fact]
        public void AnErrorAnswer_Gives1()
        {
            var run = new Run { Answer = _ => "error unknown-profile" };
            Assert.Equal((true, 1), run.Go("--profile", "Nope"));
            Assert.Equal(new[] { "error unknown-profile" }, run.Errors);
            Assert.Empty(run.Said);
        }

        [Fact]
        public void NotAllowed_Gives5()
        {
            var run = new Run { Answer = _ => ClientRun.NotAllowedReply };
            Assert.Equal((true, 5), run.Go("--engine", "off"));
            Assert.Equal(ClientRun.NotAllowed, 5);
            Assert.Equal(new[] { "error not-allowed" }, run.Errors);
            Assert.Equal(AutomationCommands.NotAllowed, ClientRun.NotAllowedReply);
        }

        [Fact]
        public void StepsStopAtTheFirstFailure()
        {
            var run = new Run { Answer = line => line == "engine on" ? "error busy" : "ok" };
            Assert.Equal((true, 1), run.Go("--controllers", "off", "--engine", "on", "--profile", "Racing", "--wait"));
            Assert.Equal(new[] { "controllers off", "engine on" }, run.Sent);
            Assert.Equal(new[] { "ok" }, run.Said);
            Assert.Equal(new[] { "error busy" }, run.Errors);
        }

        // ── Automation off ──

        [Fact]
        public void WithAutomationOff_ASwitchGives5_AndSaysWhereToTurnItOn()
        {
            var run = new Run { Pipe = false };
            Assert.Equal((true, 5), run.Go("--profile", "Racing"));
            Assert.Equal(new[] { ClientRun.AutomationOffText }, run.Errors);
            Assert.Empty(run.Sent);
            // The copy was asked whether it had finished starting, and had.
            Assert.Equal(1, run.Settles);
        }

        [Fact]
        public void WithAutomationOff_ShowStillWorks()
        {
            var run = new Run { Pipe = false };
            Assert.Equal((true, 0), run.Go("--show"));
            Assert.Equal(1, run.Shown);
            Assert.Equal(new[] { "ok" }, run.Said);
            Assert.Empty(run.Errors);

            // And where the pipe serves plugins alone and refuses the line.
            var refused = new Run { Answer = _ => ClientRun.NotAllowedReply };
            Assert.Equal((true, 0), refused.Go("--show"));
            Assert.Equal(1, refused.Shown);
            Assert.Equal(new[] { "ok" }, refused.Said);
        }

        /// <summary>The show line cannot carry the right to come to the
        /// front, so it is lent before the line goes out.</summary>
        [Fact]
        public void Show_LendsTheForegroundBeforeItsLine()
        {
            var run = new Run();
            Assert.Equal((true, 0), run.Go("--show"));
            Assert.Equal(new[] { "lend", "send show" }, run.Order);
            Assert.Equal(0, run.Shown);

            var other = new Run();
            other.Go("--engine", "on");
            Assert.Equal(0, other.Lent);
        }

        // ── A copy that is starting or closing ──

        [Fact]
        public void ACopyStillStarting_IsWaitedFor_AndTheLineSentAgain()
        {
            var run = new Run { Pipe = false };
            // The pipe comes up while the client waits.
            run.Seams.Settle = () => { run.Settles++; run.Pipe = true; return RunningCopy.Settled.Ready; };
            Assert.Equal((true, 0), run.Go("--profile", "Racing"));
            Assert.Equal(new[] { "activate Racing" }, run.Sent);
            Assert.Equal(new[] { "send activate Racing", "send activate Racing" }, run.Order);
            Assert.Empty(run.Errors);
        }

        [Fact]
        public void ACopyThatClosedFirst_IsNoCopy()
        {
            // A line that can start PadForge goes on to become the app.
            var starts = new Run { Pipe = false, Settled = RunningCopy.Settled.Gone };
            Assert.Equal((false, 0), starts.Go("--profile", "Racing"));
            Assert.Empty(starts.Errors);

            // One that cannot says nothing runs.
            var needs = new Run { Pipe = false, Settled = RunningCopy.Settled.Gone };
            Assert.Equal((true, 4), needs.Go("--state"));
            Assert.Equal(new[] { ClientRun.NotRunningText }, needs.Errors);

            // After a line went out there is no starting over.
            var midway = new Run();
            midway.Seams.Exchange = line =>
            {
                if (line == "engine on") { midway.Pipe = false; return null; }
                midway.Sent.Add(line);
                return "ok";
            };
            midway.Settled = RunningCopy.Settled.Gone;
            Assert.Equal((true, 4), midway.Go("--profile", "Racing", "--engine", "on"));
            Assert.Equal(new[] { "activate Racing" }, midway.Sent);
        }

        [Fact]
        public void ACopyThatNeverSettles_IsAnsweredByItsPipe()
        {
            var run = new Run { Pipe = false, Settled = RunningCopy.Settled.Starting };
            Assert.Equal((true, 5), run.Go("--engine", "on"));
            Assert.Equal(new[] { ClientRun.AutomationOffText }, run.Errors);
        }

        [Fact]
        public void APipeThatStandsAndSaysNothing_Gives1()
        {
            var run = new Run { Answer = _ => null };
            Assert.Equal((true, 1), run.Go("--engine", "on"));
            Assert.Equal(new[] { ClientRun.NoAnswerText }, run.Errors);
            Assert.Equal(0, run.Settles);
        }

        // ── --wait ──

        [Fact]
        public void Wait_ReturnsTheCopysExitCode()
        {
            var run = new Run { Wait = () => 42 };
            Assert.Equal((true, 42), run.Go("--wait"));
            Assert.Empty(run.Sent);

            // The copy is taken hold of before the line that closes it.
            var exits = new Run { Wait = () => 0 };
            Assert.Equal((true, 0), exits.Go("--exit", "--wait"));
            Assert.Equal(new[] { "watch", "send exit" }, exits.Order);

            // A copy that cannot be watched leaves the lines' own result.
            var blind = new Run { Wait = null };
            Assert.Equal((true, 0), blind.Go("--profile", "Racing", "--wait"));
            var unknown = new Run { Wait = () => null };
            Assert.Equal((true, 0), unknown.Go("--wait"));

            // Nothing is watched without the switch.
            var plain = new Run { Wait = () => 9 };
            Assert.Equal((true, 0), plain.Go("--profile", "Racing"));
            Assert.DoesNotContain("watch", plain.Order);
        }

        // ── What needs no copy ──

        [Fact]
        public void HelpAndVersion_NeedNothing_AndHaveToBeSeen()
        {
            foreach (bool copy in new[] { true, false })
            {
                var help = new Run { Copy = copy, Pipe = false };
                Assert.Equal((true, 0), help.Go("--help"));
                Assert.Equal(new[] { CommandLineSwitches.HelpText }, help.Told);

                var version = new Run { Copy = copy, Pipe = false };
                Assert.Equal((true, 0), version.Go("--version"));
                Assert.Equal(new[] { "PadForge 9.9.9" }, version.Told);
                Assert.Empty(version.Sent);
                Assert.Equal(0, version.Shown);
            }

            // Help wins over whatever stands beside it, and nothing is sent.
            var both = new Run();
            Assert.Equal((true, 0), both.Go("--exit", "--help"));
            Assert.Empty(both.Sent);
        }

        [Fact]
        public void AWrongCommandLine_Gives2_SaysWhy_AndSendsNothing()
        {
            foreach (bool copy in new[] { true, false })
            {
                var run = new Run { Copy = copy };
                Assert.Equal((true, 2), run.Go("--exit", "--frobnicate"));
                Assert.Equal(ClientRun.WrongCommandLine, 2);
                string said = Assert.Single(run.ToldErrors);
                Assert.StartsWith("PadForge does not know the switch --frobnicate.", said, StringComparison.Ordinal);
                Assert.EndsWith(CommandLineSwitches.HelpText, said, StringComparison.Ordinal);
                Assert.Empty(run.Sent);
            }
        }

        [Fact]
        public void Mcp_ServesTheAssistant_WhetherOrNotACopyRuns()
        {
            foreach (bool copy in new[] { true, false })
            {
                var run = new Run { Copy = copy };
                Assert.Equal((true, 7), run.Go("--mcp"));
                Assert.Equal(1, run.Served);
                Assert.Empty(run.Sent);
            }
        }

        /// <summary>--mcp runs the copy an assistant starts, the mirror of
        /// PadForge's tools (AutomationAssistantMirrorTests holds what it
        /// does).</summary>
        [Fact]
        public void TheAssistantConnection_IsTheMirror()
        {
            string source = OffLimitsSource.Read("PadForge.App/Services/ClientRun.cs");
            Assert.Contains("public Func<int> ServeAssistant = AssistantMirror.Run;", source, StringComparison.Ordinal);
        }

        [Fact]
        public void TheUpdateHelpersLine_IsNotAClientRun()
        {
            var run = new Run();
            Assert.Equal((false, 0), run.Go(UpdateService.ApplyUpdateSwitch, @"C:\PadForge\PadForge.exe", "1234"));
            Assert.Empty(run.Order);
        }

        // ── The pipe's client end, against a pipe of this test's own ──

        private static string OwnPipe(string tag) => "PadForge.Tests.Client." + tag + "." + Guid.NewGuid().ToString("N");

        [Fact]
        public void TheClient_SendsOneLine_AndReadsOneAnswerOfAnyLength()
        {
            string pipe = OwnPipe("long");
            string longAnswer = "ok " + new string('x', 20000) + " caf\u00e9";
            string seen = null;
            using var service = new ExternalControlService(line =>
            {
                seen = line;
                return longAnswer;
            }, pipe);
            Assert.Equal(ExternalControlService.PipeState.Serving, service.Start());

            Assert.True(ControlPipeClient.Stands(pipe));
            Assert.Equal(longAnswer, ControlPipeClient.Exchange(pipe, "state", 5000));
            Assert.Equal("state", seen);
            // And again: the name is still there for the next command.
            Assert.Equal(longAnswer, ControlPipeClient.Exchange(pipe, "activate Caf\u00e9", 5000));
            Assert.Equal("activate Caf\u00e9", seen);
        }

        [Fact]
        public void APipeThatIsNotThere_DoesNotStand_AndAnswersNothing()
        {
            string pipe = OwnPipe("none");
            Assert.False(ControlPipeClient.Stands(pipe));
            Assert.Null(ControlPipeClient.Exchange(pipe, "query", 200));
        }

        /// <summary>A copy that takes a line and never answers does not keep
        /// its caller: the client gives up at its deadline and has no
        /// answer.</summary>
        [Fact]
        public async Task ACopyThatTakesALineAndSaysNothing_IsGivenUpOn()
        {
            Assert.Equal(60000, ControlPipeClient.AnswerMs);
            string pipe = OwnPipe("mute");
            using var mute = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var accepted = mute.WaitForConnectionAsync();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Assert.Null(await Task.Run(() => ControlPipeClient.Exchange(pipe, "engine on", 2000, 300)));
            Assert.True(clock.ElapsedMilliseconds < 5000, clock.ElapsedMilliseconds + " ms");
            await accepted;
        }

        /// <summary>The test host neither looks for nor writes to the pipe a
        /// running PadForge serves, whatever case the name is in.</summary>
        [Fact]
        public void TheAppsOwnPipe_IsNeverReachedFromTheTestHost()
        {
            string client = OffLimitsSource.Read("PadForge.App/Services/ClientRun.cs");
            FenceSource.AsksFirst("PadForge.App/Services/ClientRun.cs", "internal static bool Stands(string pipeName)",
                "if (ExternalControlService.OffLimitsForTest && IsTheAppsOwn(pipeName)) return false;");
            FenceSource.AsksFirst("PadForge.App/Services/ClientRun.cs", "internal static string Exchange(string pipeName, string line, int connectMs, int answerMs = AnswerMs)",
                "if (ExternalControlService.OffLimitsForTest && IsTheAppsOwn(pipeName)) return null;");
            Assert.Contains("=> string.Equals(pipeName, ExternalControlService.PipeName, StringComparison.OrdinalIgnoreCase);", client, StringComparison.Ordinal);
            new TestHostFenceTests().EveryFence_IsSetInTheTestHost();

            foreach (string name in new[] { ExternalControlService.PipeName, "padforge.control", "PADFORGE.CONTROL" })
            {
                Assert.False(ControlPipeClient.Stands(name));
                Assert.Null(ControlPipeClient.Exchange(name, "query", 200));
            }
            Assert.False(ControlPipeClient.Stands());
            Assert.Null(ControlPipeClient.Exchange("query"));
        }
    }
}
