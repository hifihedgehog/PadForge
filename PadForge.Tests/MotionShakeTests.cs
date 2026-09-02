using System;
using System.Reflection;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The "Motion Shake" pair (#364, asked in discussion #358) and the
    /// lean-on-button fix that shipped with it.
    ///
    /// <para>Shake is a magnitude event: the read must fire on an
    /// accelerometer-magnitude jolt and stay silent through any slow
    /// reorientation, which is the discriminator the tilt sources lack and
    /// the reason the family exists (the Nunchuk has no gyroscope, so no
    /// rate source can serve it).</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MotionShakeTests
    {
        private const string Dev = "11111111-2222-3333-4444-555555555555";

        private static MappingSource ShakeSrc(bool aux = false, int deadZone = 0) => new()
        {
            Kind = "Direct",
            Descriptor = aux ? SourceCoercion.MotionShakeAuxDescriptor : SourceCoercion.MotionShakeDescriptor,
            DeviceGuid = Dev,
            DeadZone = deadZone,
        };

        [Fact]
        public void Descriptor_Predicates_AreExactAndDisjoint()
        {
            Assert.True(SourceCoercion.IsMotionShakeDescriptor("Motion Shake"));
            Assert.True(SourceCoercion.IsMotionShakeDescriptor(" motion shake "));
            Assert.False(SourceCoercion.IsMotionShakeDescriptor("Motion Shake L"));
            Assert.True(SourceCoercion.IsMotionShakeAuxDescriptor("Motion Shake L"));
            Assert.False(SourceCoercion.IsMotionShakeAuxDescriptor("Motion Shake"));
            // Disjoint from the lean pair despite the shared prefix.
            Assert.False(SourceCoercion.IsMotionLeanDescriptor("Motion Shake"));
            Assert.False(SourceCoercion.IsMotionShakeDescriptor("Motion Lean"));
            // Inherits the benign Motion classification like the lean pair.
            Assert.True(SourceCoercion.IsMotionDescriptor("Motion Shake"));
            Assert.True(SourceCoercion.IsMotionDescriptor("Motion Shake L"));
        }

        [Fact]
        public void ButtonRead_FiresPastThreshold_AndRestsBelow()
        {
            var oldP = SourceCoercion.ShakeEnvelopeProvider;
            var oldA = SourceCoercion.ShakeEnvelopeProviderAux;
            try
            {
                float env = 0f;
                SourceCoercion.ShakeEnvelopeProvider = _ => env;
                SourceCoercion.ShakeEnvelopeProviderAux = _ => 0f;

                var state = new CustomInputState();
                var src = ShakeSrc();

                env = 0f;
                Assert.False(SourceCoercion.EvaluateForButtonTarget(state, src, 30));
                // Just under the 25% default.
                env = 0.20f;
                Assert.False(SourceCoercion.EvaluateForButtonTarget(state, src, 30));
                // Past it.
                env = 0.40f;
                Assert.True(SourceCoercion.EvaluateForButtonTarget(state, src, 30));

                // The per-source DeadZone overrides the default threshold.
                var strict = ShakeSrc(deadZone: 60);
                Assert.False(SourceCoercion.EvaluateForButtonTarget(state, strict, 30));
                env = 0.70f;
                Assert.True(SourceCoercion.EvaluateForButtonTarget(state, strict, 30));
            }
            finally
            {
                SourceCoercion.ShakeEnvelopeProvider = oldP;
                SourceCoercion.ShakeEnvelopeProviderAux = oldA;
            }
        }

        [Fact]
        public void AuxRead_UsesTheAuxProvider_NotThePrimary()
        {
            var oldP = SourceCoercion.ShakeEnvelopeProvider;
            var oldA = SourceCoercion.ShakeEnvelopeProviderAux;
            try
            {
                bool primaryAsked = false;
                SourceCoercion.ShakeEnvelopeProvider = _ => { primaryAsked = true; return 1f; };
                SourceCoercion.ShakeEnvelopeProviderAux = _ => 0.9f;

                var state = new CustomInputState();
                Assert.True(SourceCoercion.EvaluateForButtonTarget(state, ShakeSrc(aux: true), 30));
                Assert.False(primaryAsked);
            }
            finally
            {
                SourceCoercion.ShakeEnvelopeProvider = oldP;
                SourceCoercion.ShakeEnvelopeProviderAux = oldA;
            }
        }

        [Fact]
        public void AxisRead_ReturnsTheEnvelope_ThroughTheDirectPromotion()
        {
            var oldP = SourceCoercion.ShakeEnvelopeProvider;
            try
            {
                SourceCoercion.ShakeEnvelopeProvider = _ => 0.62f;
                float v = SourceEvaluator.EvaluateForBipolarAxisTarget(
                    new CustomInputState(), ShakeSrc(), 0, "RawAxis2", 0,
                    new SourceKindRuntime(), 0.016, Dev);
                Assert.Equal(0.62f, v, 2);
            }
            finally
            {
                SourceCoercion.ShakeEnvelopeProvider = oldP;
            }
        }

        /// <summary>The detector itself, driven through the private state
        /// step: a jolt raises the envelope, the envelope decays after the
        /// jolt, and a slow reorientation (constant magnitude, changing
        /// direction) never raises it at all. The last case is the tilt
        /// immunity the family exists for.</summary>
        [Fact]
        public void ShakeState_SpikesOnJolt_DecaysAfter_AndIgnoresTilt()
        {
            var svcType = Type.GetType("PadForge.Services.InputService, PadForge");
            Assert.NotNull(svcType);
            var step = svcType.GetMethod("UpdateShakeState", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(step);

            var dictType = typeof(System.Collections.Generic.Dictionary<,>)
                .MakeGenericType(typeof(Guid), typeof((float, float)));
            dynamic dict = Activator.CreateInstance(dictType);
            var g = Guid.NewGuid();

            void Feed(float ax, float ay, float az, float dt = 0.016f)
                => step.Invoke(null, new object[] { dict, g, ax, ay, az, dt });

            float Env()
            {
                (float, float) s = dict[g];
                return s.Item2;
            }

            // Settle at rest, face up: |a| = 9.8.
            for (int i = 0; i < 60; i++) Feed(0f, 9.8f, 0f);
            Assert.InRange(Env(), 0f, 0.03f);

            // Slow reorientation: same magnitude, rotating direction. The
            // magnitude never moves, so the envelope must not either.
            for (int i = 0; i <= 30; i++)
            {
                double th = i / 30.0 * Math.PI / 2;
                Feed((float)(9.8 * Math.Sin(th)), (float)(9.8 * Math.Cos(th)), 0f);
            }
            Assert.InRange(Env(), 0f, 0.03f);

            // A jolt: one g of extra magnitude for a few samples.
            for (int i = 0; i < 4; i++) Feed(0f, 19.6f, 0f);
            Assert.True(Env() > 0.30f, $"envelope {Env()} after a 1 g jolt");

            // Release: the envelope decays with its 150 ms constant rather
            // than snapping, so the button holds through the oscillation
            // crossings, then lets go.
            float peak = Env();
            Feed(0f, 9.8f, 0f, dt: 0.10f);
            float mid = Env();
            Assert.True(mid < peak && mid > 0.2f * peak, $"decay step {peak} -> {mid}");
            Feed(0f, 9.8f, 0f, dt: 1.0f);
            Assert.InRange(Env(), 0f, 0.05f);
        }

        /// <summary>THE #364 DEFECT: a "Motion Lean" or "Motion Lean L"
        /// source on a BUTTON target read false, always. It fell through to
        /// the numeric-descriptor parser, which cannot parse it. The fix
        /// gives the pair the same wedge grammar the gravity-tilt family
        /// has, over the steering lean's own math, so the reporter's
        /// "Nunchuk Lean on ZR" mapping presses when the Nunchuk tilts past
        /// the threshold.</summary>
        [Fact]
        public void LeanOnButton_FiresPastTilt_AndRestsFlat()
        {
            var oldA = SourceCoercion.GravityProviderAux;
            try
            {
                // Rest face-up first, so the neutral latch captures a level
                // grip (reaction force +1g up, the provider convention).
                var grav = (gx: 0f, gy: 9.8f, gz: 0f);
                SourceCoercion.GravityProviderAux = _ => grav;
                SourceCoercion.ResetGyroLeanNeutral();

                var state = new CustomInputState();
                var src = new MappingSource
                {
                    Kind = "Direct",
                    Descriptor = SourceCoercion.MotionLeanAuxDescriptor,
                    DeviceGuid = Dev,
                    DeadZone = 20,
                };

                Assert.False(SourceCoercion.EvaluateForButtonTarget(state, src, 30));

                // A hard side tilt (60 degrees): gravity leaks into X well
                // past the 20% threshold of the remapped lean value.
                grav = (gx: -8.49f, gy: 4.9f, gz: 0f);
                Assert.True(SourceCoercion.EvaluateForButtonTarget(state, src, 30));

                // Back to rest: releases.
                grav = (gx: 0f, gy: 9.8f, gz: 0f);
                Assert.False(SourceCoercion.EvaluateForButtonTarget(state, src, 30));
            }
            finally
            {
                SourceCoercion.GravityProviderAux = oldA;
                SourceCoercion.ResetGyroLeanNeutral();
            }
        }

        /// <summary>The shake pair is advertised beside the lean pair, gated
        /// on the same capabilities, with the contextual aux label. Source
        /// contract on the picker builder.</summary>
        [Fact]
        public void PickerAdvertisesShakeBesideLean()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                FindRepoRoot(), "PadForge.App", "Common", "MappingDisplayResolver.cs"));
            int lean = src.IndexOf("Descriptor = \"Motion Lean\"", StringComparison.Ordinal);
            int shake = src.IndexOf("MotionShakeDescriptor, DisplayName = si.Mapping_MotionShake", StringComparison.Ordinal);
            int shakeAux = src.IndexOf("MotionShakeAuxDescriptor, DisplayName = ResolveMotionShakeAuxName(ud)", StringComparison.Ordinal);
            Assert.True(lean > 0 && shake > lean && shakeAux > shake);
            Assert.Contains("ResolveMotionShakeAuxName", src);
        }

        private static string FindRepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        /// <summary>The per-device ResetGyroLeanNeutral overload (the Gyro
        /// Recenter macro's per-slot path) must drop the static lean-on-button
        /// latch too, primary and aux keys, or a recenter leaves the button
        /// read aligned to the old grip while the runtime lean re-zeroes.</summary>
        [Fact]
        public void PerDeviceNeutralResetClearsTheStaticLeanLatch()
        {
            var dictField = typeof(PadForge.Engine.Common.Mapping.SourceCoercion)
                .GetField("_motionLeanNeutralStatic",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(dictField);
            // The latch carries the hold it was captured under (#392), the
            // fourth tuple member.
            var dict = (System.Collections.Concurrent.ConcurrentDictionary<string, (double x, double y, double z, string grip)>)dictField.GetValue(null);

            string guid = System.Guid.NewGuid().ToString("d");
            dict[guid] = (0, 0, -1, "");
            dict[guid + "|L"] = (0, 0, -1, "");
            // Same-window positive control: an unrelated device's latch survives.
            string other = System.Guid.NewGuid().ToString("d");
            dict[other] = (0, 0, -1, "");

            PadForge.Engine.Common.Mapping.SourceCoercion.ResetGyroLeanNeutral(guid);

            Assert.False(dict.ContainsKey(guid));
            Assert.False(dict.ContainsKey(guid + "|L"));
            Assert.True(dict.ContainsKey(other));
            dict.TryRemove(other, out _);
        }
    }
}
