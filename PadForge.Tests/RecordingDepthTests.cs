using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Privacy;
using PadForge.Engine.Recording;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// What a recording holds beyond its first build (#600): a window that
    /// does not answer, device calls, an engine with no pass, health lines,
    /// exceptions, force effects, what a game reads back through XInput, Raw
    /// Input's list, how a device is held, settings changed while recording,
    /// the system part's facts, a device's GUID as its label, and Windows'
    /// record of a native crash copied into crash.log.
    /// </summary>
    [Collection("RecordingFolderStatics")]
    public class RecordingDepthTests
    {
        private static (RecorderTestHost Host, ProblemRecorder Recorder, UserDevice Pad) Recording(Action<RecorderTestHost> fill = null)
        {
            var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad("Wireless Controller");
            host.DeviceList.Add(pad);
            fill?.Invoke(host);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp(), HealthEvery = TimeSpan.Zero };
            Assert.True(recorder.Start().Ok);
            recorder.CaptureForTest.Input(pad, RecorderTestKit.Rest());
            return (host, recorder, pad);
        }

        // ── The window's thread ──

        [Fact]
        public void TheWaitChain_SaysAThreadWaitingIsBlocked_AndARunningOneRuns()
        {
            using var mutex = new Mutex(false);
            using var held = new ManualResetEventSlim();
            using var done = new ManualResetEventSlim();
            int waiter = 0;
            var holder = new Thread(() =>
            {
                mutex.WaitOne();
                held.Set();
                done.Wait();
                mutex.ReleaseMutex();
            }) { IsBackground = true };
            holder.Start();
            held.Wait();
            var waiting = new Thread(() =>
            {
                waiter = WindowWatch.CurrentThreadId();
                mutex.WaitOne();
                mutex.ReleaseMutex();
            }) { IsBackground = true };
            waiting.Start();
            string chain = null;
            for (int i = 0; i < 100 && (chain == null || chain == "Windows sees it running"); i++)
            {
                Thread.Sleep(20);
                chain = WindowWatch.WaitChain(waiter);
            }
            done.Set();
            waiting.Join();
            holder.Join();
            // .NET waits alertably, so Windows may name the mutex or see the
            // thread blocked on something it does not name: either is a
            // thread that waits.
            Assert.NotNull(chain);
            Assert.Matches("^Windows sees it (wait on a mutex|blocked on nothing it can name)", chain);
            Assert.Equal("Windows sees it running", WindowWatch.WaitChain(WindowWatch.CurrentThreadId()));
        }

        private static void Marked() { }

        [Fact]
        public void ADispatcherOperation_IsNamedByTheMethodItRuns()
        {
            string name = null;
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var operation = dispatcher.BeginInvoke(new Action(Marked), DispatcherPriority.Background);
                name = WindowWatch.OperationName(operation);
                operation.Abort();
                dispatcher.InvokeShutdown();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            Assert.Equal("RecordingDepthTests.Marked at Background priority", name);
            Assert.Equal("WM_PAINT", WindowWatch.MessageName(0x000F));
            Assert.StartsWith("a registered message 0xC123", WindowWatch.MessageName(0xC123));
        }

        // ── Device calls and the engine ──

        [Fact]
        public void ADeviceCallThatHangs_IsSaidWhileItHangs_AndOneThatTookLong_ByItsDevice()
        {
            var (host, recorder, pad) = Recording();
            using var owned = host;
            var capture = recorder.CaptureForTest;
            capture.BeginCall(pad, RecordingCodec.CallKind.Read);
            capture.Advance(2);
            recorder.WaitForFlush();
            recorder.WaitForFlush();
            capture.EndCall();
            string path = recorder.Stop(wait: true).Path;
            var events = RecorderTestKit.Events(path);
            Assert.Contains(events, e => e.Layer == 'T' && e.Subject == "D1" && e.Payload.StartsWith("has been in its read for 2.0 s", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Layer == 'T' && e.Subject == "D1" && e.Payload == "took ms=2000 call=read");
        }

        [Fact]
        public void AnEngineWithNoPass_IsSaidWhileItLasts_AndWhenItRunsAgain()
        {
            var (host, recorder, pad) = Recording(h => h.EngineRuns = true);
            using var owned = host;
            var capture = recorder.CaptureForTest;
            capture.EndPass();
            capture.Advance(3);
            recorder.WaitForFlush();
            recorder.WaitForFlush();
            capture.EndPass();
            recorder.WaitForFlush();
            recorder.WaitForFlush();
            string path = recorder.Stop(wait: true).Path;
            var timing = RecorderTestKit.Events(path).Where(e => e.Layer == 'T').Select(e => e.Payload).ToList();
            Assert.Contains("engine has run no pass for 3.0 s", timing);
            Assert.Contains(timing, t => t.StartsWith("engine runs again after", StringComparison.Ordinal));
        }

        // ── Health, exceptions, notes ──

        [Fact]
        public void TheHealthLine_HoldsNumbersOnly()
        {
            using var host = new RecorderTestHost();
            var recorder = new ProblemRecorder(host);
            Assert.Null(recorder.Health(System.Diagnostics.Stopwatch.GetTimestamp()));
            string line = recorder.Health(System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency);
            Assert.StartsWith("health: PadForge ", line);
            foreach (string word in new[] { " MB in use", " handles, ", " window objects, ", " drawing objects, ", " threads, .NET collections ", "PC memory" })
                Assert.Contains(word, line);
            Assert.DoesNotContain(RecorderTestHost.User, line);
        }

        [Fact]
        public void Exceptions_AreWrittenScrubbed_UpToTheirLimit_AndTheRestCounted()
        {
            var (host, recorder, _) = Recording(h => h.Live.Add((PrivateKind.UserName, RecorderTestHost.User)));
            using var owned = host;
            for (int i = 0; i < ProblemRecorder.MostExceptions + 5; i++)
                recorder.TakeException(new IOException(@"C:\Users\" + RecorderTestHost.User + @"\file.txt was locked"), windowThread: i == 0);
            string path = recorder.Stop(wait: true).Path;
            var errors = RecorderTestKit.Events(path).Where(e => e.Layer == 'E').Select(e => e.Payload).ToList();
            Assert.Equal(ProblemRecorder.MostExceptions + 1, errors.Count);
            Assert.StartsWith("exception IOException on the window's thread: ", errors[0]);
            Assert.All(errors, e => Assert.DoesNotContain(RecorderTestHost.User, e));
            Assert.Equal("5 more exceptions past the first 40 were left out", errors[^1]);
        }

        // ── Force feedback ──

        [Fact]
        public void ForceUpdates_AreCountedEachSecond_ByDevice()
        {
            var (host, recorder, pad) = Recording();
            using var owned = host;
            var capture = recorder.CaptureForTest;
            var live = System.Reflection.DispatchProxy.Create<ISdlInputDevice, NothingBehind>();
            pad.Device = live;
            for (int i = 0; i < 30; i++) capture.CountForce(live, made: i == 0, started: i == 0);
            capture.Second(capture.Ticks(), 1000, 1000, 0, 0, 0, 0, 0, new long[RecordingCodec.StepNames.Length]);
            string path = recorder.Stop(wait: true).Path;
            Assert.Contains(RecorderTestKit.Events(path), e => e.Layer == 'F' && e.Subject == "D1" && e.Payload == "force updates=30 made=1 started=1");
        }

        /// <summary>A device the engine drives, with nothing behind it.</summary>
        public class NothingBehind : System.Reflection.DispatchProxy
        {
            protected override object Invoke(System.Reflection.MethodInfo targetMethod, object[] args)
                => targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void) ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }

        [Fact]
        public void ANoteThatRepeatsEveryPass_IsWrittenOnce_AndCounted()
        {
            var (host, recorder, pad) = Recording();
            using var owned = host;
            int made = 0;
            for (int i = 0; i < 5000; i++)
                recorder.TakeRepeatedNote(RecordingCodec.LayerFeedback, -1, pad, "failed making 128", () =>
                {
                    made++;
                    return "force failed making a spring effect: refused";
                });
            string path = recorder.Stop(wait: true).Path;
            var feedback = RecorderTestKit.Events(path).Where(e => e.Layer == 'F' && e.Payload.StartsWith("force failed", StringComparison.Ordinal)).ToList();
            Assert.Single(feedback);
            Assert.Equal(1, made);
        }

        [Fact]
        public void EachFile_StartsItsWatchAnew_WhileTheLastMinuteIsKept()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            host.Settings = new Dictionary<string, string> { ["app PollingRateHz"] = "1000" };
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp(), KeepLastMinute = true };
            try
            {
                Assert.True(recorder.Start().Ok);
                recorder.UiTick();
                for (int i = 0; i < ProblemRecorder.MostExceptions + 3; i++) recorder.TakeException(new InvalidOperationException("one"), false);
                recorder.Stop(wait: true);

                // Changed between the two recordings: no line in the second.
                host.Settings["app PollingRateHz"] = "500";
                Thread.Sleep(1100);
                Assert.True(recorder.Start().Ok);
                recorder.WaitForFlush();
                recorder.UiTick();
                recorder.TakeException(new InvalidOperationException("two"), false);
                string path = recorder.Stop(wait: true).Path;
                var events = RecorderTestKit.Events(path);
                Assert.DoesNotContain(events, e => e.Layer == 'C');
                Assert.Contains(events, e => e.Layer == 'E' && e.Payload == "exception InvalidOperationException: two");
            }
            finally { recorder.KeepLastMinute = false; }
        }

        [Fact]
        public void RumbleWrittenToADevice_IsKept_WithTheInputPartLeftOut()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            pad.IsOnline = true;
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host)
            {
                FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp(),
                Parts = RecordingParts.All & ~RecordingParts.Input,
            };
            Assert.True(recorder.Start().Ok);
            pad.ForceFeedbackState = new ForceFeedbackState();
            pad.ForceFeedbackState.TryRecordMotorSnapshot(1000, 0);
            recorder.CaptureForTest.EndPass();
            string path = recorder.Stop(wait: true).Path;
            Assert.Contains(RecorderTestKit.Events(path), e => e.Layer == 'F' && e.Subject == "D1" && e.Payload.StartsWith("wrote=1000", StringComparison.Ordinal));
        }

        [Fact]
        public void TheAppSettingsWritten_NameNothingPrivate()
        {
            var names = RecorderHost.AppSettingNames;
            Assert.Contains("PollingRateHz", names);
            Assert.Contains("ProblemRecordingLeaveOut", names);
            foreach (string kept in new[] { "SteamVrInstallDir", "ProblemRecordingSaveFolder", "RemoteLinkIdentityPrivate", "WebControllerAccessCode", "TwitchChatChannel" })
                Assert.DoesNotContain(kept, names);
            Assert.All(names, n => Assert.DoesNotMatch("(?i)(path|folder|dir|file|key|token|password|secret|host|code|name|serial)", n));
        }

        // ── XInput read back, and a second reader ──

        [Fact]
        public void WhatAGameReadsBack_IsWritten_AndThePlaceAVirtualControllerGot()
        {
            var before = XInputReadBack.ReadSeam;
            // A place read empty is asked again a second later, so the
            // game's controller is there before the recording's first read.
            var read = new XInputRead(true, Gamepad.A, 0, 0, 1200, 0, 0, 0);
            XInputReadBack.ReadSeam = place => place == 1 ? read : default;
            try
            {
                var (host, recorder, _) = Recording();
                using var owned = host;
                var capture = recorder.CaptureForTest;
                capture.OutGamepad(0, new Gamepad { Buttons = Gamepad.A, ThumbLX = 1200 });
                recorder.WaitForFlush();
                recorder.WaitForFlush();
                string path = recorder.Stop(wait: true).Path;
                var events = RecorderTestKit.Events(path);
                Assert.Contains(events, e => e.Layer == 'X' && e.Payload == "place=2 Buttons=A LeftTrigger=0 RightTrigger=0 LeftX=1200 LeftY=0 RightX=0 RightY=0");
                Assert.Contains(events, e => e.Layer == 'X' && e.Subject == "C1" && e.Payload == "reads at XInput place 2");
            }
            finally { XInputReadBack.ReadSeam = before; }
        }

        [Fact]
        public void TwoReadersOfOnePad_ThatDisagree_AreSaid()
        {
            var state = RecorderTestKit.Rest();
            state.Axis[4] = 32768;
            state.Buttons[0] = true;
            Assert.Null(ProblemRecorder.Difference(state, new XInputRead(true, Gamepad.A, 0, 0, 0, -1, 0, -1)));
            string differs = ProblemRecorder.Difference(state, new XInputRead(true, 0, 0, 0, 20000, -1, 0, -1));
            Assert.Contains("A up while PadForge's reader reads it down", differs);
            Assert.Contains("LeftX 20000 while PadForge's reader reads 0", differs);
            Assert.Null(ProblemRecorder.Difference(state, default));
        }

        // ── Raw Input, hold, settings ──

        [Fact]
        public void ADeviceRawInputStopsListing_IsSaid_AndWhenItIsBack()
        {
            var keyboard = RecorderTestKit.Keyboard();
            var (host, recorder, _) = Recording(h => h.DeviceList.Add(keyboard));
            using var owned = host;
            host.RawListing = new List<(UserDevice, bool)> { (keyboard, false) };
            recorder.WaitForFlush();
            recorder.WaitForFlush();
            host.RawListing = new List<(UserDevice, bool)> { (keyboard, true) };
            recorder.WaitForFlush();
            recorder.WaitForFlush();
            string path = recorder.Stop(wait: true).Path;
            var reader = RecorderTestKit.Events(path).Where(e => e.Layer == 'R').Select(e => e.Payload).ToList();
            Assert.Contains("is missing from Raw Input", reader);
            Assert.Contains("is back in Raw Input", reader);
        }

        [Fact]
        public void ASettingChangedWhileRecording_IsWritten_AndAHoldThatChanged()
        {
            var (host, recorder, pad) = Recording();
            using var owned = host;
            host.Settings = new Dictionary<string, string> { ["app PollingRateHz"] = "1000", ["row C1 ButtonA Base"] = "[" + pad.InstanceGuid + "]" };
            recorder.UiTick();
            Thread.Sleep(1100);
            host.Settings["app PollingRateHz"] = "500";
            pad.HidHideEnabled = true;
            recorder.UiTick();
            string path = recorder.Stop(wait: true).Path;
            var events = RecorderTestKit.Events(path);
            Assert.Contains(events, e => e.Layer == 'C' && e.Payload == "setting app PollingRateHz = 500");
            Assert.Contains(events, e => e.Layer == 'R' && e.Subject == "D1" && e.Payload == "held hidden");
        }

        [Fact]
        public void ADevicesGuidInFreeText_BecomesItsLabel()
        {
            var scrubber = new PrivacyScrubber(new PrivacyEnvironment());
            var guid = Guid.NewGuid();
            Assert.Equal("D1", scrubber.DeviceAlias(guid.ToString()));
            Assert.Equal("opened D1 again", scrubber.Text("opened " + guid.ToString().ToUpperInvariant() + " again"));
            Assert.StartsWith("opened <id", scrubber.Text("opened " + Guid.NewGuid() + " again"));
        }

        // ── The system part ──

        [Fact]
        public void TheWindowsLine_GivesBuildVersionEditionAndScale()
        {
            var before = RecorderHost.WindowsSeam;
            int scale = WindowWatch.DisplayScale;
            RecorderHost.WindowsSeam = () => ("26200", "7171", "25H2", "Professional");
            WindowWatch.DisplayScale = 150;
            try
            {
                Assert.Equal("Windows build 26200.7171, version 25H2, Professional, " + Environment.ProcessorCount + " processors, display at 150 %",
                    RecorderHost.WindowsLine());
            }
            finally
            {
                RecorderHost.WindowsSeam = before;
                WindowWatch.DisplayScale = scale;
            }
            Assert.StartsWith("Time zone: ", RecorderHost.TimeZoneLine());
        }

        private sealed class Sample
        {
            public int PollingRateHz { get; set; } = 1000;
            public bool StartMinimized { get; set; } = true;
            public string SettingsFolderPath { get; set; } = @"C:\Users\marisol";
            public string RemoteLinkIdentityPrivate { get; set; } = "secret";
            public string DjiRemoteHosts { get; set; } = "host";
            public string ProfileName { get; set; } = "Marisol's";
        }

        [Fact]
        public void ASettingsValues_LeavePrivateNamesOut()
        {
            var values = RecorderHost.SimpleValues(new Sample()).ToList();
            Assert.Equal(new[] { ("PollingRateHz", "1000"), ("StartMinimized", "1") }, values);
        }

        [Fact]
        public void ADevicesLine_SaysHowItIsHeld_AndItsXInputPlace()
        {
            var pad = RecorderTestKit.Pad();
            pad.DevicePath = "XInput#2";
            pad.ConsumeInputEnabled = true;
            var sb = new StringBuilder();
            new RecorderTestHost().AppendDeviceFacts(sb, pad);
            Assert.Equal(" held=only xinput=3", sb.ToString());
        }

        [Fact]
        public void TheActivityPart_CarriesWhatProgramsAsked_Scrubbed_AndOnlyWhenHeld()
        {
            var (host, recorder, _) = Recording(h =>
            {
                h.Live.Add((PrivateKind.UserName, RecorderTestHost.User));
                h.Activity = "2026-11-24 12:31:00 steer.ahk: Switch to profile " + RecorderTestHost.User + " -> ok\n2026-11-24 12:31:02 steer.ahk: Read devices -> ok\n";
            });
            using var owned = host;
            string path = recorder.Stop(wait: true).Path;
            var activity = RecorderTestKit.Events(path).Where(e => e.Layer == 'A').Select(e => e.Payload).ToList();
            Assert.Equal(2, activity.Count);
            Assert.StartsWith("2026-11-24 12:31:00 steer.ahk: Switch to profile ", activity[0]);
            Assert.All(activity, a => Assert.DoesNotContain(RecorderTestHost.User, a));

            recorder.Parts = RecordingParts.All & ~RecordingParts.Activity;
            Assert.True(recorder.Start().Ok);
            path = recorder.Stop(wait: true).Path;
            Assert.DoesNotContain(RecorderTestKit.Events(path), e => e.Layer == 'A');
        }

        [Fact]
        public void AnotherControllerProgram_ThatStartsWhileRecording_IsSaid()
        {
            var (host, recorder, _) = Recording(h => h.Programs = "none");
            using var owned = host;
            recorder.WaitForFlush();
            recorder.WaitForFlush();
            host.Programs = "Steam, DS4Windows";
            recorder.WaitForFlush();
            recorder.WaitForFlush();
            string path = recorder.Stop(wait: true).Path;
            Assert.Contains(RecorderTestKit.Events(path), e => e.Layer == 'W' && e.Payload == "other controller programs running now: Steam, DS4Windows");
        }

        // ── Native crashes ──

        [Fact]
        public void WindowsRecordOfANativeCrash_IsCopiedIntoCrashLogOnce()
        {
            string folder = Path.Combine(Path.GetTempPath(), "PadForge.RecorderTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string crashLog = Path.Combine(folder, "crash.log");
            var before = NativeCrashRecord.RecordsSeam;
            NativeCrashRecord.RecordsSeam = () => new[]
            {
                new NativeCrash(4411, new DateTime(2026, 11, 23, 21, 4, 5), "PadForge.exe", "6.1.0.0", "SDL3.dll", "3.5.0.0", "0xC0000005", "0x0000000000012A4F"),
            };
            try
            {
                Assert.Equal(1, NativeCrashRecord.CopyNew(crashLog));
                string text = File.ReadAllText(crashLog);
                Assert.Contains("Windows Application Error record 4411: PadForge.exe 6.1.0.0 stopped in native code.", text);
                Assert.Contains("Faulting module: SDL3.dll 3.5.0.0", text);
                Assert.Contains("Exception code: 0xC0000005", text);
                Assert.Contains("Offset: 0x0000000000012A4F", text);
                Assert.Equal(0, NativeCrashRecord.CopyNew(crashLog));
                Assert.Equal("0xE0434352", NativeCrashRecord.Hex("e0434352"));
                Assert.Equal("0x00000000000C408A", NativeCrashRecord.Hex("00000000000c408a"));
                Assert.Equal(text, File.ReadAllText(crashLog));
            }
            finally
            {
                NativeCrashRecord.RecordsSeam = before;
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
