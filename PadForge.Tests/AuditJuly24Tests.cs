using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Contract locks for the 2026-07-24 whole-codebase audit. Each test
    /// fails if its fix is reverted (mutation-verified), so the contracts
    /// cannot silently regress.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AuditJuly24Tests
    {
        /// <summary>R2 (lens 1r, data loss): the KBM legacy-merge grammar
        /// must accept the horizontal wheel. KbmScrollH / KbmScrollHNeg are
        /// real targets (MappingTranslation), and a target the migrator
        /// rejects rebuilds no row, which the clear-then-rewrite save then
        /// wipes.</summary>
        [Theory]
        [InlineData("KbmScrollH")]
        [InlineData("KbmScrollHNeg")]
        [InlineData("KbmScroll")]
        [InlineData("KbmScrollNeg")]
        [InlineData("KbmMouseX")]
        [InlineData("KbmMouseYNeg")]
        public void KbmDictionaryTargets_SurviveTheLegacyMerge(string key)
        {
            var ps = new PadSetting();
            ps.SetKbmMapping(key, "Button 3");
            // The setter writes the in-memory dict; the migrator reads the
            // serialized array, which is what a loaded profile carries.
            ps.FlushKbmMappings();

            var ms = MappingSetMigrator.BuildFromLegacy(0, new[]
            {
                (DeviceGuid: "11111111-1111-1111-1111-111111111111",
                 PadSetting: ps,
                 IsGamepadEligible: false),
            });

            string wanted = key.EndsWith("Neg", StringComparison.Ordinal)
                ? key.Substring(0, key.Length - 3)
                : key;
            Assert.Contains(ms.Rows, r =>
                string.Equals(r.Target, wanted, StringComparison.Ordinal));
        }

        /// <summary>R3 (#238 audit): Toggle consumes its trigger button on
        /// BOTH presses when ConsumeTriggerButtons is set. The unlatch press
        /// drives the latch false and clears IsExecuting, so a gate keyed on
        /// those two leaked the physical press to the game.</summary>
        [Fact]
        public void Toggle_ConsumesTheTriggerOnTheUnlatchPress()
        {
            var im = new InputManager();
            var m = new MacroItem
            {
                Name = "T",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = Gamepad.A,
                TriggerMode = MacroTriggerMode.Toggle,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = true,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisSet,
                AxisTarget = MacroAxisTarget.LeftTrigger,
                AxisValue = 20000,
            });
            var macros = new[] { m };

            ushort Tick(bool held)
            {
                var gp = new Gamepad { Buttons = held ? Gamepad.A : (ushort)0 };
                im.EvaluateSlotMacros(ref gp, macros);
                return gp.Buttons;
            }

            Assert.Equal(0, Tick(true));   // press 1: latches, consumed
            Tick(false);                    // release: latch holds
            // Press 2 unlatches. The button must STILL be eaten, or the
            // game sees a press the macro was configured to swallow.
            Assert.Equal(0, Tick(true));
        }

        /// <summary>R10: switching fire mode mid-run ends the run. The
        /// setter already voided what the old mode ARMED; what it STARTED
        /// outlived the switch, and the new mode's stop conditions never
        /// match a run it did not begin, so an all-continuous sequence
        /// asserted forever and no later press could restart the macro.</summary>
        [Fact]
        public void ChangingFireMode_EndsAnExecutingRun()
        {
            var m = new MacroItem
            {
                Name = "M",
                IsEnabled = true,
                TriggerMode = MacroTriggerMode.Toggle,
                IsExecuting = true,
                CurrentActionIndex = 3,
                ComboResumeIndex = 2,
                AwaitReleaseAfterBreak = true,
                ToggleTriggerLatched = true,
                ToggleRawWasActive = true,
            };

            m.TriggerMode = MacroTriggerMode.OnPress;

            Assert.False(m.IsExecuting);
            Assert.Equal(0, m.CurrentActionIndex);
            Assert.Equal(0, m.ComboResumeIndex);
            Assert.False(m.AwaitReleaseAfterBreak);
            Assert.False(m.ToggleTriggerLatched);
            Assert.False(m.ToggleRawWasActive);
        }

        /// <summary>R7 (lens 1m): the combined gen-1 Joy-Con pair is
        /// Bluetooth by construction (Joy-Cons only combine wirelessly), but
        /// its synthetic SDL path carries no BT marker, so the transport
        /// gate refused it and its NFC reader never armed while the picker
        /// still offered the pair's tag sources.</summary>
        [Fact]
        public void CombinedJoyConPair_ClassifiesAsBluetooth()
        {
            Assert.True(PadForge.Common.DeviceTransport.IsBluetooth(
                "nintendo_joycons_combined", 0x057E, 0x2008));
            // A single Joy-Con still needs a real BT path marker, and a
            // USB Switch Pro must stay non-Bluetooth (the NFC arming gate
            // depends on that half).
            Assert.False(PadForge.Common.DeviceTransport.IsBluetooth(
                @"\\?\HID#VID_057E&PID_2009#6&abc&0&0000", 0x057E, 0x2009));
        }
    }
}
