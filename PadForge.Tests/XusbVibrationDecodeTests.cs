using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The XUSB SET_STATE decode (#350).
    ///
    /// <para>The xusb wire struct is five bytes and byte 4 is a FLAGS byte
    /// (OpenXInput InSetState_t: deviceIndex, ledState, leftMotorSpeed,
    /// rightMotorSpeed, flags). An earlier decode invented an "extended"
    /// form and read bytes 4 and 5 as impulse-trigger motors whenever the
    /// caller's buffer was 7 bytes or longer. HM forwards the caller's
    /// buffer verbatim at any size, so a padded struct handed the flags
    /// byte to the trigger lane, and the Sony impulse-AT pass turned even
    /// a value of 2 into an engaged vibration-mode adaptive trigger on a
    /// DualSense: stiff, faintly buzzing triggers on Virtual Xbox 360
    /// slots. The decode lives in an event lambda with no unit seam, so
    /// this is a source contract.</para></summary>
    public class XusbVibrationDecodeTests
    {
        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        [Fact]
        public void TheXusbBranchReadsNoTriggerBytes()
        {
            string src = RepoText("PadForge.App", "Common", "Input", "HMaestroVirtualController.cs");
            int at = src.IndexOf("pkt.Source == HMOutputSource.XInput", StringComparison.Ordinal);
            Assert.True(at > 0);
            // The branch body, up to its return.
            int end = src.IndexOf("return;", at, StringComparison.Ordinal);
            string branch = src.Substring(at, end - at);

            // Motors decode from bytes 2 and 3 of the five-byte struct.
            Assert.Contains("data[2]", branch);
            Assert.Contains("data[3]", branch);
            // The flags byte and beyond are never motor data.
            Assert.DoesNotContain("data[4]", branch);
            Assert.DoesNotContain("data[5]", branch);
            // And an XUSB vibration write clears the trigger lane.
            Assert.Contains("LeftTriggerMotorSpeed = 0", branch);
            Assert.Contains("RightTriggerMotorSpeed = 0", branch);
        }

        /// <summary>Real impulse triggers keep their lane: the 7-byte HID
        /// 0x0F shape with trigger magnitudes at bytes 0 and 1, probed
        /// 2026-05-19 and re-probed live 2026-08-29.</summary>
        [Fact]
        public void TheHidImpulseLaneStillDecodesTriggersAtTheFront()
        {
            string src = RepoText("PadForge.App", "Common", "Input", "HMaestroVirtualController.cs");
            int at = src.IndexOf("pkt.Source == HMOutputSource.HidOutput", StringComparison.Ordinal);
            Assert.True(at > 0);
            int end = src.IndexOf("return;", at, StringComparison.Ordinal);
            string branch = src.Substring(at, end - at);
            Assert.Contains("LeftTriggerMotorSpeed = (ushort)(data[0] * 655)", branch);
            Assert.Contains("RightTriggerMotorSpeed = (ushort)(data[1] * 655)", branch);
        }

        /// <summary>The #350 evidence lines log once per SHAPE, and a shape
        /// hash must exclude the motor magnitudes: hashing them made every
        /// rumble frame a new shape and flooded the diagnostics ring at
        /// rumble rate, evicting the context the lines exist to preserve.</summary>
        [Fact]
        public void TraceSignaturesHashShapeNotPayload()
        {
            string src = RepoText("PadForge.App", "Common", "Input", "HMaestroVirtualController.cs");
            // FFBANY: source + report id + length only, no data-byte loop.
            Assert.DoesNotContain("sig = sig * 31 + dspan[i]", src);
            // FFBXIN: the length is the dialect, nothing else joins the hash.
            Assert.Contains("int sig = data.Length;", src);
            Assert.DoesNotContain("sig = sig * 31 + data[i]", src);
        }
    }
}
