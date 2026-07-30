using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guard pins for the 2026-07-25 round-three audit, which reviewed the
    /// #253/#254 delta the same agent had just written. Every case here is
    /// a defect that shipped in fcb3cedf and was caught by an independent
    /// reviewer, not by the author.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AuditJuly25RoundThreeTests : IDisposable
    {
        private const short Fire = 30000;
        private static readonly Guid DevGuid = new("31313131-3131-3131-3131-313131313131");

        public void Dispose() => InputManager.ClearAllShiftRuntime();

        private static MacroItem Macro(MacroTriggerMode mode, int holdMs = 500,
            string layerMask = "", int pad = 0)
        {
            var m = new MacroItem
            {
                Name = "R3",
                IsEnabled = true,
                PadIndex = pad,
                TriggerButtons = Gamepad.A,
                TriggerMode = mode,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
                TriggerHoldMs = holdMs,
                LayerMask = layerMask,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.AxisSet,
                AxisTarget = MacroAxisTarget.LeftTrigger,
                AxisValue = Fire,
            });
            return m;
        }

        private static ushort Tick(InputManager im, MacroItem[] macros, bool held)
        {
            var gp = new Gamepad { Buttons = held ? Gamepad.A : (ushort)0 };
            im.EvaluateSlotMacros(ref gp, macros);
            return gp.LeftTrigger;
        }

        // ── C1: the picker rebuild must not clobber a scope ──

        /// <summary>A null write is a picker artifact (the ComboBox losing
        /// its match when MacroLayerChoices is Clear()ed), never a user
        /// choice. Accepting it silently downgraded a scoped macro to
        /// "Any layer" and persisted the loss.</summary>
        [Fact]
        public void LayerMask_IgnoresNullWriteBack_FromPickerRebuild()
        {
            var m = Macro(MacroTriggerMode.OnPress, layerMask: "Shift");
            m.LayerMask = null;                 // what WPF's Selector pushes
            Assert.Equal("Shift", m.LayerMask); // the persisted mask is the truth
            Assert.True(m.HasLayerScope);

            m.LayerMask = "";                   // an explicit user pick still lands
            Assert.Equal("", m.LayerMask);
            Assert.False(m.HasLayerScope);
        }

        // ── C14: a button already held at start is not a tap ──

        [Fact]
        public void ShortPress_HeldAtEngineStart_DoesNotFireOnRelease()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.ShortPress, holdMs: 500) };

            // First tick ever sees the button ALREADY down: the press edge
            // was never observed, so the window must not arm.
            Assert.Equal((ushort)0, Tick(im, macros, held: true));
            Assert.Equal((ushort)0, Tick(im, macros, held: false));

            // A genuine tap afterwards still works.
            Assert.Equal((ushort)0, Tick(im, macros, held: true));
            Assert.Equal((ushort)Fire, Tick(im, macros, held: false));
        }

        // ── C15: a layer close is not a release ──

        [Fact]
        public void ShortPress_LayerClose_DoesNotFire()
        {
            var set = new MappingSet();
            set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = DevGuid.ToString(),
                Descriptor = "Button 9",
                LayerMask = "Shift",
                LayerName = "Shift",
                Mode = "Hold",
            });
            var saved = (MappingSet[])SettingsManager.SlotMappingSets.Clone();
            try
            {
                for (int i = 0; i < SettingsManager.SlotMappingSets.Length; i++)
                    SettingsManager.SlotMappingSets[i] = null;
                SettingsManager.SlotMappingSets[0] = set;

                var state = new PadForge.Engine.CustomInputState();
                var im = new InputManager();
                var macros = new[] { Macro(MacroTriggerMode.ShortPress, holdMs: 500, layerMask: "Shift") };

                // Engage the layer, observe an idle tick so the window is
                // ARMABLE (C14), then press so it is genuinely armed. Without
                // the arm this test would pass on the C14 guard alone and
                // never exercise the layer-close guard at all (caught by
                // mutation testing: the C15 revert stayed green).
                state.Buttons[9] = true;
                InputManager.ResolveActiveLayerMask(0, set, state, DevGuid.ToString());
                Assert.Equal((ushort)0, Tick(im, macros, held: false));
                Assert.Equal((ushort)0, Tick(im, macros, held: true));

                // Close the layer while the button is STILL held. The gated
                // trigger falls, but the user released the LAYER, not the
                // macro's button, so nothing may fire.
                state.Buttons[9] = false;
                InputManager.ResolveActiveLayerMask(0, set, state, DevGuid.ToString());
                Assert.Equal((ushort)0, Tick(im, macros, held: true));
            }
            finally
            {
                for (int i = 0; i < saved.Length; i++)
                    SettingsManager.SlotMappingSets[i] = saved[i];
            }
        }

        // ── C17: the Repeat section is dead for a fires-at-release mode ──

        [Fact]
        public void ShortPress_HidesTheRepeatSection()
        {
            var m = Macro(MacroTriggerMode.ShortPress);
            Assert.False(m.ShowsRepeatSection);
            m.TriggerMode = MacroTriggerMode.OnPress;
            Assert.True(m.ShowsRepeatSection);
        }

        // ── C3: an unrepresentable scope stays visible ──

        [Fact]
        public void ScopedMacro_ShowsItsLayerRow_EvenWithoutSlotLayers()
        {
            var m = Macro(MacroTriggerMode.OnPress, layerMask: "");
            Assert.False(m.ShowsLayerRow);
            m.LayerMask = "Layer_9_2";   // e.g. an imported mask
            Assert.True(m.ShowsLayerRow);
        }

        // ── C5: a slot declares the masks its cycle ring steps through ──

        [Fact]
        public void CycleStopMask_CountsAsDeclaredByItsOwnSlot()
        {
            var ownSet = new MappingSet();
            ownSet.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "Ring1",
                LayerName = "Ring1",
                Mode = "Cycle",
                CycleLayers = "Ring1|Ring2",
                Descriptor = "Button 8",
            });
            var foreignSet = new MappingSet();
            foreignSet.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = DevGuid.ToString(),
                LayerMask = "Ring2",
                LayerName = "Ring2",
                Mode = "Hold",
                Descriptor = "Button 9",
            });

            var saved = (MappingSet[])SettingsManager.SlotMappingSets.Clone();
            try
            {
                for (int i = 0; i < SettingsManager.SlotMappingSets.Length; i++)
                    SettingsManager.SlotMappingSets[i] = null;
                SettingsManager.SlotMappingSets[0] = ownSet;
                SettingsManager.SlotMappingSets[1] = foreignSet;

                // Engage "Ring2" on the FOREIGN slot only.
                var st = new PadForge.Engine.CustomInputState();
                st.Buttons[9] = true;
                InputManager.ResolveActiveLayerMask(1, foreignSet, st, DevGuid.ToString());

                // Slot 0 owns "Ring2" as a cycle stop, so the foreign
                // engagement must NOT open its macro (pre-fix, the stop was
                // invisible to SlotDeclaresMask and the gate fell back to
                // the any-slot walk this scoping exists to retire).
                var im = new InputManager();
                var macros = new[] { Macro(MacroTriggerMode.OnPress, layerMask: "Ring2", pad: 0) };
                Assert.Equal((ushort)0, Tick(im, macros, held: true));
            }
            finally
            {
                for (int i = 0; i < saved.Length; i++)
                    SettingsManager.SlotMappingSets[i] = saved[i];
            }
        }

        // ── C6 REVERSED (round four, R8/R9): a layerless slot's Base
        // macro must NOT be closed by an unrelated layer on ANOTHER pad.
        // The round-three fallback coupled every layerless pad to every
        // other pad's layer state; the split-import case it was meant to
        // fix is now handled at the translator instead (the set-switch
        // mirror onto the macro host slot), so Base is purely own-slot. ──

        [Fact]
        public void BaseScopedMacro_IsNotClosedByAnotherPadsLayer()
        {
            var ownSet = new MappingSet();          // pad 0: a Base macro, no activators
            var foreignSet = new MappingSet();      // pad 1: an unrelated layer
            foreignSet.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = DevGuid.ToString(),
                LayerMask = "Shift",
                LayerName = "Shift",
                Mode = "Hold",
                Descriptor = "Button 9",
                InheritUnmapped = false,
            });

            var saved = (MappingSet[])SettingsManager.SlotMappingSets.Clone();
            try
            {
                for (int i = 0; i < SettingsManager.SlotMappingSets.Length; i++)
                    SettingsManager.SlotMappingSets[i] = null;
                SettingsManager.SlotMappingSets[0] = ownSet;
                SettingsManager.SlotMappingSets[1] = foreignSet;

                // Player 2 (pad 1) holds their own Shift layer.
                var st = new PadForge.Engine.CustomInputState();
                st.Buttons[9] = true;
                InputManager.ResolveActiveLayerMask(1, foreignSet, st, DevGuid.ToString());

                // Player 1's (pad 0) Base macro is unaffected: controller
                // isolation. Pre-fix the fallback closed it.
                var im = new InputManager();
                var macros = new[] { Macro(MacroTriggerMode.OnPress, layerMask: "Base", pad: 0) };
                Assert.Equal((ushort)Fire, Tick(im, macros, held: true));
            }
            finally
            {
                for (int i = 0; i < saved.Length; i++)
                    SettingsManager.SlotMappingSets[i] = saved[i];
            }
        }

        // ── C25: the preview names mouse buttons 0-based ──

        [Fact]
        public void PreviewHumanizer_MouseButtonsAreZeroBased()
        {
            var si = PadForge.Resources.Strings.Strings.Instance;
            Assert.Equal(si.Mouse_LeftClick,
                PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("KbmMBtn0"));
            Assert.Equal(si.Mouse_RightClick,
                PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("KbmMBtn1"));
            Assert.Equal(si.Mouse_MiddleClick,
                PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("KbmMBtn2"));
            Assert.Equal(si.Mouse_Button4,
                PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("KbmMBtn3"));
            Assert.Equal(si.Mouse_Button5,
                PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("KbmMBtn4"));

            // The analog lanes use the editor's own row names, so the
            // preview and the mapping grid name a target identically.
            Assert.Equal(si.Mouse_X, PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("KbmMouseX"));
            Assert.Equal(si.Mouse_Y, PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("KbmMouseY"));
            Assert.Equal(si.Mouse_Scroll, PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("KbmScroll"));

            // Keys still resolve, and an out-of-range index stays raw.
            Assert.Equal("Z", PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("KbmKey5A"));
            Assert.Equal("KbmMBtn9", PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("KbmMBtn9"));
        }
    }
}
