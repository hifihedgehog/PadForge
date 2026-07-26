using PadForge.Engine;
using PadForge.Engine.RemoteLink;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Audit 2026-07-26 round seventeen.
    ///
    /// <para>NO DEFECT FOUND in the Remote Link codec. The encode and
    /// decode of the extension tail are symmetric, the decode is
    /// bounds-checked before the magic byte, every field read slices (so a
    /// truncated frame throws into the fail-closed catch rather than
    /// reading adjacent memory), non-finite floats reset to neutral, and
    /// MaxEncodedSize's budget for the tail (1 magic + 2 mask + 12
    /// payload) exactly matches what Encode writes.</para>
    ///
    /// <para>What the round DID find is a measured coverage gap. The codec
    /// tests carry 6 to 31 references each for NfcTag, CapSense, Midi,
    /// Touchpads and MouseRaw, and exactly ONE for GyroAux, which turned
    /// out to be a field NAME inside the reflection-based completeness
    /// list. That list proves the field is known about, never that its
    /// values survive the wire. GyroAux is the only occupant of the
    /// extension tail, which is the newest and structurally oddest part of
    /// the format (the u16 presence mask was full at Block.Nfc, so #252
    /// added a positional magic-byte tail after it). Least-covered and
    /// least-ordinary is a bad pair, so these close it.</para></summary>
    public class AuditJuly26RoundSeventeenTests
    {
        private static CustomInputState WithAux(float p, float y, float r)
        {
            var s = new CustomInputState();
            s.GyroAux[0] = p;
            s.GyroAux[1] = y;
            s.GyroAux[2] = r;
            return s;
        }

        /// <summary>THE ROUND TRIP. The tail carries all three axes intact
        /// when the peer advertises the capability.</summary>
        [Fact]
        public void GyroAux_SurvivesTheWire_WhenAdvertised()
        {
            var src = WithAux(0.25f, -1.5f, 42.125f);
            var caps = new CustomInputStateCodec.Caps(gyro: false, accel: false,
                accelAux: false, gyroAux: true);

            var frame = CustomInputStateCodec.Encode(src, caps);
            var dst = CustomInputStateCodec.Decode(frame);

            Assert.NotNull(dst);
            Assert.Equal(0.25f, dst.GyroAux[0]);
            Assert.Equal(-1.5f, dst.GyroAux[1]);
            Assert.Equal(42.125f, dst.GyroAux[2]);
        }

        /// <summary>Without the capability the tail is never written, and
        /// the receiver must land on neutral rather than on whatever the
        /// previous frame left in the target. DecodeInto reuses a caller's
        /// state object, so a stale aux reading persisting across a
        /// capability change would feed the tuning chain a value the sender
        /// never sent.</summary>
        [Fact]
        public void GyroAux_DecodesNeutral_WhenNotAdvertised()
        {
            var src = WithAux(9f, 9f, 9f);
            var noAux = new CustomInputStateCodec.Caps(gyro: false, accel: false,
                accelAux: false, gyroAux: false);

            var frame = CustomInputStateCodec.Encode(src, noAux);

            // Reuse a target that already holds a live aux reading.
            var target = WithAux(7f, 7f, 7f);
            Assert.True(CustomInputStateCodec.DecodeInto(frame, target));

            Assert.Equal(0f, target.GyroAux[0]);
            Assert.Equal(0f, target.GyroAux[1]);
            Assert.Equal(0f, target.GyroAux[2]);
        }

        /// <summary>The tail is positional, so a frame that simply ends
        /// before it is complete and valid. This is what lets a peer that
        /// predates #252 talk to one that does not.</summary>
        [Fact]
        public void FrameWithoutTail_StillDecodes()
        {
            var src = WithAux(1f, 2f, 3f);
            var noAux = new CustomInputStateCodec.Caps(gyro: false, accel: false,
                accelAux: false, gyroAux: false);
            var withAux = new CustomInputStateCodec.Caps(gyro: false, accel: false,
                accelAux: false, gyroAux: true);

            var shortFrame = CustomInputStateCodec.Encode(src, noAux);
            var longFrame = CustomInputStateCodec.Encode(src, withAux);

            Assert.True(longFrame.Length > shortFrame.Length);
            Assert.NotNull(CustomInputStateCodec.Decode(shortFrame));
        }

        /// <summary>A hostile or corrupt non-finite value fails closed,
        /// the documented AccelAux rule, rather than reaching the tuning
        /// chain. Built by corrupting the encoded frame directly so the
        /// decoder faces bytes it never produced.</summary>
        [Fact]
        public void NonFiniteAux_FailsClosed()
        {
            var src = WithAux(0.5f, 0.5f, 0.5f);
            var caps = new CustomInputStateCodec.Caps(gyro: false, accel: false,
                accelAux: false, gyroAux: true);
            var frame = CustomInputStateCodec.Encode(src, caps);

            // The tail's three floats are the last 12 bytes of the frame.
            // NaN is 0x7FC00000 little-endian.
            int firstFloat = frame.Length - 12;
            frame[firstFloat + 0] = 0x00;
            frame[firstFloat + 1] = 0x00;
            frame[firstFloat + 2] = 0xC0;
            frame[firstFloat + 3] = 0x7F;

            var target = new CustomInputState();
            Assert.False(CustomInputStateCodec.DecodeInto(frame, target));
            Assert.Equal(0f, target.GyroAux[0]);
        }

        /// <summary>The tail's size budget is exact. MaxEncodedSize adds
        /// 15 bytes for it (1 magic + 2 mask + 3 floats), and the array
        /// overload allocates from that number, so an under-count would
        /// overflow the buffer rather than degrade quietly.</summary>
        [Fact]
        public void TailFitsItsBudget_Exactly()
        {
            var src = WithAux(1f, 2f, 3f);
            var noAux = new CustomInputStateCodec.Caps(gyro: false, accel: false,
                accelAux: false, gyroAux: false);
            var withAux = new CustomInputStateCodec.Caps(gyro: false, accel: false,
                accelAux: false, gyroAux: true);

            int budgetDelta = CustomInputStateCodec.MaxEncodedSize(src, withAux)
                            - CustomInputStateCodec.MaxEncodedSize(src, noAux);
            int actualDelta = CustomInputStateCodec.Encode(src, withAux).Length
                            - CustomInputStateCodec.Encode(src, noAux).Length;

            Assert.Equal(15, budgetDelta);
            Assert.Equal(budgetDelta, actualDelta);
        }
    }
}
