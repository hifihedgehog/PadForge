using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The battery a game reads off a virtual Sony pad, decoded the way the
    /// readers actually decode it rather than the way the packer writes it.
    ///
    /// <para>The DS4 packer put the canonical battery byte one position late.
    /// This buffer holds DATA BYTES ONLY, and the references number their
    /// offsets from the report id: DS4Windows reads inputReport[30] while its
    /// own stick X sits at inputReport[1], and SDL points PS4StatePacket_t at
    /// &amp;data[1] with ucBatteryLevel at 29. Carrying that 30 across wrote
    /// the battery into a pad byte, so the real one stayed zero, and zero
    /// decodes as on-battery at the bottom of the scale. The DualSense packer
    /// had it right, which is the control that proves the cause is the offset
    /// and not the value being fed in.</para>
    /// </summary>
    public class SonyBatteryReportTests
    {
        private const int Ds4BatteryByte = 29;   // SDL PS4StatePacket_t.ucBatteryLevel
        private const int Ds5BatteryByte = 52;   // SDL PS5StatePacket_t.ucBatteryLevel
        private const int Ds5ConnectByte = 53;

        private static byte[] PackDs4(byte percent, bool charging)
        {
            var dest = new byte[63];
            Pack("dualshock-4-v2", percent, charging, dest);
            return dest;
        }

        private static byte[] PackDs5(byte percent, bool charging)
        {
            var dest = new byte[63];
            Pack("dualsense", percent, charging, dest);
            return dest;
        }

        private static void Pack(string profileId, byte percent, bool charging, byte[] dest)
        {
            var packer = SonyReportPackers.ForProfile(profileId);
            Assert.NotNull(packer);
            Gamepad gp = default;
            TouchpadState tp = default;
            MotionSnapshot motion = default;
            packer(in gp, in tp, in motion, percent, charging, 1u, 0u, dest);
        }

        /// <summary>SDL's PS4 decode, verbatim from SDL_hidapi_ps4.c: the low
        /// nibble is the level, bit 4 is the cable, and a discharging pad
        /// reports level * 10 + 5.</summary>
        private static (string State, int Percent) DecodeLikeSdlPs4(byte b)
        {
            int level = b & 0x0F;
            if ((b & 0x10) != 0)
            {
                if (level <= 10) return ("charging", Math.Min(level * 10 + 5, 100));
                if (level == 11) return ("charged", 100);
                return ("unknown", 0);
            }
            return ("on battery", Math.Min(level * 10 + 5, 100));
        }

        /// <summary>DS4Windows' decode: the nibble over the max for the cable
        /// state, 8 discharging and 11 on USB.</summary>
        private static int DecodeLikeDs4Windows(byte b)
        {
            bool charging = (b & 0x10) != 0;
            int max = charging ? 11 : 8;
            return Math.Min((b & 0x0F) * 100 / max, 100);
        }

        /// <summary>A full pad reads full. Before the fix the byte a reader
        /// consults was never written, so this read 5 percent.</summary>
        [Fact]
        public void AFullDs4ReadsFullAndNotTheBottomOfTheScale()
        {
            var frame = PackDs4(100, charging: false);

            var (state, percent) = DecodeLikeSdlPs4(frame[Ds4BatteryByte]);
            Assert.Equal("on battery", state);
            Assert.Equal(85, percent);          // level 8 of 8, SDL's 8*10+5
            Assert.Equal(100, DecodeLikeDs4Windows(frame[Ds4BatteryByte]));
            Assert.NotEqual(5, percent);
        }

        /// <summary>The level tracks the pad rather than sitting at one value,
        /// which is the "hardcoded" half of the report.</summary>
        [Theory]
        [InlineData((byte)0, 0)]
        [InlineData((byte)25, 25)]
        [InlineData((byte)50, 50)]
        [InlineData((byte)75, 75)]
        [InlineData((byte)100, 100)]
        public void TheDs4LevelFollowsTheAssignedPad(byte percent, int expected)
        {
            var frame = PackDs4(percent, charging: false);

            Assert.Equal(expected, DecodeLikeDs4Windows(frame[Ds4BatteryByte]));
        }

        /// <summary>A charging pad sets the cable bit and uses the wider
        /// range, which is what tells a reader to scale over 11.</summary>
        [Fact]
        public void AChargingDs4SetsTheCableBit()
        {
            var frame = PackDs4(100, charging: true);

            Assert.Equal(0x10, frame[Ds4BatteryByte] & 0x10);
            var (state, percent) = DecodeLikeSdlPs4(frame[Ds4BatteryByte]);
            Assert.Equal("charged", state);     // level 11
            Assert.Equal(100, percent);
            Assert.Equal(100, DecodeLikeDs4Windows(frame[Ds4BatteryByte]));
        }

        /// <summary>Byte 30 belongs to the pad block a real DS4 leaves alone.
        /// It held the battery, which is where it was going wrong.</summary>
        [Fact]
        public void ThePadByteAfterTheBatteryStaysClear()
        {
            Assert.Equal(0, PackDs4(100, charging: false)[30]);
            Assert.Equal(0, PackDs4(0, charging: true)[30]);
        }

        /// <summary>The DualSense was always correct. It is the control: same
        /// input, same slot battery, right offset all along.</summary>
        [Theory]
        [InlineData((byte)0, 5)]
        [InlineData((byte)50, 55)]
        [InlineData((byte)100, 100)]
        public void TheDualSenseLevelWasNeverWrong(byte percent, int expectedSdlPercent)
        {
            var frame = PackDs5(percent, charging: false);

            int status = (frame[Ds5BatteryByte] >> 4) & 0x0F;
            int level = frame[Ds5BatteryByte] & 0x0F;
            Assert.Equal(0, status);            // discharging
            Assert.Equal(expectedSdlPercent, Math.Min(level * 10 + 5, 100));
            Assert.Equal(0x08, frame[Ds5ConnectByte]);   // USB cable
        }

        /// <summary>Both packers write their battery where the reader looks,
        /// and the two offsets are genuinely different bytes in different
        /// reports. Pinned so neither drifts onto the other's number.</summary>
        [Fact]
        public void EachFamilyWritesItsOwnOffset()
        {
            var ds4 = PackDs4(100, charging: false);
            var ds5 = PackDs5(100, charging: false);

            Assert.NotEqual(0, ds4[Ds4BatteryByte]);
            Assert.NotEqual(0, ds5[Ds5BatteryByte]);
            Assert.Equal(0, ds4[Ds5BatteryByte]);
        }
    }
}
