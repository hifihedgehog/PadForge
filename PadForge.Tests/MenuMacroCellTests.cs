using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Engine.Menus;
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
            // The macro picker carries the stale name as a marked entry.
            var stale = cell.MacroOptions.FirstOrDefault(o => o.Descriptor == "Deleted Macro");
            Assert.NotNull(stale);
            Assert.NotEqual("Deleted Macro", stale.Label);
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
        /// and an executing macro keeps evaluating so a cell-started run
        /// completes and releases its latches.</summary>
        [Fact]
        public void BothEvaluatorTwins_ConsumeTheStamp()
        {
            string ev = RepoText("PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs");
            Assert.Contains("MacroPassTick++;", ev);
            // Twice: the Gamepad twin and the Extended twin.
            Assert.Equal(2, CountOf(ev, "bool menuCellHeld = macro.MenuTriggerTick == MacroPassTick;"));
            Assert.Equal(2, CountOf(ev, "if (!hasOwnTrigger && !menuCellHeld && !macro.IsExecuting)"));
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
                got = null; // ignore the construction-time set
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
}
