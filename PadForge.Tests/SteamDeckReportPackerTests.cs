using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #338: the Neptune input frame the steam-deck-composite persona will
    /// submit. Every expectation is transcribed from the two references the
    /// packer cites: HandheldCompanion SteamDeckTarget.BuildReport (emitting
    /// side, proven against live Steam) and SDL's SteamDeckStatePacket_t +
    /// SDL_hidapi_steamdeck.c decode (consuming side). Where the two
    /// references disagree (gyro scale: HC 16 LSB/deg/s vs SDL 2000 deg/s
    /// full scale), the consuming side wins and the test pins that choice.
    /// </summary>
    public class SteamDeckReportPackerTests
    {
        private static byte[] Pack(Action<RawHidState> arrange = null,
            TouchpadState tp = default, MotionSnapshot motion = default,
            uint packetNum = 7)
        {
            var raw = RawHidState.Create(8, 22, 1);
            raw.Povs[0] = -1;
            arrange?.Invoke(raw);
            var dest = new byte[SteamDeckReportPacker.ReportSize];
            SteamDeckReportPacker.ByProfileId["steam-deck-composite"](
                raw, tp, motion, packetNum, dest);
            return dest;
        }

        private static ushort U16(byte[] b, int off) => (ushort)(b[off] | (b[off + 1] << 8));
        private static short I16(byte[] b, int off) => (short)(b[off] | (b[off + 1] << 8));

        /// <summary>The lookup is the activation gate: it must miss for
        /// every profile id shipping today, so the lane stays inert until
        /// HM#56 ships the persona.</summary>
        [Theory]
        [InlineData("steam-deck")]
        [InlineData("steam-controller")]
        [InlineData("dualsense-composite")]
        [InlineData(null)]
        public void ShippingProfiles_DoNotActivateTheLane(string id)
            => Assert.Null(SteamDeckReportPacker.ForProfile(id));

        [Fact]
        public void PersonaProfile_Activates()
            => Assert.NotNull(SteamDeckReportPacker.ForProfile("Steam-Deck-Composite"));

        /// <summary>ValveInReport header: version 0x0001, type 0x09
        /// (ID_CONTROLLER_DECK_STATE), length 64.</summary>
        [Fact]
        public void Header_IsVersionTypeLength()
        {
            var b = Pack();
            Assert.Equal(0x01, b[0]);
            Assert.Equal(0x00, b[1]);
            Assert.Equal(0x09, b[2]);
            Assert.Equal(0x40, b[3]);
        }

        /// <summary>THE DIVERGENCE FROM HC, on purpose: unPacketNum
        /// increments. SDL's struct comment licenses consumers to skip
        /// processing while the packet number is unchanged, so HC's constant
        /// zero freezes any consumer honoring that contract.</summary>
        [Fact]
        public void PacketNumber_IsCarried()
        {
            var b = Pack(packetNum: 0x0403_0201);
            Assert.Equal(0x01, b[4]);
            Assert.Equal(0x02, b[5]);
            Assert.Equal(0x03, b[6]);
            Assert.Equal(0x04, b[7]);
        }

        /// <summary>Face and shoulder bits per STEAMDECK_LBUTTON: A 0x80,
        /// B 0x20, X 0x40, Y 0x10, L 0x08, R 0x04 in byte 8.</summary>
        [Theory]
        [InlineData(0, 8, 0x80)]   // A
        [InlineData(1, 8, 0x20)]   // B
        [InlineData(2, 8, 0x40)]   // X
        [InlineData(3, 8, 0x10)]   // Y
        [InlineData(4, 8, 0x08)]   // LB -> L
        [InlineData(5, 8, 0x04)]   // RB -> R
        [InlineData(7, 9, 0x10)]   // Start -> Menu
        [InlineData(10, 9, 0x20)]  // Guide -> Steam
        [InlineData(6, 9, 0x40)]   // Back -> View
        [InlineData(8, 10, 0x40)]  // LS -> L3
        [InlineData(11, 14, 0x04)] // QAM
        public void Buttons_LandOnTheirNeptuneBits(int rawIndex, int byteOff, byte bit)
        {
            var b = Pack(r => r.SetButton(rawIndex, true));
            Assert.Equal(bit, (byte)(b[byteOff] & bit));
        }

        /// <summary>R3 is the lone bit in byte 11 (ulButtonsL bits 24..31).</summary>
        [Fact]
        public void RightStickClick_LandsInByte11()
        {
            var b = Pack(r => r.SetButton(9, true));
            Assert.Equal(0x04, b[11]);
        }

        /// <summary>The paddle cross-map is the part worth pinning: the raw
        /// surface uses SDL's paddle order (12 RIGHT_PADDLE1, 13 LEFT_PADDLE1,
        /// 14 RIGHT_PADDLE2, 15 LEFT_PADDLE2) and the Deck names them R4, L4,
        /// R5, L5 across three different bytes (SDL_hidapi_steamdeck.c decode:
        /// HBUTTON_R4 0x0400, HBUTTON_L4 0x0200, LBUTTON_R5 0x00010000,
        /// LBUTTON_L5 0x00008000).</summary>
        [Theory]
        [InlineData(12, 13, 0x04)] // RIGHT_PADDLE1 -> R4
        [InlineData(13, 13, 0x02)] // LEFT_PADDLE1 -> L4
        [InlineData(14, 10, 0x01)] // RIGHT_PADDLE2 -> R5
        [InlineData(15, 9, 0x80)]  // LEFT_PADDLE2 -> L5
        public void Paddles_CrossMapToDeckNames(int rawIndex, int byteOff, byte bit)
        {
            var b = Pack(r => r.SetButton(rawIndex, true));
            Assert.Equal(bit, (byte)(b[byteOff] & bit));
        }

        /// <summary>Sticks: X passes through, Y flips (HID top-down to the
        /// Deck's +Y up), and -32768 saturates instead of overflowing.</summary>
        [Fact]
        public void Sticks_FlipYAndSaturate()
        {
            var b = Pack(r =>
            {
                r.Axes[0] = 1000;
                r.Axes[1] = short.MinValue;
                r.Axes[3] = -2000;
                r.Axes[4] = 3000;
            });
            Assert.Equal(1000, I16(b, 48));
            Assert.Equal(short.MaxValue, I16(b, 50));
            Assert.Equal(-2000, I16(b, 52));
            Assert.Equal(-3000, I16(b, 54));
        }

        /// <summary>Triggers ride 0..32767 (HC's scale), negatives clamp to
        /// rest, and a pulled trigger also sets its digital full-pull bit
        /// (byte 8: L2 0x02, R2 0x01).</summary>
        [Fact]
        public void Triggers_ClampAndSetDigitalBits()
        {
            var b = Pack(r => { r.Axes[2] = 16000; r.Axes[5] = -5; });
            Assert.Equal(16000, U16(b, 44));
            Assert.Equal(0, U16(b, 46));
            Assert.Equal(0x02, b[8] & 0x03);
        }

        /// <summary>Finger 0 is the LEFT pad, finger 1 the RIGHT, coordinates
        /// centered signed with Y flipped, touch bits in byte 10.</summary>
        [Fact]
        public void Touch_SplitsFingersAcrossThePads()
        {
            var tp = new TouchpadState
            {
                Down0 = true, X0 = 1f, Y0 = 0f,   // left pad, top-right corner
                Down1 = true, X1 = 0.5f, Y1 = 0.5f, // right pad, center
            };
            var b = Pack(tp: tp);
            Assert.Equal(0x08 | 0x10, b[10] & 0x18);
            Assert.Equal(short.MaxValue, I16(b, 16)); // X0=1 -> +max
            Assert.Equal(short.MaxValue, I16(b, 18)); // Y0=0 (top) -> +max (up)
            Assert.Equal(0, I16(b, 20));              // X1=0.5 -> center
            Assert.Equal(0, I16(b, 22));
        }

        /// <summary>The single click slot clicks the touched pad, and the
        /// left one when neither is touched. Pressure mirrors the click at
        /// full scale (HC's value).</summary>
        [Fact]
        public void PadClick_FollowsTheTouchedPad()
        {
            var right = Pack(r => r.SetButton(16, true),
                tp: new TouchpadState { Down1 = true, X1 = 0.5f, Y1 = 0.5f });
            Assert.Equal(0x04, right[10] & 0x06);          // right click only
            Assert.Equal(ushort.MaxValue, U16(right, 58)); // right pressure

            var none = Pack(r => r.SetButton(16, true));
            Assert.Equal(0x02, none[10] & 0x06);           // left fallback
            Assert.Equal(ushort.MaxValue, U16(none, 56));
        }

        /// <summary>IMU frame and scale: the exact inverse of SDL's decode.
        /// SDL.x = sGyroX, SDL.y = sGyroZ, SDL.z = -sGyroY, 2000 deg/s and
        /// 2 g full scale over i16. A 500 deg/s yaw (SDL.y) must land in
        /// sGyroZ at one quarter of full scale, and a 1 g AccelY (SDL.y)
        /// in sAccelZ at half scale.</summary>
        [Fact]
        public void Imu_InvertsSdlsDecode()
        {
            var m = new MotionSnapshot
            {
                GyroYaw = 500f,   // SDL.y
                AccelY = 1f,      // SDL.y, in g
                HasMotion = true,
            };
            var b = Pack(motion: m);
            Assert.Equal(8192, I16(b, 34));   // sGyroZ = 500/2000*32768
            Assert.Equal(16384, I16(b, 28));  // sAccelZ = 1/2*32768
            Assert.Equal(0, I16(b, 30));      // sGyroX untouched
        }

        /// <summary>Roll (SDL.z) lands NEGATED in sGyroY, accel likewise:
        /// the one sign flip in the frame map.</summary>
        [Fact]
        public void Imu_NegatesTheYSlot()
        {
            var m = new MotionSnapshot { GyroRoll = 1000f, AccelZ = 2f, HasMotion = true };
            var b = Pack(motion: m);
            Assert.Equal(-16384, I16(b, 32));       // sGyroY = -1000/2000*32768
            Assert.Equal(short.MinValue, I16(b, 26)); // sAccelY = -2/2*32768, clamped
        }

        /// <summary>The hat decomposes 8-way: 31500 (up-left) sets both up
        /// and left, centered (-1) sets none.</summary>
        [Fact]
        public void Dpad_DecodesDiagonalsFromTheHat()
        {
            var b = Pack(r => r.Povs[0] = 31500);
            Assert.Equal(0x01 | 0x04, b[9] & 0x0F);
            var c = Pack();
            Assert.Equal(0, c[9] & 0x0F);
        }

        /// <summary>Nothing past the struct: bytes 60..63 stay zero (HC
        /// writes stick pseudo-pressures there, past SteamDeckStatePacket_t's
        /// end; this packer stops where the struct stops), and the quaternion
        /// block stays zero.</summary>
        [Fact]
        public void UnmodeledBytes_StayZero()
        {
            var b = Pack(r => { r.SetButton(8, true); r.SetButton(9, true); });
            for (int i = 36; i < 44; i++) Assert.Equal(0, b[i]);
            for (int i = 60; i < 64; i++) Assert.Equal(0, b[i]);
        }
    }
}
