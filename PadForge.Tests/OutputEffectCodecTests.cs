using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public class OutputEffectCodecTests
    {
        [Fact]
        public void RumbleRoundTrips()
        {
            byte[] wire = OutputEffectCodec.EncodeRumble(0x1234, 0xABCD, 0x00FF, 0xFF00);
            Assert.True(OutputEffectCodec.TryDecode(wire, out var e));
            Assert.Equal(OutputEffectCodec.Kind.Rumble, e.Kind);
            Assert.Equal(0x1234, e.Left);
            Assert.Equal(0xABCD, e.Right);
            Assert.Equal(0x00FF, e.LeftTrigger);
            Assert.Equal(0xFF00, e.RightTrigger);
            Assert.Null(e.Effect);
        }

        [Fact]
        public void SonyEffectRoundTripsVerbatim()
        {
            var payload = new byte[47];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 3 + 1);
            byte[] wire = OutputEffectCodec.EncodeSonyEffect(payload);
            Assert.True(OutputEffectCodec.TryDecode(wire, out var e));
            Assert.Equal(OutputEffectCodec.Kind.SonyEffect, e.Kind);
            Assert.Equal(payload, e.Effect);
        }

        [Fact]
        public void Ds4LengthSonyEffectRoundTrips()
        {
            var payload = new byte[31];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(0xFF - i);
            byte[] wire = OutputEffectCodec.EncodeSonyEffect(payload);
            Assert.True(OutputEffectCodec.TryDecode(wire, out var e));
            Assert.Equal(payload, e.Effect);
        }

        [Theory]
        [InlineData(new byte[0])]                       // empty
        [InlineData(new byte[] { 99 })]                 // unknown kind
        [InlineData(new byte[] { 1, 0, 0, 0 })]         // Rumble too short
        [InlineData(new byte[] { 2 })]                  // SonyEffect with no body
        public void MalformedFramesFailClosed(byte[] wire)
        {
            Assert.False(OutputEffectCodec.TryDecode(wire, out var e));
            Assert.Equal(default, e.Kind);
        }

        [Fact]
        public void OversizedSonyEffectRejected()
        {
            byte[] wire = new byte[1 + 512];
            wire[0] = (byte)OutputEffectCodec.Kind.SonyEffect;
            Assert.False(OutputEffectCodec.TryDecode(wire, out _));
        }
    }
}
