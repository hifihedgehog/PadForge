using System;
using System.IO;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #433: a game with adaptive triggers reads the DualSense's
    /// trigger feedback bytes (payload 41, 42 and the effect nibbles at 47)
    /// back from the pad, and the virtual DualSense left the whole 40..47
    /// range zero, so an L2 hold against a trigger effect never registered.
    /// The physical pad's bytes now ride into the packed report once the SDL
    /// fork publishes them. Until then the value is zero and the report is
    /// byte-for-byte what it was.
    /// </summary>
    public class DualSenseStatusBytesTests
    {
        private static byte[] Pack(string profile)
        {
            var gp = default(Gamepad);
            var tp = default(TouchpadState);
            var motion = default(MotionSnapshot);
            var dest = new byte[63];
            SonyReportPackers.ForProfile(profile)(in gp, in tp, in motion, 100, false, 1, 0, dest);
            return dest;
        }

        [Fact]
        public void StatusBytesLandAtPayload40To47LittleEndian()
        {
            var dest = Pack("dualsense");
            var untouched = (byte[])dest.Clone();
            SonyReportPackers.ApplyDualSenseStatusBytes(dest, 0x4746454443424140UL);
            for (int i = 0; i < 8; i++)
                Assert.Equal((byte)(0x40 + i), dest[40 + i]);
            // Right trigger feedback, left trigger feedback, effect modes.
            Assert.Equal(0x41, dest[41]);
            Assert.Equal(0x42, dest[42]);
            Assert.Equal(0x47, dest[47]);
            for (int i = 0; i < dest.Length; i++)
                if (i < 40 || i > 47) Assert.Equal(untouched[i], dest[i]);
        }

        [Fact]
        public void ZeroLeavesTheReportUntouched()
        {
            var dest = Pack("dualsense-edge-composite");
            var untouched = (byte[])dest.Clone();
            SonyReportPackers.ApplyDualSenseStatusBytes(dest, 0);
            Assert.Equal(untouched, dest);
            for (int i = 40; i < 48; i++) Assert.Equal(0, dest[i]);
        }

        [Theory]
        [InlineData("dualsense", true)]
        [InlineData("dualsense-edge", true)]
        [InlineData("dualsense-composite", true)]
        [InlineData("dualsense-edge-composite", true)]
        [InlineData("dualshock-4-v2", false)]
        [InlineData("dualshock-4-v2-composite", false)]
        [InlineData("dualshock-4-v1-full", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void OnlyDualSenseProfilesCarryTheBytes(string profile, bool expected)
        {
            Assert.Equal(expected, SonyReportPackers.IsDualSenseProfile(profile));
        }

        [Fact]
        public void ReaderIsInertWithoutAGamepad()
        {
            Assert.Equal(0UL, DualSenseStatusBytes.Read(IntPtr.Zero));
            Assert.True(DualSenseStatusBytes.IsDualSense(0x054C, 0x0CE6));
            Assert.True(DualSenseStatusBytes.IsDualSense(0x054C, 0x0DF2));
            Assert.False(DualSenseStatusBytes.IsDualSense(0x054C, 0x09CC));
            Assert.False(DualSenseStatusBytes.IsDualSense(0x045E, 0x0CE6));
            Assert.Equal("SDL.joystick.hidapi.ps5.status_bytes", DualSenseStatusBytes.Property);
        }

        [Fact]
        public void StepFiveStampsTheBytesBetweenThePackerAndTheSubmit()
        {
            string step5 = Read("PadForge.App/Common/Input/InputManager.Step5.VirtualDevices.cs");
            string between = Between(step5,
                "var packer = SonyReportPackers.ForProfile(hmPs.ProfileId);",
                "hmPs.SubmitRawReport(rawReportScratch);");
            Assert.Contains("SonyReportPackers.IsDualSenseProfile(hmPs.ProfileId)", between);
            Assert.Contains("SonyReportPackers.ApplyDualSenseStatusBytes(", between);
            Assert.Contains("Ds5StatusBytes[padIndex]", between);
            string manager = Read("PadForge.App/Common/Input/InputManager.cs");
            Assert.Contains("Ds5StatusBytes[padIndex] = ds5StatusBytes;", manager);
            Assert.Contains("DualSenseStatusBytes.Read(ud.Device.GamepadHandle)", manager);
        }

        private static string Root()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "PadForge.App"))) root = root.Parent;
            Assert.NotNull(root);
            return root.FullName;
        }

        /// <summary>Only a PlayStation slot reads the bytes from SDL on a
        /// pass, and every reader of them in Step 5 sits in the PlayStation
        /// slot's branch, so no other slot's frame differs for it.</summary>
        [Fact]
        public void OnlyAPlayStationSlotReadsTheBytes()
        {
            foreach (VirtualControllerType type in Enum.GetValues(typeof(VirtualControllerType)))
                Assert.Equal(type == VirtualControllerType.PlayStation, InputManager.SlotWantsDualSenseStatusBytes(type));
            string step5 = Read("PadForge.App/Common/Input/InputManager.Step5.VirtualDevices.cs");
            // The PlayStation slot branch: its test is the line before.
            Assert.Matches(@"SlotControllerTypes\[padIndex\] == VirtualControllerType\.PlayStation\s+&& vc is HMaestroVirtualController hmPs\)", step5);
            string branch = Between(step5, "&& vc is HMaestroVirtualController hmPs)", "hmPs.SubmitRawReport(rawReportScratch);");
            int all = step5.Split("Ds5StatusBytes[").Length - 1;
            Assert.True(all > 0);
            Assert.Equal(all, branch.Split("Ds5StatusBytes[").Length - 1);
            string manager = Read("PadForge.App/Common/Input/InputManager.cs");
            Assert.Contains("bool sonySlot = SlotWantsDualSenseStatusBytes(SlotControllerTypes[padIndex]);", manager);
            Assert.Contains("if (sonySlot && ds5StatusBytes == 0", manager);
        }

        private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative));

        private static string Between(string text, string start, string end)
        {
            int a = text.IndexOf(start, StringComparison.Ordinal);
            Assert.True(a >= 0, start);
            int b = text.IndexOf(end, a, StringComparison.Ordinal);
            Assert.True(b > a, end);
            return text[a..b];
        }
    }
}
