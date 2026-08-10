using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A Sony USB-shape profile with no registered packer falls through to
    /// plain SubmitState and silently loses touchpad, gyro, accel and
    /// battery. That is invisible in code review and presents to the user as
    /// "the touchpad does not work at all". The composite personas shipped
    /// missing exactly that, so every USB-shape Sony profile PadForge can
    /// select is pinned here.
    /// </summary>
    public class SonyPackerCoverageTests
    {
        [Theory]
        [InlineData("dualsense")]
        [InlineData("dualsense-edge")]
        [InlineData("dualshock-4-v1")]
        [InlineData("dualshock-4-v1-full")]
        [InlineData("dualshock-4-v2")]
        [InlineData("dualsense-composite")]
        [InlineData("dualsense-edge-composite")]
        [InlineData("dualshock-4-v2-composite")]
        public void EveryUsbShapeSonyProfile_HasAPacker(string profileId)
        {
            Assert.NotNull(SonyReportPackers.ForProfile(profileId));
        }

        [Fact]
        public void CompositePersonas_UseTheSamePackerAsTheirBaseProfile()
        {
            // The composite HID interface is byte-identical to the base
            // profile's, so the packing must be identical too. Compare the
            // target METHOD: each dictionary entry builds its own delegate
            // instance, so reference identity is not the property under test.
            Assert.Equal(SonyReportPackers.ForProfile("dualsense").Method,
                         SonyReportPackers.ForProfile("dualsense-composite").Method);
            Assert.Equal(SonyReportPackers.ForProfile("dualsense-edge").Method,
                         SonyReportPackers.ForProfile("dualsense-edge-composite").Method);
            Assert.Equal(SonyReportPackers.ForProfile("dualshock-4-v2").Method,
                         SonyReportPackers.ForProfile("dualshock-4-v2-composite").Method);
        }

        // ── The default profile must be packable ──
        //
        // The composite personas shipped with no packer, and the same change
        // made one of them the PlayStation default. A slot created from the
        // default then lost touchpad, gyro, accel, battery and the rolling
        // counter at once. Tie the two together so a future default change
        // cannot reintroduce it.

        [Fact]
        public void TheDefaultPlayStationProfile_HasAPacker()
        {
            string id = InputManager.GetDefaultProfileId(
                PadForge.Engine.VirtualControllerType.PlayStation);
            Assert.False(string.IsNullOrEmpty(id));
            Assert.NotNull(SonyReportPackers.ForProfile(id));
        }

        // ── The packer's OUTPUT, not merely its presence ──
        //
        // Registration alone does not prove the passthrough works. These pin
        // the bytes the user actually reported missing: touchpad, gyro and
        // the rolling counter.

        private static byte[] PackDualSense(
            TouchpadState tp, MotionSnapshot motion, uint frameCounter)
        {
            var gp = default(Gamepad);
            var dest = new byte[63];
            SonyReportPackers.ForProfile("dualsense-composite")(
                in gp, in tp, in motion, 100, false, frameCounter, dest);
            return dest;
        }

        [Fact]
        public void Packer_WritesTheRollingCounter()
        {
            // ds.daidr.me shows this as the sequence number. A frozen counter
            // is what a stalled or absent packer looks like on the wire.
            var tp = default(TouchpadState);
            var m = default(MotionSnapshot);
            Assert.Equal(0x00, PackDualSense(tp, m, 0)[6]);
            Assert.Equal(0x2A, PackDualSense(tp, m, 0x2A)[6]);
            Assert.Equal(0xFF, PackDualSense(tp, m, 0xFF)[6]);
            Assert.Equal(0x00, PackDualSense(tp, m, 0x100)[6]);   // wraps in one byte
        }

        [Fact]
        public void Packer_WritesTouchpadContactState()
        {
            var m = default(MotionSnapshot);

            var up = default(TouchpadState);
            up.Down0 = false;
            // Bit 7 set means "no contact" in the Sony touch byte.
            Assert.Equal(0x80, PackDualSense(up, m, 0)[32] & 0x80);

            var down = default(TouchpadState);
            down.Down0 = true;
            down.X0 = 0.5f; down.Y0 = 0.5f;
            Assert.Equal(0x00, PackDualSense(down, m, 0)[32] & 0x80);
        }

        [Fact]
        public void Packer_MovingTheFingerChangesTheTouchBytes()
        {
            var m = default(MotionSnapshot);
            var a = default(TouchpadState); a.Down0 = true; a.X0 = 0.10f; a.Y0 = 0.10f;
            var b = default(TouchpadState); b.Down0 = true; b.X0 = 0.90f; b.Y0 = 0.90f;

            var pa = PackDualSense(a, m, 0);
            var pb = PackDualSense(b, m, 0);
            Assert.NotEqual(
                new[] { pa[33], pa[34], pa[35] },
                new[] { pb[33], pb[34], pb[35] });
        }

        [Fact]
        public void Packer_WritesGyroAndAccel()
        {
            var tp = default(TouchpadState);
            var still = default(MotionSnapshot);
            var moving = default(MotionSnapshot);
            moving.GyroPitch = 1.5f; moving.GyroYaw = -2.0f; moving.GyroRoll = 0.75f;
            moving.AccelX = 0.5f; moving.AccelY = -0.25f; moving.AccelZ = 1.0f;

            var ps = PackDualSense(tp, still, 0);
            var pm = PackDualSense(tp, moving, 0);

            // Gyro occupies bytes 15-20, accel 21-26, int16 LE each.
            bool gyroDiffers = false, accelDiffers = false;
            for (int i = 15; i < 21; i++) if (ps[i] != pm[i]) gyroDiffers = true;
            for (int i = 21; i < 27; i++) if (ps[i] != pm[i]) accelDiffers = true;
            Assert.True(gyroDiffers, "gyro bytes 15-20 did not follow the motion snapshot");
            Assert.True(accelDiffers, "accel bytes 21-26 did not follow the motion snapshot");
        }

        [Fact]
        public void UnknownProfile_HasNoPacker()
        {
            Assert.Null(SonyReportPackers.ForProfile("not-a-profile"));
            Assert.Null(SonyReportPackers.ForProfile(""));
            Assert.Null(SonyReportPackers.ForProfile(null));
        }
    }
}
