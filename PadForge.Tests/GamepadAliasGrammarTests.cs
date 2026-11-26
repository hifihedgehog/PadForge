using System;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #9 code-audit guards for the "Gamepad ..." alias namespace against the
    /// descriptor-grammar inspectors OUTSIDE the coercion pipeline (the 1k
    /// lens, second sweep): the VM predicates that gate sliders / checkboxes
    /// on the primary's I/H-prefixed encoded form, the macro trigger-entry
    /// builder, and the InvertOnHold modifier read. Each of these parsed the
    /// raw descriptor grammar and silently missed either the legacy prefix
    /// encoding or the alias family.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class GamepadAliasGrammarTests
    {
        private static CustomInputState CenteredState()
        {
            var s = new CustomInputState();
            for (int i = 0; i < 6; i++) s.Axis[i] = 32768;
            return s;
        }

        // ── MappingItem primary predicates (encoded I/H form) ──

        [Theory]
        [InlineData("Axis 2")]
        [InlineData("IAxis 2")]
        [InlineData("HAxis 2")]
        [InlineData("IHAxis 2")]
        [InlineData("Gamepad LeftStickY")]
        [InlineData("IHGamepad LeftStickY")]
        public void MappingItem_AnalogGates_SurvivePrefixAndAlias(string encoded)
        {
            var mi = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            mi.LoadDescriptor(encoded);
            Assert.True(mi.IsDeadZoneApplicable);
            Assert.True(mi.IsHalfAxisApplicable);
        }

        [Theory]
        [InlineData("Gyro Pitch")]
        [InlineData("IGyro Pitch")]
        public void MappingItem_GyroGate_SurvivesPrefix(string encoded)
        {
            var mi = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            mi.LoadDescriptor(encoded);
            Assert.True(mi.IsGyroSource);
        }

        [Fact]
        public void MappingItem_PrefixExemptFamilies_AreNotStripped()
        {
            var mi = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            mi.LoadDescriptor("IR Pointer X");
            Assert.False(mi.IsInverted);
            Assert.True(mi.IsDeadZoneApplicable); // engine family reads the per-source DeadZone
        }

        [Theory]
        [InlineData("IR Pointer X")]
        [InlineData("IIR Pointer X")]
        public void MappingItem_IrPointerGate_SurvivesPrefix(string encoded)
        {
            var mi = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            mi.LoadDescriptor(encoded);
            Assert.True(mi.IsIrPointerSource);
        }

        // ── MappingSourceItem (grid extras, clean descriptors) ──

        [Fact]
        public void MappingSourceItem_AliasGates_MatchRawEquivalents()
        {
            var alias = new MappingSourceItem
            {
                Descriptor = "Gamepad LeftTrigger",
                ParentTargetIsDiscrete = true,
            };
            Assert.True(alias.IsHalfAxisApplicable);
            Assert.True(alias.IsDeadZoneApplicable);
            Assert.False(alias.IsButtonClassDescriptor);

            var aliasButton = new MappingSourceItem { Descriptor = "Gamepad ButtonA" };
            Assert.True(aliasButton.IsButtonClassDescriptor);
            var aliasPov = new MappingSourceItem { Descriptor = "Gamepad DPadUp" };
            Assert.True(aliasPov.IsButtonClassDescriptor);
        }

        // ── Macro trigger entries from alias picker choices ──

        [Theory]
        [InlineData("Gamepad ButtonA")]
        [InlineData("Gamepad DPadLeft")]
        [InlineData("Gamepad LeftStickX")]
        [InlineData("Gamepad RightTrigger")]
        public void MacroTriggerEntry_ConvertsAliasChoices(string descriptor)
        {
            var choice = new InputChoice
            {
                Descriptor = descriptor,
                DeviceGuid = Guid.NewGuid().ToString(),
            };
            Assert.True(MacroItem.TryBuildTriggerEntry(choice, out var entry));
            Assert.NotNull(entry);
        }

        [Fact]
        public void MacroTriggerEntry_AliasAxisMapsToTheSameTargetAsRaw()
        {
            var guid = Guid.NewGuid().ToString();
            Assert.True(MacroItem.TryBuildTriggerEntry(
                new InputChoice { Descriptor = "Gamepad LeftStickX", DeviceGuid = guid }, out var alias));
            Assert.True(MacroItem.TryBuildTriggerEntry(
                new InputChoice { Descriptor = "Axis 0", DeviceGuid = guid }, out var raw));
            Assert.Equal(raw.AxisTarget, alias.AxisTarget);
        }

        // ── InvertOnHold modifier read (the row's modifier, in Step 3) ──

        /// <summary>A "Gamepad ..." name as an Invert on Hold modifier flips
        /// its stick row while the button it resolves to is held.</summary>
        [Fact]
        public void InvertOnHold_AliasModifier_FlipsItsRow()
        {
            string pass = Guid.NewGuid().ToString();
            var set = RowModifierHarness.Row("LeftThumbAxisX", "Axis 0", "Gamepad ButtonA");
            var state = CenteredState();
            state.Axis[0] = 65535; // full right

            state.Buttons[0] = false;
            short unheld = RowModifierHarness.StickX(set, state, pass, 0);
            state.Buttons[0] = true;
            short held = RowModifierHarness.StickX(set, state, pass, 0);

            Assert.True(unheld > 32000, "unheld " + unheld);
            Assert.True(held < -32000, "held " + held);
        }

        // ── Balance providers receive the caller-resolved device guid ──

        [Fact]
        public void BalanceRead_PassesEvaluatedGuidToProviders()
        {
            var oldCal = SourceCoercion.BalanceCalibrationProvider;
            try
            {
                string calGuid = null;
                SourceCoercion.BalanceCalibrationProvider = g => { calGuid = g; return null; };

                var state = CenteredState();
                state.Axis[0] = 40000; // some load so the read runs to the calibration
                var src = new MappingSource { Descriptor = "Balance Total Weight", DeviceGuid = "" };
                SourceCoercion.EvaluateForTriggerTarget(state, src, 0, evaluatedDeviceGuid: "device-guid");

                Assert.Equal("device-guid", calGuid);
            }
            finally
            {
                SourceCoercion.BalanceCalibrationProvider = oldCal;
            }
        }
    }
}
