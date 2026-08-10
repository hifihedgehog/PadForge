using System;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins the #271 item 6 pair: the explicit "Gyro R ..." right-half
    /// family and the FUSED bare "Gyro ..." family on devices carrying
    /// the aux (left) gyro. On a pair SDL's primary sensor IS the right
    /// half, so the R family reads the primary raw; the bare family
    /// averages the two calibrated halves when (and only when)
    /// HasGyroAuxProvider says the device really has the second sensor.
    ///
    /// Arithmetic assertions compare against a REFERENCE evaluation of
    /// the same pipeline (never hand-computed stick units), so scale and
    /// saturation changes downstream cannot silently pass a wrong fuse.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class GyroRFamilyTests
    {
        private const string Dev = "12121212-3434-5656-7878-9a9a9a9a9a9a";

        private static MappingSource Src(string descriptor) => new()
        {
            Kind = "Direct",
            Descriptor = descriptor,
            DeviceGuid = Dev,
        };

        private static CustomInputState StateWith(
            float p, float y, float r, float ap, float ay, float ar)
        {
            var s = new CustomInputState();
            s.Gyro[0] = p; s.Gyro[1] = y; s.Gyro[2] = r;
            s.GyroAux[0] = ap; s.GyroAux[1] = ay; s.GyroAux[2] = ar;
            return s;
        }

        private static float Read(CustomInputState state, string descriptor, string target = "LeftThumbAxisY")
            => SourceEvaluator.EvaluateForBipolarAxisTarget(
                state, Src(descriptor), 0, target, 0, null, 0.016, Dev);

        private static void RunIsolated(Action body, Func<string, bool> hasAux = null)
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            var oldAuxBias = SourceCoercion.GyroAuxBiasProvider;
            var oldEngage = SourceCoercion.AimEngageStateProvider;
            var oldHasAux = SourceCoercion.HasGyroAuxProvider;
            try
            {
                SourceCoercion.GyroTuningProvider = null;
                SourceCoercion.GyroBiasProvider = null;
                SourceCoercion.GyroAuxBiasProvider = null;
                SourceCoercion.AimEngageStateProvider = null;
                SourceCoercion.HasGyroAuxProvider = hasAux;
                body();
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.GyroBiasProvider = oldBias;
                SourceCoercion.GyroAuxBiasProvider = oldAuxBias;
                SourceCoercion.AimEngageStateProvider = oldEngage;
                SourceCoercion.HasGyroAuxProvider = oldHasAux;
            }
        }

        // ── Grammar ──

        [Fact]
        public void RPredicate_IsExactAndDisjoint()
        {
            Assert.True(SourceCoercion.IsGyroRDescriptor("Gyro R Pitch"));
            Assert.True(SourceCoercion.IsGyroRDescriptor(" gyro r yaw "));
            Assert.True(SourceCoercion.IsGyroRDescriptor("Gyro R Roll"));
            Assert.True(SourceCoercion.IsGyroRDescriptor("Gyro R Horizontal"));
            Assert.False(SourceCoercion.IsGyroRDescriptor("Gyro Pitch"));
            Assert.False(SourceCoercion.IsGyroRDescriptor("Gyro L Pitch"));
            Assert.False(SourceCoercion.IsGyroRDescriptor("Gyro R  Pitch")); // mangled fails closed
            // Still a gyro (every gyro behavior applies), not lean, not aux.
            Assert.True(SourceCoercion.IsGyroDescriptor("Gyro R Pitch"));
            Assert.False(SourceCoercion.IsGyroLeanDescriptor("Gyro R Pitch"));
            Assert.False(SourceCoercion.IsGyroAuxDescriptor("Gyro R Pitch"));
        }

        [Fact]
        public void PitchAxisPredicate_CoversTheRSpelling()
        {
            // The stick-X rate flip excludes pitch BY AXIS; the R family
            // spells it "Gyro R Pitch" and must be excluded the same way.
            Assert.True(SourceCoercion.IsGyroPitchAxisDescriptor("Gyro R Pitch"));
            Assert.False(SourceCoercion.IsGyroPitchAxisDescriptor("Gyro R Yaw"));
            Assert.False(SourceCoercion.IsGyroPitchAxisDescriptor("Gyro R Horizontal"));
        }

        // ── Isolation and fusion ──

        [Fact]
        public void ThreeFamilies_ReadThreeDistinctSignals_InOneEvaluation()
        {
            RunIsolated(() =>
            {
                // Primary (right) pitch 0.4, aux (left) pitch 0.1: small
                // rates, far from any output clamp.
                var st = StateWith(0.4f, 0f, 0f, 0.1f, 0f, 0f);

                float r = Read(st, "Gyro R Pitch");
                float l = Read(st, "Gyro L Pitch");
                float bare = Read(st, "Gyro Pitch");

                Assert.True(Math.Abs(r) > Math.Abs(l), $"R ({r}) should exceed L ({l})");

                // The fused read equals the same pipeline fed the average
                // rate (0.25) through the raw right-half path.
                var reference = StateWith(0.25f, 0f, 0f, 0f, 0f, 0f);
                float expected = Read(reference, "Gyro R Pitch");
                Assert.Equal(expected, bare, 4);
            }, hasAux: _ => true);
        }

        [Fact]
        public void BareFamily_WithoutAuxCapability_ReadsPrimaryUnchanged()
        {
            RunIsolated(() =>
            {
                var st = StateWith(0.4f, 0f, 0f, 0.1f, 0f, 0f);
                // Provider null: no capability answer → primary,
                // byte-identical to the pre-fusion behavior even though
                // GyroAux holds data.
                float bare = Read(st, "Gyro Pitch");
                float r = Read(st, "Gyro R Pitch");
                Assert.Equal(r, bare, 5);
            });

            RunIsolated(() =>
            {
                var st = StateWith(0.4f, 0f, 0f, 0.1f, 0f, 0f);
                float bare = Read(st, "Gyro Pitch");
                float r = Read(st, "Gyro R Pitch");
                Assert.Equal(r, bare, 5);
            }, hasAux: _ => false);
        }

        [Fact]
        public void Fusion_SubtractsEachHalfsOwnBias()
        {
            RunIsolated(() =>
            {
                // Right half drifts +0.1, left half drifts -0.1 on pitch.
                SourceCoercion.GyroBiasProvider = (g, s) => (0.1f, 0f, 0f);
                SourceCoercion.GyroAuxBiasProvider = (g, s) => (-0.1f, 0f, 0f);

                // Raw reads: right 0.5 (true 0.4), left 0.0 (true 0.1).
                var st = StateWith(0.5f, 0f, 0f, 0.0f, 0f, 0f);
                float bare = Read(st, "Gyro Pitch");

                // Debiased average = (0.4 + 0.1) / 2 = 0.25. Compare against
                // the unbiased pipeline fed 0.25 directly.
                SourceCoercion.GyroBiasProvider = null;
                SourceCoercion.GyroAuxBiasProvider = null;
                var reference = StateWith(0.25f, 0f, 0f, 0f, 0f, 0f);
                float expected = Read(reference, "Gyro R Pitch");
                Assert.Equal(expected, bare, 4);
            }, hasAux: _ => true);
        }

        [Fact]
        public void RPitch_DoesNotGetTheStickXRateFlip_UnlikeRYaw()
        {
            RunIsolated(() =>
            {
                var pitchState = StateWith(0.4f, 0f, 0f, 0f, 0f, 0f);
                var yawState = StateWith(0f, 0.4f, 0f, 0f, 0f, 0f);

                float rPitchOnStickX = Read(pitchState, "Gyro R Pitch", "RightThumbAxisX");
                float rYawOnStickX = Read(yawState, "Gyro R Yaw", "RightThumbAxisX");

                Assert.True(Math.Sign(rPitchOnStickX) != Math.Sign(rYawOnStickX),
                    $"pitch {rPitchOnStickX} vs yaw {rYawOnStickX}: the stick-X flip must hit yaw only");
            });
        }

        [Fact]
        public void RHorizontal_ReadsTheRawRightBlend_NotTheFused()
        {
            RunIsolated(() =>
            {
                // Primary roll dominates; aux silent, so fusion halves it.
                var st = StateWith(0f, 0.05f, 0.4f, 0f, 0f, 0f);
                float rHoriz = Read(st, "Gyro R Horizontal");
                float bareHoriz = Read(st, "Gyro Horizontal");
                Assert.NotEqual(0f, rHoriz);
                Assert.True(Math.Abs(rHoriz) > Math.Abs(bareHoriz),
                    $"raw right blend {rHoriz} should exceed fused {bareHoriz}");
            }, hasAux: _ => true);
        }

        [Fact]
        public void PassthroughGyro_StaysRawPrimary_NeverFused()
        {
            RunIsolated(() =>
            {
                var st = StateWith(0.4f, 0f, 0f, 0.1f, 0f, 0f);
                SourceCoercion.GetPassthroughGyro(st, Dev, 0, out float pitch, out _, out _);

                // The motion passthrough is a real single-sensor stream: the
                // native-frame contract forbids a fabricated fused IMU.
                Assert.Equal(0.4f, pitch, 3);
            }, hasAux: _ => true);
        }
    }
}
