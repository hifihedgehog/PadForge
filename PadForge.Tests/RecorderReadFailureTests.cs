using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A failed read in a recording (#600 item 32): how many times in a row,
    /// the error's own text, and "reads again" when a good read ends the
    /// run. A run that never ends is written as its count doubles, so a
    /// device that fails every pass never floods the file.
    /// </summary>
    [Collection("RecordingFolderStatics")]
    public class RecorderReadFailureTests
    {
        [Fact]
        public void FailedReads_AreCountedInARow_WrittenAsTheyDouble_AndEndWithReadsAgain()
        {
            using var host = new RecorderTestHost();
            var pad = RecorderTestKit.Pad();
            host.DeviceList.Add(pad);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            capture.Input(pad, RecorderTestKit.Rest());

            // The error's text is made only for a line that is written.
            int asked = 0;
            for (int i = 0; i < 5; i++)
            {
                int inARow = capture.ReadFailed(pad);
                if (!ProblemRecorder.WritesReadFailure(inARow)) continue;
                asked++;
                recorder.TakeReadFailed(pad, inARow, "HID read failed (0x8007048F)");
            }
            // A failure between two doublings is never written.
            recorder.TakeReadFailed(pad, 3, "never written");
            recorder.TakeReadsAgain(pad, capture.Input(pad, RecorderTestKit.Rest()));
            // A good read with no failure before it says nothing.
            recorder.TakeReadsAgain(pad, capture.Input(pad, RecorderTestKit.Rest()));
            // A new run counts from one.
            recorder.TakeReadFailed(pad, capture.ReadFailed(pad), "gone");

            string path = recorder.Stop(wait: true).Path;
            var lines = RecorderTestKit.Events(path)
                .Where(e => e.Layer == 'R' && e.Subject == "D1" && e.Payload.StartsWith("read", StringComparison.Ordinal))
                .Select(e => e.Payload).ToList();
            Assert.Equal(new[]
            {
                "read failed 1 time in a row: HID read failed (0x8007048F)",
                "read failed 2 times in a row: HID read failed (0x8007048F)",
                "read failed 4 times in a row: HID read failed (0x8007048F)",
                "reads again after 5 failed reads",
                "read failed 1 time in a row: gone",
            }, lines);
            Assert.Equal(3, asked);
        }

        [Fact]
        public void AReaderThatIsNotSdl_GivesWindowsErrorText_OrSaysItGaveNone()
        {
            var live = System.Reflection.DispatchProxy.Create<ISdlInputDevice, RecordingDepthTests.NothingBehind>();
            // Made first: Windows' words for an error are looked up through
            // a call that sets the last error again.
            string expected = "the read's last Windows error, 1167 (0x0000048F): " + new Win32Exception(1167).Message;
            Marshal.SetLastPInvokeError(1167);
            string text = InputManager.ReadError(live);
            Assert.Equal(expected, text);
            Marshal.SetLastPInvokeError(0);
            Assert.Equal(live.GetType().Name + " gave no reason", InputManager.ReadError(live));
        }

        [Fact]
        public void AReadThatThrows_GivesItsExceptionAsItsErrorText()
        {
            Assert.Equal("IOException: The device is not connected.", InputManager.ThrownReadError(new IOException("The device is not connected.")));
        }
    }
}
