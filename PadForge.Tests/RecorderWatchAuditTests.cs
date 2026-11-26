using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Privacy;
using PadForge.Engine.Recording;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The recorder's host, watch and review views (#600), as the code
    /// audit of 2026-11-26 left them: each line says only what the code
    /// read, and each look reads what the running capture holds.
    /// </summary>
    [Collection("RecordingFolderStatics")]
    public class RecorderWatchAuditTests
    {
        [Fact]
        public void AnEmptyDriverVersion_IsWrittenAsNoKnownVersion()
        {
            var before = RecorderHost.Main;
            var main = new MainViewModel();
            main.Settings.IsHIDMaestroInstalled = true;
            main.Settings.HIDMaestroVersion = string.Empty;
            main.Settings.IsHidHideInstalled = true;
            main.Settings.HidHideVersion = string.Empty;
            RecorderHost.Main = main;
            try
            {
                var lines = new List<string>();
                new RecorderHost().FactLines(lines);
                Assert.Contains("Virtual controller driver: HIDMaestro of no known version", lines);
                Assert.Contains("HidHide: of no known version", lines);
            }
            finally { RecorderHost.Main = before; }
        }

        [Fact]
        public void SdlsVirtualBus_IsNamedVirtual()
        {
            // SDL_HARDWARE_BUS_VIRTUAL is 0xFF, written as the GUID's first
            // little-endian word (SDL_sysjoystick.h:155, SDL_virtualjoystick.c:234).
            Assert.Equal("virtual", RecorderHost.BusOf("ff000000000000000000000000000000"));
            Assert.Equal("usb", RecorderHost.BusOf("030000004c050000e60c000000006800"));
            Assert.Equal("0600", RecorderHost.BusOf("06000000000000000000000000000000"));
        }

        [Fact]
        public void TheReviewsSizes_AreDrawnInTabularFigures()
        {
            string path = Path.Combine(OffLimitsSource.RepoRoot(), "PadForge.App", "Views", "RecordingReviewView.xaml");
            var doc = XDocument.Load(path);
            var sized = doc.Descendants().Where(e => e.Name.LocalName == "TextBlock")
                .Where(e => e.Attributes().Any(a => a.Name.LocalName == "Text"
                    && (a.Value.Contains("SizesText") || a.Value.Contains("ShownSizes") || a.Value.Contains("SizeText"))))
                .ToList();
            Assert.Equal(3, sized.Count);
            foreach (var text in sized)
                Assert.True(text.Attributes().Any(a => a.Name.LocalName == "Typography.NumeralAlignment" && a.Value == "Tabular"),
                    text.Attribute("Text")?.Value);
        }

        [Fact]
        public void TheSettingsWatch_FollowsTheRunningCapturesParts()
        {
            using var host = new RecorderTestHost();
            host.DeviceList.Add(RecorderTestKit.Pad());
            host.Settings = new Dictionary<string, string> { ["app PollingRateHz"] = "1000" };
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            recorder.UiTick();
            // The next recording leaves Settings and devices out. This one
            // holds them still.
            recorder.Parts = RecordingParts.All & ~RecordingParts.SettingsAndDevices;
            host.Settings["app PollingRateHz"] = "500";
            Thread.Sleep(1100);
            recorder.UiTick();
            string path = recorder.Stop(wait: true).Path;
            recorder.Parts = RecordingParts.All;
            Assert.Contains(RecorderTestKit.Events(path),
                e => e.Layer == RecordingCodec.LayerSettingChange && e.Payload == "setting app PollingRateHz = 500");
        }

        [Fact]
        public void AProcessListThatCouldNotBeRead_IsSaidSo_AndIsNoChange()
        {
            var images = RecorderHost.RunningImages;
            var state = DiagnosticsLogControl.StateSeam;
            RecorderHost.RunningImages = () => null;
            DiagnosticsLogControl.StateSeam = () => Array.Empty<string>();
            try
            {
                Assert.Null(new RecorderHost().ControllerProgramsNow());
                var lines = new List<string>();
                new RecorderHost().SystemLines(new PrivacyScrubber(new PrivacyEnvironment()), lines);
                Assert.DoesNotContain(lines, l => l.Contains("none of the", StringComparison.Ordinal));
                Assert.Contains("Other controller programs running: the process list could not be read", lines);
                Assert.Contains("Device makers' programs running: the process list could not be read", lines);
            }
            finally
            {
                RecorderHost.RunningImages = images;
                DiagnosticsLogControl.StateSeam = state;
            }
        }

        [Fact]
        public void AFrontThatCouldNotBeRead_IsNoFront()
        {
            var before = RecorderHost.InFrontSeam;
            try
            {
                RecorderHost.InFrontSeam = () => (@"C:\Games\Game.exe", null);
                Assert.True(new RecorderHost().Front(out string path, out bool named, out _));
                Assert.Equal(@"C:\Games\Game.exe", path);
                Assert.False(named);
                RecorderHost.InFrontSeam = () => (null, null);
                Assert.False(new RecorderHost().Front(out _, out _, out _));
            }
            finally { RecorderHost.InFrontSeam = before; }
        }

        [Fact]
        public void AStopThatFindsTheRecordingEnded_IsNoRefusal()
        {
            Assert.Null(MainWindow.ProblemRecordingRefusal(RecorderResult.No(RecorderRefusal.NotRecording)));
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Settings_RecordProblem_Busy,
                MainWindow.ProblemRecordingRefusal(RecorderResult.No(RecorderRefusal.Busy)));
        }

        [Fact]
        public void TurningTheKeptMinuteOff_SaysSoOnceTheCaptureHasEnded()
        {
            using var host = new RecorderTestHost();
            host.DeviceList.Add(RecorderTestKit.Pad());
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            recorder.KeepLastMinute = true;
            recorder.WaitForFlush();
            Assert.True(recorder.IsCapturing);
            var seen = new List<bool>();
            recorder.Changed += () => { lock (seen) seen.Add(recorder.IsCapturing); };
            using var release = new ManualResetEventSlim(false);
            recorder.HoldWriterForTest(release);
            recorder.KeepLastMinute = false;
            release.Set();
            recorder.WaitForFlush();
            Assert.False(recorder.IsCapturing);
            lock (seen)
            {
                Assert.NotEmpty(seen);
                Assert.False(seen[^1], "the last Changed came while the capture still stood");
            }
        }

        private sealed class Opened : IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        [Fact]
        public async System.Threading.Tasks.Task ASecondReviewAskedWhileTheFirstReads_LeavesOneOpen()
        {
            var opens = new MainWindow.LatestOpen();
            var first = new Opened();
            var second = new Opened();
            using var gate = new ManualResetEventSlim(false);
            var a = opens.Open(() => { gate.Wait(TimeSpan.FromSeconds(10)); return first; });
            var b = opens.Open(() => second);
            Assert.Same(second, await b);
            gate.Set();
            Assert.Null(await a);
            Assert.True(first.Disposed, "the review read first was left open");
            Assert.False(second.Disposed);
        }

        [Fact]
        public void TheRecorderRowsStatus_FollowsTheLanguage()
        {
            var vm = new SettingsViewModel();
            string text = "one";
            vm.ShowProblemRecordingStatus(() => text);
            Assert.Equal("one", vm.ProblemRecordingStatus);
            text = "two";
            CultureChanged(typeof(SettingsViewModel), vm);
            Assert.Equal("two", vm.ProblemRecordingStatus);
            // A line set as it is stays as it is.
            vm.ProblemRecordingStatus = "plain";
            text = "three";
            CultureChanged(typeof(SettingsViewModel), vm);
            Assert.Equal("plain", vm.ProblemRecordingStatus);
        }

        [Fact]
        public void HomesWaitingRecordingLine_FollowsTheLanguage()
        {
            var vm = new DashboardViewModel();
            vm.SetWaitingRecording(@"C:\Temp\PadForge-recording-x.zip", DateTime.Now);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            CultureChanged(typeof(DashboardViewModel), vm);
            Assert.Contains(nameof(DashboardViewModel.WaitingRecordingText), raised);
        }

        private static void CultureChanged(Type type, object vm)
            => type.GetMethod("OnCultureChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);

        // ── Raw Input's lists ──

        private sealed class ListingHost : RecorderHost
        {
            public readonly List<UserDevice> List = new();
            public override List<UserDevice> Devices() => new(List);
        }

        private static RawInputListener.DeviceInfo KeyboardInfo(string path, long handle)
            => new() { Handle = (IntPtr)handle, Name = "Keyboard", DevicePath = path, Type = 1 };

        private const string KeyboardPath = @"\\?\hid#vid_046d&pid_c31c&mi_00#7&2b3c4d5e&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";

        [Fact]
        public void TheRawInputListing_IsTheEnginesOwn_AndListsNothingAgain()
        {
            var engine = RecorderHost.Engine;
            int calls = 0;
            RawInputListener.ListSeam = () => { calls++; return false; };
            try
            {
                var im = new PadForge.Common.Input.InputManager();
                im.SetRawInputListsForTest(new[] { KeyboardInfo(KeyboardPath, 0x1234) }, Array.Empty<RawInputListener.DeviceInfo>());
                RecorderHost.Engine = im;
                var host = new ListingHost();
                var keyboard = RecorderTestKit.Keyboard();
                keyboard.DevicePath = KeyboardPath;
                host.List.Add(keyboard);
                var listing = host.RawInputListing();
                Assert.NotNull(listing);
                Assert.Equal(0, calls);
                Assert.True(Assert.Single(listing).Listed);
            }
            finally
            {
                RawInputListener.ListSeam = null;
                RecorderHost.Engine = engine;
            }
        }

        [Fact]
        public void ARawInputListThatCouldNotBeRead_IsNoListing()
        {
            var engine = RecorderHost.Engine;
            RawInputListener.ListSeam = () => false;
            try
            {
                var im = new PadForge.Common.Input.InputManager();
                im.ListRawInput();
                RecorderHost.Engine = im;
                var host = new ListingHost();
                var keyboard = RecorderTestKit.Keyboard();
                keyboard.DevicePath = KeyboardPath;
                host.List.Add(keyboard);
                Assert.Null(host.RawInputListing());
            }
            finally
            {
                RawInputListener.ListSeam = null;
                RecorderHost.Engine = engine;
            }
        }

        [Fact]
        public void TheEngine_KeepsItsLastList_WhenAListCallFails()
        {
            RawInputListener.ListSeam = () => false;
            try
            {
                var im = new PadForge.Common.Input.InputManager();
                var keyboards = new[] { KeyboardInfo(KeyboardPath, 0x1234) };
                var mice = new[] { KeyboardInfo(@"\\?\hid#vid_046d&pid_c077#mouse", 0x5678) };
                im.SetRawInputListsForTest(keyboards, mice);
                im.ListRawInput();
                var lists = im.RawInputLists;
                Assert.NotNull(lists);
                Assert.Same(keyboards, lists.Value.Keyboards);
                Assert.Same(mice, lists.Value.Mice);
            }
            finally { RawInputListener.ListSeam = null; }
        }

        /// <summary>Nine failures that repeat on every pass are more than the
        /// ear's ring holds: each is still written once per RepeatEvery, as
        /// the repeat dictionary writes it.</summary>
        [Fact]
        public void NineForceFailuresThatRepeatEveryPass_AreEachWrittenOnce()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var live = System.Reflection.DispatchProxy.Create<PadForge.Engine.ISdlInputDevice, RecordingDepthTests.NothingBehind>();
            pad.Device = live;
            var ear = (PadForge.Engine.Recording.IForceTrace)Activator.CreateInstance(
                typeof(ProblemRecorder).GetNestedType("ForceEar", System.Reflection.BindingFlags.NonPublic)!, recorder)!;
            var before = PadForge.Common.Recording.RecordingCapture.Active;
            PadForge.Common.Recording.RecordingCapture.Active = recorder.CaptureForTest;
            try
            {
                for (int pass = 0; pass < 500; pass++)
                    for (int kind = 0; kind < 9; kind++)
                        ear.Failed(live, PadForge.Engine.Recording.ForceTrace.Making, 1u << kind);
            }
            finally { PadForge.Common.Recording.RecordingCapture.Active = before; }
            string path = recorder.Stop(wait: true).Path;
            var lines = RecorderTestKit.Events(path).Where(e => e.Layer == RecordingCodec.LayerFeedback
                && e.Payload.StartsWith("force failed making", StringComparison.Ordinal)).ToList();
            Assert.Equal(9, lines.Count);
        }

        [Fact]
        public void AForceFailureThatRepeatsEveryPass_AllocatesNothing_AndIsCounted()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var live = System.Reflection.DispatchProxy.Create<PadForge.Engine.ISdlInputDevice, RecordingDepthTests.NothingBehind>();
            pad.Device = live;
            var ear = (PadForge.Engine.Recording.IForceTrace)Activator.CreateInstance(
                typeof(ProblemRecorder).GetNestedType("ForceEar", System.Reflection.BindingFlags.NonPublic)!, recorder)!;
            var before = PadForge.Common.Recording.RecordingCapture.Active;
            PadForge.Common.Recording.RecordingCapture.Active = recorder.CaptureForTest;
            long allocated;
            try
            {
                const uint Spring = 1u << 7;
                ear.Failed(live, PadForge.Engine.Recording.ForceTrace.Making, Spring);
                for (int i = 0; i < 2000; i++) ear.Failed(live, PadForge.Engine.Recording.ForceTrace.Making, Spring);
                Thread.Sleep(300);
                for (int i = 0; i < 2000; i++) ear.Failed(live, PadForge.Engine.Recording.ForceTrace.Making, Spring);
                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++) ear.Failed(live, PadForge.Engine.Recording.ForceTrace.Making, Spring);
                allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            }
            finally { PadForge.Common.Recording.RecordingCapture.Active = before; }
            string path = recorder.Stop(wait: true).Path;
            Assert.Single(RecorderTestKit.Events(path), e => e.Layer == RecordingCodec.LayerFeedback
                && e.Payload.StartsWith("force failed making a spring effect", StringComparison.Ordinal));
            Assert.Equal(0, allocated);
        }
    }
}
