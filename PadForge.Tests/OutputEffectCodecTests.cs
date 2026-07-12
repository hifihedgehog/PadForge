using PadForge.Engine;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public class OutputEffectCodecTests
    {
        [Fact]
        public void SonyEffectRoundTripsVerbatim()
        {
            var payload = new byte[47];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 3 + 1);
            byte[] wire = OutputEffectCodec.EncodeSonyEffect(payload);
            Assert.True(OutputEffectCodec.TryDecode(wire, out var e));
            Assert.Equal(OutputEffectCodec.Kind.SonyEffect, e.Kind);
            Assert.Equal(payload, e.SonyBody);
        }

        [Fact]
        public void Ds4LengthSonyEffectRoundTrips()
        {
            var payload = new byte[31];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(0xFF - i);
            byte[] wire = OutputEffectCodec.EncodeSonyEffect(payload);
            Assert.True(OutputEffectCodec.TryDecode(wire, out var e));
            Assert.Equal(payload, e.SonyBody);
        }

        [Fact]
        public void VibrationScalarRoundTrips()
        {
            var v = new Vibration
            {
                LeftMotorSpeed = 0x1234,
                RightMotorSpeed = 0xABCD,
                LeftTriggerMotorSpeed = 0x00FF,
                RightTriggerMotorSpeed = 0xFF00,
                DeviceGain = 200,
            };
            byte[] wire = OutputEffectCodec.EncodeVibration(v);
            Assert.True(OutputEffectCodec.TryDecode(wire, out var e));
            Assert.Equal(OutputEffectCodec.Kind.Vibration, e.Kind);
            Assert.Equal(0x1234, e.Vibration.LeftMotorSpeed);
            Assert.Equal(0xABCD, e.Vibration.RightMotorSpeed);
            Assert.Equal(0x00FF, e.Vibration.LeftTriggerMotorSpeed);
            Assert.Equal(0xFF00, e.Vibration.RightTriggerMotorSpeed);
            Assert.Equal(200, e.Vibration.DeviceGain);
            Assert.False(e.Vibration.HasDirectionalData);
            Assert.False(e.Vibration.HasConditionData);
        }

        [Fact]
        public void VibrationDirectionalAndConditionRoundTrips()
        {
            var v = new Vibration
            {
                LeftMotorSpeed = 10, RightMotorSpeed = 20,
                HasDirectionalData = true,
                EffectType = 4,
                SignedMagnitude = -7000,
                Direction = 16384,
                Period = 250,
                DeviceGain = 128,
                HasConditionData = true,
                ConditionAxisCount = 1,
                ConditionAxes = new[]
                {
                    new ConditionAxisData
                    {
                        PositiveCoefficient = 5000, NegativeCoefficient = -5000,
                        Offset = -1000, DeadBand = 200,
                        PositiveSaturation = 9000, NegativeSaturation = 8000,
                    },
                },
            };
            byte[] wire = OutputEffectCodec.EncodeVibration(v);
            Assert.True(OutputEffectCodec.TryDecode(wire, out var e));
            var d = e.Vibration;
            Assert.True(d.HasDirectionalData);
            Assert.Equal(4u, d.EffectType);
            Assert.Equal(-7000, d.SignedMagnitude);
            Assert.Equal(16384, d.Direction);
            Assert.Equal(250u, d.Period);
            Assert.True(d.HasConditionData);
            Assert.Equal(1, d.ConditionAxisCount);
            Assert.Equal(5000, d.ConditionAxes[0].PositiveCoefficient);
            Assert.Equal(-5000, d.ConditionAxes[0].NegativeCoefficient);
            Assert.Equal(-1000, d.ConditionAxes[0].Offset);
            Assert.Equal(9000u, d.ConditionAxes[0].PositiveSaturation);
        }

        [Fact]
        public void WheelRoundTrips()
        {
            byte[] wire = OutputEffectCodec.EncodeWheel(
                hasCond: true, dir: false, force: -12345, peak: 9000, ac: 0xC000, effect: 8, period: 33,
                pc: 4000, nc: -3000, off: 500, db: 100, ps: 7000, ns: 6000, condGain: 75,
                rangeDeg: 900, ledMask: 0x1FF, ledValid: true);
            Assert.True(OutputEffectCodec.TryDecode(wire, out var e));
            Assert.Equal(OutputEffectCodec.Kind.Wheel, e.Kind);
            var w = e.Wheel;
            Assert.True(w.HasCond);
            Assert.False(w.Dir);
            Assert.True(w.LedValid);
            Assert.Equal(-12345, w.Force);
            Assert.Equal(9000, w.Peak);
            Assert.Equal(0xC000, w.Ac);
            Assert.Equal(8u, w.Effect);
            Assert.Equal(33, w.Period);
            Assert.Equal(4000, w.Pc);
            Assert.Equal(-3000, w.Nc);
            Assert.Equal(500, w.Off);
            Assert.Equal(75, w.CondGain);
            Assert.Equal(900, w.RangeDeg);
            Assert.Equal(0x1FF, w.LedMask);
        }

        [Fact]
        public void PlayerIndexRoundTrips()
        {
            byte[] wire = OutputEffectCodec.EncodePlayerIndex(3);
            Assert.True(OutputEffectCodec.TryDecode(wire, out var e));
            Assert.Equal(OutputEffectCodec.Kind.PlayerIndex, e.Kind);
            Assert.Equal(3, e.PlayerIndex);
        }

        [Fact]
        public void VibrationImpulseTriggersSurviveAlongsideDirectional()
        {
            // GAP 3 wire pin: the relayed Vibration must carry all FOUR channels
            // (large / small / LT / RT) so the owner can drive an Xbox impulse
            // pad's trigger motors. The four must survive with a directional
            // payload present, since that's what an FFB game frame looks like.
            var v = new Vibration
            {
                LeftMotorSpeed = 0x1111,
                RightMotorSpeed = 0x2222,
                LeftTriggerMotorSpeed = 0x3333,
                RightTriggerMotorSpeed = 0x4444,
                HasDirectionalData = true,
                EffectType = 2,
                SignedMagnitude = 6000,
                Direction = 8192,
                Period = 100,
            };
            byte[] wire = OutputEffectCodec.EncodeVibration(v);
            Assert.True(OutputEffectCodec.TryDecode(wire, out var e));
            var d = e.Vibration;
            Assert.Equal(0x1111, d.LeftMotorSpeed);
            Assert.Equal(0x2222, d.RightMotorSpeed);
            Assert.Equal(0x3333, d.LeftTriggerMotorSpeed);
            Assert.Equal(0x4444, d.RightTriggerMotorSpeed);
            Assert.True(d.HasDirectionalData);
            Assert.Equal(8192, d.Direction);
        }

        [Fact]
        public void GuideLedRoundTrips()
        {
            byte[] wire = OutputEffectCodec.EncodeGuideLed(35);
            Assert.True(OutputEffectCodec.TryDecode(wire, out var e));
            Assert.Equal(OutputEffectCodec.Kind.GuideLed, e.Kind);
            Assert.Equal(35, e.GuideLedPercent);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(100, 100)]
        [InlineData(250, 100)]   // encode clamps above 100
        [InlineData(-7, 0)]      // encode clamps below 0
        public void GuideLedPercentClampsOnEncode(int input, int expected)
        {
            byte[] wire = OutputEffectCodec.EncodeGuideLed(input);
            Assert.True(OutputEffectCodec.TryDecode(wire, out var e));
            Assert.Equal(expected, e.GuideLedPercent);
        }

        [Fact]
        public void GuideLedOutOfRangeByteClampsOnDecode()
        {
            // A hand-framed percent above 100 (e.g. a future/garbled sender)
            // decodes to the 100 ceiling rather than an out-of-range value.
            Assert.True(OutputEffectCodec.TryDecode(
                new byte[] { (byte)OutputEffectCodec.Kind.GuideLed, 200 }, out var e));
            Assert.Equal(OutputEffectCodec.Kind.GuideLed, e.Kind);
            Assert.Equal(100, e.GuideLedPercent);
        }

        [Theory]
        [InlineData(new byte[0])]               // empty
        [InlineData(new byte[] { 99 })]         // unknown kind
        [InlineData(new byte[] { 1 })]          // SonyEffect with no body
        [InlineData(new byte[] { 2, 0, 0 })]    // Vibration truncated
        [InlineData(new byte[] { 3, 0, 0 })]    // Wheel truncated
        [InlineData(new byte[] { 5 })]          // PlayerIndex with no number
        [InlineData(new byte[] { 6 })]          // GuideLed with no percent byte
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
