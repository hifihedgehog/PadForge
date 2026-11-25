using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PadForge.Common;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Privacy;
using PadForge.Engine.Recording;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Read Your Recording (#600): a finished recording read part by part,
    /// its sizes, its scrub check of the parts kept, a copy with parts left
    /// out, Save… as the user, Copy Summary and Delete, a snapshot through
    /// the same review, and --summarize.
    /// </summary>
    [Collection("RecordingFolderStatics")]
    public class RecordingReviewTests
    {
        private static (RecorderTestHost Host, string Zip) Recorded(Action<RecorderTestHost> fill = null, RecordingParts parts = RecordingParts.All)
        {
            var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad("Wireless Controller");
            host.DeviceList.Add(pad);
            host.System.Add("Windows build 26200");
            host.SettingsPart.Add("assign device=D1 controller=C1");
            host.Lines.Add((System.Diagnostics.Stopwatch.GetTimestamp(), "HEARTBEAT planted-word-zz load=1"));
            fill?.Invoke(host);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp(), Parts = parts };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            var state = RecorderTestKit.Rest();
            for (int i = 0; i < 40; i++)
            {
                state.Axis[0] = 30000 + i * 50;
                capture.Input(pad, state);
                capture.OutGamepad(0, new Gamepad { ThumbLX = (short)(i * 100) });
                capture.Advance(0.02);
            }
            recorder.Mark("the stick drifts");
            string zip = recorder.Stop(wait: true).Path;
            return (host, zip);
        }

        [Fact]
        public void AReview_CutsTheFileIntoItsParts_AndShowsEachLineAsItIsShared()
        {
            var (host, zip) = Recorded();
            using var owned = host;
            using var review = RecordingReview.Open(zip);
            Assert.Equal(RecordingParts.All, review.Recorded);
            Assert.Equal(ProblemRecorder.HowUser, review.Ending, ignoreCase: true);
            var input = review.LinesOf(RecordingParts.Input);
            Assert.NotEmpty(input);
            Assert.All(input, line => Assert.Contains(" I D", line));
            Assert.Contains(input, line => line.Contains("LeftX=30050", StringComparison.Ordinal));
            Assert.Contains(review.LinesOf(RecordingParts.Output), line => line.Contains("LeftX=100", StringComparison.Ordinal));
            Assert.Contains(review.LinesOf(RecordingParts.DiagnosticsLines), line => line.Contains("HEARTBEAT", StringComparison.Ordinal));
            Assert.Contains(review.LinesOf(RecordingParts.SettingsAndDevices), line => line.StartsWith("D1 ", StringComparison.Ordinal));
            Assert.Contains(review.LinesOf(RecordingReview.Summary), line => line == RecordingCodec.SummaryHeader);
            // The parts' text adds up to no more than the whole, and the zip
            // on disk is the size of the file.
            Assert.True(review.Parts.Sum(p => p.TextBytes) < review.TextBytes);
            Assert.Equal(new FileInfo(zip).Length, review.ZipBytes);
            // Each part packed on its own: a few bytes of packing over a tiny
            // part, far less than the text of a large one.
            Assert.All(review.Parts.Where(p => p.TextBytes > 0), p => Assert.True(p.ZipBytes > 0));
            Assert.All(review.Parts.Where(p => p.TextBytes > 2000), p => Assert.True(p.ZipBytes < p.TextBytes));
            Assert.Equal(TimeSpan.FromSeconds(0.8), review.Length);
        }

        [Fact]
        public void TheReviewsUnpackedText_GoesWithTheReview()
        {
            var (host, zip) = Recorded();
            using var owned = host;
            var review = RecordingReview.Open(zip);
            Assert.Single(Directory.GetFiles(host.Dir, "*" + ProblemRecorder.TempSuffix));
            review.Dispose();
            Assert.Empty(Directory.GetFiles(host.Dir, "*" + ProblemRecorder.TempSuffix));
        }

        [Fact]
        public void APartTurnedOff_MakesACopyWithoutIt_WithItsOwnHoldsSummaryCheckAndHash()
        {
            var (host, zip) = Recorded();
            using var owned = host;
            using var review = RecordingReview.Open(zip);
            review.PartOf(RecordingParts.Input).Keep = false;
            review.PartOf(RecordingParts.SettingsAndDevices).Keep = false;
            Assert.False(review.KeepsAll);
            Assert.True(review.KeptSize().Text < review.TextBytes);

            string copy = review.WriteKept(new List<(PrivateKind, string)>(), host.Environment());
            Assert.NotEqual(zip, copy);
            Assert.EndsWith(RecordingReview.CopySuffix, copy);
            using (var reader = RecordingReader.Open(copy))
            {
                Assert.Equal(RecordingParts.All & ~(RecordingParts.Input | RecordingParts.SettingsAndDevices), reader.Parts);
                Assert.StartsWith("Status: finished. The user stopped it", reader.StatusLine);
                Assert.Contains(reader.ScrubCheck, l => l.Contains("these parts were left out of this copy: settings and devices, input", StringComparison.Ordinal));
                Assert.NotEmpty(reader.Summary);
                while (reader.Read()) Assert.NotEqual(RecordingCodec.LayerInput, reader.Layer);
                Assert.True(reader.Ended && reader.HasHash && reader.HashMatches);
                // The device keeps its shape, which a replay needs, and
                // nothing else.
                var device = reader.DeviceByNumber(1);
                Assert.Equal("gamepad", device.Fact("controls"));
                Assert.Equal(string.Empty, device.Fact("name"));
                Assert.DoesNotContain(reader.SystemPart, l => l.Contains("26200", StringComparison.Ordinal));
            }
            // The copy waits for no review, and the sweep takes one left
            // behind.
            Assert.DoesNotContain(RecordingStore.Waiting(host.Dir), w => w.Path == copy);
            File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddDays(-8));
            File.SetLastWriteTimeUtc(zip, DateTime.UtcNow);
            RecordingStore.Sweep(host.Dir, DateTime.UtcNow, ProblemRecorder.KeptUnsaved);
            Assert.False(File.Exists(copy));
            Assert.True(File.Exists(zip));
        }

        [Fact]
        public void ACopyWithSettingsAndDevicesLeftOut_StillReplays()
        {
            var (host, zip) = Recorded();
            using var owned = host;
            using var review = RecordingReview.Open(zip);
            review.PartOf(RecordingParts.SettingsAndDevices).Keep = false;
            string copy = review.WriteKept(new List<(PrivateKind, string)>(), host.Environment());
            var replay = PadForge.Services.Plugins.ReplayFile.Read(copy, out string error);
            Assert.Null(error);
            Assert.Single(replay.Devices);
        }

        [Fact]
        public void ARecordingMadeWithSettingsAndDevicesLeftOut_StillReplays()
        {
            var (host, zip) = Recorded(parts: RecordingParts.All & ~RecordingParts.SettingsAndDevices);
            using var owned = host;
            var replay = PadForge.Services.Plugins.ReplayFile.Read(zip, out string error);
            Assert.Null(error);
            Assert.Single(replay.Devices);
        }

        [Fact]
        public void TheScrubCheck_FindsThisPcsValueInAKeptPart_AndPassesWhenThatPartIsOff()
        {
            var (host, zip) = Recorded();
            using var owned = host;
            using var review = RecordingReview.Open(zip);
            // A value this PC holds now that the recording never knew, in a
            // diagnostics line.
            var live = new List<(PrivateKind, string)> { (PrivateKind.DeviceName, "planted-word-zz") };
            Assert.Equal(1, review.Check(live).Found);
            review.PartOf(RecordingParts.DiagnosticsLines).Keep = false;
            Assert.Equal(0, review.Check(live).Found);
        }

        [Fact]
        public async Task TheModel_SavesAsTheUser_ThenTheWaitingRecordingLeaves_AndSaveStaysOffWhileTheCheckFails()
        {
            var (host, zip) = Recorded();
            using var owned = host;
            string target = Path.Combine(host.Dir, "saved", "mine.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var live = new List<(PrivateKind, string)> { (PrivateKind.DeviceName, "planted-word-zz") };
            var review = RecordingReview.Open(zip);
            var model = new RecordingReviewModel(review, () => live, host.Environment());
            (string From, string To) copied = default;
            string explorer = null;
            bool closed = false;
            model.PickTarget = (folder, name) => target;
            model.SaveAsUser = (from, to) =>
            {
                copied = (from, to);
                File.Copy(from, to, overwrite: true);
                return Task.FromResult<string>(null);
            };
            model.ShowInExplorer = path => explorer = path;
            model.Closed = () => closed = true;

            Assert.True(model.CheckFailed);
            Assert.False(model.CanSave);
            var diagnostics = model.Parts.Single(p => p.Info.Part == RecordingParts.DiagnosticsLines);
            diagnostics.Keep = false;
            Assert.False(model.CheckFailed);
            Assert.True(model.CanSave);
            Assert.Contains("left out", diagnostics.SizeText);

            await model.SaveAsync();
            Assert.Equal(target, copied.To);
            Assert.EndsWith(RecordingReview.CopySuffix, copied.From);
            Assert.True(File.Exists(target));
            Assert.Equal(target, explorer);
            Assert.True(closed);
            Assert.False(File.Exists(zip));
            Assert.False(File.Exists(copied.From));
            Assert.Empty(Directory.GetFiles(host.Dir, "*" + ProblemRecorder.TempSuffix));
            using var reader = RecordingReader.Open(target);
            Assert.Equal(RecordingParts.All & ~RecordingParts.DiagnosticsLines, reader.Parts);
        }

        [Fact]
        public void CopySummary_HandsTheSummaryOver_AndDelete_TakesTheRecording()
        {
            var (host, zip) = Recorded();
            using var owned = host;
            var model = new RecordingReviewModel(RecordingReview.Open(zip), () => Array.Empty<(PrivateKind, string)>(), host.Environment());
            string copied = null;
            model.CopyText = text => copied = text;
            model.CopySummary();
            using (var reader = RecordingReader.Open(zip))
                Assert.Equal(string.Join(Environment.NewLine, reader.Summary), copied);
            model.Delete();
            Assert.False(File.Exists(zip));
        }

        [Theory]
        [InlineData(ProblemRecorder.HowUser, "Stopped by you.")]
        [InlineData(ProblemRecorder.HowMacro, "Stopped by a macro.")]
        [InlineData(ProblemRecorder.HowCommand, "Stopped by a program or --record stop.")]
        [InlineData(ProblemRecorder.HowAssistant, "Stopped by the assistant that started it.")]
        [InlineData(ProblemRecorder.HowClosed, "Stopped as PadForge closed.")]
        [InlineData(ProblemRecorder.EndedByClosing, "Stopped as PadForge closed.")]
        [InlineData("It stopped by itself at 30 minutes", "Stopped by itself at 30 minutes.")]
        [InlineData("It stopped by itself at 20 MB compressed", "Stopped by itself at 20 MB in the zip.")]
        public void TheReview_SaysWhyItStopped(string ending, string words)
        {
            Assert.Equal(words, RecordingReviewModel.StopWords(ending));
        }

        // ── Save as the user ──

        [Fact]
        public void TheSaveRun_CopiesOnlyARecordingOfTheUsersOwnFolder()
        {
            var (host, zip) = Recorded();
            using var owned = host;
            var before = RecordingFolder.Seam;
            RecordingFolder.Seam = () => host.Dir;
            try
            {
                string target = Path.Combine(host.Dir, "out.zip");
                Assert.Equal(0, RecordingSave.Run(zip, target, out string error));
                Assert.Null(error);
                Assert.Equal(File.ReadAllBytes(zip), File.ReadAllBytes(target));

                // A file outside the folder, and one of the folder's that is
                // no recording, are refused.
                string outside = Path.Combine(Path.GetTempPath(), "PadForge.RecorderTests", Guid.NewGuid().ToString("N") + ".zip");
                Directory.CreateDirectory(Path.GetDirectoryName(outside));
                File.Copy(zip, outside);
                Assert.Equal(2, RecordingSave.Run(outside, target + "2", out _));
                File.Delete(outside);
                string other = Path.Combine(host.Dir, "notes.zip");
                File.WriteAllText(other, "x");
                Assert.Equal(2, RecordingSave.Run(other, target + "3", out _));
                Assert.False(File.Exists(target + "2"));
                Assert.False(File.Exists(target + "3"));
            }
            finally { RecordingFolder.Seam = before; }
        }

        [Fact]
        public void TheCommandLine_TakesSummarizeAndRecordSave_AsRunsOfTheirOwn()
        {
            var summarize = CommandLineSwitches.Parse(new[] { "--summarize", @"C:\r.zip" });
            Assert.Null(summarize.Error);
            Assert.Equal(@"C:\r.zip", summarize.Summarize);
            Assert.Empty(summarize.Steps);
            var save = CommandLineSwitches.Parse(new[] { "--record", "save", "a.zip", "b.zip" });
            Assert.Null(save.Error);
            Assert.Equal(("a.zip", "b.zip"), save.RecordSave);
            Assert.Empty(save.Steps);
            Assert.NotNull(CommandLineSwitches.Parse(new[] { "--record", "save", "a.zip" }).Error);
            Assert.NotNull(CommandLineSwitches.Parse(new[] { "--summarize" }).Error);
        }

        [Fact]
        public void Summarize_PrintsTheSummaryTheReviewShows()
        {
            var (host, zip) = Recorded();
            using var owned = host;
            string said = null, error = null;
            Assert.Equal(0, RecordingSummaryPrint.Run(zip, s => said = s, e => error = e));
            Assert.Null(error);
            using (var reader = RecordingReader.Open(zip))
                Assert.Equal(string.Join(Environment.NewLine, reader.Summary), said);
            string other = Path.Combine(host.Dir, "plain.txt");
            File.WriteAllText(other, "hello");
            Assert.Equal(2, RecordingSummaryPrint.Run(other, s => { }, e => error = e));
            Assert.Equal(1, RecordingSummaryPrint.Run(Path.Combine(host.Dir, "missing.zip"), s => { }, e => error = e));
        }

        // ── Snapshot ──

        [Fact]
        public void ASnapshot_IsARecordingWithNoEventsOfItsOwn_ThroughTheSameReview()
        {
            using var host = new RecorderTestHost();
            host.DeviceList.Add(RecorderTestKit.Pad("Wireless Controller"));
            host.Lines.Add((System.Diagnostics.Stopwatch.GetTimestamp(), "HEARTBEAT before the snapshot"));
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            var taken = recorder.Snapshot(wait: true);
            Assert.True(taken.Ok);
            Assert.False(recorder.State.Recording);
            string zip = taken.Path;
            Assert.True(File.Exists(zip));
            using (var reader = RecordingReader.Open(zip))
            {
                Assert.Equal("Status: finished. A snapshot of how things stood.", reader.StatusLine);
                var layers = new List<char>();
                while (reader.Read()) layers.Add(reader.Layer);
                Assert.Contains(RecordingCodec.LayerLine, layers);
                Assert.DoesNotContain(RecordingCodec.LayerInput, layers);
                Assert.True(reader.HashMatches);
            }
            Assert.Contains(RecordingStore.Waiting(host.Dir), w => w.Path == zip);
            using var review = RecordingReview.Open(zip);
            Assert.StartsWith(ProblemRecorder.HowSnapshot, review.Ending, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ThePercentBesideALine_ReadsTheExactValues()
        {
            Assert.Equal("LeftX 100%, LeftTrigger 50%", RecordingPercent.Of("1.000000 I D1 LeftX=65535 LeftTrigger=32768 A=1"));
            Assert.Equal("LeftX -100%", RecordingPercent.Of("1.000000 O C1 LeftX=-32768"));
            Assert.Equal(string.Empty, RecordingPercent.Of("1.000000 L - HEARTBEAT"));
        }

        /// <summary>The review is hosted as Add a Controller is, so it keeps
        /// that dialog's manners: its host says it is a dialog and closes on
        /// Escape, the focus goes in when it opens and back when it closes,
        /// and the pages leave the Tab order while it stands over them.</summary>
        [Fact]
        public void TheReview_OpensAsTheOtherHostedDialogsDo()
        {
            string root = OffLimitsSource.RepoRoot();
            string window = File.ReadAllText(Path.Combine(root, "PadForge.App", "MainWindow.ProblemRecording.cs"));
            string open = window.Substring(window.IndexOf("private async void OpenRecordingReview(", StringComparison.Ordinal));
            open = open.Substring(0, open.IndexOf("private void CloseRecordingReview()", StringComparison.Ordinal));
            Assert.Contains("RecordingReviewHost.Open(", open);
            Assert.Contains("SetPagesTabStop(false);", open);
            Assert.Contains("RecordingReviewView.FocusFirstPart", open);
            Assert.Contains("RecordingReviewHost.Close(returnFocus: true);", open);
            Assert.Contains("SetPagesTabStop(true);", open);
            Assert.Contains("RecordingReviewHost.CloseRequested += (s, e) => CloseRecordingReview();", window);

            string shell = File.ReadAllText(Path.Combine(root, "PadForge.App", "Resources", "ControllerIcons.xaml"));
            string style = shell.Substring(shell.IndexOf("<Style TargetType=\"{x:Type controls:ShellDialog}\">", StringComparison.Ordinal));
            style = style.Substring(0, style.IndexOf("</Style>", StringComparison.Ordinal));
            Assert.Contains("<Setter Property=\"AutomationProperties.IsDialog\" Value=\"True\"/>", style);
            string host = File.ReadAllText(Path.Combine(root, "PadForge.App", "MainWindow.xaml"));
            Assert.Contains("<controls:ShellDialog x:Name=\"RecordingReviewHost\"", host);
            Assert.Contains("Key.Escape", File.ReadAllText(Path.Combine(root, "PadForge.App", "Controls", "ShellDialog.cs")));
        }
    }
}
