using System;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins the "Gyro L Pitch/Yaw/Roll" aux gyro family (issue #252, from
    /// discussion #231): the LEFT Joy-Con of a combined pair, SDL's
    /// SDL_SENSOR_GYRO_L. On a pair SDL feeds the RIGHT half into the primary
    /// SDL_SENSOR_GYRO, so these are a SECOND physical sensor rather than a
    /// second view of the first.
    ///
    /// The load-bearing contract is isolation: the two sensors share one
    /// device GUID, so every keyed piece of state (source array, calibration
    /// bias, smoothing lane, gravity for space projection) has to be aux-aware
    /// or one sensor silently reads the other's data.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class GyroAuxTests
    {
        private const string Dev = "77777777-8888-9999-aaaa-bbbbbbbbbbbb";

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

        // ── Grammar ──

        [Fact]
        public void Descriptor_Predicates_AreExactAndDisjointFromThePrimary()
        {
            Assert.True(SourceCoercion.IsGyroAuxDescriptor("Gyro L Pitch"));
            Assert.True(SourceCoercion.IsGyroAuxDescriptor(" gyro l yaw "));
            Assert.True(SourceCoercion.IsGyroAuxDescriptor("Gyro L Roll"));
            Assert.False(SourceCoercion.IsGyroAuxDescriptor("Gyro Pitch"));
            Assert.False(SourceCoercion.IsGyroAuxDescriptor("Gyro Lean X"));
            Assert.False(SourceCoercion.IsGyroAuxDescriptor("Gyro Horizontal"));

            // The family is deliberately still a gyro: every gyro behavior
            // (rate-direct reads, half-axis, the per-row sensitivity slider)
            // applies. Only the source sensor differs.
            Assert.True(SourceCoercion.IsGyroDescriptor("Gyro L Pitch"));
            // And it must NOT be mistaken for the gravity-lean family, which
            // shares the prefix but reads the accelerometer.
            Assert.False(SourceCoercion.IsGyroLeanDescriptor("Gyro L Pitch"));
        }

        /// <summary>The stick-X rate flip excludes PITCH, and the aux spells
        /// pitch differently. An exact compare against "Gyro Pitch" would have
        /// flipped the aux pitch while leaving the primary correct.</summary>
        [Fact]
        public void PitchAxisPredicate_CoversBothSpellings()
        {
            Assert.True(SourceCoercion.IsGyroPitchAxisDescriptor("Gyro Pitch"));
            Assert.True(SourceCoercion.IsGyroPitchAxisDescriptor("Gyro L Pitch"));
            Assert.False(SourceCoercion.IsGyroPitchAxisDescriptor("Gyro Yaw"));
            Assert.False(SourceCoercion.IsGyroPitchAxisDescriptor("Gyro L Yaw"));
            Assert.False(SourceCoercion.IsGyroPitchAxisDescriptor("Gyro Horizontal"));
        }

        /// <summary>The stick-X rate flip must exclude the aux PITCH exactly
        /// as it excludes the primary's. This drives the real call site
        /// (SourceEvaluator.ShouldFlipForAxisFrame), not just the predicate:
        /// the pre-fix exact compare against "Gyro Pitch" left the aux pitch
        /// flipped on a stick-X target while every predicate test still
        /// passed.</summary>
        [Fact]
        public void AuxPitch_DoesNotGetTheStickXRateFlip_UnlikeAuxYaw()
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            var oldAuxBias = SourceCoercion.GyroAuxBiasProvider;
            var oldEngage = SourceCoercion.AimEngageStateProvider;
            try
            {
                SourceCoercion.GyroTuningProvider = null;
                SourceCoercion.GyroBiasProvider = null;
                SourceCoercion.GyroAuxBiasProvider = null;
                SourceCoercion.AimEngageStateProvider = null;

                // Same positive rate on the aux pitch and the aux yaw.
                var pitchState = StateWith(0f, 0f, 0f, 2.0f, 0f, 0f);
                var yawState   = StateWith(0f, 0f, 0f, 0f, 2.0f, 0f);

                float auxPitchOnStickX = SourceEvaluator.EvaluateForBipolarAxisTarget(
                    pitchState, Src("Gyro L Pitch"), 0, "RightThumbAxisX", 0, null, 0.016, Dev);
                float auxYawOnStickX = SourceEvaluator.EvaluateForBipolarAxisTarget(
                    yawState, Src("Gyro L Yaw"), 0, "RightThumbAxisX", 0, null, 0.016, Dev);

                // Pitch keeps the raw sign on stick X; yaw is rate-flipped.
                // Comparing them against each other is what pins the
                // exclusion: both are positive rates, so identical signs
                // would mean the flip either hit both or neither.
                Assert.True(auxPitchOnStickX > 0f, $"aux pitch should not flip, got {auxPitchOnStickX}");
                Assert.True(auxYawOnStickX < 0f, $"aux yaw should flip, got {auxYawOnStickX}");

                // The primary spelling must behave identically, which is the
                // property the aux family inherits rather than redefines.
                var pPitchState = StateWith(2.0f, 0f, 0f, 0f, 0f, 0f);
                float primaryPitchOnStickX = SourceEvaluator.EvaluateForBipolarAxisTarget(
                    pPitchState, Src("Gyro Pitch"), 0, "RightThumbAxisX", 0, null, 0.016, Dev);
                Assert.True(primaryPitchOnStickX > 0f, $"primary pitch should not flip, got {primaryPitchOnStickX}");
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.GyroBiasProvider = oldBias;
                SourceCoercion.GyroAuxBiasProvider = oldAuxBias;
                SourceCoercion.AimEngageStateProvider = oldEngage;
            }
        }

        // ── Isolation: the test that actually proves the feature ──

        /// <summary>Distinct primary and aux vectors in ONE state must produce
        /// distinct reads in the SAME evaluation. Moving one Joy-Con must not
        /// move rows bound to the other half.</summary>
        [Fact]
        public void AuxRow_ReadsTheAuxSensor_PrimaryRowUnaffected_SameEvaluation()
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            var oldAuxBias = SourceCoercion.GyroAuxBiasProvider;
            var oldEngage = SourceCoercion.AimEngageStateProvider;
            try
            {
                SourceCoercion.GyroTuningProvider = null;   // engine defaults
                SourceCoercion.GyroBiasProvider = null;
                SourceCoercion.GyroAuxBiasProvider = null;
                SourceCoercion.AimEngageStateProvider = null;

                // Right half still, left half yawing hard.
                var state = StateWith(0f, 0f, 0f, 0f, 2.0f, 0f);

                float primary = SourceCoercion.EvaluateForBipolarAxisTarget(
                    state, Src("Gyro Yaw"), 0, false, Dev);
                float aux = SourceCoercion.EvaluateForBipolarAxisTarget(
                    state, Src("Gyro L Yaw"), 0, false, Dev);

                Assert.True(Math.Abs(primary) < 0.001f, $"primary should be still, got {primary}");
                Assert.True(Math.Abs(aux) > 0.01f, $"aux should move, got {aux}");

                // And the mirror case, same state shape reversed.
                var state2 = StateWith(0f, 2.0f, 0f, 0f, 0f, 0f);
                float primary2 = SourceCoercion.EvaluateForBipolarAxisTarget(
                    state2, Src("Gyro Yaw"), 0, false, Dev);
                float aux2 = SourceCoercion.EvaluateForBipolarAxisTarget(
                    state2, Src("Gyro L Yaw"), 0, false, Dev);

                Assert.True(Math.Abs(primary2) > 0.01f, $"primary should move, got {primary2}");
                Assert.True(Math.Abs(aux2) < 0.001f, $"aux should be still, got {aux2}");
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.GyroBiasProvider = oldBias;
                SourceCoercion.GyroAuxBiasProvider = oldAuxBias;
                SourceCoercion.AimEngageStateProvider = oldEngage;
            }
        }

        /// <summary>Calibration bias is per SENSOR, not per device. Feeding the
        /// primary's drift to the aux would cancel real aux motion (and vice
        /// versa), which is why the provider is aux-aware.</summary>
        [Fact]
        public void AuxBias_IsItsOwn_NotThePrimarys()
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            var oldAuxBias = SourceCoercion.GyroAuxBiasProvider;
            var oldEngage = SourceCoercion.AimEngageStateProvider;
            try
            {
                SourceCoercion.GyroTuningProvider = null;
                SourceCoercion.AimEngageStateProvider = null;
                bool primaryBiasAsked = false, auxBiasAsked = false;
                SourceCoercion.GyroBiasProvider = (_, _) => { primaryBiasAsked = true; return (0f, 2.0f, 0f); };
                SourceCoercion.GyroAuxBiasProvider = (_, _) => { auxBiasAsked = true; return (0f, 0f, 0f); };

                // Both sensors read the same raw rate. The primary's stored
                // bias cancels its reading; the aux has no bias, so it moves.
                var state = StateWith(0f, 2.0f, 0f, 0f, 2.0f, 0f);

                float primary = SourceCoercion.EvaluateForBipolarAxisTarget(
                    state, Src("Gyro Yaw"), 0, false, Dev);
                float aux = SourceCoercion.EvaluateForBipolarAxisTarget(
                    state, Src("Gyro L Yaw"), 0, false, Dev);

                Assert.True(primaryBiasAsked, "primary row must consult the primary bias");
                Assert.True(auxBiasAsked, "aux row must consult the AUX bias");
                Assert.True(Math.Abs(primary) < 0.001f, $"primary bias should cancel, got {primary}");
                Assert.True(Math.Abs(aux) > 0.01f, $"aux must not inherit the primary bias, got {aux}");
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.GyroBiasProvider = oldBias;
                SourceCoercion.GyroAuxBiasProvider = oldAuxBias;
                SourceCoercion.AimEngageStateProvider = oldEngage;
            }
        }

        /// <summary>Player / World space projects the rate against GRAVITY, and
        /// the aux gyro's gravity is its own half's accelerometer (the #199
        /// aux provider), never the primary's.</summary>
        [Fact]
        public void AuxSpaceProjection_UsesTheAuxGravityProvider()
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            var oldAuxBias = SourceCoercion.GyroAuxBiasProvider;
            var oldEngage = SourceCoercion.AimEngageStateProvider;
            var oldGrav = SourceCoercion.GravityProvider;
            var oldGravAux = SourceCoercion.GravityProviderAux;
            try
            {
                SourceCoercion.GyroBiasProvider = null;
                SourceCoercion.GyroAuxBiasProvider = null;
                SourceCoercion.AimEngageStateProvider = null;
                SourceCoercion.GyroTuningProvider = (_, _) => new SourceCoercion.GyroTuning
                {
                    SensH = 1f, SensV = 1f, OutputCurve = "Linear", Space = "Player",
                };
                bool primaryGravAsked = false, auxGravAsked = false;
                SourceCoercion.GravityProvider = _ => { primaryGravAsked = true; return (0f, 0f, -1f); };
                SourceCoercion.GravityProviderAux = _ => { auxGravAsked = true; return (0f, 0f, -1f); };

                var state = StateWith(0f, 1.0f, 0f, 0f, 1.0f, 0f);
                SourceCoercion.EvaluateForBipolarAxisTarget(
                    state, Src("Gyro L Yaw"), 0, false, Dev);

                Assert.True(auxGravAsked, "aux row must project against the AUX gravity");
                Assert.False(primaryGravAsked, "aux row must not read the primary gravity");
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.GyroBiasProvider = oldBias;
                SourceCoercion.GyroAuxBiasProvider = oldAuxBias;
                SourceCoercion.AimEngageStateProvider = oldEngage;
                SourceCoercion.GravityProvider = oldGrav;
                SourceCoercion.GravityProviderAux = oldGravAux;
            }
        }

        // ── Passthrough (DSU / virtual controller) ──

        [Fact]
        public void MotionGyroAuxDescriptor_IsExactAndDisjoint()
        {
            Assert.True(MappingSetMigrator.IsMotionGyroAuxDescriptor("Motion Gyro L"));
            Assert.True(MappingSetMigrator.IsMotionGyroAuxDescriptor(" motion gyro l "));
            Assert.False(MappingSetMigrator.IsMotionGyroAuxDescriptor("Motion Gyro"));
            Assert.False(MappingSetMigrator.IsMotionAccelAuxDescriptor("Motion Gyro L"));
        }

        /// <summary>The passthrough stream honors the aux flag, so a slot can
        /// send the LEFT half's rates to DSU / a virtual DualSense.</summary>
        [Fact]
        public void PassthroughGyro_AuxFlagSelectsTheAuxSensor()
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            var oldAuxBias = SourceCoercion.GyroAuxBiasProvider;
            try
            {
                SourceCoercion.GyroTuningProvider = null;
                SourceCoercion.GyroBiasProvider = null;
                SourceCoercion.GyroAuxBiasProvider = null;

                var state = StateWith(1.0f, 0f, 0f, 0f, 0f, 3.0f);

                SourceCoercion.GetPassthroughGyro(state, Dev, 0,
                    out float p1, out float y1, out float r1, aux: false);
                SourceCoercion.GetPassthroughGyro(state, Dev, 0,
                    out float p2, out float y2, out float r2, aux: true);

                Assert.True(Math.Abs(p1 - 1.0f) < 0.001f, $"primary pitch {p1}");
                Assert.True(Math.Abs(r1) < 0.001f, $"primary roll {r1}");
                Assert.True(Math.Abs(p2) < 0.001f, $"aux pitch {p2}");
                Assert.True(Math.Abs(r2 - 3.0f) < 0.001f, $"aux roll {r2}");
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.GyroBiasProvider = oldBias;
                SourceCoercion.GyroAuxBiasProvider = oldAuxBias;
            }
        }

        // ── Wire ──

        /// <summary>The per-frame codec's presence mask was a FULL u16, so the
        /// aux gyro rides an extension tail. Round-trip proves the tail is
        /// read back, and the capability gate proves a zeroed array is not
        /// mistaken for "absent" (a still controller is a real reading).</summary>
        [Fact]
        public void GyroAux_RoundTripsThroughTheCodecExtensionTail()
        {
            var s = new CustomInputState();
            s.GyroAux[0] = 0.25f; s.GyroAux[1] = -1.5f; s.GyroAux[2] = 3.75f;

            var caps = new CustomInputStateCodec.Caps(false, false, false, gyroAux: true);
            byte[] wire = CustomInputStateCodec.Encode(s, caps);

            var back = new CustomInputState();
            Assert.True(CustomInputStateCodec.DecodeInto(wire, back));
            Assert.Equal(0.25f, back.GyroAux[0], 4);
            Assert.Equal(-1.5f, back.GyroAux[1], 4);
            Assert.Equal(3.75f, back.GyroAux[2], 4);
        }

        [Fact]
        public void GyroAux_OmittedWhenCapabilityAbsent_AndOlderFramesStillDecode()
        {
            var s = new CustomInputState();
            s.GyroAux[0] = 9f;

            // No capability: the tail is not written at all, which is exactly
            // the frame an older peer produces.
            var caps = new CustomInputStateCodec.Caps(false, false, false, gyroAux: false);
            byte[] wire = CustomInputStateCodec.Encode(s, caps);

            var back = new CustomInputState();
            back.GyroAux[0] = 5f; // stale value must be cleared, not kept
            Assert.True(CustomInputStateCodec.DecodeInto(wire, back));
            Assert.Equal(0f, back.GyroAux[0]);
        }

        /// <summary>The device-list v1 capability byte was exhausted at #199,
        /// so the aux gyro's capability rides the v3 tail beside the NFC bit.
        /// Both must survive together.</summary>
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void GyroAuxCapability_RoundTripsBesideTheNfcBit(bool gyroAux, bool nfc)
        {
            var info = new RemotePeerDeviceInfo
            {
                Slot = 3,
                PeerLocalDeviceId = "dev-1",
                Name = "Joy-Con (L/R)",
                VendorId = 0x057E,
                ProductId = 0x2008,
                HasGyro = true,
                HasAccel = true,
                HasAccelAux = true,
                HasGyroAux = gyroAux,
                HasNfcReader = nfc,
            };

            byte[] wire = LinkConnection.EncodeDeviceList(new[] { info });
            var back = LinkConnection.DecodeDeviceList(wire);

            Assert.Single(back);
            Assert.Equal(gyroAux, back[0].HasGyroAux);
            Assert.Equal(nfc, back[0].HasNfcReader);
            // The v1 caps byte's own contents must survive the tail additions.
            Assert.True(back[0].HasAccelAux);
        }
    }
}
