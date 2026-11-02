using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Valve frames PadForge packs, read the way SDL's drivers read them
    /// (#518). HIDMaestro passes these frames on byte for byte, so SDL's
    /// decode in SDL-reference is the definition of each word:
    /// SDL_hidapi_steam.c for the 2015 controller, SDL_hidapi_steamdeck.c for
    /// the Deck and SDL_hidapi_steam_triton.c for the 2026 controller.
    ///
    /// <para>Four places differed. The 2015 frame carried roll negated, all
    /// three set their digital trigger bits at any travel, the Deck and 2026
    /// frames wrote 0xFFFF pressure on a click and 0 on a touch, and the 2026
    /// frame carried its frame counter where SDL reads microseconds.</para>
    /// </summary>
    public class ValveReadersTests
    {
        private const string Deck = "steam-deck-composite";
        private const string Sc2015 = "steam-controller-composite";
        private const string Sc2026 = "steam-controller-2";

        private static byte[] Pack(string id, Action<RawHidState> arrange = null,
            TouchpadState tp = default, MotionSnapshot motion = default, uint imuMicros = 0)
        {
            var raw = RawHidState.Create(8, 32, 1);
            raw.Povs[0] = -1;
            raw.Axes[2] = raw.Axes[5] = short.MinValue;   // triggers at rest
            arrange?.Invoke(raw);
            var dest = new byte[ValveReportPackers.MaxReportSize];
            ValveReportPackers.ForProfile(id).Pack(raw, tp, motion, 1, imuMicros, dest);
            return dest;
        }

        private static short I16(byte[] b, int at) => (short)(b[at] | (b[at + 1] << 8));
        private static ushort U16(byte[] b, int at) => (ushort)(b[at] | (b[at + 1] << 8));
        private static uint U32(byte[] b, int at) => (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));

        /// <summary>The gyro and accel words of each frame: X, Y and Z in
        /// the device's own order.</summary>
        private static (int gx, int gy, int gz, int ax, int ay, int az) Imu(string id) => id switch
        {
            Deck => (30, 32, 34, 24, 26, 28),
            Sc2015 => (34, 36, 38, 28, 30, 32),
            Sc2026 => (40, 42, 44, 34, 36, 38),
            _ => throw new ArgumentException(id),
        };

        /// <summary>SDL's sensor values for a frame, in degrees per second
        /// and g. Every driver scales a word by 2000 degrees per second or
        /// 2 g over 32768 and sends (X, Z, -Y), but the 2015 driver reads its
        /// gyro Z as +Y (SDL_hidapi_steam.c 1684 to 1691,
        /// SDL_hidapi_steamdeck.c 250 to 257, SDL_hidapi_steam_triton.c 279
        /// to 286).</summary>
        private static (float[] gyro, float[] accel) SdlReads(string id, byte[] b)
        {
            var o = Imu(id);
            float G(int at) => I16(b, at) / 32768f * 2000f;
            float A(int at) => I16(b, at) / 32768f * 2f;
            float gyroZ = id == Sc2015 ? G(o.gy) : -G(o.gy);
            return (new[] { G(o.gx), G(o.gz), gyroZ }, new[] { A(o.ax), A(o.az), -A(o.ay) });
        }

        /// <summary>Pitch, yaw and roll, and accel on all three axes, read
        /// back by SDL as they went in, on every frame. The 2015 frame
        /// carried roll negated.</summary>
        [Theory]
        [InlineData(Deck)]
        [InlineData(Sc2015)]
        [InlineData(Sc2026)]
        public void SdlReadsTheMotionAsItWentIn(string id)
        {
            var m = new MotionSnapshot
            {
                GyroPitch = 30f, GyroYaw = 90f, GyroRoll = -60f,
                AccelX = 0.25f, AccelY = 1f, AccelZ = -0.5f,
                HasMotion = true,
            };
            var (gyro, accel) = SdlReads(id, Pack(id, motion: m));
            const float GyroStep = 2000f / 32768f, AccelStep = 2f / 32768f;
            Assert.InRange(gyro[0], 30f - GyroStep, 30f + GyroStep);
            Assert.InRange(gyro[1], 90f - GyroStep, 90f + GyroStep);
            Assert.InRange(gyro[2], -60f - GyroStep, -60f + GyroStep);
            Assert.InRange(accel[0], 0.25f - AccelStep, 0.25f + AccelStep);
            Assert.InRange(accel[1], 1f - AccelStep, 1f + AccelStep);
            Assert.InRange(accel[2], -0.5f - AccelStep, -0.5f + AccelStep);
        }

        /// <summary>Where each frame keeps its two digital trigger bits.</summary>
        private static bool TriggerBit(string id, byte[] b, bool left) => id switch
        {
            Deck => (b[8] & (left ? 0x02 : 0x01)) != 0,
            Sc2015 => (b[8] & (left ? 0x02 : 0x01)) != 0,
            Sc2026 => (U32(b, 2) & (1u << (left ? 27 : 23))) != 0,
            _ => throw new ArgumentException(id),
        };

        /// <summary>The Deck and the 2026 controller set a digital trigger bit
        /// at 90 percent of the pull, 29490 of 32767, Valve's firmware
        /// default, which HIDMaestro 2.0.2 carries as triggerThreshold. A raw
        /// 26210 reaches the wire as 29489 and 26212 as 29490.</summary>
        [Theory]
        [InlineData(Deck)]
        [InlineData(Sc2026)]
        public void TheDeckAndThe2026PadClickTheirTriggersAt90Percent(string id)
        {
            foreach (bool left in new[] { true, false })
            {
                int slot = left ? 2 : 5;
                Assert.False(TriggerBit(id, Pack(id, r => r.Axes[slot] = 16000), left));
                Assert.False(TriggerBit(id, Pack(id, r => r.Axes[slot] = 26210), left));
                Assert.True(TriggerBit(id, Pack(id, r => r.Axes[slot] = 26212), left));
                Assert.True(TriggerBit(id, Pack(id, r => r.Axes[slot] = short.MaxValue), left));
                Assert.False(TriggerBit(id, Pack(id), left));
            }
        }

        /// <summary>The 2015 controller's trigger bits are its triggers'
        /// end-of-travel click: set where its 8-bit trigger byte reads 255,
        /// HIDMaestro 2.0.2's threshold for it (255 of 255). A raw 32510
        /// reaches the byte as 254 and 32512 as 255.</summary>
        [Fact]
        public void The2015PadClicksItsTriggersAtTheEndOfTravel()
        {
            foreach (bool left in new[] { true, false })
            {
                int slot = left ? 2 : 5, at = left ? 11 : 12;
                var short254 = Pack(Sc2015, r => r.Axes[slot] = 32510);
                Assert.Equal(254, short254[at]);
                Assert.False(TriggerBit(Sc2015, short254, left));
                var full = Pack(Sc2015, r => r.Axes[slot] = 32512);
                Assert.Equal(255, full[at]);
                Assert.True(TriggerBit(Sc2015, full, left));
                Assert.False(TriggerBit(Sc2015, Pack(Sc2015, r => r.Axes[slot] = 26212), left));
            }
        }

        /// <summary>A touched or clicked pad reads pressure 1.0 in SDL, which
        /// divides the word by 32768 (SDL_hidapi_steamdeck.c 272 and 284,
        /// SDL_hidapi_steam_triton.c 258 and 270), and an untouched pad 0.
        /// The frames once wrote 0xFFFF on a click, which SDL read as 2.0,
        /// and 0 on a touch.</summary>
        [Theory]
        [InlineData(Deck, 56, 58)]
        [InlineData(Sc2026, 22, 28)]
        public void APressedPadReadsFullPressure(string id, int leftAt, int rightAt)
        {
            float Sdl(byte[] b, int at) => U16(b, at) / 32768f;

            var touch = Pack(id, tp: new TouchpadState { Down0 = true, X0 = 0.5f, Y0 = 0.5f });
            Assert.InRange(Sdl(touch, leftAt), 0.999f, 1f);
            Assert.Equal(0f, Sdl(touch, rightAt));

            var click = Pack(id, tp: new TouchpadState { Down1 = true, X1 = 0.5f, Y1 = 0.5f, Click = true });
            Assert.InRange(Sdl(click, rightAt), 0.999f, 1f);

            var idle = Pack(id);
            Assert.Equal(0f, Sdl(idle, leftAt));
            Assert.Equal(0f, Sdl(idle, rightAt));
        }

        /// <summary>The 2026 frame's IMU timestamp is the time it is handed,
        /// in microseconds, and SDL moves its sensor clock by the difference
        /// between two frames' words (SDL_hidapi_steam_triton.c 274 to 277),
        /// across the 32-bit wrap too. It carried the frame counter.</summary>
        [Fact]
        public void The2026PadsImuTimestampCountsMicroseconds()
        {
            var first = Pack(Sc2026, imuMicros: 4_294_965_000);
            var second = Pack(Sc2026, imuMicros: 2_704);
            Assert.Equal(4_294_965_000u, U32(first, 30));
            Assert.Equal(2_704u, U32(second, 30));
            Assert.Equal(5_000u, unchecked(U32(second, 30) - U32(first, 30)));
        }

        /// <summary>The clock the engine hands the packers moves forward in
        /// microseconds.</summary>
        [Fact]
        public void TheMicrosecondClockMovesForward()
        {
            uint a = ValveReportPackers.MicrosNow();
            System.Threading.Thread.Sleep(20);
            uint b = ValveReportPackers.MicrosNow();
            Assert.InRange(unchecked(b - a), 15_000u, 2_000_000u);
        }
    }
}
