using System;
using PadForge.Common.Input;
using PadForge.Engine.Haptics;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #223: the combined Joy-Con pair's dual-coil tone sink routes
    /// audio by the slot's commanded motor sides. Left motor commanded -> left
    /// Joy-Con coil, right -> right (the SDL split: the pair's left child keeps
    /// only low_frequency_rumble, the right child only high_frequency_rumble,
    /// SDL_hidapi_switch.c:2148-2156). Both motors -> both coils. NO motor ->
    /// both coils, because audio is not rumble: macro sounds, the system-audio
    /// mirror, and remote frames arrive with no motor state at all, so motor
    /// state only steers side emphasis. A side stays hot for PairSideHoldMs
    /// after its motor drops so packet-rate rumble flap never strobes the tone.
    ///
    /// Packet ground truth: joycon-singer main_pc.cpp:54-86 (0x10 report,
    /// timer &amp; 0x0F, 4-byte halves, RUMBLE_NEUTRAL {0x00,0x01,0x40,0x40}
    /// in the unused half, rumble.h:10) and dekuNukem bluetooth_hid_notes.md:45
    /// ("a timing byte, then 4 bytes of rumble data for left Joy-Con, followed
    /// by 4 bytes for right Joy-Con").
    /// </summary>
    public class JoyConPairDualCoilTests
    {
        // Fresh-sink "never seen a motor" timestamp, the Sink field default.
        private const long Cold = long.MinValue / 2;

        private static readonly byte[] Neutral = { 0x00, 0x01, 0x40, 0x40 };

        // ── ResolvePairSides: left / right / both / none ─────────────────

        [Fact]
        public void NoMotorCommanded_PlaysBothCoils()
        {
            long l = Cold, r = Cold;
            var (left, right) = HapticToneService.ResolvePairSides(
                false, false, nowMs: 10_000, ref l, ref r);
            Assert.True(left);
            Assert.True(right);
        }

        [Fact]
        public void LeftMotorOnly_RoutesLeftCoilOnly()
        {
            long l = Cold, r = Cold;
            var (left, right) = HapticToneService.ResolvePairSides(
                true, false, nowMs: 10_000, ref l, ref r);
            Assert.True(left);
            Assert.False(right);
        }

        [Fact]
        public void RightMotorOnly_RoutesRightCoilOnly()
        {
            long l = Cold, r = Cold;
            var (left, right) = HapticToneService.ResolvePairSides(
                false, true, nowMs: 10_000, ref l, ref r);
            Assert.False(left);
            Assert.True(right);
        }

        [Fact]
        public void BothMotors_PlaysBothCoils()
        {
            long l = Cold, r = Cold;
            var (left, right) = HapticToneService.ResolvePairSides(
                true, true, nowMs: 10_000, ref l, ref r);
            Assert.True(left);
            Assert.True(right);
        }

        // ── Hold window ──────────────────────────────────────────────────

        [Fact]
        public void DroppedMotor_StaysHotThroughHoldWindow()
        {
            long l = Cold, r = Cold;
            HapticToneService.ResolvePairSides(true, false, 1000, ref l, ref r, holdMs: 300);

            // At exactly the hold boundary the side is still hot and the
            // other side is still cold: no stutter, no premature both-coils.
            var (left, right) = HapticToneService.ResolvePairSides(
                false, false, 1300, ref l, ref r, holdMs: 300);
            Assert.True(left);
            Assert.False(right);
        }

        [Fact]
        public void HoldExpiredWithNoMotor_ReturnsToBothCoils()
        {
            long l = Cold, r = Cold;
            HapticToneService.ResolvePairSides(true, false, 1000, ref l, ref r, holdMs: 300);

            // Past the hold with nothing commanded: back to the no-motor
            // default, both coils.
            var (left, right) = HapticToneService.ResolvePairSides(
                false, false, 1301, ref l, ref r, holdMs: 300);
            Assert.True(left);
            Assert.True(right);
        }

        [Fact]
        public void PulsingMotor_NeverStrobes()
        {
            // Game pulses the left motor every 100 ms (packet-rate flap).
            // Every intermediate tick must keep left hot and right cold.
            long l = Cold, r = Cold;
            for (long t = 1000; t <= 2000; t += 10)
            {
                bool pulseOn = ((t - 1000) / 100) % 2 == 0;
                var (left, right) = HapticToneService.ResolvePairSides(
                    pulseOn, false, t, ref l, ref r, holdMs: 300);
                Assert.True(left);
                Assert.False(right);
            }
        }

        [Fact]
        public void RightEngagesDuringLeftHold_BothCoils()
        {
            long l = Cold, r = Cold;
            HapticToneService.ResolvePairSides(true, false, 1000, ref l, ref r, holdMs: 300);

            // Left dropped 100 ms ago (still holding), right just engaged.
            var (left, right) = HapticToneService.ResolvePairSides(
                false, true, 1100, ref l, ref r, holdMs: 300);
            Assert.True(left);
            Assert.True(right);
        }

        [Fact]
        public void HoldIsPerSide()
        {
            long l = Cold, r = Cold;
            HapticToneService.ResolvePairSides(true, true, 1000, ref l, ref r, holdMs: 300);

            // Right dropped, left keeps going: right holds until 1300 then
            // goes cold while left stays hot.
            var during = HapticToneService.ResolvePairSides(
                true, false, 1200, ref l, ref r, holdMs: 300);
            Assert.True(during.Left);
            Assert.True(during.Right);

            var after = HapticToneService.ResolvePairSides(
                true, false, 1400, ref l, ref r, holdMs: 300);
            Assert.True(after.Left);
            Assert.False(after.Right);
        }

        [Fact]
        public void HoldConstant_MatchesStreamHangover()
        {
            // The grounded choice: the side hold reuses HangoverMs' value
            // (300 ms), this file's existing "quiet dips inside a cue must
            // not break the stream" smoothing constant.
            Assert.Equal(300, HapticToneService.PairSideHoldMs);
        }

        // ── 0x10 packet halves (joycon-singer / dekuNukem layout) ────────

        [Fact]
        public void RumblePacket_LayoutMatchesReference()
        {
            byte[] left4 = { 1, 2, 3, 4 };
            byte[] right4 = { 5, 6, 7, 8 };
            var buf = HapticToneService.BuildJoyConRumblePacket(5, left4, right4, 49);

            Assert.Equal(49, buf.Length);              // padded to OutputReportByteLength
            Assert.Equal(0x10, buf[0]);                // rumble-only report id (main_pc.cpp:62)
            Assert.Equal(5, buf[1]);                   // rolling timer (main_pc.cpp:63)
            Assert.Equal(left4, buf[2..6]);            // left Joy-Con half (bluetooth_hid_notes.md:45)
            Assert.Equal(right4, buf[6..10]);          // right Joy-Con half
            for (int i = 10; i < buf.Length; i++) Assert.Equal(0, buf[i]);
        }

        [Fact]
        public void RumblePacket_TimerWrapsToFourBits()
        {
            var buf = HapticToneService.BuildJoyConRumblePacket(
                0x1F, Neutral, Neutral, 49);
            Assert.Equal(0x0F, buf[1]); // timer & 0x0F (main_pc.cpp:63)
        }

        [Fact]
        public void RumblePacket_NeverShorterThanReferenceTenBytes()
        {
            // A caps failure falls back to the reference's own 10-byte packet
            // (uint8_t pkt[10], main_pc.cpp:70).
            var buf = HapticToneService.BuildJoyConRumblePacket(0, Neutral, Neutral, 0);
            Assert.Equal(10, buf.Length);
        }

        [Fact]
        public void LeftChildPacket_ToneInLeftHalf_NeutralRight()
        {
            // The dual-handle left-side write: this child's half carries the
            // tone, the other half RUMBLE_NEUTRAL (main_pc.cpp:69-75).
            var tone4 = HapticToneEncoder.EncodeJoyConRumble(220f, 0.5f);
            var buf = HapticToneService.BuildJoyConRumblePacket(
                0, tone4, HapticToneEncoder.JoyConNeutral(), 49);

            Assert.Equal(tone4, buf[2..6]);
            Assert.Equal(Neutral, buf[6..10]);         // RUMBLE_NEUTRAL (rumble.h:10)
        }

        [Fact]
        public void RightChildPacket_NeutralLeft_ToneInRightHalf()
        {
            // Mirror of the left side (main_pc.cpp:77-83).
            var tone4 = HapticToneEncoder.EncodeJoyConRumble(220f, 0.5f);
            var buf = HapticToneService.BuildJoyConRumblePacket(
                0, HapticToneEncoder.JoyConNeutral(), tone4, 49);

            Assert.Equal(Neutral, buf[2..6]);
            Assert.Equal(tone4, buf[6..10]);
        }

        [Fact]
        public void AudibleTone_IsDistinguishableFromNeutral()
        {
            // Side routing only means anything if the neutral half differs
            // from the tone half on the wire.
            var tone4 = HapticToneEncoder.EncodeJoyConRumble(220f, 0.5f);
            Assert.NotEqual(Neutral, tone4);
        }

        // ── Child-path selection / single-coil degradation ───────────────

        [Fact]
        public void BothChildrenPresent_LeftIsPrimary_RightIsSecond()
        {
            var (primary, second, primaryIsRight) =
                HapticToneService.SelectPairChildPaths(@"\\?\hid#L", @"\\?\hid#R");
            Assert.Equal(@"\\?\hid#L", primary);
            Assert.Equal(@"\\?\hid#R", second);
            Assert.False(primaryIsRight);
        }

        [Fact]
        public void LeftOnly_DegradesToLeftCoil()
        {
            var (primary, second, primaryIsRight) =
                HapticToneService.SelectPairChildPaths(@"\\?\hid#L", null);
            Assert.Equal(@"\\?\hid#L", primary);
            Assert.Null(second);
            Assert.False(primaryIsRight);
        }

        [Fact]
        public void RightOnly_DegradesToRightCoil_AndRecordsSide()
        {
            // PairPrimaryIsRight steers which packet half the primary handle
            // gets once the left child is retried in, so it must be recorded
            // at selection time.
            var (primary, second, primaryIsRight) =
                HapticToneService.SelectPairChildPaths(null, @"\\?\hid#R");
            Assert.Equal(@"\\?\hid#R", primary);
            Assert.Null(second);
            Assert.True(primaryIsRight);
        }

        [Fact]
        public void NeitherChild_KeepsSyntheticPathFailure()
        {
            // Null primary keeps the synthetic SDL path, CreateFileW fails as
            // in #184, and the 3 s Reconcile retry stands.
            var (primary, second, _) = HapticToneService.SelectPairChildPaths(null, null);
            Assert.Null(primary);
            Assert.Null(second);
        }
    }
}
