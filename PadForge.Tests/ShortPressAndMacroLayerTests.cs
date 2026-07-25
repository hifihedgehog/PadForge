using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #253 (On Short Press: fires at release when the hold stayed under
    /// TriggerHoldMs, the On Long Press twin sharing its threshold) and
    /// #254 (macro shift-layer scope: "" = any layer, "Base" follows the
    /// engaged layer's inheritance, a named mask fires only while that
    /// layer is engaged on the MACRO'S OWN slot).
    /// </summary>
    /// <remarks>Shares the SettingsManagerStatics collection: the layer
    /// cases swap SettingsManager.SlotMappingSets and drive the static
    /// shift runtime, so running beside another class that touches the
    /// same statics made this class intermittently fail (observed once in
    /// three full-suite runs before enrolling it here).</remarks>
    [Collection("SettingsManagerStatics")]
    public class ShortPressAndMacroLayerTests
    {
        private const short Fire = 30000;

        private static MacroItem Macro(MacroTriggerMode mode, int holdMs = 500, string layerMask = "", int pad = 0)
        {
            var m = new MacroItem
            {
                Name = "SP",
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

        /// <summary>Drives the raw evaluator and returns the left-trigger
        /// channel. A freshly created raw state starts every axis at 0, so
        /// the probe is "did the macro write the channel", not a rest-value
        /// comparison (Step 3 is what parks a real trigger at MinValue).</summary>
        private static short TickExtended(InputManager im, MacroItem[] macros, bool held)
        {
            var raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = (byte)(held ? 1 : 0);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            return raw.Axes[2];
        }

        // ── #253 On Short Press ──

        /// <summary>A tap under the threshold fires once, AT RELEASE (not
        /// on the press: the duration is unknown until the button is up).</summary>
        [Fact]
        public void ShortPress_TapUnderThreshold_FiresOnceAtRelease()
        {
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.ShortPress, holdMs: 500) };

            // One idle tick first: the C14 guard treats a button that is
            // already down on a macro's FIRST evaluated tick as held-through
            // -start (its press edge was never observed), so a genuine tap
            // must be preceded by an observed release, which at poll rate it
            // always is.
            Assert.Equal((ushort)0, Tick(im, macros, held: false));
            // Press: nothing yet, the hold could still become long.
            Assert.Equal((ushort)0, Tick(im, macros, held: true));
            // Release inside the window: fires.
            Assert.Equal((ushort)Fire, Tick(im, macros, held: false));
        }

        /// <summary>Holding past the threshold fires nothing, which is what
        /// makes tap-vs-hold composable on one button.</summary>
        [Fact]
        public void ShortPress_HeldPastThreshold_NeverFires()
        {
            var im = new InputManager();
            var m = Macro(MacroTriggerMode.ShortPress, holdMs: 200);
            var macros = new[] { m };

            Assert.Equal((ushort)0, Tick(im, macros, held: false)); // observed idle (C14)
            Assert.Equal((ushort)0, Tick(im, macros, held: true));
            // Back-date the arm instead of sleeping (the MacroWave1b idiom).
            m.TriggerHoldStartUtc = DateTime.UtcNow.AddMilliseconds(-600);
            Assert.Equal((ushort)0, Tick(im, macros, held: false));
            // And it stays quiet on subsequent idle ticks.
            Assert.Equal((ushort)0, Tick(im, macros, held: false));
        }

        /// <summary>The run survives the until-release stop: a short press
        /// starts with the trigger ALREADY UP, so without
        /// RunReleasedFireToCompletion the stop block kills it on the same
        /// frame it starts and the action never asserts.</summary>
        [Fact]
        public void ShortPress_UntilReleaseShape_StillRunsItsPass()
        {
            var im = new InputManager();
            var m = Macro(MacroTriggerMode.ShortPress, holdMs: 500);
            m.RepeatMode = MacroRepeatMode.UntilRelease;
            var macros = new[] { m };

            Assert.Equal((ushort)0, Tick(im, macros, held: false)); // observed idle (C14)
            Assert.Equal((ushort)0, Tick(im, macros, held: true));
            Assert.Equal((ushort)Fire, Tick(im, macros, held: false));
        }

        /// <summary>Short and long on the same button each fire only in
        /// their own case, the pair the feature exists for.</summary>
        [Fact]
        public void ShortAndLongPress_EachFireOnlyInTheirOwnCase()
        {
            // Tap: short fires, long does not.
            var im = new InputManager();
            var shortM = Macro(MacroTriggerMode.ShortPress, holdMs: 300);
            var longM = Macro(MacroTriggerMode.HoldForMs, holdMs: 300);
            longM.Actions[0].AxisTarget = MacroAxisTarget.RightTrigger;
            var macros = new[] { shortM, longM };

            // Observed idle first (C14).
            var gpIdle = new Gamepad();
            im.EvaluateSlotMacros(ref gpIdle, macros);

            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal((ushort)0, gp.LeftTrigger);
            Assert.Equal((ushort)0, gp.RightTrigger);

            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal((ushort)Fire, gp.LeftTrigger);   // short fired at release
            Assert.Equal((ushort)0, gp.RightTrigger);     // long never did

            // Hold: long fires, short does not.
            var im2 = new InputManager();
            var shortM2 = Macro(MacroTriggerMode.ShortPress, holdMs: 300);
            var longM2 = Macro(MacroTriggerMode.HoldForMs, holdMs: 300);
            longM2.Actions[0].AxisTarget = MacroAxisTarget.RightTrigger;
            var macros2 = new[] { shortM2, longM2 };

            gpIdle = new Gamepad();
            im2.EvaluateSlotMacros(ref gpIdle, macros2); // observed idle (C14)

            gp = new Gamepad { Buttons = Gamepad.A };
            im2.EvaluateSlotMacros(ref gp, macros2);
            shortM2.TriggerHoldStartUtc = DateTime.UtcNow.AddMilliseconds(-600);
            longM2.TriggerHoldStartUtc = DateTime.UtcNow.AddMilliseconds(-600);

            gp = new Gamepad { Buttons = Gamepad.A };
            im2.EvaluateSlotMacros(ref gp, macros2);
            Assert.Equal((ushort)Fire, gp.RightTrigger);  // long fired at the threshold
            gp = new Gamepad();
            im2.EvaluateSlotMacros(ref gp, macros2);
            Assert.Equal((ushort)0, gp.LeftTrigger);      // short stayed quiet
        }

        /// <summary>The extended (raw-HID) evaluator carries the same mode.
        /// Its trigger-mode switch is a VERBATIM DUPLICATE of the gamepad
        /// loop's, so every mode needs coverage on both.</summary>
        [Fact]
        public void ShortPress_WorksOnTheExtendedLoop()
        {
            var im = new InputManager();
            var m = Macro(MacroTriggerMode.ShortPress, holdMs: 500);
            m.TriggerButtons = 0;
            m.TriggerCustomButtons = "00000001,00000000,00000000,00000000";
            var macros = new[] { m };

            TickExtended(im, macros, held: false); // observed idle (C14)
            // Held: nothing written yet (the hold could still become long).
            Assert.Equal((short)0, TickExtended(im, macros, held: true));
            // Released inside the window: the action writes the trigger on
            // the pull scale (MinValue rest + AxisValue doubled, #253/C36).
            Assert.Equal((short)(short.MinValue + Fire * 2), TickExtended(im, macros, held: false));
        }

        [Fact]
        public void ShortPress_EnumOrdinalIsPinnedAtTheTail()
        {
            // The macro clipboard serializes TriggerMode NUMERICALLY.
            Assert.Equal(11, (int)MacroTriggerMode.ShortPress);
            var values = Enum.GetValues<MacroTriggerMode>();
            Assert.Equal(MacroTriggerMode.ShortPress, values[^1]);
        }

        [Fact]
        public void ShortPress_SharesTheHoldTimeRowAndItsTooltip()
        {
            var m = Macro(MacroTriggerMode.ShortPress);
            Assert.True(m.ShowsHoldTimeRow);
            Assert.True(m.ShowsTriggerComboEditor);   // the recorder must not vanish
            m.TriggerMode = MacroTriggerMode.HoldForMs;
            Assert.True(m.ShowsHoldTimeRow);          // shared with the twin
            m.TriggerMode = MacroTriggerMode.OnPress;
            Assert.False(m.ShowsHoldTimeRow);
        }

        // ── #254 macro layer scope ──

        private static MappingSet SetWithLayer(string mask)
        {
            var ms = new MappingSet();
            ms.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = mask,
                LayerName = mask,
                Descriptor = "Button 9",
                Mode = "Hold",
            });
            return ms;
        }

        private static IDisposable WithSlotSets(params (int Slot, MappingSet Set)[] sets)
        {
            var saved = (MappingSet[])SettingsManager.SlotMappingSets.Clone();
            for (int i = 0; i < SettingsManager.SlotMappingSets.Length; i++)
                SettingsManager.SlotMappingSets[i] = null;
            foreach (var (slot, set) in sets)
                SettingsManager.SlotMappingSets[slot] = set;
            return new Restore(saved);
        }

        private sealed class Restore : IDisposable
        {
            private readonly MappingSet[] _saved;
            public Restore(MappingSet[] saved) { _saved = saved; }
            public void Dispose()
            {
                for (int i = 0; i < _saved.Length; i++)
                    SettingsManager.SlotMappingSets[i] = _saved[i];
            }
        }

        /// <summary>"" is the ungated default every pre-#254 macro carries:
        /// it fires no matter which layer is engaged.</summary>
        [Fact]
        public void EmptyMask_FiresRegardlessOfEngagedLayer()
        {
            using var _ = WithSlotSets((0, SetWithLayer("Shift")));
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.OnPress, layerMask: "") };
            Assert.Equal((ushort)Fire, Tick(im, macros, held: true));
        }

        /// <summary>A named mask fires only while that layer is engaged on
        /// the macro's own slot. Nothing is engaged here, so it stays shut.</summary>
        [Fact]
        public void NamedMask_DoesNotFireWhileItsLayerIsNotEngaged()
        {
            using var _ = WithSlotSets((0, SetWithLayer("Shift")));
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.OnPress, layerMask: "Shift") };
            Assert.Equal((ushort)0, Tick(im, macros, held: true));
        }

        /// <summary>THE cross-slot bug the gate scoping exists to prevent:
        /// hand-authored masks derive from layer names and are uniquified
        /// only WITHIN a slot, so two slots can both own "Shift". A foreign
        /// slot engaging that mask must NOT open a macro whose own slot
        /// declares it.</summary>
        [Fact]
        public void ForeignSlotEngagement_DoesNotOpenAMacroWhoseOwnSlotDeclaresTheMask()
        {
            var ownSet = SetWithLayer("Shift");      // slot 0 declares "Shift", not engaged
            var foreignSet = SetWithLayer("Shift");  // slot 1 declares it too
            using var _ = WithSlotSets((0, ownSet), (1, foreignSet));

            // Engage the layer on the FOREIGN slot only.
            var foreignState = new PadForge.Engine.CustomInputState();
            foreignState.Buttons[9] = true;
            InputManager.ResolveActiveLayerMask(1, foreignSet, foreignState, "");

            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.OnPress, layerMask: "Shift", pad: 0) };
            Assert.Equal((ushort)0, Tick(im, macros, held: true));
        }

        /// <summary>The split-config case the old any-slot walk existed for
        /// survives: when the macro's OWN slot does not declare the mask,
        /// another slot's engagement still opens it.</summary>
        [Fact]
        public void SplitConfig_ForeignEngagementOpensWhenOwnSlotDoesNotDeclareTheMask()
        {
            var ownSet = new MappingSet();           // slot 0 declares nothing
            var foreignSet = SetWithLayer("Layer_7_2");
            using var _ = WithSlotSets((0, ownSet), (1, foreignSet));

            var foreignState = new PadForge.Engine.CustomInputState();
            foreignState.Buttons[9] = true;
            InputManager.ResolveActiveLayerMask(1, foreignSet, foreignState, "");

            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.OnPress, layerMask: "Layer_7_2", pad: 0) };
            Assert.Equal((ushort)Fire, Tick(im, macros, held: true));
        }

        /// <summary>"Base" means what it means for a mapping ROW: open
        /// while Base is engaged.</summary>
        [Fact]
        public void BaseMask_FiresWhileBaseIsEngaged()
        {
            using var _ = WithSlotSets((0, SetWithLayer("Shift")));
            var im = new InputManager();
            var macros = new[] { Macro(MacroTriggerMode.OnPress, layerMask: "Base") };
            Assert.Equal((ushort)Fire, Tick(im, macros, held: true));
        }

        [Fact]
        public void LayerMask_SurvivesTheMacroDataRoundTrip()
        {
            var m = Macro(MacroTriggerMode.OnPress, layerMask: "Shift");
            var data = PadForge.Services.SettingsService.BuildMacroDataForMacro(m, 0);
            var back = PadForge.Services.SettingsService.LoadMacroFromData(
                data, VirtualControllerType.Xbox, null);
            Assert.Equal("Shift", back.LayerMask);
        }

        [Fact]
        public void LayerScopeDot_TracksTheMask()
        {
            var m = Macro(MacroTriggerMode.OnPress, layerMask: "");
            Assert.False(m.HasLayerScope);
            m.LayerMask = "Shift";
            Assert.True(m.HasLayerScope);
            m.LayerMask = "Base";
            Assert.True(m.HasLayerScope);
        }

        // ── Workshop preview: keycodes read as keys ──

        [Fact]
        public void WorkshopPreview_RendersKeyCodesAsKeyNames()
        {
            // 0x5A = Z, 0x20 = Space: the two a user would otherwise see
            // as "KbmKey5A" / "KbmKey20" in the community-profile preview.
            Assert.Equal("Z", PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("KbmKey5A"));
            Assert.Equal(
                PadForge.ViewModels.MacroAction.VirtualKeyDisplayName(PadForge.Common.VirtualKey.Space),
                PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("KbmKey20"));

            // Mouse buttons are ZERO-based (audit 2026-07-25, C25: this
            // assertion previously encoded the off-by-one it was meant to
            // guard, naming KbmMBtn1 as the LEFT button). The analog lanes
            // use the editor's own row names (C26).
            Assert.Equal(
                PadForge.Resources.Strings.Strings.Instance.Mouse_LeftClick,
                PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("KbmMBtn0"));
            Assert.Equal(
                PadForge.Resources.Strings.Strings.Instance.Mouse_RightClick,
                PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("KbmMBtn1"));
            Assert.Equal(
                PadForge.Resources.Strings.Strings.Instance.Mouse_X,
                PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("KbmMouseX"));

            // Non-KBM targets pass through: they are already readable.
            Assert.Equal("ButtonA", PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("ButtonA"));
            Assert.Equal("LeftThumbAxisX", PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("LeftThumbAxisX"));
            // An undefined VK keeps its raw spelling rather than lying.
            Assert.Equal("KbmKeyFE", PadForge.Views.WorkshopBrowseDialog.HumanizeKbmTarget("KbmKeyFE"));
        }
    }
}
