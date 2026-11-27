using System;
using System.Threading;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PadForge.Common;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Privacy;
using PadForge.Engine.Recording;
using PadForge.Services;
using PadForge.Resources.Strings;
using PadForge.Services.Plugins;
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

        /// <summary>A file planted in the folder with more lines than any
        /// recording holds is refused before its lines are indexed, and its
        /// unpacked text goes. Within the bound it opens: the positive
        /// control.</summary>
        [Fact]
        public void AFileOfMoreLinesThanTheBound_IsRefused()
        {
            string folder = Path.Combine(Path.GetTempPath(), "PadForge.RecorderTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string planted = Path.Combine(folder, ProblemRecorder.FilePrefix + "20261120-120000" + ProblemRecorder.ZipSuffix);
                File.WriteAllText(planted, RecordingCodec.FirstLineStart + " 2\r\n" + string.Concat(Enumerable.Repeat("\r\n", 200)));
                Assert.Throws<InvalidDataException>(() => RecordingReview.Open(planted, 100));
                Assert.Empty(Directory.GetFiles(folder, "*" + ProblemRecorder.TempSuffix));
                using (var review = RecordingReview.Open(planted, 1000)) Assert.NotNull(review);
            }
            finally { Directory.Delete(folder, recursive: true); }
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
            model.SaveFile = (from, to) =>
            {
                copied = (from, to);
                File.Copy(from, to, overwrite: true);
                return Task.FromResult<string>(null);
            };
            model.ShowInExplorer = path => explorer = path;
            model.Closed = () => closed = true;

            await model.Checked;
            Assert.True(model.CheckFailed);
            Assert.False(model.CanSave);
            var diagnostics = model.Parts.Single(p => p.Info.Part == RecordingParts.DiagnosticsLines);
            diagnostics.Keep = false;
            await model.Checked;
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

        /// <summary>Save and Post (#600 item 29): the save as Save… makes
        /// it, then the summary on the clipboard, the file in Explorer and
        /// the page where reports go, in that order, before the review
        /// closes.</summary>
        [Fact]
        public async Task SaveAndPost_SavesFirst_ThenCopiesTheSummary_ShowsTheFile_AndOpensTheReportPage()
        {
            var (host, zip) = Recorded();
            using var owned = host;
            string target = Path.Combine(host.Dir, "posted", "mine.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string[] summary;
            using (var reader = RecordingReader.Open(zip)) summary = reader.Summary.ToArray();
            var model = new RecordingReviewModel(RecordingReview.Open(zip), () => Array.Empty<(PrivateKind, string)>(), host.Environment());
            var steps = new List<string>();
            string copied = null, page = null;
            model.PickTarget = (folder, name) => target;
            model.SaveFile = (from, to) =>
            {
                steps.Add("save");
                File.Copy(from, to, overwrite: true);
                return Task.FromResult<string>(null);
            };
            model.CopyText = text => { steps.Add("copy"); copied = text; };
            model.ShowInExplorer = path => steps.Add("explorer " + Path.GetFileName(path));
            model.OpenPage = url => { steps.Add("page"); page = url; };
            model.Closed = () => steps.Add("closed");

            await model.Checked;
            await model.SaveAndPostAsync();

            Assert.Equal(new[] { "save", "copy", "explorer mine.zip", "page", "closed" }, steps);
            Assert.Equal(string.Join(Environment.NewLine, summary), copied);
            Assert.Equal(UpdateService.ReportPage, page);
            Assert.True(File.Exists(target));
            Assert.False(File.Exists(zip));
        }

        /// <summary>A save box closed with no file, a save that fails and a
        /// review whose check fails copy nothing and open nothing.</summary>
        [Fact]
        public async Task SaveAndPost_CopiesAndOpensNothing_WhenNothingWasSaved()
        {
            var (host, zip) = Recorded();
            using var owned = host;
            var live = new List<(PrivateKind, string)>();
            var model = new RecordingReviewModel(RecordingReview.Open(zip), () => live, host.Environment());
            var steps = new List<string>();
            model.CopyText = _ => steps.Add("copy");
            model.ShowInExplorer = _ => steps.Add("explorer");
            model.OpenPage = _ => steps.Add("page");
            await model.Checked;

            // The box closed with no file.
            model.PickTarget = (folder, name) => null;
            await model.SaveAndPostAsync();
            Assert.Empty(steps);

            // The save as the user failed.
            model.PickTarget = (folder, name) => Path.Combine(host.Dir, "failed.zip");
            model.SaveFile = (from, to) => Task.FromResult("refused");
            await model.SaveAndPostAsync();
            Assert.Empty(steps);
            Assert.Contains("refused", model.Status, StringComparison.Ordinal);
            Assert.True(File.Exists(zip));

            // The scrub check found this PC's value in a kept part.
            live.Add((PrivateKind.DeviceName, "planted-word-zz"));
            model.PartsChanged();
            Assert.False(model.CanSave);
            await model.Checked;
            Assert.False(model.CanSave);
            model.SaveFile = (from, to) => { steps.Add("save"); return Task.FromResult<string>(null); };
            await model.SaveAndPostAsync();
            Assert.Empty(steps);
            model.Close();
        }

        /// <summary>The page Save and Post and the crash box send a report
        /// to is the one the repository's own forms name first: a Q&amp;A
        /// discussion, which the bug form requires before a bug report. It
        /// carries no title or body, since GitHub documents those query
        /// parameters for a new issue and not for a new
        /// discussion.</summary>
        [Fact]
        public void TheReportPage_IsTheQandADiscussionTheRepositorysFormsName()
        {
            Assert.Equal("https://github.com/" + UpdateService.Repo + "/discussions/new?category=q-a", UpdateService.ReportPage);
            string root = OffLimitsSource.RepoRoot();
            string chooser = File.ReadAllText(Path.Combine(root, ".github", "ISSUE_TEMPLATE", "config.yml"));
            string bug = File.ReadAllText(Path.Combine(root, ".github", "ISSUE_TEMPLATE", "bug_report.yml"));
            Assert.Contains("url: " + UpdateService.ReportPage, chooser, StringComparison.Ordinal);
            Assert.Contains("(" + UpdateService.ReportPage + ")", bug, StringComparison.Ordinal);
            Assert.DoesNotContain("title=", UpdateService.ReportPage, StringComparison.Ordinal);
            Assert.DoesNotContain("body=", UpdateService.ReportPage, StringComparison.Ordinal);
        }

        /// <summary>The crash box (#600 item 29) says, after the error and
        /// above its stack, that the details are in crash.log beside
        /// PadForge.exe, or that they could not be written there, and where
        /// to send the report. Both boxes that show it build it in CrashText,
        /// which takes the format for whether crash.log was written and hands
        /// it the report page. The stack goes last: it can run to 16 KB, more
        /// lines than a screen holds.</summary>
        [Fact]
        public void TheCrashBox_NamesCrashLog_AndThePageWhereReportsGo_AboveTheStack()
        {
            var resx = System.Xml.Linq.XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.resx"));
            foreach (var (key, says) in new[]
                     {
                         ("App_UnexpectedError_Format", "PadForge wrote the details to crash.log beside PadForge.exe"),
                         ("App_UnexpectedErrorNotWritten_Format", "PadForge could not write the details to crash.log beside PadForge.exe"),
                     })
            {
                string format = (string)resx.Root.Elements("data").Single(d => (string)d.Attribute("name") == key).Element("value");
                string box = string.Format(format, "MESSAGE", "STACK", UpdateService.ReportPage);
                int message = box.IndexOf("MESSAGE", StringComparison.Ordinal);
                int log = box.IndexOf(says, StringComparison.Ordinal);
                int page = box.IndexOf(UpdateService.ReportPage, StringComparison.Ordinal);
                int stack = box.IndexOf("STACK", StringComparison.Ordinal);
                Assert.True(message >= 0 && log > message && page > log && stack > page, key + ": " + box);
            }

            // Both boxes build their text through App.CrashText, which hands
            // either format the report page.
            string app = ExtrasPageTests.RepoText("PadForge.App", "App.xaml.cs");
            string text = FenceSource.FirstStatement(app, "internal static string CrashText(bool written, string messages, string stacks)");
            Assert.Contains("string.Format(written ? Strings.Instance.App_UnexpectedError_Format : Strings.Instance.App_UnexpectedErrorNotWritten_Format,", text, StringComparison.Ordinal);
            Assert.Contains("messages, stacks, UpdateService.ReportPage)", text, StringComparison.Ordinal);
            // Both boxes build their text there, each with its own append's result.
            Assert.Equal(2, Regex.Matches(app, @"CrashText\(written, [^,]+, ExceptionStackChain\(").Count);
            Assert.Single(Regex.Matches(app, @"Strings\.Instance\.App_UnexpectedError_Format"));
            Assert.Contains(UpdateService.ReportPage, PadForge.App.CrashText(true, "MESSAGE", "STACK"), StringComparison.Ordinal);
            Assert.Contains(UpdateService.ReportPage, PadForge.App.CrashText(false, "MESSAGE", "STACK"), StringComparison.Ordinal);
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

        /// <summary>The review's X while Save… runs: the close waits for the
        /// save, which reads the review's text, and the save makes it as it
        /// ends, deleting the recording it saved. A read after the review is
        /// closed says so.</summary>
        [Fact]
        public async Task ACloseWhileTheSaveRuns_WaitsForIt_AndTheSavedRecordingLeaves()
        {
            var (host, zip) = Recorded();
            using var owned = host;
            string target = Path.Combine(host.Dir, "held", "mine.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var review = RecordingReview.Open(zip);
            var model = new RecordingReviewModel(review, () => Array.Empty<(PrivateKind, string)>(), host.Environment());
            await model.Checked;
            var held = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool closed = false;
            model.PickTarget = (folder, name) => target;
            model.SaveFile = (from, to) =>
            {
                File.Copy(from, to, overwrite: true);
                return held.Task;
            };
            model.Closed = () => closed = true;
            model.Parts.Single(p => p.Info.Part == RecordingParts.DiagnosticsLines).Keep = false;
            await model.Checked;

            var saving = model.SaveAsync();
            model.Close();
            Assert.False(closed);
            Assert.True(File.Exists(zip));
            held.SetResult(null);
            await saving;
            Assert.True(closed);
            Assert.True(File.Exists(target));
            Assert.False(File.Exists(zip));
            Assert.Throws<ObjectDisposedException>(() => review.Check(Array.Empty<(PrivateKind, string)>()));
        }

        /// <summary>A part's switch starts the scrub check off the window's
        /// thread and returns at once, Save off and the line saying it checks
        /// until the answer comes. An answer a newer switch outdated is
        /// dropped.</summary>
        [Fact]
        public async Task APartsSwitch_ChecksOffTheWindowsThread_WithSaveOffUntilTheAnswer()
        {
            var (host, zip) = Recorded();
            using var owned = host;
            var model = new RecordingReviewModel(RecordingReview.Open(zip), () => Array.Empty<(PrivateKind, string)>(), host.Environment());
            await model.Checked;
            Assert.True(model.CanSave);
            using var gate = new ManualResetEventSlim();
            model.RunsCheck = (live, kept) =>
            {
                gate.Wait(TimeSpan.FromSeconds(5));
                return new RecordingReview.CheckResult(0, 0, 1);
            };
            var started = System.Diagnostics.Stopwatch.StartNew();
            model.Parts.Single(p => p.Info.Part == RecordingParts.DiagnosticsLines).Keep = false;
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), "the switch waited for the check");
            Assert.False(model.CanSave);
            Assert.Equal(Strings.Instance.Review_Scrub_Checking, model.ScrubText);
            var outdated = model.Checked;
            // A newer switch, whose check answers at once.
            model.RunsCheck = (live, kept) => new RecordingReview.CheckResult(0, 0, 0);
            model.Parts.Single(p => p.Info.Part == RecordingParts.Activity).Keep = false;
            await model.Checked;
            gate.Set();
            await outdated;
            Assert.True(model.CanSave);
            Assert.False(model.CheckFailed);
            model.Close();
        }

        /// <summary>The save is made in this process, and a refused one says
        /// why in the user's language, from Strings. Run under French, each
        /// line is the Strings text formatted in that culture (the French
        /// words come with the translation round).</summary>
        [Fact]
        public async Task TheSavesLines_ComeFromStrings_AndTheSaveIsMadeInThisProcess()
        {
            var (host, zip) = Recorded();
            using var owned = host;
            var folder = RecordingFolder.Seam;
            var culture = CultureInfo.CurrentCulture;
            var uiCulture = CultureInfo.CurrentUICulture;
            RecordingFolder.Seam = () => host.Dir;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
                var s = Strings.Instance;
                string target = Path.Combine(host.Dir, "out.zip");

                Assert.Null(await RecordingSave.SaveAsync(zip, target));
                Assert.True(File.Exists(target));

                // A file that is no recording of the folder is refused.
                string other = Path.Combine(host.Dir, "notes.zip");
                File.WriteAllText(other, "x");
                Assert.Equal(s.Review_SaveNotOwn, await RecordingSave.SaveAsync(other, target));

                Assert.Equal(string.Format(CultureInfo.CurrentCulture, s.Review_SizeKB_Format, "2"), RecordingReviewModel.Size(2048));
                Assert.Equal(string.Format(CultureInfo.CurrentCulture, s.Review_SizeMB_Format, "1,5"), RecordingReviewModel.Size(1536 * 1024));
                Assert.Equal(string.Format(CultureInfo.CurrentCulture, s.Review_Stop_Size_Format, RecordingReviewModel.Size(20L * 1024 * 1024)),
                    RecordingReviewModel.StopWords("It stopped by itself at 20 MB compressed"));
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = uiCulture;
                RecordingFolder.Seam = folder;
            }
        }

        /// <summary>The review's sizes and its stop line take their units
        /// from Strings: a locale that writes Ko and Mo (French) shows them in
        /// a part's size, the sizes line and the stop line. The value is set
        /// through Strings' seam, since no locale holds the keys until the
        /// translation round.</summary>
        [Fact]
        public void TheReviewsSizes_TakeTheirUnitsFromStrings()
        {
            var before = Strings.ValueSeam;
            Strings.ValueSeam = key => key switch
            {
                nameof(Strings.Review_SizeKB_Format) => "{0} Ko",
                nameof(Strings.Review_SizeMB_Format) => "{0} Mo",
                _ => null,
            };
            try
            {
                Assert.Equal("2 Ko", RecordingReviewModel.Size(2048));
                Assert.Equal("20 Mo", RecordingReviewModel.Size(20L * 1024 * 1024));
                Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Instance.Review_Stop_Size_Format, "20 Mo"),
                    RecordingReviewModel.StopWords("It stopped by itself at 20 MB compressed"));
            }
            finally { Strings.ValueSeam = before; }
        }

        /// <summary>A scrub check that could not read the review's text says
        /// so, and Save stays off. A switch after it checks again.</summary>
        [Fact]
        public async Task AScrubCheckThatCouldNotRead_SaysSo_AndSaveStaysOff()
        {
            var (host, zip) = Recorded();
            using var owned = host;
            var model = new RecordingReviewModel(RecordingReview.Open(zip), () => Array.Empty<(PrivateKind, string)>(), host.Environment());
            await model.Checked;
            model.RunsCheck = (live, kept) => throw new IOException("unreadable");
            model.Parts.Single(p => p.Info.Part == RecordingParts.Activity).Keep = false;
            await model.Checked;
            Assert.Equal(Strings.Instance.Review_Scrub_Unread, model.ScrubText);
            Assert.False(model.CanSave);
            // The positive control: a check that reads gives Save back.
            model.RunsCheck = (live, kept) => new RecordingReview.CheckResult(0, 0, 0);
            model.Parts.Single(p => p.Info.Part == RecordingParts.Activity).Keep = true;
            await model.Checked;
            Assert.True(model.CanSave);
            model.Close();
        }

        // ── Save as the user ──

        private sealed class CountingLauncher : PadForge.Services.Plugins.IPluginLauncher
        {
            public int Starts;
            public PadForge.Services.Plugins.PluginLaunch Start(PadForge.Services.Plugins.PluginCommand command, IReadOnlyDictionary<string, string> variables)
            {
                Starts++;
                return PadForge.Services.Plugins.PluginLaunch.Refused(PadForge.Services.Plugins.PluginRefusal.Failed);
            }
        }

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
        public void TheCommandLine_TakesSummarize_AsARunOfItsOwn()
        {
            var summarize = CommandLineSwitches.Parse(new[] { "--summarize", @"C:\r.zip" });
            Assert.Null(summarize.Error);
            Assert.Equal(@"C:\r.zip", summarize.Summarize);
            Assert.Empty(summarize.Steps);
            Assert.NotNull(CommandLineSwitches.Parse(new[] { "--record", "save", "a.zip", "b.zip" }).Error);
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
