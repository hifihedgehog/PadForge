using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guard pins for the 2026-07-25 round-FIVE audit of the round-four fix
    /// commit. Round four's fixes were broader than round three's, and the
    /// failures were over-reach: a global rename that merged unrelated
    /// layers, an all-slots runtime reset, a translator mirror that
    /// manufactured slots, and a delete whose declare-scan was satisfied by
    /// a ring it then destroyed.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AuditJuly25RoundFiveTests : IDisposable
    {
        private const short Fire = 30000;
        private static readonly Guid DevGuid = new("33333333-3333-3333-3333-333333333333");

        public void Dispose() => InputManager.ClearAllShiftRuntime();

        private static MacroItem Macro(string layerMask, int pad = 0)
        {
            var m = new MacroItem
            {
                Name = "R5",
                IsEnabled = true,
                PadIndex = pad,
                TriggerButtons = Gamepad.A,
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
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

        private static IDisposable Sets(params (int Slot, MappingSet Set)[] sets)
        {
            var saved = (MappingSet[])SettingsManager.SlotMappingSets.Clone();
            for (int i = 0; i < SettingsManager.SlotMappingSets.Length; i++)
                SettingsManager.SlotMappingSets[i] = null;
            foreach (var (slot, set) in sets) SettingsManager.SlotMappingSets[slot] = set;
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

        private static MappingSet SetWith(string mask, string descriptor = "Button 9")
        {
            var ms = new MappingSet();
            ms.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = DevGuid.ToString(),
                Descriptor = descriptor,
                LayerMask = mask,
                LayerName = mask,
                Mode = "Hold",
                InheritUnmapped = false,
            });
            return ms;
        }

        // ── X2/X5: import-domain scoping ──

        /// <summary>The grammar that carries import identity. Set masks are
        /// "Layer_{fileId}_{presetId}"; the fileId segment is the domain.
        /// Hand-authored masks are name-derived and match nothing.</summary>
        [Theory]
        [InlineData("Layer_123_1", "Layer_123_2", true)]    // same import
        [InlineData("Layer_123_1", "Layer_999_2", false)]   // different imports
        [InlineData("Shift", "Layer_123_2", false)]         // hand-authored vs import
        [InlineData("Shift", "Shift", false)]               // two hand-authored: NEVER coupled
        [InlineData("Layer_123_1", "Shift", false)]
        public void ImportDomain_OnlyMatchesTheSameImport(string ownMask, string foreignMask, bool shares)
        {
            var own = SetWith(ownMask);
            Assert.Equal(shares, InputManager.SlotSharesImportDomain(own, foreignMask));
        }

        /// <summary>A split Workshop import: the set switch lives on the KBM
        /// slot, the macros ride the Xbox slot. Engaging the set REPLACES
        /// Base, so a Base-scoped macro must close.</summary>
        [Fact]
        public void BaseMacro_ClosesWhenARelatedImportSlotEngagesItsSet()
        {
            var xbox = SetWith("Layer_555_1");      // same import, declares a set mask
            var kbm = SetWith("Layer_555_2");       // the set switch
            using var _ = Sets((0, xbox), (1, kbm));

            var im = new InputManager();
            Assert.Equal((ushort)Fire, Tick(im, new[] { Macro("Base") }, held: true));

            var st = new PadForge.Engine.CustomInputState();
            st.Buttons[9] = true;
            InputManager.ResolveActiveLayerMask(1, kbm, st, DevGuid.ToString());

            var im2 = new InputManager();
            Assert.Equal((ushort)0, Tick(im2, new[] { Macro("Base") }, held: true));
        }

        /// <summary>The isolation half, and the one round three got wrong:
        /// two players' hand-authored layers share no import, so player 2's
        /// Shift must never close player 1's Base macro.</summary>
        [Fact]
        public void BaseMacro_IsNotClosedByAnUnrelatedPadsHandAuthoredLayer()
        {
            var mine = SetWith("Aim");              // pad 0, hand-authored
            var theirs = SetWith("Shift");          // pad 1, unrelated
            using var _ = Sets((0, mine), (1, theirs));

            var st = new PadForge.Engine.CustomInputState();
            st.Buttons[9] = true;
            InputManager.ResolveActiveLayerMask(1, theirs, st, DevGuid.ToString());

            var im = new InputManager();
            Assert.Equal((ushort)Fire, Tick(im, new[] { Macro("Base") }, held: true));
        }

        // ── Layer picker: the selected item must SURVIVE a rebuild ──

        /// <summary>Field repro 2026-07-25: after renaming a layer the macro's
        /// Layer picker went blank while the macro still gated correctly. The
        /// rebuild Clear()ed the choices, so WPF dropped the picker's
        /// SelectedItem, and re-pushing the unchanged mask set SelectedValue to
        /// the value the control already held. No change, no re-resolution,
        /// nothing to display. The rebuild now reconciles in place, so the
        /// instance carrying the selected mask is never removed and a rename
        /// just updates its label.</summary>
        [Fact]
        public void RebuildingLayerTabs_KeepsTheChoiceInstanceForAnUnchangedMask()
        {
            var vm = new PadViewModel(0);
            var acts = new System.Collections.Generic.List<ShiftActivator>
            {
                new ShiftActivator { LayerMask = "Shift", LayerName = "Shift", Mode = "Hold", Descriptor = "Button 9" },
            };
            vm.RebuildLayerTabs(acts);

            ShiftLayerInfo before = null;
            foreach (var c in vm.MacroLayerChoices)
                if (c.LayerMask == "Shift") { before = c; break; }
            Assert.NotNull(before);

            // Rename the LAYER (mask unchanged), exactly as the inline rename does.
            acts[0].LayerName = "Combat";
            vm.RebuildLayerTabs(acts);

            ShiftLayerInfo after = null;
            foreach (var c in vm.MacroLayerChoices)
                if (c.LayerMask == "Shift") { after = c; break; }

            Assert.NotNull(after);
            Assert.Same(before, after);              // the selected item survived
            Assert.Equal("Combat", after.LayerName); // and shows the new label
        }

        /// <summary>Adds and removals still land, so the picker tracks the real
        /// layer set rather than going stale in the name of stability.</summary>
        [Fact]
        public void RebuildingLayerTabs_StillAddsAndRemovesLayers()
        {
            var vm = new PadViewModel(0);
            var acts = new System.Collections.Generic.List<ShiftActivator>
            {
                new ShiftActivator { LayerMask = "A", LayerName = "A", Mode = "Hold", Descriptor = "Button 9" },
            };
            vm.RebuildLayerTabs(acts);
            int withOne = vm.MacroLayerChoices.Count;      // "", Base, A

            acts.Add(new ShiftActivator { LayerMask = "B", LayerName = "B", Mode = "Hold", Descriptor = "Button 8" });
            vm.RebuildLayerTabs(acts);
            Assert.Equal(withOne + 1, vm.MacroLayerChoices.Count);

            acts.RemoveAt(0);
            vm.RebuildLayerTabs(acts);
            Assert.Equal(withOne, vm.MacroLayerChoices.Count);
            bool hasA = false;
            foreach (var c in vm.MacroLayerChoices) if (c.LayerMask == "A") hasA = true;
            Assert.False(hasA);                            // the removed layer is gone
        }

        // ── X11: engine start clears ACTION latches ──

        [Fact]
        public void ActionToggleLatches_AreClearedByTheStartResetContract()
        {
            var m = Macro("");
            var act = new MacroAction { Type = MacroActionType.ToggleVcButton, ButtonFlags = Gamepad.B };
            act.VcToggleLatched = true;
            m.Actions.Add(act);

            // The field list InputService.Start() clears.
            foreach (var a in m.Actions)
            {
                a.VcToggleLatched = false;
                a.KeyToggleLatched = false;
                a.MouseToggleLatched = false;
                a.VcAxisToggleLatched = false;
                a.WheelToggleLatched = false;
            }

            // A cleared latch must not re-assert on the first pass.
            var im = new InputManager();
            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, new[] { m });
            Assert.Equal((ushort)0, (ushort)(gp.Buttons & Gamepad.B));
        }

        // ── X14: a macro emptied mid-run releases ──

        [Fact]
        public void MacroEmptiedMidRun_DoesNotStayWedged()
        {
            var im = new InputManager();
            var m = Macro("");
            m.TriggerMode = MacroTriggerMode.OnPress;
            var macros = new[] { m };

            Tick(im, macros, held: false);
            Tick(im, macros, held: true);          // starts
            Assert.True(m.IsExecuting);

            m.Actions.Clear();                     // user deletes the last action
            Tick(im, macros, held: true);
            Assert.False(m.IsExecuting);           // released, not wedged
        }

        // ── X15: known discontinuities break continuity ──

        [Fact]
        public void ReAuthoringTheTrigger_BreaksEdgeContinuity()
        {
            var m = Macro("");
            m.LastEvaluatedUtc = DateTime.UtcNow;
            m.WasTriggerActive = true;

            m.SetTriggerInputEntries(new System.Collections.Generic.List<MacroItem.TriggerInputEntry>());

            Assert.Equal(DateTime.MinValue, m.LastEvaluatedUtc);
            Assert.False(m.WasTriggerActive);
        }

        [Fact]
        public void DisablingAMacro_BreaksEdgeContinuity()
        {
            var im = new InputManager();
            var m = Macro("");
            m.TriggerMode = MacroTriggerMode.ShortPress;
            m.TriggerHoldMs = 300;
            var macros = new[] { m };

            Tick(im, macros, held: false);         // observed idle
            m.IsEnabled = false;
            Tick(im, macros, held: true);          // the disable lane runs

            Assert.Equal(DateTime.MinValue, m.LastEvaluatedUtc);
            Assert.False(m.WasTriggerActive);
        }

        // ── X9/X10: the delete-time declare policy ──

        /// <summary>An UNRELATED pad owning a same-named hand-authored layer
        /// must NOT keep this slot's macros alive: doing so left them gated
        /// by that pad's controller through the split fallback, the exact
        /// coupling this lineage removed from the Base branch.</summary>
        [Fact]
        public void DeletePolicy_UnrelatedPadSharingAMaskDoesNotCount()
        {
            var own = new MappingSet();                 // its activator was just removed
            var unrelated = SetWith("Shift");           // another player's own layer
            var all = new[] { own, unrelated };
            Assert.False(PadForge.Views.PadPage.RelatedSlotStillDeclares(all, own, "Shift"));
        }

        /// <summary>A slot from the SAME import does count: the layer still
        /// exists in that configuration and its macros keep working.</summary>
        [Fact]
        public void DeletePolicy_RelatedImportSlotCounts()
        {
            var own = SetWith("Layer_777_1");           // same import, still declares a set
            var sibling = SetWith("Layer_777_2");
            var all = new[] { own, sibling };
            Assert.True(PadForge.Views.PadPage.RelatedSlotStillDeclares(all, own, "Layer_777_2"));
        }

        /// <summary>X9: a ring stop on the OWN slot counts only while it is
        /// really there. The delete path now scrubs rings BEFORE asking, so
        /// a stop it is about to remove can no longer spare the macros and
        /// then vanish, leaving them tagged with a mask nothing declares.</summary>
        [Fact]
        public void DeletePolicy_ScrubbedRingStopNoLongerCounts()
        {
            var own = new MappingSet();
            own.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "Ring1",
                LayerName = "Ring1",
                Mode = "Cycle",
                CycleLayers = "Ring1",                  // "Gone" already scrubbed
                Descriptor = "Button 8",
            });
            var all = new[] { own };
            Assert.False(PadForge.Views.PadPage.RelatedSlotStillDeclares(all, own, "Gone"));

            // Pre-scrub the ring would have contained it and wrongly counted.
            own.ShiftActivators[0].CycleLayers = "Ring1|Gone";
            Assert.True(PadForge.Views.PadPage.RelatedSlotStillDeclares(all, own, "Gone"));
        }

        // ── X13: a non-user rebuild must not dirty ──

        [Fact]
        public void SuppressedLayerBindingRefresh_RaisesNothing()
        {
            var m = Macro("Shift");
            bool raised = false;
            m.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MacroItem.LayerMask)) raised = true;
            };

            MacroItem.SuppressLayerBindingDirty = true;
            try { m.RefreshLayerBinding(); }
            finally { MacroItem.SuppressLayerBindingDirty = false; }
            Assert.False(raised);

            m.RefreshLayerBinding();               // unsuppressed still works
            Assert.True(raised);
        }
    }
}
