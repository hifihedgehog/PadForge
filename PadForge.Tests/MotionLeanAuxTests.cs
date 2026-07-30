using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins the "Motion Lean L" aux-accelerometer source family (issue #199,
    /// Nunchuk / left Joy-Con). The two load-bearing contracts: the aux channel
    /// reads GravityProviderAux (never the primary provider), and the aux
    /// neutral-grip capture is keyed separately from the primary's so the two
    /// sensors on one shared device GUID never cross-contaminate each other's
    /// realignment (the recipe's mandatory cite-verify finding).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MotionLeanAuxTests
    {
        // Raw accelerometer convention (SourceCoercion.GravityProvider doc +
        // TickMotionLean's negation comment): the provider returns the reaction
        // force, +1g UP at rest, SDL frame (+Y up). Rest face-up:
        private static readonly (float, float, float) Rest = (0f, 9.8f, 0f);
        // ~30° side tilt about the Forward axis: gravity leaks into X.
        // asin(4.9/9.8) = 30°, inner deadzone 15 / outer 135 defaults remap to
        // (30 - 15) / (180 - 135 - 15) = 0.5.
        private static readonly (float, float, float) Tilt30 = (-4.9f, 8.4870f, 0f);

        private static MappingSource AuxSrc() => new()
        {
            Kind = "Direct",
            Descriptor = SourceCoercion.MotionLeanAuxDescriptor,
            DeviceGuid = "11111111-2222-3333-4444-555555555555",
        };

        [Fact]
        public void Descriptor_Predicates_AreExactAndDisjoint()
        {
            Assert.True(SourceCoercion.IsMotionLeanAuxDescriptor("Motion Lean L"));
            Assert.True(SourceCoercion.IsMotionLeanAuxDescriptor(" motion lean l "));
            Assert.False(SourceCoercion.IsMotionLeanAuxDescriptor("Motion Lean"));
            Assert.False(SourceCoercion.IsMotionLeanDescriptor("Motion Lean L"));
            // The family inherits the benign Motion classification the primary has.
            Assert.True(SourceCoercion.IsMotionDescriptor("Motion Lean L"));
        }

        [Fact]
        public void AuxTick_ReadsAuxProvider_NotPrimary()
        {
            var runtime = new SourceKindRuntime();
            bool primaryCalled = false, auxCalled = false;
            var oldP = SourceCoercion.GravityProvider;
            var oldA = SourceCoercion.GravityProviderAux;
            try
            {
                SourceCoercion.GravityProvider = _ => { primaryCalled = true; return Rest; };
                SourceCoercion.GravityProviderAux = _ => { auxCalled = true; return Rest; };

                runtime.TickMotionLean(0, "LeftThumbAxisX", 0, AuxSrc(), new CustomInputState(), "g", aux: true);

                Assert.True(auxCalled);
                Assert.False(primaryCalled);
            }
            finally { SourceCoercion.GravityProvider = oldP; SourceCoercion.GravityProviderAux = oldA; }
        }

        [Fact]
        public void AuxNeutral_IsIndependentOfPrimaryNeutral()
        {
            // Primary captures its neutral at rest. If the aux capture shared
            // the primary's key, the aux's FIRST tilted sample would realign
            // against the rest neutral and read ~0.5 immediately. With its own
            // key it latches the tilt AS its neutral and reads ~0.
            var runtime = new SourceKindRuntime();
            var oldP = SourceCoercion.GravityProvider;
            var oldA = SourceCoercion.GravityProviderAux;
            try
            {
                SourceCoercion.GravityProvider = _ => Rest;
                SourceCoercion.GravityProviderAux = _ => Tilt30;

                var src = AuxSrc();
                var state = new CustomInputState();

                double primary = runtime.TickMotionLean(0, "LeftThumbAxisX", 0,
                    new MappingSource { Kind = "Direct", Descriptor = SourceCoercion.MotionLeanDescriptor, DeviceGuid = src.DeviceGuid },
                    state, src.DeviceGuid);
                Assert.Equal(0, primary, 2);

                double auxFirst = runtime.TickMotionLean(0, "RightThumbAxisX", 0, src, state, src.DeviceGuid, aux: true);
                Assert.Equal(0, auxFirst, 2);
            }
            finally { SourceCoercion.GravityProvider = oldP; SourceCoercion.GravityProviderAux = oldA; }
        }

        [Fact]
        public void AuxLean_ProducesProportionalOutput_AfterNeutralAtRest()
        {
            // Capture the aux neutral at rest, then tilt: 30° with the default
            // 15/135 deadzones remaps to 0.5 on the mapped side.
            var runtime = new SourceKindRuntime();
            var oldP = SourceCoercion.GravityProvider;
            var oldA = SourceCoercion.GravityProviderAux;
            try
            {
                var aux = Rest;
                SourceCoercion.GravityProvider = _ => Rest;
                SourceCoercion.GravityProviderAux = _ => aux;

                var src = AuxSrc();
                var state = new CustomInputState();

                double atRest = runtime.TickMotionLean(0, "LeftThumbAxisX", 0, src, state, src.DeviceGuid, aux: true);
                Assert.Equal(0, atRest, 2);

                aux = Tilt30;
                double tilted = runtime.TickMotionLean(0, "LeftThumbAxisX", 0, src, state, src.DeviceGuid, aux: true);
                Assert.Equal(0.5, System.Math.Abs(tilted), 1);
            }
            finally { SourceCoercion.GravityProvider = oldP; SourceCoercion.GravityProviderAux = oldA; }
        }

        [Fact]
        public void MotionAccelAux_Predicates_AreExactAndDisjoint()
        {
            // "Motion Accel L" (#199 follow-up): the MotionAccel passthrough
            // row sourced from the aux accelerometer.
            Assert.True(MappingSetMigrator.IsMotionAccelAuxDescriptor("Motion Accel L"));
            Assert.True(MappingSetMigrator.IsMotionAccelAuxDescriptor(" motion accel l "));
            Assert.False(MappingSetMigrator.IsMotionAccelAuxDescriptor("Motion Accel"));
            Assert.False(MappingSetMigrator.IsMotionAccelAuxDescriptor("Motion Lean L"));
            // Inherited-benign classification, like the lean family.
            Assert.True(SourceCoercion.IsMotionDescriptor("Motion Accel L"));
            // The sub-channel parser must NOT read "Accel L" as the primary
            // accel channel: exact-match only ("Accel" -> 1, "Accel L" -> -1).
            Assert.Equal(1, SourceCoercion.ParseMotionSubChannel("Motion Accel"));
            Assert.Equal(-1, SourceCoercion.ParseMotionSubChannel("Motion Accel L"));
        }

        [Fact]
        public void CustomInputState_Clone_CarriesAccelAux()
        {
            var s = new CustomInputState();
            s.AccelAux[0] = 1f; s.AccelAux[1] = 2f; s.AccelAux[2] = 3f;
            var c = s.Clone();
            Assert.Equal(new[] { 1f, 2f, 3f }, c.AccelAux);
            // Deep copy, not a shared reference.
            c.AccelAux[0] = 9f;
            Assert.Equal(1f, s.AccelAux[0]);
        }
    }
}
