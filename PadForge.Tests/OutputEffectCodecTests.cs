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

        [Theory]
        [InlineData(new byte[0])]               // empty
        [InlineData(new byte[] { 99 })]         // unknown kind
        [InlineData(new byte[] { 1 })]          // SonyEffect with no body
        [InlineData(new byte[] { 2, 0, 0 })]    // Vibration truncated
        [InlineData(new byte[] { 3, 0, 0 })]    // Wheel truncated
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
