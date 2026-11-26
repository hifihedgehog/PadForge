using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Privacy;
using PadForge.Engine.Recording;
using PadForge.Search;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The problem recorder (#597) from the outside: what a recording holds of
    /// keyboards and mice, the one switch that writes typed letters and
    /// numbers as one key, Keep the Last 60 Seconds, a recording finished at
    /// the next start, the check that refuses to start, the search at the
    /// stop, the macro action and the Diagnostics card's rows.
    /// </summary>
    public class ProblemRecorderTests
    {
        private static UserDevice Mouse()
        {
            var mouse = RecorderTestKit.Pad("Test Mouse", 0x046D, 0xC077, InputDeviceType.Mouse);
            mouse.SerialNumber = string.Empty;
            mouse.DevicePath = @"\\?\hid#vid_046d&pid_c077#7&1f2e3d4c&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
            return mouse;
        }

        private static List<string> Tokens(string path, string subject)
            => RecorderTestKit.Events(path)
                .Where(e => e.Layer == RecordingCodec.LayerInput && e.Subject == subject && !e.Payload.StartsWith("full", StringComparison.Ordinal))
                .SelectMany(e => e.Payload.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToList();

        /// <summary>Presses and releases each key of <paramref name="keys"/>
        /// on a keyboard, one pass each, a millisecond apart.</summary>
        private static void Type(RecordingCapture capture, UserDevice keyboard, params int[] keys)
        {
            var state = new CustomInputState();
            capture.Input(keyboard, state);
            foreach (int key in keys)
            {
                capture.Advance(0.001);
                state.Buttons[key] = true;
                capture.Input(keyboard, state);
                capture.Advance(0.001);
                state.Buttons[key] = false;
                capture.Input(keyboard, state);
            }
        }

        // ── Keyboards and mice ──

        [Fact]
        public void EveryKeyAndMouseButton_IsInTheFileAsPressed_ByDefault()
        {
            using var host = new RecorderTestHost();
            var keyboard = RecorderTestKit.Keyboard();
            var mouse = Mouse();
            host.DeviceList.Add(keyboard);
            host.DeviceList.Add(mouse);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.False(recorder.MaskTypedKeys);
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;

            // A, B, the space bar, 1 on the top row and 1 on the number pad.
            Type(capture, keyboard, 0x41, 0x42, 0x20, 0x31, 0x61);
            var click = new CustomInputState();
            capture.Input(mouse, click);
            capture.Advance(0.001);
            click.Buttons[0] = true;
            click.MouseRawDX = 5;
            click.MouseRawDY = -3;
            capture.Input(mouse, click);
            string path = recorder.Stop(wait: true).Path;

            Assert.Equal(new[] { "KeyA=1", "KeyA=0", "KeyB=1", "KeyB=0", "KeySpace=1", "KeySpace=0", "KeyDigit1=1", "KeyDigit1=0", "KeyNumpad1=1", "KeyNumpad1=0" }, Tokens(path, "D1"));
            Assert.Equal(new[] { "MouseLeft=1", "mr=5,-3" }, Tokens(path, "D2"));
            string notice = RecorderTestKit.Lines(path)[3];
            Assert.Contains("Every key and button pressed on every device while this recorded is in this file, keyboards and mice included.", notice);
            Assert.DoesNotContain("written as key", notice);
        }

        [Fact]
        public void TheTypedKeysSwitch_WritesUnreadLettersAndNumbersAsOneKey_AndLeavesEveryOtherKeyAlone()
        {
            using var host = new RecorderTestHost();
            var keyboard = RecorderTestKit.Keyboard();
            var mouse = Mouse();
            host.DeviceList.Add(keyboard);
            host.DeviceList.Add(mouse);
            // A mapping reads B on this keyboard.
            var read = new ulong[4];
            RecorderHost.KeyUse.Set(read, 0x42);
            host.Read[keyboard.InstanceGuid] = read;
            var recorder = new ProblemRecorder(host) { MaskTypedKeys = true, FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;

            Type(capture, keyboard, 0x41, 0x42, 0x20, 0x31, 0x61);
            var click = new CustomInputState();
            capture.Input(mouse, click);
            capture.Advance(0.001);
            click.Buttons[0] = true;
            capture.Input(mouse, click);
            string path = recorder.Stop(wait: true).Path;

            const string one = "KeyTyped";
            // A, 1 and the number pad's 1 are one key. B, which a mapping
            // reads, and the space bar, which is no letter or number, are
            // written as pressed. Each press and release keeps its time.
            Assert.Equal(new[] { one + "=1", one + "=0", "KeyB=1", "KeyB=0", "KeySpace=1", "KeySpace=0", one + "=1", one + "=0", one + "=1", one + "=0" }, Tokens(path, "D1"));
            var times = RecorderTestKit.Events(path).Where(e => e.Layer == 'I' && e.Subject == "D1" && e.Payload.StartsWith(one, StringComparison.Ordinal)).Select(e => e.Seconds).ToList();
            Assert.Equal(times.Distinct().Count(), times.Count);
            Assert.Equal(new[] { "MouseLeft=1" }, Tokens(path, "D2"));
            Assert.Contains("Letter and number keys that no mapping or macro reads are written as KeyTyped",
                RecorderTestKit.Lines(path)[3]);
        }

        [Fact]
        public void TheTypedKeysSwitch_CoversTheLettersAndBothRowsOfDigits_AndNothingElse()
        {
            var typed = Enumerable.Range(0, 256).Where(RecordingCapture.IsTypedKeyIndex).ToList();
            var expected = Enumerable.Range(0x41, 26).Concat(Enumerable.Range(0x30, 10)).Concat(Enumerable.Range(0x60, 10)).OrderBy(k => k);
            Assert.Equal(expected, typed);
            // The key the switch writes is one Windows gives to no key.
            Assert.False(RecordingCapture.IsTypedKeyIndex(RecordingCapture.TypedKey));
            Assert.Equal(0xE8, RecordingCapture.TypedKey);
        }

        [Fact]
        public void TheKeysAMappingReads_AreFoundOnTheKeyboardTheyName()
        {
            var keyboard = Guid.NewGuid().ToString();
            var other = Guid.NewGuid().ToString();
            var bits = new ulong[4];
            RecorderHost.KeyUse.Collect(new[]
            {
                new KeyRead { DeviceGuid = keyboard, Descriptor = "Button 65" },
                new KeyRead { DeviceGuid = other, Descriptor = "Button 66" },
                new KeyRead { DeviceGuid = string.Empty, Descriptor = "Button 67" },
            }, keyboard, bits);
            Assert.True((bits[1] & (1UL << (65 - 64))) != 0, "the row on this keyboard");
            Assert.True((bits[1] & (1UL << (66 - 64))) == 0, "a row on another keyboard");
            Assert.True((bits[1] & (1UL << (67 - 64))) != 0, "a row on any device");
        }

        /// <summary>The shape a mapping source has: a device and an input.</summary>
        public sealed class KeyRead
        {
            public string DeviceGuid { get; set; }
            public string Descriptor { get; set; }
        }

        // ── Keep the Last 60 Seconds ──

        [Fact]
        public void KeepTheLast60Seconds_HoldsAMinute_AndNoMore()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.Equal(RecorderRefusal.NothingKept, recorder.SaveLastMinute().Refusal);

            recorder.KeepLastMinute = true;
            recorder.WaitForFlush();
            var capture = recorder.CaptureForTest;
            Assert.NotNull(capture);

            // Ninety seconds of a stick that moves every half second.
            var state = RecorderTestKit.Rest();
            for (int step = 0; step <= 180; step++)
            {
                state.Axis[0] = 1000 + step * 10;
                capture.Input(pad, state);
                capture.Advance(0.5);
                if (step % 10 == 0) recorder.WaitForFlush();
            }
            var saved = recorder.SaveLastMinute(wait: true);
            Assert.True(saved.Ok);
            Assert.True(recorder.State.KeepingLastMinute);
            string path = saved.Path;

            Assert.Equal("Status: finished. Saved the last 60 seconds.", RecorderTestKit.Lines(path)[1]);
            var input = RecorderTestKit.Events(path).Where(e => e.Layer == 'I' && e.Subject == "D1").ToList();
            Assert.All(input, e => Assert.InRange(e.Seconds, 0, 60.0));
            // The minute opens with the state as it stood a minute back.
            var first = input[0];
            Assert.Equal(0, first.Seconds, 6);
            Assert.StartsWith("full online LeftX=", first.Payload);
            int opening = int.Parse(first.Payload.Split(' ')[2].Substring("LeftX=".Length));
            var changes = input.Where(e => !e.Payload.StartsWith("full", StringComparison.Ordinal)).ToList();
            // The clock stands still but for the test's half seconds: the
            // minute holds the change at its first moment and the 119 after.
            Assert.Equal(120, changes.Count);
            Assert.Equal(1000 + 60 * 10, opening);
            Assert.Equal("LeftX=" + (opening + 10), changes[0].Payload);
            Assert.Equal("LeftX=" + (1000 + 180 * 10), changes[^1].Payload);

            using (var reader = RecordingReader.Open(path))
            {
                while (reader.Read()) { }
                Assert.True(reader.Ended && reader.HasHash && reader.HashMatches);
            }
            recorder.KeepLastMinute = false;
        }

        // ── A recording PadForge did not stop ──

        [Fact]
        public void ARecordingLeftUnfinished_IsFinishedAtTheNextStart()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            string running = recorder.Start().Path;
            var capture = recorder.CaptureForTest;
            var state = RecorderTestKit.Rest();
            for (int i = 0; i < 20; i++)
            {
                state.Axis[0] = 32768 + i * 100;
                capture.Input(pad, state);
                capture.Advance(0.01);
            }
            recorder.WaitForFlush();

            // What a PadForge that closed while recording leaves: the partial
            // file as far as it was written, its status saying it records.
            Assert.EndsWith(ProblemRecorder.PartialSuffix, running);
            string left = Path.Combine(host.Dir, ProblemRecorder.FilePrefix + "20261123-100000" + ProblemRecorder.PartialSuffix);
            using (var source = new FileStream(running, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var copy = File.Create(left))
                source.CopyTo(copy);
            string stopped = recorder.Stop(wait: true).Path;
            Assert.True(ProblemRecorder.IsUnfinished(left));
            Assert.False(File.Exists(running));
            Assert.True(File.Exists(stopped));

            var next = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            string zip = ProblemRecorder.ZipPathOf(left);
            Assert.Equal(new[] { zip }, next.FinishLeftOver());
            Assert.Equal(zip, next.State.LastFile);
            Assert.False(File.Exists(left));
            Assert.Equal(new[] { stopped, zip }.OrderBy(p => p), Directory.GetFiles(host.Dir).OrderBy(p => p));
            var lines = RecorderTestKit.Lines(zip);
            Assert.Equal("Status: finished. " + ProblemRecorder.EndedByClosing + ".", lines[1]);
            Assert.Contains(RecordingCodec.SummaryHeader, lines);
            Assert.Contains(RecordingCodec.ScrubHeader, lines);
            Assert.Contains("PadForge closed while this recording ran, so the counts above are of the search alone.", lines);
            using (var reader = RecordingReader.Open(zip))
            {
                // Every change written before PadForge closed is still there.
                while (reader.Read()) { }
                Assert.Equal(32768 + 19 * 100, reader.DeviceByNumber(1).State.Axis[0]);
                Assert.True(reader.Ended && reader.HasHash && reader.HashMatches);
            }
            using (var reader = RecordingReader.Open(zip))
                Assert.StartsWith("Length: ", Assert.IsType<string>(reader.Summary[0]));
            // Once finished, it is left alone.
            Assert.Empty(new ProblemRecorder(host).FinishLeftOver());
        }

        // ── Every change, or an exact count of the ones left out ──

        [Fact]
        public void EveryChangeThatEntersTheRing_ReachesTheFile()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;

            // 20,000 passes of a stick and a button at 10,000 passes a
            // second: 40,000 changes, well inside the ring, so the test needs
            // no word from the writer's thread about when it drains.
            var state = RecorderTestKit.Rest();
            for (int i = 0; i < 20_000; i++)
            {
                state.Axis[0] = 1000 + i;
                state.Buttons[3] = i % 2 == 1;
                capture.Input(pad, state);
                capture.Advance(0.0001);
            }
            Assert.True(40_000 < capture.Ring.Capacity);
            string path = recorder.Stop(wait: true).Path;

            Assert.Equal(0, capture.Ring.Lost);
            var frames = RecorderTestKit.PassFrames(path, 1);
            Assert.Equal(20_000, frames.Count);
            for (int i = 0; i < frames.Count; i++)
            {
                Assert.Equal(1000 + i, frames[i].State.Axis[0]);
                Assert.Equal(i % 2 == 1, frames[i].State.Buttons[3]);
            }
            Assert.DoesNotContain(RecorderTestKit.Events(path), e => e.Layer == 'T' && e.Payload.StartsWith("lost=", StringComparison.Ordinal));
        }

        [Fact]
        public void WhatTheRingHasNoRoomFor_IsCountedExactly_InTheFileAndItsSummary()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp(), RingCapacity = 64 };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            Assert.Equal(64, capture.Ring.Capacity);

            // The writer's thread is held, so the ring fills and nothing
            // leaves it until the test lets go.
            using var release = new ManualResetEventSlim(false);
            recorder.HoldWriterForTest(release);
            // The first pass says the device is online and its four centered
            // sticks: five changes. Each pass after it changes a stick and a
            // button: 99 passes, 198 changes. The ring keeps 63 of the 203,
            // the first pass and the next 29 whole.
            var state = RecorderTestKit.Rest();
            for (int i = 0; i < 100; i++)
            {
                if (i > 0)
                {
                    state.Axis[0] = 1000 + i;
                    state.Buttons[3] = i % 2 == 1;
                }
                capture.Input(pad, state);
                capture.Advance(0.001);
            }
            Assert.Equal(203 - 63, capture.Ring.Lost);
            release.Set();
            string path = recorder.Stop(wait: true).Path;

            long written = RecorderTestKit.Events(path)
                .Where(e => e.Layer == 'T' && e.Payload.StartsWith("lost=", StringComparison.Ordinal))
                .Sum(e => long.Parse(e.Payload.Substring(5), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(140, written);
            var frames = RecorderTestKit.PassFrames(path, 1);
            Assert.Equal(30, frames.Count);
            Assert.Equal(1000 + 29, frames[^1].State.Axis[0]);
            using var reader = RecordingReader.Open(path);
            Assert.Contains(reader.Summary, l => l.EndsWith("140 changes did not fit and were left out.", StringComparison.Ordinal));
        }

        // ── The check at the start and the search at the stop ──

        [Fact]
        public void TheCheckAtTheStart_RefusesWithTheKindsThatCameThrough_AndMakesNoFile()
        {
            using var host = new RecorderTestHost();
            host.DeviceList.Add(RecorderTestKit.Pad());
            host.Live.Add((PrivateKind.AccessCode, "742916"));
            // A scrubber that lets everything through, standing in for a rule
            // that misses.
            host.CheckScrub = line => line;
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            var result = recorder.Start();

            Assert.False(result.Ok);
            Assert.Equal(RecorderRefusal.Privacy, result.Refusal);
            Assert.Contains(PrivacyScrubber.KindLabel(PrivateKind.UserName), result.Detail);
            Assert.Contains(PrivacyScrubber.KindLabel(PrivateKind.AccessCode), result.Detail);
            Assert.DoesNotContain(RecorderTestHost.User, result.Detail);
            Assert.DoesNotContain("742916", result.Detail);
            Assert.Empty(Directory.GetFiles(host.Dir));
            Assert.False(recorder.State.Recording);
            Assert.Null(recorder.CaptureForTest);
        }

        [Fact]
        public void TheSearchAtTheStop_FindsAValueNoRuleCaught_ReplacesIt_AndCountsIt()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            capture.Input(pad, RecorderTestKit.Rest());

            // A line names a device's serial number, which has no shape a
            // rule knows, before the device is in the list.
            recorder.TakeLine(capture.Ticks(), "SDL opened pad serial QX7-LATE-0042 over USB");
            var late = RecorderTestKit.Pad("Late Pad", 0x045E, 0x0B13);
            late.SerialNumber = "QX7-LATE-0042";
            late.DevicePath = @"\\?\hid#vid_045e&pid_0b13#9&5e6f7a8b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            host.DeviceList.Add(late);
            string path = recorder.Stop(wait: true).Path;

            string text = RecorderTestKit.Text(path);
            Assert.DoesNotContain("QX7-LATE-0042", text);
            // The search ran before the zip was made, and the partial file,
            // which held the value as written, is gone.
            Assert.Equal(new[] { path }, Directory.GetFiles(host.Dir));
            Assert.Contains("SDL opened pad serial <serial ", text);
            var check = new List<string>();
            using (var reader = RecordingReader.Open(path)) check.AddRange(reader.ScrubCheck);
            Assert.Contains(check, l => l.StartsWith(PrivacyScrubber.KindLabel(PrivateKind.SerialNumber) + ": ", StringComparison.Ordinal)
                && l.EndsWith("1 more found by the search at the stop", StringComparison.Ordinal));
            Assert.Contains(check, l => l.StartsWith("At the stop, the file was searched for ", StringComparison.Ordinal) && l.EndsWith("1 place found and replaced.", StringComparison.Ordinal));
            using (var reader = RecordingReader.Open(path))
            {
                while (reader.Read()) { }
                Assert.True(reader.HashMatches, "the hash is of the file as the search left it");
            }
        }

        [Fact]
        public void NoPrivateValueOfTheTestPc_ReachesTheFile()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            pad.DisplayName = "Marisol's pad";
            host.DeviceList.Add(pad);
            host.Live.Add((PrivateKind.AccessCode, "742916"));
            host.System.Add(@"Loaded C:\Users\" + RecorderTestHost.User + @"\AppData\Roaming\x.ini on " + RecorderTestHost.Pc);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            capture.Input(pad, RecorderTestKit.Rest());
            recorder.TakeLine(capture.Ticks(), "web code 742916 from 192.168.1.42 by " + RecorderTestHost.User);
            recorder.Mark("pressed it at " + RecorderTestHost.Pc);
            string path = recorder.Stop(wait: true).Path;

            string text = RecorderTestKit.Text(path);
            foreach (string value in new[] { RecorderTestHost.User, RecorderTestHost.Pc, "742916", "192.168.1.42", "A0AB51C32FCB", "1a2b3c4d", "Marisol's pad" })
                Assert.True(text.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0, value + " is in the file");
            Assert.Contains("M - mark pressed it at <pc>", text);
        }

        [Fact]
        public void AHeadsetAPhoneAndASentence_NamedAfterPeople_NeverReachTheFile()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            // A phone's browser drives a web controller, its viewer named
            // after its owner, and a custom layout carries the name its maker
            // gave it.
            var web = RecorderTestKit.Pad("DualShock 4 Web Controller: Dmitri Volkov's Pixel 8", 0x054C, 0x05C4);
            web.DevicePath = "web://c1-7f3a";
            web.SerialNumber = string.Empty;
            host.DeviceList.Add(web);
            var custom = RecorderTestKit.Pad("Ellery's Flight Deck: Noor's iPad", 0x045E, 0x028E);
            custom.DevicePath = "web://c2-11b0";
            custom.SerialNumber = string.Empty;
            host.DeviceList.Add(custom);
            host.Live.Add((PrivateKind.AudioDevice, "Headset Microphone (Priya Raman's AirPods Pro)"));
            var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            capture.Input(pad, RecorderTestKit.Rest());
            recorder.TakeLine(capture.Ticks(), "VOICE [Headset Microphone (Priya Raman's AirPods Pro)] heard \"call me back at nine tonight\" conf=0.91 gate=1 fires=0");
            recorder.TakeLine(capture.Ticks(), "VOICE capture alive: Headset Microphone (Priya Raman's AirPods Pro) fmt=48000Hz/1ch/f32");
            recorder.TakeLine(capture.Ticks(), "WEBGP deadline DualShock 4 Web Controller: Dmitri Volkov's Pixel 8 idle=1200ms neutralized");
            recorder.TakeLine(capture.Ticks(), "DEV - Ellery's Flight Deck: Noor's iPad");
            string path = recorder.Stop(wait: true).Path;

            string text = RecorderTestKit.Text(path);
            foreach (string value in new[] { "Priya", "Dmitri", "call me back", "Ellery", "Noor" })
                Assert.True(text.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0, value + " is in the file");
            Assert.Contains("heard \"<speech of 28 characters>\"", text);
            Assert.Contains("DualShock 4 Web Controller: <phone ", text);
        }

        [Fact]
        public void AWebControllersName_GivesUpTheViewerAndACustomLayoutsName_AndPadForgesOwnNamesGiveNothing()
        {
            List<(PrivateKind, string)> Of(string name)
            {
                var into = new List<(PrivateKind Kind, string Value)>();
                ProblemRecorder.WebNames(name, into);
                return into.Select(v => ((PrivateKind)v.Kind, v.Value)).ToList();
            }
            Assert.Equal(new[] { (PrivateKind.Phone, "Dmitri") }, Of("Xbox 360 Web Controller: Dmitri"));
            Assert.Equal(new[] { (PrivateKind.Phone, "Dmitri") }, Of("Browser Gamepad: Dmitri"));
            Assert.Equal(new[] { (PrivateKind.Phone, "Noor"), (PrivateKind.DeviceName, "Race Deck") }, Of("Race Deck: Noor"));
            Assert.Equal(new[] { (PrivateKind.DeviceName, "Race Deck") }, Of("Race Deck 2"));
            Assert.Empty(Of("Xbox 360 Web Controller 3"));
            Assert.Empty(Of("Custom Pad 4"));
            Assert.Empty(Of("Web Touchpad 2"));
            Assert.Empty(Of("Web Menus 1"));
        }

        [Fact]
        public void TheCheckAtTheStart_TriesTheVoiceAndWebLinesToo()
        {
            using var host = new RecorderTestHost();
            host.DeviceList.Add(RecorderTestKit.Pad());
            host.CheckScrub = line => line;
            var result = new ProblemRecorder(host).Start();
            Assert.Equal(RecorderRefusal.Privacy, result.Refusal);
            foreach (var kind in new[] { PrivateKind.AudioDevice, PrivateKind.Phone, PrivateKind.Speech })
                Assert.Contains(PrivacyScrubber.KindLabel(kind), result.Detail);
        }

        // ── The macro action ──

        private static MacroItem PressA(ProblemRecordingMode mode)
        {
            var m = new MacroItem
            {
                Name = "Rec",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
            };
            m.Actions.Add(new MacroAction { Type = MacroActionType.ProblemRecording, ProblemRecordingMode = mode });
            return m;
        }

        [Fact]
        public void TheMacroAction_HasOrdinal72_AndThreeChoicesInOrder()
        {
            Assert.Equal(72, (int)MacroActionType.ProblemRecording);
            Assert.Equal(new[] { ProblemRecordingMode.Toggle, ProblemRecordingMode.Mark, ProblemRecordingMode.SaveLastMinute },
                Enum.GetValues<ProblemRecordingMode>());
            Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<ProblemRecordingMode>().Select(m => (int)m));

            var action = new MacroAction { Type = MacroActionType.ProblemRecording };
            Assert.True(action.IsProblemRecordingType);
            Assert.Equal(ProblemRecordingMode.Toggle, action.ProblemRecordingMode);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.MacroAction_ProblemRecording_Toggle, action.DisplayText);
            action.ProblemRecordingModeIndex = 1;
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.MacroAction_ProblemRecording_Mark, action.DisplayText);
            action.ProblemRecordingModeIndex = 2;
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Settings_RecordProblem_SaveLastMinute, action.DisplayText);
            action.ProblemRecordingModeIndex = 9;
            Assert.Equal(ProblemRecordingMode.Toggle, action.ProblemRecordingMode);
            action.ProblemRecordingMode = ProblemRecordingMode.Mark;
            action.ResetSettingCommand.Execute(nameof(MacroAction.ProblemRecordingMode));
            Assert.Equal(ProblemRecordingMode.Toggle, action.ProblemRecordingMode);
        }

        [Theory]
        [InlineData(ProblemRecordingMode.Toggle)]
        [InlineData(ProblemRecordingMode.Mark)]
        [InlineData(ProblemRecordingMode.SaveLastMinute)]
        public void TheMacroAction_KeepsItsChoice_ThroughSaveAndLoad(ProblemRecordingMode mode)
        {
            var m = new MacroItem { Name = "RT" };
            m.Actions.Add(new MacroAction { Type = MacroActionType.ProblemRecording, ProblemRecordingMode = mode });
            var data = SettingsService.BuildMacroDataForMacro(m, 0);
            var saved = Assert.Single(data.Actions);
            Assert.Equal(mode != ProblemRecordingMode.Toggle, saved.ShouldSerializeProblemRecordingMode());
            var clone = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);
            var action = Assert.Single(clone.Actions);
            Assert.Equal(MacroActionType.ProblemRecording, action.Type);
            Assert.Equal(mode, action.ProblemRecordingMode);
        }

        [Fact]
        public void TheMacroAction_ItsThreeChoicesDoWhatTheySay()
        {
            var before = RecordingCapture.Active;
            var capture = new RecordingCapture(1 << 12);
            try
            {
                foreach (var mode in Enum.GetValues<ProblemRecordingMode>())
                {
                    var im = new InputManager();
                    var macros = new[] { PressA(mode) };
                    RecordingCapture.Active = capture;
                    capture.Ring.Drain(new RecordingEntry[capture.Ring.Capacity]);
                    var gp = new Gamepad { Buttons = Gamepad.A };
                    im.EvaluateSlotMacros(ref gp, macros);
                    gp = new Gamepad();
                    im.EvaluateSlotMacros(ref gp, macros);
                    RecordingCapture.Active = before;

                    var drained = new RecordingEntry[capture.Ring.Capacity];
                    int n = capture.Ring.Drain(drained);
                    int marks = drained.Take(n).Count(e => e.Field == RecordingField.Marker);
                    switch (mode)
                    {
                        case ProblemRecordingMode.Toggle:
                            // Starting and stopping make a file, which the UI
                            // thread does when it next looks.
                            Assert.Equal(InputManager.PendingRecordingToggle, im.PendingProblemRecording);
                            Assert.Equal(0, marks);
                            break;
                        case ProblemRecordingMode.Mark:
                            // A marker goes in on the poll thread, at the
                            // moment the macro fired.
                            Assert.Equal(0, im.PendingProblemRecording);
                            Assert.Equal(1, marks);
                            break;
                        default:
                            Assert.Equal(InputManager.PendingRecordingSave, im.PendingProblemRecording);
                            Assert.Equal(0, marks);
                            break;
                    }
                }
            }
            finally
            {
                RecordingCapture.Active = before;
            }
        }

        [Fact]
        public void AMarkFromTheMacro_IsAnMLine_AtTheTimeItFired()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            capture.Input(pad, RecorderTestKit.Rest());
            capture.Advance(2.5);
            capture.Mark();
            string path = recorder.Stop(wait: true).Path;
            var mark = Assert.Single(RecorderTestKit.Events(path), e => e.Layer == 'M' && e.Payload == "mark");
            Assert.Equal(2.5, mark.Seconds, 6);
            Assert.Equal("-", mark.Subject);
            using var reader = RecordingReader.Open(path);
            Assert.Contains(reader.Summary, l => l.StartsWith("Marker at ", StringComparison.Ordinal));
        }

        // ── What the summary and the scrub check say ──

        private static UserDevice Web(string name)
        {
            var web = RecorderTestKit.Pad(name, 0, 0);
            web.SerialNumber = string.Empty;
            web.DevicePath = ProblemRecorder.WebDevicePrefix + Guid.NewGuid().ToString("N");
            return web;
        }

        private static UserDevice LatePad(string name = "Late Pad")
        {
            var late = RecorderTestKit.Pad(name, 0x045E, 0x0B13);
            late.DevicePath = @"\\?\hid#vid_045e&pid_0b13#9&5e6f7a8b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            return late;
        }

        [Fact]
        public void TheSummary_HoldsNoValueTheSearchAtTheStopFound_AndKeepsItsOwnWords()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            var web = Web("Browser Gamepad");
            host.DeviceList.Add(pad);
            host.DeviceList.Add(web);
            var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            capture.Input(pad, RecorderTestKit.Rest());
            capture.Input(web, RecorderTestKit.Rest());
            // An error names a serial number before its device is in the list.
            recorder.TakeLine(capture.Ticks(), "ERROR pad serial QX7-LATE-0042 stopped answering");
            capture.Advance(1);
            var moved = RecorderTestKit.Rest();
            moved.Axis[0] = 1000;
            capture.Input(web, moved);
            var late = LatePad();
            late.SerialNumber = "QX7-LATE-0042";
            host.DeviceList.Add(late);
            string path = recorder.Stop(wait: true).Path;

            Assert.DoesNotContain("QX7-LATE-0042", RecorderTestKit.Text(path));
            using var reader = RecordingReader.Open(path);
            Assert.Contains(reader.Summary, l => l.StartsWith("  error at ", StringComparison.Ordinal) && l.Contains("ERROR pad serial <serial ", StringComparison.Ordinal));
            // The summary's own words around a device's name stay as written.
            Assert.Contains(reader.Summary, l => l.StartsWith("D2 Browser Gamepad: online 1.000 s, ", StringComparison.Ordinal));
        }

        [Fact]
        public void AWebControllerThatComesAndGoesWhileRecording_LeavesNoNameItsUsersTyped()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            capture.Input(pad, RecorderTestKit.Rest());
            capture.Advance(0.5);
            // A custom layout the user named, on a phone its viewer named.
            var web = Web("Gloria Layout: Smith, Jane");
            host.DeviceList.Add(web);
            capture.Input(web, RecorderTestKit.Rest());
            recorder.WaitForFlush();
            // The phone leaves before the stop, so the search at the stop
            // no longer knows its names.
            host.DeviceList.Remove(web);
            capture.Advance(0.5);
            recorder.TakeLine(capture.Ticks(), "DEV - Gloria Layout: Smith, Jane");
            string path = recorder.Stop(wait: true).Path;

            string text = RecorderTestKit.Text(path);
            Assert.DoesNotContain("Gloria", text);
            Assert.DoesNotContain("Jane", text);
        }

        [Fact]
        public void TheScrubCheck_CountsTheValuesItSearchedFor_AndTheFindsOfThoseValuesAlone()
        {
            (string Start, string Stop) Run(bool oneCharacter, bool typedName)
            {
                using var host = new RecorderTestHost();
                var pad = RecorderTestKit.Pad();
                host.DeviceList.Add(pad);
                if (oneCharacter) host.Live.Add((PrivateKind.SerialNumber, "J"));
                var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
                Assert.True(recorder.Start().Ok);
                var capture = recorder.CaptureForTest;
                capture.Input(pad, RecorderTestKit.Rest());
                if (typedName)
                {
                    // A name the user typed, learned after a line held it.
                    recorder.TakeLine(capture.Ticks(), "renamed a pad to Gloria Pad");
                    capture.Advance(0.1);
                    var named = LatePad("Named Pad");
                    named.SerialNumber = string.Empty;
                    named.DisplayName = "Gloria Pad";
                    capture.Input(named, RecorderTestKit.Rest());
                }
                string path = recorder.Stop(wait: true).Path;
                Assert.DoesNotContain("Gloria", RecorderTestKit.Text(path));
                using var reader = RecordingReader.Open(path);
                return (Assert.Single(reader.ScrubCheck, l => l.StartsWith("At the start, ", StringComparison.Ordinal)),
                    Assert.Single(reader.ScrubCheck, l => l.StartsWith("At the stop, ", StringComparison.Ordinal)));
            }

            var plain = Run(oneCharacter: false, typedName: false);
            // The same values at the start and the stop are the same count.
            static string Number(string line) => new string(line.SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit).ToArray());
            Assert.Equal(Number(plain.Start), Number(plain.Stop));
            // A value of one character is not searched for, so it is in
            // neither count.
            Assert.Equal(plain, Run(oneCharacter: true, typedName: false));
            // The stop's line counts the finds of this PC's values, and a
            // name learned while the file ran is none.
            var typed = Run(oneCharacter: false, typedName: true);
            Assert.EndsWith(": none found.", typed.Stop);
        }

        // ── A file PadForge finishes at the next start ──

        [Fact]
        public void ALeftOverFileWithALineLongerThanAnyRecordingHas_IsLeftAsItIs_AndTheStartGoesOn()
        {
            using var host = new RecorderTestHost();
            string left = Path.Combine(host.Dir, ProblemRecorder.FilePrefix + "20261123-100000" + ProblemRecorder.PartialSuffix);
            File.WriteAllText(left, RecordingCodec.FirstLine + "\r\n" + RecordingCodec.StatusRecording + "\r\n"
                + new string('x', BoundedLineReader.LongestLine + 1) + "\r\n");
            var recorder = new ProblemRecorder(host);
            Assert.Empty(recorder.FinishLeftOver());
            Assert.Equal(new[] { left }, Directory.GetFiles(host.Dir));
        }

        [Fact]
        public void AWebDevicesTypedNames_AreKnownBeforeAnyLineNamesIt_ThisFileAndTheNext()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            capture.Input(pad, RecorderTestKit.Rest());
            // The web controller names the device as a phone joins, before
            // the device reports anything or reaches the list.
            recorder.KnowTypedNames("Browser Gamepad: Smith, Jane");
            recorder.KnowTypedNames("Gloria Layout: Okafor, Ruth");
            recorder.TakeLine(capture.Ticks(), "WEBGP caps Browser Gamepad: Smith, Jane mapping=\"standard\" buttons=17 axes=4 vibrate=False");
            recorder.TakeLine(capture.Ticks(), "WEBGP deadline Gloria Layout: Okafor, Ruth idle=900ms neutralized");
            string first = recorder.Stop(wait: true).Path;
            Assert.True(recorder.Start().Ok);
            capture = recorder.CaptureForTest;
            capture.Input(pad, RecorderTestKit.Rest());
            recorder.TakeLine(capture.Ticks(), "WEBGP caps Browser Gamepad: Smith, Jane mapping=\"standard\" buttons=17 axes=4 vibrate=False");
            string second = recorder.Stop(wait: true).Path;

            foreach (string path in new[] { first, second })
            {
                string text = RecorderTestKit.Text(path);
                Assert.DoesNotContain("Jane", text);
                Assert.DoesNotContain("Ruth", text);
                Assert.DoesNotContain("Gloria", text);
            }
        }

        [Fact]
        public void ALineLongerThanAReaderTakes_IsCutInTheFile_AndTheFileFinishes()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            capture.Input(pad, RecorderTestKit.Rest());
            recorder.TakeLine(capture.Ticks(), "SDL said " + new string('x', BoundedLineReader.LongestLine + 100));
            string path = recorder.Stop(wait: true).Path;

            Assert.True(File.Exists(path), "the recording was not finished");
            Assert.All(RecorderTestKit.Lines(path), l => Assert.True(l.Length <= BoundedLineReader.LongestLine, "a line of " + l.Length));
            var said = Assert.Single(RecorderTestKit.Events(path), e => e.Payload.StartsWith("SDL said x", StringComparison.Ordinal));
            Assert.EndsWith("...", said.Payload);
        }

        [Fact]
        public void AFolderTheRecorderCannotUse_IsRefusedInTheWordsOfTheUsersLanguage()
        {
            using var host = new RecorderTestHost { FolderOverride = string.Empty };
            host.DeviceList.Add(RecorderTestKit.Pad());
            var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
            var result = recorder.Start();
            Assert.False(result.Ok);
            Assert.Equal(RecorderRefusal.File, result.Refusal);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Settings_RecordProblem_NoTempFolder, result.UserDetail);
            // A program is told in the protocol's own words.
            Assert.Equal(ProblemRecorder.NoTempFolderText, result.Detail);
        }

        [Fact]
        public void EveryFile_NamesTheProgramInFrontFromItsStart()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            host.FrontPath = @"C:\Games\Quarry\quarry.exe";
            var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
            try
            {
                recorder.KeepLastMinute = true;
                recorder.WaitForFlush();
                // The window's beat sees the game while the minute is kept.
                recorder.UiTick();
                recorder.WaitForFlush();
                Assert.True(recorder.Start().Ok);
                recorder.UiTick();
                string path = recorder.Stop(wait: true).Path;
                Assert.Single(RecorderTestKit.Events(path), e => e.Layer == RecordingCodec.LayerWindow && e.Payload == "front=quarry.exe");

                // The minute kept names it too once its line is older than
                // the minute.
                var capture = recorder.CaptureForTest;
                capture.Advance(70);
                capture.Input(pad, RecorderTestKit.Rest());
                recorder.WaitForFlush();
                var saved = recorder.SaveLastMinute(wait: true);
                Assert.True(saved.Ok);
                Assert.Single(RecorderTestKit.Events(saved.Path), e => e.Layer == RecordingCodec.LayerWindow && e.Payload == "front=quarry.exe");
            }
            finally
            {
                recorder.KeepLastMinute = false;
            }
        }

        // ── The Diagnostics card ──

        [Fact]
        public void TheCardsNewSettings_ResetToOff()
        {
            var vm = new SettingsViewModel { ProblemRecordingKeepLastMinute = true, ProblemRecordingHideTypedKeys = true };
            vm.ResetSettingCommand.Execute(nameof(SettingsViewModel.ProblemRecordingKeepLastMinute));
            vm.ResetSettingCommand.Execute(nameof(SettingsViewModel.ProblemRecordingHideTypedKeys));
            Assert.False(vm.ProblemRecordingKeepLastMinute);
            Assert.False(vm.ProblemRecordingHideTypedKeys);
            // Both are written to PadForge.xml only while on.
            var app = new AppSettingsData();
            Assert.False(app.ShouldSerializeProblemRecordingKeepLastMinute());
            Assert.False(app.ShouldSerializeProblemRecordingHideTypedKeys());
            app.ProblemRecordingKeepLastMinute = app.ProblemRecordingHideTypedKeys = true;
            Assert.True(app.ShouldSerializeProblemRecordingKeepLastMinute());
            Assert.True(app.ShouldSerializeProblemRecordingHideTypedKeys());
        }

        [Fact]
        public void SearchFinds_TheCardsRowsAndButtons()
        {
            var markup = SearchSources.FileNames.ToDictionary(p => p.Key, p => ExtrasPageTests.RepoText("PadForge.App", "Views", p.Value));
            var index = SearchIndexBuilder.Read(markup, PadPage.TabOfGroupName);
            var settings = index.Entries.Where(e => e.Page == SearchPage.Settings).ToList();
            foreach (string key in new[] { "Settings_RecordProblem_KeepLastMinute", "Settings_RecordProblem_HideTypedKeys" })
                Assert.Contains(settings, e => e.Kind == SearchKind.Setting && e.Label?.Key == key);
            foreach (string key in new[] { "Settings_RecordProblem_Start", "Settings_RecordProblem_SaveLastMinute" })
                Assert.Contains(settings, e => e.Kind == SearchKind.Action && e.Label?.Key == key);
        }
    }
}
