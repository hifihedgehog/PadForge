using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Macro cells (#390): a menu cell triggers a saved macro of its
    /// slot by name, as an additional trigger source stamped by the menu
    /// runtime and consumed by both evaluator twins. The editor offers
    /// the kind only while the slot has macros, keeps a marked entry for
    /// a stale name, and clears the other value spaces on pick, the
    /// binding-kind discipline every kind follows.
    /// </summary>
    public class MenuMacroCellTests
    {
        // ── Schema ──

        [Fact]
        public void Clone_CarriesMacroName()
        {
            var def = new MenuDefinitionEntry();
            def.Items.Add(new MenuItemDefinition { Index = 1, MacroName = "Jump Combo" });
            var copy = def.Clone();
            Assert.Equal("Jump Combo", copy.Items[0].MacroName);
        }

        [Fact]
        public void MacroName_RoundTripsTheMenusJson()
        {
            // The clipboard wire serializes the live entries, so the new
            // field must survive a JSON round trip like every other.
            var def = new MenuDefinitionEntry { MenuId = 3 };
            def.Items.Add(new MenuItemDefinition { Index = 2, MacroName = "Reload" });
            string json = System.Text.Json.JsonSerializer.Serialize(new List<MenuDefinitionEntry> { def });
            var back = System.Text.Json.JsonSerializer.Deserialize<List<MenuDefinitionEntry>>(json);
            Assert.Equal("Reload", back[0].Items[0].MacroName);
        }

        // ── Editor ──

        private static (MenuEditorItem Editor, MenuCellItem Cell) MakeEditor(
            IReadOnlyList<string> macroNames, MenuItemDefinition seed = null)
        {
            var entry = new MenuDefinitionEntry { MenuId = 1, CellCount = 4 };
            if (seed != null) entry.Items.Add(seed);
            var editor = new MenuEditorItem(entry)
            {
                MacroNamesProvider = macroNames == null ? null : () => macroNames,
            };
            var cell = editor.Cells.First(c => c.Index == (seed?.Index ?? 1));
            return (editor, cell);
        }

        [Fact]
        public void MacroKind_OfferedOnlyWhileTheSlotHasMacros()
        {
            var (_, noMacros) = MakeEditor(Array.Empty<string>());
            Assert.DoesNotContain(noMacros.BindingKindOptions, o => o.Value == MenuCellItem.MacroKind);

            var (_, withMacros) = MakeEditor(new[] { "Jump" });
            Assert.Contains(withMacros.BindingKindOptions, o => o.Value == MenuCellItem.MacroKind);
        }

        [Fact]
        public void StaleName_KeepsAMarkedEntry_TheNeverLieRule()
        {
            var seed = new MenuItemDefinition { Index = 1, MacroName = "Deleted Macro" };
            var (_, cell) = MakeEditor(Array.Empty<string>(), seed);
            // The kind still reads Macro and the kind list still offers it.
            Assert.Equal(MenuCellItem.MacroKind, cell.BindingKind);
            Assert.Contains(cell.BindingKindOptions, o => o.Value == MenuCellItem.MacroKind);
            // The macro picker carries the stale name as a marked entry,
            // worded for what it is: the macro is gone, never "not on
            // this slot type" (the button picker's message for a
            // different condition).
            var stale = cell.MacroOptions.FirstOrDefault(o => o.Descriptor == "Deleted Macro");
            Assert.NotNull(stale);
            Assert.Equal(string.Format(Strings.Instance.Menu_Macro_Missing_Format, "Deleted Macro"), stale.Label);
            Assert.NotEqual(string.Format(Strings.Instance.Menu_Binding_Unsupported_Format, "Deleted Macro"), stale.Label);
        }

        /// <summary>A macro-only cell out past a smaller shape's reach
        /// survives the round trip like every other value space: the
        /// prune's emptiness test omitted MacroName while
        /// DropItemIfEmpty had it, so Radial 8 to Grid 4 and back deleted
        /// the cell. The VirtualKey twin is the positive control.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OutOfReachCell_SurvivesAShapeRoundTrip(bool macroCell)
        {
            var entry = new MenuDefinitionEntry { MenuId = 1, Kind = MenuKind.Radial, CellCount = 8 };
            entry.Items.Add(macroCell
                ? new MenuItemDefinition { Index = 5, MacroName = "Jump" }
                : new MenuItemDefinition { Index = 5, VirtualKey = 0x20 });
            var editor = new MenuEditorItem(entry) { MacroNamesProvider = () => new[] { "Jump" } };

            editor.KindIndex = 1;      // Grid
            editor.CellCount = 4;      // index 5 is now out of reach
            editor.KindIndex = 0;      // Radial
            editor.CellCount = 8;

            var it = Assert.Single(entry.Items);
            Assert.Equal(5, it.Index);
            if (macroCell) Assert.Equal("Jump", it.MacroName);
            else Assert.Equal(0x20, it.VirtualKey);
        }

        /// <summary>The picker's no-change test is case-insensitive like
        /// the runtime's resolve and the option list: a pick that
        /// differs only in case is not a new binding.</summary>
        [Fact]
        public void PickingTheSameNameInADifferentCase_IsNoChange()
        {
            var seed = new MenuItemDefinition { Index = 1, MacroName = "jump" };
            var (editor, cell) = MakeEditor(new[] { "Jump" }, seed);
            int edits = 0;
            editor.Changed += () => edits++;
            cell.SelectedMacroName = "JUMP";
            Assert.Equal("jump", cell.SelectedMacroName);
            Assert.Equal(0, edits);
        }

        /// <summary>The settings loader's object initializer assigns Name
        /// once per macro per profile load. That first assignment is
        /// construction, never a rename: announcing it retagged every
        /// cell whose MacroName was literally "New Macro" to the macro
        /// being loaded.</summary>
        [Fact]
        public void LoadFromData_DoesNotAnnounceARename()
        {
            var md = SettingsService.BuildMacroDataForMacro(new MacroItem { Name = "Foo" }, 0);
            int fired = 0;
            Action<MacroItem, string, string> h = (_, _, _) => fired++;
            MacroItem.Renamed += h;
            try
            {
                var loaded = SettingsService.LoadMacroFromData(md, VirtualControllerType.Xbox, null);
                Assert.Equal("Foo", loaded.Name);
                Assert.Equal(0, fired);
                // A real rename after construction still announces.
                loaded.Name = "Bar";
                Assert.Equal(1, fired);
            }
            finally { MacroItem.Renamed -= h; }
        }

        [Fact]
        public void PickingMacro_DefaultsToFirst_AndClearsOtherValueSpaces()
        {
            var seed = new MenuItemDefinition { Index = 1, VirtualKey = 0x20 };
            var (_, cell) = MakeEditor(new[] { "First", "Second" }, seed);
            Assert.Equal(1, cell.BindingKind);

            cell.BindingKind = MenuCellItem.MacroKind;
            Assert.Equal("First", cell.SelectedMacroName);
            Assert.Equal(0, cell.SelectedKeyVk);
            Assert.True(cell.ShowMacroPicker);

            cell.SelectedMacroName = "Second";
            Assert.Equal("Second", cell.SelectedMacroName);

            // Switching back to a key clears the macro name.
            cell.BindingKind = 1;
            Assert.Equal("", cell.SelectedMacroName);
            Assert.Equal(0x20, cell.SelectedKeyVk);
        }

        [Fact]
        public void MacroOnlyCell_IsData_AndResetClearsIt()
        {
            var seed = new MenuItemDefinition { Index = 1, MacroName = "Held" };
            var (editor, cell) = MakeEditor(new[] { "Held" }, seed);
            Assert.Single(editor.Entry.Items);

            cell.ResetCellCommand.Execute(null);
            Assert.Empty(editor.Entry.Items);
            Assert.Equal(0, cell.BindingKind);
        }

        // ── Source contracts: the runtime lane ──

        /// <summary>The stamping walk sits in the menu runtime's direct-
        /// output pass (which runs BEFORE the evaluators), resolves the
        /// name against the slot's macro snapshot case-insensitively,
        /// and stamps MenuTriggerTick with the current pass tick.</summary>
        [Fact]
        public void RuntimeStamp_LivesInTheMenuWalk()
        {
            string mr = RepoText("PadForge.App", "Common", "Input", "InputManager.MenuRuntime.cs");
            int at = mr.IndexOf("private void CollectMenuDirectOutputs()", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = mr.Substring(at, 4200);
            Assert.Contains("item.MacroName", body);
            Assert.Contains("IsMenuItemFired(slot, null, def.MenuId, item.Index)", body);
            Assert.Contains("MacroSnapshots[slot]", body);
            Assert.Contains("StringComparison.OrdinalIgnoreCase", body);
            Assert.Contains("mac.MenuTriggerTick = MacroPassTick;", body);
        }

        /// <summary>Both evaluator twins carry the identical trigger
        /// integration: the pass tick increments once per pass, a current
        /// stamp bypasses the no-trigger skip and ORs into triggerActive,
        /// and a macro that has ever been stamped keeps evaluating so its
        /// unstamped tick lands as a release edge and its latches stay
        /// applied. Only the never-stamped sentinel skips.</summary>
        [Fact]
        public void BothEvaluatorTwins_ConsumeTheStamp()
        {
            string ev = RepoText("PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs");
            Assert.Contains("MacroPassTick++;", ev);
            // Twice: the Gamepad twin and the Extended twin.
            Assert.Equal(2, CountOf(ev, "bool menuCellHeld = macro.MenuTriggerTick == MacroPassTick;"));
            Assert.Equal(2, CountOf(ev, "if (!hasOwnTrigger && !menuCellHeld && !macro.IsExecuting && macro.MenuTriggerTick < 0)"));
            Assert.Equal(2, CountOf(ev, "triggerActive = menuCellHeld;"));
            // Four OR sites: each twin's custom-expression branch and its
            // component-AND assembly.
            Assert.Equal(4, CountOf(ev, "|| menuCellHeld;"));
        }

        /// <summary>The rename hook: the macro name setter announces old
        /// and new, and the pad VM retags its slot's menu cells and marks
        /// the settings dirty, the RetagMacrosEverywhere discipline.</summary>
        [Fact]
        public void RenameHook_RetagsMenuCells()
        {
            string mi = RepoText("PadForge.App", "ViewModels", "MacroItem.cs");
            Assert.Contains("public static event Action<MacroItem, string, string> Renamed;", mi);
            Assert.Contains("Renamed?.Invoke(this, old, value);", mi);

            string pv = RepoText("PadForge.App", "ViewModels", "PadViewModel.cs");
            Assert.Contains("MacroItem.Renamed += OnMacroRenamed;", pv);
            int at = pv.IndexOf("private void OnMacroRenamed", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = pv.Substring(at, 1800);
            Assert.Contains("macro.PadIndex != PadIndex", body);
            Assert.Contains("it.MacroName = newName;", body);
            Assert.Contains("ConfigItemDirtyCallback?.Invoke();", body);
        }

        [Fact]
        public void RenameEvent_FiresWithOldAndNew()
        {
            MacroItem got = null; string gotOld = null, gotNew = null;
            Action<MacroItem, string, string> h = (m, o, n) => { got = m; gotOld = o; gotNew = n; };
            MacroItem.Renamed += h;
            try
            {
                var mac = new MacroItem { Name = "Before" };
                Assert.Null(got); // construction is not a rename
                mac.Name = "After";
                Assert.Same(mac, got);
                Assert.Equal("Before", gotOld);
                Assert.Equal("After", gotNew);
            }
            finally { MacroItem.Renamed -= h; }
        }

        private static int CountOf(string text, string needle)
        {
            int count = 0, at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
            return count;
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

    /// <summary>The runtime half of macro cells, on the REAL slot
    /// evaluators, plus the slot-level rename retag. Serialized with the
    /// other macro-running classes: the evaluators mutate static latch
    /// state, and the retag reads SettingsManager.SlotMappingSets.</summary>
    [Collection("SettingsManagerStatics")]
    public class MenuMacroCellRuntimeTests
    {
        private static MacroItem CellOnlyMacro(MacroTriggerMode mode, params MacroAction[] actions)
        {
            var m = new MacroItem
            {
                Name = "Cell",
                IsEnabled = true,
                PadIndex = 0,
                TriggerMode = mode,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
            };
            foreach (var a in actions) m.Actions.Add(a);
            return m;
        }

        /// <summary>One evaluator pass. The top-level EvaluateMacros
        /// increments the pass tick and the menu walk stamps the macro.
        /// The test plays both roles so the slot evaluator sees a
        /// stamped or an unstamped tick.</summary>
        private static ushort Tick(InputManager im, MacroItem m, bool stamped)
        {
            im.MacroPassTick++;
            if (stamped) m.MenuTriggerTick = im.MacroPassTick;
            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, new[] { m });
            return gp.Buttons;
        }

        private static uint TickRaw(InputManager im, MacroItem m, bool stamped)
        {
            im.MacroPassTick++;
            if (stamped) m.MenuTriggerTick = im.MacroPassTick;
            var raw = RawHidState.Create(8, 32, 1);
            im.EvaluateSlotMacrosExtended(ref raw, new[] { m });
            return raw.Buttons[0];
        }

        /// <summary>A cell-only macro's release edge is the first
        /// UNSTAMPED tick after a stamped one. The evaluator skipped
        /// every unstamped tick for a macro with no trigger of its own,
        /// so the edge tracking never ran and OnRelease never fired.</summary>
        [Fact]
        public void CellOnly_OnRelease_FiresOnTheUnstampedTick()
        {
            var im = new InputManager();
            var m = CellOnlyMacro(MacroTriggerMode.OnRelease,
                new MacroAction { Type = MacroActionType.ButtonPress, ButtonFlags = Gamepad.B, DurationMs = 0 });

            Assert.Equal(0, Tick(im, m, stamped: true));        // held: nothing yet
            Assert.Equal(Gamepad.B, Tick(im, m, stamped: false)); // released: fires
        }

        /// <summary>The raw twin, same rule.</summary>
        [Fact]
        public void CellOnly_OnRelease_FiresOnTheUnstampedTick_RawTwin()
        {
            var im = new InputManager();
            var m = CellOnlyMacro(MacroTriggerMode.OnRelease,
                new MacroAction
                {
                    Type = MacroActionType.ButtonPress,
                    CustomButtons = "00000002,00000000,00000000,00000000",
                    DurationMs = 0,
                });

            Assert.Equal(0u, TickRaw(im, m, stamped: true) & 2u);
            Assert.Equal(2u, TickRaw(im, m, stamped: false) & 2u);
        }

        /// <summary>OnPress must re-arm: a run that finished INSIDE the
        /// cell's fire window (the cell held for several ticks, the run
        /// over before the hold ends) left WasTriggerActive true forever
        /// when the unstamped ticks were skipped, so the second fire of
        /// the same cell never started a second run. A run still
        /// executing on the unstamped tick was evaluated anyway, which
        /// is why the window here outlasts the run.</summary>
        [Fact]
        public void CellOnly_OnPress_ReArmsAfterAnUnstampedTick()
        {
            var im = new InputManager();
            var m = CellOnlyMacro(MacroTriggerMode.OnPress,
                new MacroAction { Type = MacroActionType.ButtonPress, ButtonFlags = Gamepad.B, DurationMs = 0 });

            Assert.Equal(Gamepad.B, Tick(im, m, stamped: true));  // first fire
            Tick(im, m, stamped: true);                            // the run winds down inside the window
            Tick(im, m, stamped: true);
            Assert.False(m.IsExecuting);                           // over, cell still held
            Assert.Equal(0, Tick(im, m, stamped: false));         // release edge
            Assert.Equal(Gamepad.B, Tick(im, m, stamped: true));  // second fire starts a second run
        }

        /// <summary>The per-tick latch overlay only runs past the skip,
        /// so once a cell-started run was over, a cell-latched ToggleKey
        /// dropped out of the desired set on the first unstamped tick
        /// and the reconcile released it.</summary>
        [Fact]
        public void CellOnly_ToggleKey_StaysLatchedAcrossUnstampedTicks()
        {
            var im = new InputManager();
            var action = new MacroAction { Type = MacroActionType.ToggleKey, KeyCode = 0x41 };
            var m = CellOnlyMacro(MacroTriggerMode.OnPress, action);

            Tick(im, m, stamped: true);
            Assert.True(action.KeyToggleLatched);
            Assert.Contains((ushort)0x41, im._desiredLatchedKeys);
            Tick(im, m, stamped: true);                            // the run winds down inside the window
            Tick(im, m, stamped: true);
            Assert.False(m.IsExecuting);

            im._desiredLatchedKeys.Clear();                       // the per-frame rebuild
            Tick(im, m, stamped: false);
            Assert.Contains((ushort)0x41, im._desiredLatchedKeys);
            Assert.True(action.KeyToggleLatched);
        }

        /// <summary>A macro that was NEVER stamped and has no trigger of
        /// its own still costs nothing: the skip keeps the -1 sentinel.</summary>
        [Fact]
        public void NeverStamped_CellLessMacro_IsStillSkipped()
        {
            var im = new InputManager();
            var m = CellOnlyMacro(MacroTriggerMode.OnRelease,
                new MacroAction { Type = MacroActionType.ButtonPress, ButtonFlags = Gamepad.B, DurationMs = 0 });
            Assert.Equal(-1L, m.MenuTriggerTick);
            Assert.Equal(0, Tick(im, m, stamped: false));
            Assert.Equal(DateTime.MinValue, m.LastEvaluatedUtc);   // never evaluated
        }

        /// <summary>The rename retag matches the cell's name the way the
        /// runtime resolves it, case-insensitively: a cell "jump" bound
        /// to the macro "Jump" follows the rename to "Leap".</summary>
        [Fact]
        public void Rename_RetagsACellWhoseCaseDiffers()
        {
            var saved = (MappingSet[])SettingsManager.SlotMappingSets.Clone();
            try
            {
                var set = new MappingSet();
                var def = new MenuDefinitionEntry { MenuId = 1, CellCount = 4 };
                var item = new MenuItemDefinition { Index = 1, MacroName = "jump" };
                def.Items.Add(item);
                set.Menus.Add(def);
                SettingsManager.SlotMappingSets[0] = set;

                var vm = new PadViewModel(0);
                var macro = new MacroItem { PadIndex = 0, Name = "Jump" };
                macro.Name = "Leap";
                Assert.Equal("Leap", item.MacroName);
                GC.KeepAlive(vm);
            }
            finally
            {
                for (int i = 0; i < saved.Length; i++)
                    SettingsManager.SlotMappingSets[i] = saved[i];
            }
        }
    }
}
