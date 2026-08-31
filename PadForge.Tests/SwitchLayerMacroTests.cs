using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Switch Layer macro action (#377, asked in discussion #370): a
    /// one-shot action that engages a shift layer on the macro's own slot
    /// through the shift runtime's CustomLayer override, the same primitive
    /// the Latch ("Custom") activator writes. Combined with #254 layer
    /// scoping, the same physical button jumps to a different layer per
    /// engaged layer, the Steam action-set-layer graph shape.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class SwitchLayerMacroTests : IDisposable
    {
        private readonly MappingSet[] _savedSets;

        public SwitchLayerMacroTests()
        {
            _savedSets = SettingsManager.SlotMappingSets;
        }

        public void Dispose()
        {
            SettingsManager.SlotMappingSets = _savedSets;
            InputManager.ClearShiftRuntime(0);
        }

        private static MappingSet SetWithLayers(params string[] masks)
        {
            var ms = new MappingSet();
            foreach (var m in masks)
                ms.ShiftActivators.Add(new ShiftActivator
                {
                    LayerMask = m,
                    LayerName = m,
                    DeviceGuid = Guid.NewGuid().ToString(),
                    Descriptor = "PadA",
                });
            return ms;
        }

        /// <summary>The runtime semantics, on the REAL shift runtime:
        /// switching engages the mask (GetEngagedLayerMask reports it),
        /// switching again re-targets, and Base returns to the base layer.</summary>
        [Fact]
        public void ApplyMacroLayerSwitch_EngagesRetargetsAndReturns()
        {
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            var set = SetWithLayers("Shift1", "Shift2");
            SettingsManager.SlotMappingSets[0] = set;
            InputManager.ClearShiftRuntime(0);

            Assert.Equal("Base", InputManager.GetEngagedLayerMask(0, set));

            InputManager.ApplyMacroLayerSwitch(0, "Shift1");
            Assert.Equal("Shift1", InputManager.GetEngagedLayerMask(0, set));

            InputManager.ApplyMacroLayerSwitch(0, "Shift2");
            Assert.Equal("Shift2", InputManager.GetEngagedLayerMask(0, set));

            InputManager.ApplyMacroLayerSwitch(0, "Base");
            Assert.Equal("Base", InputManager.GetEngagedLayerMask(0, set));
        }

        /// <summary>A mask the slot's set does not declare is a NO-OP, so a
        /// stale action left behind by a layer rename or delete goes inert
        /// instead of engaging a rowless layer. The guard lives inside the
        /// operation.</summary>
        [Fact]
        public void ApplyMacroLayerSwitch_UndeclaredMaskIsNoOp()
        {
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            var set = SetWithLayers("Shift1");
            SettingsManager.SlotMappingSets[0] = set;
            InputManager.ClearShiftRuntime(0);

            InputManager.ApplyMacroLayerSwitch(0, "Shift1");
            Assert.Equal("Shift1", InputManager.GetEngagedLayerMask(0, set));

            // Undeclared target: the engaged layer must NOT change.
            InputManager.ApplyMacroLayerSwitch(0, "Ghost");
            Assert.Equal("Shift1", InputManager.GetEngagedLayerMask(0, set));

            // Out-of-range slot: no throw.
            InputManager.ApplyMacroLayerSwitch(-1, "Shift1");
            InputManager.ApplyMacroLayerSwitch(9999, "Shift1");
        }

        /// <summary>The enum stays append-only with SwitchLayer pinned at
        /// the tail (55). Sibling of MacroWave1bTests' pin, which this
        /// extends rather than replaces.</summary>
        [Fact]
        public void SwitchLayer_IsTheTailAt55()
        {
            var values = Enum.GetValues<MacroActionType>();
            Assert.Equal(MacroActionType.SwitchLayer, values[^1]);
            Assert.Equal(55, (int)MacroActionType.SwitchLayer);
            Assert.Equal(54, (int)MacroActionType.VoiceListenWhileHeld);
        }

        /// <summary>The DTO round-trip: the target mask survives save and
        /// load through both converters, and an action deserialized from a
        /// pre-#377 file (no element) lands on "Base".</summary>
        [Fact]
        public void SwitchLayerMask_RoundTripsAndDefaultsToBase()
        {
            var action = new MacroAction { Type = MacroActionType.SwitchLayer, SwitchLayerMask = "Shift2" };
            var item = new MacroItem { Name = "jump" };
            item.Actions.Add(action);

            var data = PadForge.Services.SettingsService.BuildMacroDataForMacro(item, 0);
            Assert.Equal("Shift2", data.Actions[0].SwitchLayerMask);

            var back = PadForge.Services.SettingsService.LoadMacroFromData(
                data, PadForge.Engine.VirtualControllerType.Xbox, null);
            Assert.Equal(MacroActionType.SwitchLayer, back.Actions[0].Type);
            Assert.Equal("Shift2", back.Actions[0].SwitchLayerMask);

            // Pre-#377 data: null mask normalizes to Base on load.
            data.Actions[0].SwitchLayerMask = null;
            var old = PadForge.Services.SettingsService.LoadMacroFromData(
                data, PadForge.Engine.VirtualControllerType.Xbox, null);
            Assert.Equal("Base", old.Actions[0].SwitchLayerMask);
        }

        /// <summary>A layer-mask rename retags SwitchLayer action targets
        /// alongside macro layer scopes, or the jump goes inert the moment
        /// the mask changes.</summary>
        [Fact]
        public void Rename_RetagsActionTargets()
        {
            var pad = new PadViewModel(0);
            var mac = new MacroItem { Name = "jump", LayerMask = "OldMask" };
            mac.Actions.Add(new MacroAction { Type = MacroActionType.SwitchLayer, SwitchLayerMask = "OldMask" });
            // A non-SwitchLayer action with a coincidental field state stays
            // untouched (same-window positive control).
            mac.Actions.Add(new MacroAction { Type = MacroActionType.Delay });
            pad.Macros.Add(mac);

            PadForge.Views.PadPage.RetagMacrosEverywhere(new[] { pad }, "OldMask", "NewMask");

            Assert.Equal("NewMask", mac.LayerMask);
            Assert.Equal("NewMask", mac.Actions[0].SwitchLayerMask);
        }

        /// <summary>Source contracts: the executor case resolves the slot
        /// through macro.PadIndex and calls the runtime helper, and the
        /// editor card binds the slot's LayerTabs with the mask as the
        /// selected value.</summary>
        [Fact]
        public void ExecutorAndEditorContracts()
        {
            string step4b = RepoText("PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs");
            int at = step4b.IndexOf("case MacroActionType.SwitchLayer:", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = step4b.Substring(at, 700);
            Assert.Contains("int slotIndex = macro.PadIndex;", body);
            Assert.Contains("ApplyMacroLayerSwitch(slotIndex, action.SwitchLayerMask);", body);
            Assert.Contains("AdvanceAction(macro);", body);

            // The Base branch clears the activator stack too, so a
            // toggled layer releases when a macro returns to Base. Source
            // contract: driving a stacked Toggle through the full resolver
            // is out of unit reach, and this pin keeps the clear from being
            // silently dropped.
            string step3 = RepoText("PadForge.App", "Common", "Input", "InputManager.Step3.MappingSetEval.cs");
            int helper = step3.IndexOf("public static void ApplyMacroLayerSwitch", StringComparison.Ordinal);
            Assert.True(helper > 0);
            string helperBody = step3.Substring(helper, 2400);
            Assert.Contains("rt.Stack.Clear();", helperBody);
            Assert.Contains("rt.Version++;", helperBody);

            string page = RepoText("PadForge.App", "Views", "PadPage.xaml");
            Assert.Contains("Binding IsSwitchLayerType", page);
            Assert.Contains("{x:Static vm:MacroActionType.SwitchLayer}", page);
            int card = page.IndexOf("Switch Layer card (#377)", StringComparison.Ordinal);
            Assert.True(card > 0);
            string cardBody = page.Substring(card, 2400);
            Assert.Contains("DataContext.LayerTabs", cardBody);
            Assert.Contains("SelectedValue=\"{Binding SwitchLayerMask, Mode=TwoWay}\"", cardBody);
            Assert.Contains("SelectedValuePath=\"LayerMask\"", cardBody);
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
