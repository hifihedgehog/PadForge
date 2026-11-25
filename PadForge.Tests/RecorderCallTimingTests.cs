using System;
using System.Linq;
using System.Threading;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Recording;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Rumble and force calls in a recording (#600 item 31): timed like
    /// reads, a call of 20 ms or more written with its device and the call,
    /// and one that has not returned after a second said while it hangs, by
    /// the recorder's own thread, never the one in the call.
    /// </summary>
    [Collection("RecordingFolderStatics")]
    public class RecorderCallTimingTests
    {
        private static (RecorderTestHost Host, ProblemRecorder Recorder, UserDevice Pad) Recording()
        {
            var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad("Wireless Controller");
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            recorder.CaptureForTest.Input(pad, RecorderTestKit.Rest());
            return (host, recorder, pad);
        }

        [Fact]
        public void AFeedbackWrite_IsTimed_AndNamedAForceWriteOnceItReachesForceEffects()
        {
            var (host, recorder, pad) = Recording();
            using var owned = host;
            var capture = recorder.CaptureForTest;
            capture.BeginCall(pad, RecordingCodec.CallKind.Rumble);
            capture.Advance(0.5);
            // The write reached the device's force effects.
            capture.CallIs(RecordingCodec.CallKind.Force);
            capture.Advance(1.5);
            recorder.WaitForFlush();
            recorder.WaitForFlush();
            capture.EndCall();
            // A rumble write that took long, and one that did not.
            capture.BeginCall(pad, RecordingCodec.CallKind.Rumble);
            capture.Advance(0.03);
            capture.EndCall();
            capture.BeginCall(pad, RecordingCodec.CallKind.Rumble);
            capture.EndCall();
            // A rename with no call under way changes nothing.
            capture.CallIs(RecordingCodec.CallKind.Force);

            string path = recorder.Stop(wait: true).Path;
            var timing = RecorderTestKit.Events(path).Where(e => e.Layer == 'T' && e.Subject == "D1").Select(e => e.Payload).ToList();
            Assert.Contains(timing, t => t.StartsWith("has been in its force write for 2.0 s", StringComparison.Ordinal));
            Assert.Contains("took ms=2000 call=force", timing);
            Assert.Contains("took ms=30 call=rumble", timing);
            Assert.Equal(2, timing.Count(t => t.StartsWith("took ", StringComparison.Ordinal)));
        }

        [Fact]
        public void AWriteOnAnotherThread_ThatHangs_IsSaidWhileItHangs_AndTimedWhenItEnds()
        {
            var (host, recorder, pad) = Recording();
            using var owned = host;
            var capture = recorder.CaptureForTest;
            // Two writes under way at once on threads of their own, as the
            // Sony pads' effect reports are written.
            RecordingTaps.WriteCall first = default, second = default;
            var writers = new[]
            {
                new Thread(() => first = RecordingTaps.BeginWrite(capture, pad.DevicePath)),
                new Thread(() => second = RecordingTaps.BeginWrite(capture, pad.DevicePath)),
            };
            foreach (var w in writers) w.Start();
            foreach (var w in writers) w.Join();
            Assert.True(first.Slot > 0 && second.Slot > 0 && first.Slot != second.Slot, $"{first.Slot} {second.Slot}");
            capture.Advance(2);
            recorder.WaitForFlush();
            recorder.WaitForFlush();
            var ender = new Thread(() =>
            {
                RecordingTaps.EndWrite(first, recorder);
                RecordingTaps.EndWrite(second, recorder);
            });
            ender.Start();
            ender.Join();
            // A path no device of the file has is not timed.
            Assert.Equal(0, RecordingTaps.BeginWrite(capture, @"\\?\hid#vid_0000&pid_0000#none").Slot);

            string path = recorder.Stop(wait: true).Path;
            var timing = RecorderTestKit.Events(path).Where(e => e.Layer == 'T' && e.Subject == "D1").Select(e => e.Payload).ToList();
            int hung = timing.FindIndex(t => t.StartsWith("has been in its rumble write for 2.0 s", StringComparison.Ordinal));
            int took = timing.IndexOf("took ms=2000 call=rumble");
            Assert.True(hung >= 0 && took > hung, string.Join(" | ", timing));
            Assert.Equal(2, timing.Count(t => t.StartsWith("has been in its rumble write", StringComparison.Ordinal)));
            Assert.Equal(2, timing.Count(t => t == "took ms=2000 call=rumble"));
        }
    }
}
