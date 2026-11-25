using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using PadForge.Services;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The problem recorder joined to Automation's command set (#597): what
    /// the <c>record</c> lines and a starting copy's <c>--record</c> do to a
    /// real recorder, through the host's entries the window sets
    /// (<c>MainWindow.ConnectRecorder</c>).
    /// </summary>
    public class ProblemRecorderAutomationTests
    {
        private sealed class Joined : IDisposable
        {
            public readonly RecorderTestHost RecorderHost = new RecorderTestHost();
            public readonly ProblemRecorder Recorder;
            public readonly AutomationHost Host;
            public readonly List<RecorderResult> Refused = new List<RecorderResult>();
            private readonly ManualResetEventSlim _finished = new ManualResetEventSlim(false);
            public string FinishedPath;

            public Joined()
            {
                RecorderHost.DeviceList.Add(RecorderTestKit.Pad());
                Recorder = new ProblemRecorder(RecorderHost) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
                Recorder.Finished += path => { FinishedPath = path; _finished.Set(); };
                // A pipe name is one for the whole PC, so the host serves a
                // name of this test's own.
                Host = TestConsent.AllowAll(new AutomationHost(new RecordingAutomationTarget(), new InlineAutomationUi(),
                    "PadForge.Tests.Recorder." + Guid.NewGuid().ToString("N")));
                Host.Apply(true, AutomationLevel.Commands);
                MainWindow.ConnectRecorder(Host, Recorder, Refused.Add);
            }

            public string Line(string line) => Host.Commands.Answer(line, CancellationToken.None).Line;

            public bool StateRecording()
            {
                string line = Line("state");
                Assert.StartsWith("ok ", line);
                using var json = JsonDocument.Parse(line.Substring(3));
                return json.RootElement.GetProperty("recording").GetBoolean();
            }

            public string WaitFinished()
            {
                Assert.True(_finished.Wait(TimeSpan.FromSeconds(30)), "the recording was not finished");
                return FinishedPath;
            }

            public void Dispose()
            {
                if (Recorder.State.Recording) Recorder.Stop(wait: true);
                Recorder.KeepLastMinute = false;
                Host.Dispose();
                RecorderHost.Dispose();
                _finished.Dispose();
            }
        }

        [Fact]
        public void RecordStart_SaysItRecords_MarkWritesTheMarker_AndStopSaysItWaits_NeverWhere()
        {
            using var joined = new Joined();
            Assert.False(joined.StateRecording());

            // A program is told that a recording runs, then that it waits for
            // the user's review, and never its file (#600).
            Assert.Equal("ok recording", joined.Line("record start"));
            Assert.True(joined.Recorder.State.Recording);
            Assert.True(joined.StateRecording());

            Assert.Equal("ok recording", joined.Line("record mark lap three"));
            Assert.Equal("ok recording", joined.Line("record mark"));

            Assert.Equal("ok " + ProblemRecorder.WaitsWord, joined.Line("record stop"));
            string finished = joined.WaitFinished();
            Assert.True(File.Exists(finished));
            using (var reader = PadForge.Engine.Recording.RecordingReader.Open(finished))
                Assert.Equal("Status: finished. A program or --record stop stopped it.", reader.StatusLine);
            Assert.False(joined.Recorder.State.Recording);
            Assert.False(joined.StateRecording());

            var marks = RecorderTestKit.Events(finished).Where(e => e.Layer == 'M' && e.Payload.StartsWith("mark", StringComparison.Ordinal)).ToList();
            Assert.Equal(new[] { "mark lap three", "mark" }, marks.Select(e => e.Payload));
            Assert.Empty(joined.Refused);
        }

        [Fact]
        public void TheRecordersRefusals_ComeBackAsBusyOrNotAvailable()
        {
            using var joined = new Joined();
            // Nothing records and nothing is kept: there is nothing to stop
            // or mark.
            Assert.Equal("error not-available", joined.Line("record stop"));
            Assert.Equal("error not-available", joined.Line("record mark"));

            Assert.StartsWith("ok ", joined.Line("record start"));
            // A second start while one runs.
            Assert.Equal("error busy", joined.Line("record start"));
            Assert.StartsWith("ok ", joined.Line("record stop"));
            joined.WaitFinished();
            Assert.Empty(joined.Refused);
        }

        [Fact]
        public void AStartTheCheckRefuses_IsNotAvailable_AndTheWindowIsTold()
        {
            using var joined = new Joined();
            // The scrubber lets every value through, so the check at the
            // start finds this PC's names in what it would write.
            joined.RecorderHost.CheckScrub = line => line;
            Assert.Equal("error not-available", joined.Line("record start"));
            Assert.False(joined.Recorder.State.Recording);
            var refused = Assert.Single(joined.Refused);
            Assert.Equal(RecorderRefusal.Privacy, refused.Refusal);
            Assert.Empty(Directory.GetFiles(joined.RecorderHost.Dir));
        }

        [Fact]
        public void AMarkWithOnlyTheLastMinuteKept_IsOk_WithNoFile()
        {
            using var joined = new Joined();
            joined.Recorder.KeepLastMinute = true;
            joined.Recorder.WaitForFlush();
            Assert.Equal("ok", joined.Line("record mark"));
            Assert.False(joined.StateRecording());
        }

        [Fact]
        public void AStartingCopysRecordStart_RunsThroughTheSameCommands()
        {
            using var joined = new Joined();
            // The window runs a starting copy's steps with RunOwn, which
            // asks no level.
            joined.Host.Apply(false, AutomationLevel.Commands);
            string start = joined.Host.Commands.RunOwn("record start");
            Assert.Equal("ok recording", start);
            Assert.True(joined.Recorder.State.Recording);
            Assert.StartsWith("ok ", joined.Host.Commands.RunOwn("record stop"));
            joined.WaitFinished();
        }

        [Theory]
        [InlineData((int)RecorderRefusal.AlreadyRecording, (int)AutomationRecordOutcome.Busy)]
        [InlineData((int)RecorderRefusal.Busy, (int)AutomationRecordOutcome.Busy)]
        [InlineData((int)RecorderRefusal.NotRecording, (int)AutomationRecordOutcome.NotAvailable)]
        [InlineData((int)RecorderRefusal.Privacy, (int)AutomationRecordOutcome.NotAvailable)]
        [InlineData((int)RecorderRefusal.File, (int)AutomationRecordOutcome.NotAvailable)]
        [InlineData((int)RecorderRefusal.NothingKept, (int)AutomationRecordOutcome.NotAvailable)]
        public void EveryRefusal_HasItsWord(int refusal, int outcome)
            => Assert.Equal((AutomationRecordOutcome)outcome, MainWindow.RecordRefusal((RecorderRefusal)refusal).Outcome);

        [Fact]
        public void TheWindow_JoinsTheRecorder_BeforeTheStartsRecordStepsRun()
        {
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            string automation = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.Automation.cs");
            // The recorder is joined to the window, then the host is made and
            // handed the recorder, then the steps run.
            int joinedToWindow = window.IndexOf("InitializeProblemRecorder();", StringComparison.Ordinal);
            int hostMade = window.IndexOf("StartAutomationHost();", StringComparison.Ordinal);
            int afterEngine = window.IndexOf("RunStartSteps(CommandLineSwitches.StartPhase.AfterEngine)", StringComparison.Ordinal);
            Assert.True(joinedToWindow > 0 && hostMade > joinedToWindow && afterEngine > hostMade);

            int made = automation.IndexOf("_automation = new AutomationHost(", StringComparison.Ordinal);
            int handed = automation.IndexOf("ConnectProblemRecorderToAutomation();", StringComparison.Ordinal);
            Assert.True(made > 0 && handed > made);
            Assert.Equal(1, automation.Split("ConnectProblemRecorderToAutomation();").Length - 1);
        }
    }
}
