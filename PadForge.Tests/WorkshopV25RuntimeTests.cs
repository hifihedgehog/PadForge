using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Translator v25 runtime pins: the shift activator's
    /// double-press gate (<see cref="ShiftActivator.DoublePressMs"/>,
    /// Steam's Double_Press layer verbs) and the macro layer gate
    /// (<c>MacroData.LayerMask</c>, Steam's always_on_action on non-Base
    /// sets).</summary>
    public class WorkshopV25RuntimeTests : IDisposable
    {
        private readonly MappingSet[] _savedSlotSets;

        public WorkshopV25RuntimeTests()
        {
            _savedSlotSets = SettingsManager.SlotMappingSets;
        }

        public void Dispose()
        {
            SettingsManager.SlotMappingSets = _savedSlotSets;
        }

        // ── ShiftActivator DoublePressMs (Step3 gate) ──

        private static MappingSet DoublePressLatchSet()
        {
            var ms = new MappingSet();
            var row = new MappingRow { Target = "ButtonA", LayerMask = "Alt" };
            row.Sources.Add(new MappingSource { Descriptor = "Button 16" });
            ms.Rows.Add(row);
            ms.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "",
                Descriptor = "Button 28",
                Mode = "Custom", // Latch: press engages, press again releases
                LayerMask = "Alt",
                LayerName = "Alt",
                Kind = "Button",
                DoublePressMs = 5000, // wide window: consecutive calls are within it
            });
            return ms;
        }

        [Fact]
        public void DoublePressGate_SinglePressEngagesNothing_PairEngages()
        {
            InputManager.ClearAllShiftRuntime();
            const int slot = 3;
            const string guid = "";
            var ms = DoublePressLatchSet();
            var state = new CustomInputState();

            // First press: gated, no engage.
            state.Buttons[28] = true;
            Assert.Equal("Base", InputManager.ResolveActiveLayerMask(slot, ms, state, guid));

            // Release: still nothing.
            state.Buttons[28] = false;
            Assert.Equal("Base", InputManager.ResolveActiveLayerMask(slot, ms, state, guid));

            // Second press inside the window: the Latch fires.
            state.Buttons[28] = true;
            Assert.Equal("Alt", InputManager.ResolveActiveLayerMask(slot, ms, state, guid));

            // Release after the pair: the latch holds (Custom is a latch).
            state.Buttons[28] = false;
            Assert.Equal("Alt", InputManager.ResolveActiveLayerMask(slot, ms, state, guid));

            // A lone third press starts a FRESH first press (the pair was
            // consumed), so the latch does not step back to Base yet.
            state.Buttons[28] = true;
            Assert.Equal("Alt", InputManager.ResolveActiveLayerMask(slot, ms, state, guid));
            state.Buttons[28] = false;
            Assert.Equal("Alt", InputManager.ResolveActiveLayerMask(slot, ms, state, guid));

            // Its second press completes another pair: latch releases.
            state.Buttons[28] = true;
            Assert.Equal("Base", InputManager.ResolveActiveLayerMask(slot, ms, state, guid));
        }

        [Fact]
        public void DoublePressGate_ZeroMs_KeepsThePlainRead()
        {
            InputManager.ClearAllShiftRuntime();
            const int slot = 4;
            var ms = DoublePressLatchSet();
            ms.ShiftActivators[0].DoublePressMs = 0;
            var state = new CustomInputState();

            state.Buttons[28] = true;
            Assert.Equal("Alt", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
        }

        // ── Macro layer gate (Step4b) ──

        private static MacroItem LayerGatedKeyMacro(string layerMask)
        {
            var m = new MacroItem
            {
                Name = "V25 gate",
                IsEnabled = true,
                PadIndex = 5,
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
                TriggerButtons = Gamepad.A,
                LayerMask = layerMask,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.ButtonPress,
                ButtonFlags = Gamepad.B,
                CustomButtons = "00000002,00000000,00000000,00000000",
                DurationMs = 1000,
            });
            return m;
        }

        [Fact]
        public void MacroLayerGate_ClosedWhenNoSlotEngagesTheMask()
        {
            InputManager.ClearAllShiftRuntime();
            // Slot sets exist but nothing engages "Layer_1_1".
            SettingsManager.SlotMappingSets = new MappingSet[16];
            SettingsManager.SlotMappingSets[5] = new MappingSet();

            var im = new InputManager();
            var macros = new[] { LayerGatedKeyMacro("Layer_1_1") };
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(macros[0].IsExecuting);
        }

        [Fact]
        public void MacroLayerGate_EmptyMask_IsUngated()
        {
            InputManager.ClearAllShiftRuntime();
            SettingsManager.SlotMappingSets = new MappingSet[16];

            var im = new InputManager();
            var macros = new[] { LayerGatedKeyMacro("") };
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(macros[0].IsExecuting);
        }

        [Fact]
        public void MacroLayerGate_OpensWhileAnySlotEngagesTheMask()
        {
            InputManager.ClearAllShiftRuntime();
            const string mask = "Layer_9_1";
            // Slot 2 carries a latched activator for the mask; the macro
            // rides slot 5 (the split-config shape: macros on the Xbox
            // slot, the layer's rows elsewhere).
            var layerSet = new MappingSet();
            layerSet.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "",
                Descriptor = "Button 28",
                Mode = "Custom",
                LayerMask = mask,
                Kind = "Button",
            });
            SettingsManager.SlotMappingSets = new MappingSet[16];
            SettingsManager.SlotMappingSets[2] = layerSet;
            SettingsManager.SlotMappingSets[5] = new MappingSet();

            // Engage the latch on slot 2.
            var state = new CustomInputState();
            state.Buttons[28] = true;
            Assert.Equal(mask, InputManager.ResolveActiveLayerMask(2, layerSet, state, ""));

            var im = new InputManager();
            var macros = new[] { LayerGatedKeyMacro(mask) };
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(macros[0].IsExecuting);
        }

        [Fact]
        public void MacroLayerMask_RoundTripsThroughTheDto()
        {
            var item = LayerGatedKeyMacro("Layer_7_2");
            var data = Services.SettingsService.BuildMacroDataForMacro(item, 5);
            Assert.Equal("Layer_7_2", data.LayerMask);
            var back = Services.SettingsService.LoadMacroFromData(data,
                Engine.VirtualControllerType.Xbox, null);
            Assert.Equal("Layer_7_2", back.LayerMask);
        }
    }
}
