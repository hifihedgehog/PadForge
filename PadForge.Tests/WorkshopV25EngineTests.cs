using System;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Engine coverage for the translator v25 channels: the constant-true
    /// "Always On" source (Steam's always_on_action), the stick deadzone
    /// geometry stamp (<see cref="MappingSource.ParamStickDeadZoneShape"/>,
    /// Steam's deadzone_shape on stick-hosted mouse pairs), the
    /// <see cref="ShiftActivator.DoublePressMs"/> gate field, and the
    /// macro layer-gate DTO round-trip.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WorkshopV25EngineTests
    {
        private static CustomInputState CenteredState()
        {
            var s = new CustomInputState();
            for (int i = 0; i < 6; i++) s.Axis[i] = 32768;
            return s;
        }

        // ─── "Always On" constant source ────────────────────────────────

        [Fact]
        public void AlwaysOn_ReadsTrue_OnEveryCoercion()
        {
            var s = new CustomInputState();
            var src = new MappingSource { Descriptor = SourceCoercion.AlwaysOnDescriptor };
            Assert.True(SourceCoercion.EvaluateForButtonTarget(s, src, 50));
            Assert.Equal(1f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
            Assert.Equal(1f, SourceCoercion.EvaluateForTriggerTarget(s, src), 3);
        }

        // ─── Stick deadzone geometry (deadzone_shape) ───────────────────

        [Fact]
        public void StickShape_Defaults_Off_AndIdentity()
        {
            Assert.Equal(0, new MappingSource().ParamStickDeadZoneShape);
            var s = CenteredState();
            s.Axis[0] = 32768 + 16384; // +0.5
            var src = new MappingSource { Descriptor = "Axis 0" };
            Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        [Fact]
        public void StickShape_LeavesTheDefaultDeadZoneSentinelAlone()
        {
            // DeadZone's DTO default is 50, the button coercion's
            // threshold sentinel. The geometry transform reads its own
            // inner field, so a stamped source without an authored inner
            // radius keeps the full analog range.
            var s = CenteredState();
            s.Axis[0] = 32768 + 16384; // +0.5
            var src = new MappingSource
            {
                Descriptor = "Axis 0",
                ParamStickDeadZoneShape = 2,
            };
            Assert.Equal(50, src.DeadZone); // the sentinel default
            Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        [Fact]
        public void StickShape_Axial_RescalesByOwnMagnitude()
        {
            // Inner 20%: |0.5| remaps to (0.5 - 0.2) / 0.8 = 0.375.
            var s = CenteredState();
            s.Axis[0] = 32768 + 16384; // +0.5
            var src = new MappingSource
            {
                Descriptor = "Axis 0",
                ParamStickDeadZoneInner = 0.2,
                ParamStickDeadZoneShape = 1,
            };
            Assert.Equal(0.375f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);

            // Inside the inner radius: dead.
            s.Axis[0] = 32768 + 3277; // +0.1
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        [Fact]
        public void StickShape_Radial_TestsThePairMagnitude()
        {
            // X alone at 0.15 with inner 20%: axial would read dead, but
            // the companion Y at 0.3 lifts the PAIR magnitude past the
            // ring, so the radial read stays live (Steam Circle).
            var s = CenteredState();
            s.Axis[0] = 32768 + 4915;  // X = +0.15
            s.Axis[1] = 32768 + 9830;  // Y = +0.3, mag ~ 0.335
            var src = new MappingSource
            {
                Descriptor = "Axis 0",
                ParamStickDeadZoneInner = 0.2,
                ParamStickDeadZoneShape = 2,
            };
            float radial = SourceCoercion.EvaluateForBipolarAxisTarget(s, src);
            Assert.True(radial > 0f, $"radial read should be live, got {radial}");

            // Same X with a centered companion: the pair magnitude IS the
            // axis magnitude, inside the ring: dead.
            s.Axis[1] = 32768;
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        [Fact]
        public void StickShape_ConsumesTheOuterRange_NotTheScalarTail()
        {
            // Outer 0.5 with a QUARTER deflection (round eight, R19): a
            // single application gives 0.25 / 0.5 = 0.5, a double
            // application would give 1.0. The old half-deflection input
            // saturated to 1.0 either way and could not distinguish them.
            var s = CenteredState();
            s.Axis[0] = 32768 + 8192; // +0.25
            var src = new MappingSource
            {
                Descriptor = "Axis 0",
                ParamStickDeadZoneShape = 1,
                ParamRangeOuter = 0.5,
            };
            Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        // ─── ShiftActivator.DoublePressMs field contract ────────────────

        [Fact]
        public void ShiftActivator_DoublePressMs_DefaultsOff_AndClones()
        {
            var a = new ShiftActivator();
            Assert.Equal(0, a.DoublePressMs);
            a.DoublePressMs = 442;
            Assert.Equal(442, a.Clone().DoublePressMs); // MemberwiseClone carries it
        }

        // ─── MappingSet gyro engage Toggle stamp ────────────────────────

        [Fact]
        public void WorkshopGyroEngageToggle_RidesTheStampCopy()
        {
            var src = new MappingSet
            {
                WorkshopGyroEngageDescriptor = "Gamepad LeftShoulder",
                WorkshopGyroEngageToggle = true,
            };
            var dst = new MappingSet();
            src.CopyWorkshopStampsTo(dst);
            Assert.True(dst.WorkshopGyroEngageToggle);
            Assert.Equal("Gamepad LeftShoulder", dst.WorkshopGyroEngageDescriptor);
        }
    }
}
