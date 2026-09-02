using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Code audit 2026-07-25 (dispatch-mirror parity): the two macro
    /// dispatch loops are mirrors, and slot routing between them is
    /// EXCLUSIVE. Step 4b runs <c>EvaluateSlotMacrosExtended</c> for a slot
    /// with a raw-HID surface and <c>EvaluateSlotMacros</c> otherwise, so a
    /// case present in only one loop is silently inert on the other's slots.
    ///
    /// <para><c>SetGyroEngaged</c> and <c>ToggleTouchpadOverlay</c> were
    /// handled only in the Gamepad loop while the macro-action picker
    /// offers both on every slot regardless of output type, so authoring
    /// either on an Extended (custom HID) slot did nothing at all. Both
    /// effects are output-type-independent by construction: a global
    /// overlay request and a slot-keyed gyro latch, neither reading or
    /// writing the Gamepad state, which is why mirroring them is correct
    /// rather than merely convenient.</para>
    ///
    /// <para>These pin the raw-HID loop specifically. Deleting either case
    /// from <c>ExecuteMacroActionsExtended</c> fails the matching test.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MacroSlotLevelActionMirrorTests
    {
        /// <summary>One raw-button trigger on word 0, bit 0, firing once on press.</summary>
        private static MacroItem RawTriggerMacro(string name, MacroAction action)
        {
            var m = new MacroItem
            {
                Name = name,
                IsEnabled = true,
                PadIndex = 0,
                TriggerCustomButtons = "00000001,00000000,00000000,00000000",
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
            };
            m.Actions.Add(action);
            return m;
        }

        [Fact]
        public void SetGyroEngaged_On_EngagesGyroOnRawHidSlot()
        {
            var im = new InputManager();
            var macros = new[]
            {
                RawTriggerMacro("gyro-on", new MacroAction
                {
                    Type = MacroActionType.SetGyroEngaged,
                    SetGyroEngagedMode = MacroSetGyroEngagedMode.On,
                }),
            };

            Assert.False(im.GyroEngagedFromMacro[0]);

            var raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            im.EvaluateSlotMacrosExtended(ref raw, macros);

            Assert.True(im.GyroEngagedFromMacro[0]);
        }

        [Fact]
        public void SetGyroEngaged_Toggle_FlipsLatchOnRawHidSlot()
        {
            var im = new InputManager();
            var macros = new[]
            {
                RawTriggerMacro("gyro-toggle", new MacroAction
                {
                    Type = MacroActionType.SetGyroEngaged,
                    SetGyroEngagedMode = MacroSetGyroEngagedMode.Toggle,
                }),
            };

            var raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.True(im.GyroEngagedFromMacro[0]);

            // Release, then a second press flips it back.
            raw = RawHidState.Create(8, 32, 1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);

            raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.False(im.GyroEngagedFromMacro[0]);
        }

        [Fact]
        public void ToggleTouchpadOverlay_RequestsOverlayOnRawHidSlot()
        {
            var im = new InputManager();
            var macros = new[]
            {
                RawTriggerMacro("overlay", new MacroAction
                {
                    Type = MacroActionType.ToggleTouchpadOverlay,
                }),
            };

            Assert.False(im.ToggleTouchpadOverlayRequested);

            var raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            im.EvaluateSlotMacrosExtended(ref raw, macros);

            Assert.True(im.ToggleTouchpadOverlayRequested);
        }

        /// <summary>SwitchLayer (#377) is the third slot-level action, and
        /// its gap was worse than inert: with no case, the raw loop never
        /// advanced the action, so the run re-dispatched the no-op every
        /// tick with CurrentActionIndex frozen. Drives the REAL shift
        /// runtime from an Extended-slot macro: the layer engages and the
        /// one-shot run finishes.</summary>
        [Fact]
        public void SwitchLayer_EngagesTheLayerOnRawHidSlot_AndTheRunAdvances()
        {
            var savedSets = SettingsManager.SlotMappingSets;
            try
            {
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                var set = new MappingSet();
                set.ShiftActivators.Add(new ShiftActivator
                {
                    LayerMask = "Shift1",
                    LayerName = "Shift1",
                    DeviceGuid = Guid.NewGuid().ToString(),
                    Descriptor = "PadA",
                });
                SettingsManager.SlotMappingSets[0] = set;
                InputManager.ClearShiftRuntime(0);

                var im = new InputManager();
                var m = RawTriggerMacro("switch", new MacroAction
                {
                    Type = MacroActionType.SwitchLayer,
                    SwitchLayerMask = "Shift1",
                });
                var macros = new[] { m };
                Assert.Equal("Base", InputManager.GetEngagedLayerMask(0, set));

                var raw = RawHidState.Create(8, 32, 1);
                raw.Buttons[0] = 1;
                im.EvaluateSlotMacrosExtended(ref raw, macros);
                Assert.Equal("Shift1", InputManager.GetEngagedLayerMask(0, set));

                // The button up, one more tick: the one-shot advanced past
                // its only action and the run is over, never re-dispatched.
                raw = RawHidState.Create(8, 32, 1);
                im.EvaluateSlotMacrosExtended(ref raw, macros);
                Assert.False(m.IsExecuting);
                Assert.Equal("Shift1", InputManager.GetEngagedLayerMask(0, set));
            }
            finally
            {
                SettingsManager.SlotMappingSets = savedSets;
                InputManager.ClearShiftRuntime(0);
            }
        }
    }
}
