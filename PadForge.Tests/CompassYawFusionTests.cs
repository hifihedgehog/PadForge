using System;
using System.IO;
using System.Xml.Serialization;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #271 item 5, the fusion half: the wrap + proportional correction
    /// (the x-io Fusion default-gain concept), the yaw-lane injection
    /// gates, and the new PadSetting calibration surface.
    /// </summary>
    public class CompassYawFusionTests
    {
        // ── WrapToPi ──

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(3f, 3f)]
        [InlineData(4f, 4f - 2f * MathF.PI)]
        [InlineData(-4f, -4f + 2f * MathF.PI)]
        [InlineData(7f, 7f - 2f * MathF.PI)]
        public void WrapToPi_MapsIntoThePrincipalRange(float input, float expected)
        {
            Assert.Equal(expected, SourceCoercion.WrapToPi(input), 4);
        }

        // ── ComputeCompassCorrection ──

        [Fact]
        public void Correction_PullsTowardTheHeading_Proportionally()
        {
            // Heading ahead of the integral by 0.2 rad: correction is
            // +gain * 0.2, pulling the integral forward.
            Assert.Equal(0.1f, SourceCoercion.ComputeCompassCorrection(0.2f, 0f), 4);
            // Behind: pulled backward.
            Assert.Equal(-0.1f, SourceCoercion.ComputeCompassCorrection(0f, 0.2f), 4);
            // Aligned: zero, so a converged aim carries no bias.
            Assert.Equal(0f, SourceCoercion.ComputeCompassCorrection(1.5f, 1.5f), 4);
        }

        [Fact]
        public void Correction_TakesTheShortWayAroundTheWrap()
        {
            // Heading at +3.1, integral at -3.1: the true error is the
            // short -0.083 rad step through pi, never the long +6.2 way.
            float corr = SourceCoercion.ComputeCompassCorrection(3.1f, -3.1f);
            float expected = 0.5f * SourceCoercion.WrapToPi(3.1f - (-3.1f));
            Assert.Equal(expected, corr, 4);
            Assert.True(Math.Abs(corr) < 0.5f, $"corr {corr} took the long way");
        }

        [Fact]
        public void Correction_ConvergesTheIntegralInSimulation()
        {
            // Integrate a still controller (yaw rate 0) with an initial
            // 1-rad drift error at 60 Hz: the closed loop must decay the
            // error, and reach near-zero within a few seconds.
            float heading = 0.5f, integ = -0.5f;
            const float dt = 1f / 60f;
            for (int i = 0; i < 600; i++)
            {
                float corr = SourceCoercion.ComputeCompassCorrection(heading, integ);
                integ = SourceCoercion.WrapToPi(integ + corr * dt);
            }
            Assert.True(Math.Abs(SourceCoercion.WrapToPi(heading - integ)) < 0.01f,
                $"integral {integ} did not converge to {heading}");
        }

        // ── The yaw-lane injection and its gates ──

        [Fact]
        public void Injection_ReachesYawLanes_NeverPitchOrRoll()
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldCorr = SourceCoercion.CompassYawCorrectionProvider;
            var oldEngage = SourceCoercion.AimEngageStateProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            try
            {
                SourceCoercion.AimEngageStateProvider = null;
                SourceCoercion.GyroBiasProvider = null;
                SourceCoercion.GyroTuningProvider = (g, s) => new SourceCoercion.GyroTuning
                {
                    SensH = 1f, SensV = 1f, OutputCurve = "Linear", CompassYaw = true,
                };
                SourceCoercion.CompassYawCorrectionProvider = _ => 0.3f;

                const string Dev = "abcdabcd-1111-2222-3333-444455556666";
                var src = new MappingSource { Kind = "Direct", DeviceGuid = Dev };
                var st = new PadForge.Engine.CustomInputState(); // all rates zero

                float Read(string d)
                {
                    src.Descriptor = d;
                    return SourceEvaluator.EvaluateForBipolarAxisTarget(
                        st, src, 0, "LeftThumbAxisY", 0, null, 0.016, Dev);
                }

                // A still controller: only the correction can move a lane.
                Assert.NotEqual(0f, Read("Gyro Yaw"));
                Assert.NotEqual(0f, Read("Gyro Horizontal"));
                Assert.Equal(0f, Read("Gyro Pitch"));
                Assert.Equal(0f, Read("Gyro Roll"));

                // And with the toggle off, the yaw lane is untouched.
                SourceCoercion.GyroTuningProvider = (g, s) => new SourceCoercion.GyroTuning
                {
                    SensH = 1f, SensV = 1f, OutputCurve = "Linear", CompassYaw = false,
                };
                Assert.Equal(0f, Read("Gyro Yaw"));
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.CompassYawCorrectionProvider = oldCorr;
                SourceCoercion.AimEngageStateProvider = oldEngage;
                SourceCoercion.GyroBiasProvider = oldBias;
            }
        }

        // ── PadSetting surface ──

        [Fact]
        public void PadSetting_CompassFields_DefaultUncalibratedAndOff()
        {
            var ps = new PadSetting();
            Assert.Equal("0", ps.GyroCompassYaw);
            Assert.Equal("0", ps.MagBiasX);
            Assert.Equal("0", ps.MagBiasY);
            Assert.Equal("0", ps.MagBiasZ);
            Assert.Equal("0", ps.MagFieldNorm);
        }

        [Fact]
        public void PadSetting_CompassFields_RoundTripThroughXml()
        {
            var ps = new PadSetting
            {
                GyroCompassYaw = "1",
                MagBiasX = "12.5", MagBiasY = "-3.25", MagBiasZ = "700",
                MagFieldNorm = "412.75",
            };
            var ser = new XmlSerializer(typeof(PadSetting));
            using var sw = new StringWriter();
            ser.Serialize(sw, ps);
            using var sr = new StringReader(sw.ToString());
            var back = (PadSetting)ser.Deserialize(sr);
            Assert.Equal("1", back.GyroCompassYaw);
            Assert.Equal("12.5", back.MagBiasX);
            Assert.Equal("-3.25", back.MagBiasY);
            Assert.Equal("700", back.MagBiasZ);
            Assert.Equal("412.75", back.MagFieldNorm);
        }

        [Fact]
        public void PadSetting_CompassFields_ChangeTheChecksum()
        {
            var a = new PadSetting();
            var b = new PadSetting { GyroCompassYaw = "1" };
            var c = new PadSetting { MagFieldNorm = "400" };
            Assert.NotEqual(a.ComputeChecksum(), b.ComputeChecksum());
            Assert.NotEqual(a.ComputeChecksum(), c.ComputeChecksum());
        }
    }
}
