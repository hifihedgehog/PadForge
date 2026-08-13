using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The degree-ranged gravity-tilt pair "Gyro Tilt X/Y" (#292): the
    /// lean read with a configurable envelope. Full deflection at
    /// ParamTiltRangeDeg (default 25, the modal Steam-corpus deflection
    /// max), subtract-style ParamTiltInnerDz so there is no step at the
    /// threshold, no sensitivity multiplier (the range is the gain).
    /// Frame conventions and the gravity harness mirror
    /// WorkshopV26EngineTests' lean tests.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class GyroTiltEnvelopeTests
    {
        private const float G = 9.81f;

        private static (float, float, float) UprightRest => (0f, G, 0f);

        private static (float, float, float) TiltedRight(double deg)
        {
            double r = deg * System.Math.PI / 180.0;
            return (-(float)(G * System.Math.Sin(r)), (float)(G * System.Math.Cos(r)), 0f);
        }

        private static (float, float, float) NoseUp(double deg)
        {
            double r = deg * System.Math.PI / 180.0;
            return (0f, (float)(G * System.Math.Cos(r)), -(float)(G * System.Math.Sin(r)));
        }

        private static float ReadTilt(MappingSource src, string guid)
            => SourceCoercion.EvaluateForBipolarAxisTarget(new CustomInputState(), src,
                evaluatedDeviceGuid: guid);

        private static MappingSource TiltX(string guid, double range = 0, double dz = 0)
            => new MappingSource
            {
                Descriptor = SourceCoercion.GyroTiltXDescriptor,
                DeviceGuid = guid,
                ParamTiltRangeDeg = range,
                ParamTiltInnerDz = dz,
            };

        [Fact]
        public void DefaultEnvelope_FullDeflectionAt25Degrees()
        {
            var old = SourceCoercion.GravityProvider;
            string guid = System.Guid.NewGuid().ToString();
            try
            {
                SourceCoercion.ResetGyroLeanNeutral();
                var sample = UprightRest;
                SourceCoercion.GravityProvider = _ => sample;

                Assert.Equal(0f, ReadTilt(TiltX(guid), guid), 3);

                // Half the default range: half deflection, linear.
                sample = TiltedRight(12.5);
                Assert.Equal(0.5f, ReadTilt(TiltX(guid), guid), 3);

                // At the range: full. Past it: clamped.
                sample = TiltedRight(25);
                Assert.Equal(1f, ReadTilt(TiltX(guid), guid), 3);
                sample = TiltedRight(60);
                Assert.Equal(1f, ReadTilt(TiltX(guid), guid), 3);

                // Left is negative.
                sample = TiltedRight(-12.5);
                Assert.Equal(-0.5f, ReadTilt(TiltX(guid), guid), 3);
            }
            finally
            {
                SourceCoercion.GravityProvider = old;
                SourceCoercion.ResetGyroLeanNeutral();
            }
        }

        [Fact]
        public void CustomRange_ScalesLinearly()
        {
            var old = SourceCoercion.GravityProvider;
            string guid = System.Guid.NewGuid().ToString();
            try
            {
                SourceCoercion.ResetGyroLeanNeutral();
                var sample = UprightRest;
                SourceCoercion.GravityProvider = _ => sample;
                _ = ReadTilt(TiltX(guid, range: 50), guid); // latch neutral

                sample = TiltedRight(25);
                Assert.Equal(0.5f, ReadTilt(TiltX(guid, range: 50), guid), 3);
                sample = TiltedRight(50);
                Assert.Equal(1f, ReadTilt(TiltX(guid, range: 50), guid), 3);
            }
            finally
            {
                SourceCoercion.GravityProvider = old;
                SourceCoercion.ResetGyroLeanNeutral();
            }
        }

        [Fact]
        public void Deadzone_IsSubtractStyle_NoStepAtThreshold()
        {
            var old = SourceCoercion.GravityProvider;
            string guid = System.Guid.NewGuid().ToString();
            try
            {
                SourceCoercion.ResetGyroLeanNeutral();
                var sample = UprightRest;
                SourceCoercion.GravityProvider = _ => sample;
                _ = ReadTilt(TiltX(guid, range: 25, dz: 5), guid); // latch neutral

                // Inside the deadzone: zero.
                sample = TiltedRight(4);
                Assert.Equal(0f, ReadTilt(TiltX(guid, range: 25, dz: 5), guid), 3);

                // Just past it: near zero (subtract-style), not a jump.
                sample = TiltedRight(5.5);
                Assert.InRange(ReadTilt(TiltX(guid, range: 25, dz: 5), guid), 0.0f, 0.05f);

                // Midway through the remapped band: (15-5)/(25-5) = 0.5.
                sample = TiltedRight(15);
                Assert.Equal(0.5f, ReadTilt(TiltX(guid, range: 25, dz: 5), guid), 3);

                // Full at the range, deadzone notwithstanding.
                sample = TiltedRight(25);
                Assert.Equal(1f, ReadTilt(TiltX(guid, range: 25, dz: 5), guid), 3);
            }
            finally
            {
                SourceCoercion.GravityProvider = old;
                SourceCoercion.ResetGyroLeanNeutral();
            }
        }

        [Fact]
        public void TiltY_NoseUp_ReadsPositive_AndSensitivityFieldIsIgnored()
        {
            var old = SourceCoercion.GravityProvider;
            string guid = System.Guid.NewGuid().ToString();
            try
            {
                SourceCoercion.ResetGyroLeanNeutral();
                var sample = UprightRest;
                SourceCoercion.GravityProvider = _ => sample;

                var srcY = new MappingSource
                {
                    Descriptor = SourceCoercion.GyroTiltYDescriptor,
                    DeviceGuid = guid,
                    // The generic Sensitivity field scales LEAN, never the
                    // tilt pair: the range is the tilt pair's gain.
                    Sensitivity = 3.0,
                };
                _ = ReadTilt(srcY, guid); // latch neutral

                sample = NoseUp(12.5);
                Assert.Equal(0.5f, ReadTilt(srcY, guid), 3);
            }
            finally
            {
                SourceCoercion.GravityProvider = old;
                SourceCoercion.ResetGyroLeanNeutral();
            }
        }

        [Fact]
        public void TiltAndLean_ShareTheNeutral_GyroRecenterCoversBoth()
        {
            var old = SourceCoercion.GravityProvider;
            string guid = System.Guid.NewGuid().ToString();
            try
            {
                SourceCoercion.ResetGyroLeanNeutral();
                // A natural grip pitched back 40 degrees latches as neutral
                // via a LEAN read; the tilt read must inherit that same
                // resting grip rather than latching its own.
                var sample = NoseUp(40);
                SourceCoercion.GravityProvider = _ => sample;
                var lean = new MappingSource
                { Descriptor = SourceCoercion.GyroLeanXDescriptor, DeviceGuid = guid };
                _ = SourceCoercion.EvaluateForBipolarAxisTarget(new CustomInputState(), lean,
                    evaluatedDeviceGuid: guid);

                // Still at that grip: tilt reads zero (same neutral).
                Assert.Equal(0f, ReadTilt(TiltX(guid), guid), 3);
            }
            finally
            {
                SourceCoercion.GravityProvider = old;
                SourceCoercion.ResetGyroLeanNeutral();
            }
        }
    }
}
