using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine.Menus;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Issue #413 (discussion #409, @brawler14801): the menu layer gate gets
    /// an editor, each cell sizes its own icon, and a layer can hold a menu
    /// open so the surface only steers. Plus the starter radial index repair
    /// found on the way. Contracts here are the ones the unified design
    /// named; the evaluator tests follow MenuRuntimeTests' Tick idiom.
    /// </summary>
    public class MenuLayerGateAndIconSizeTests
    {
        // ── Persistence ──────────────────────────────────────────────────

        [Fact]
        public void Clone_CarriesBothNewFields_AndDoesNotAliasItems()
        {
            var def = new MenuDefinitionEntry { LayerMask = "L1", LayerHoldsOpen = true };
            def.Items.Add(new MenuItemDefinition { Index = 1, Icon = "a.png", IconScalePercent = 150 });
            def.Items.Add(new MenuItemDefinition { Index = 2, Icon = "b.png", IconScalePercent = 60 });

            var copy = def.Clone();
            Assert.True(copy.LayerHoldsOpen);
            Assert.Equal(150, copy.Items[0].IconScalePercent);
            Assert.Equal(60, copy.Items[1].IconScalePercent);
            Assert.NotSame(def.Items[0], copy.Items[0]);

            // The clone lesson at MenuDefinitionEntry.Clone: a field the clone
            // drops resets on every profile apply and slot copy.
            copy.Items[0].IconScalePercent = 25;
            Assert.Equal(150, def.Items[0].IconScalePercent);
        }

        [Fact]
        public void Json_RoundTripsBothFields_AndOldJsonReadsDefaults()
        {
            var def = new MenuDefinitionEntry { MenuId = 3, LayerMask = "L1", LayerHoldsOpen = true };
            def.Items.Add(new MenuItemDefinition { Index = 2, Icon = "x.png", IconScalePercent = 175 });
            string json = System.Text.Json.JsonSerializer.Serialize(new List<MenuDefinitionEntry> { def });
            var back = System.Text.Json.JsonSerializer.Deserialize<List<MenuDefinitionEntry>>(json);
            Assert.True(back[0].LayerHoldsOpen);
            Assert.Equal(175, back[0].Items[0].IconScalePercent);

            // A file written before either field existed.
            const string old = "[{\"MenuId\":3,\"LayerMask\":\"L1\",\"Items\":[{\"Index\":2,\"Icon\":\"x.png\"}]}]";
            var legacy = System.Text.Json.JsonSerializer.Deserialize<List<MenuDefinitionEntry>>(old);
            Assert.False(legacy[0].LayerHoldsOpen);
            Assert.Equal(100, legacy[0].Items[0].IconScalePercent);
        }

        // ── ComputeSurfaceActive: the one engagement rule ────────────────

        [Theory]
        // flag off: physical AND layer, the shape every menu had before
        [InlineData(false, "L1", true, true, true)]
        [InlineData(false, "L1", false, true, false)]
        [InlineData(false, "L1", true, false, false)]
        // flag on with a real layer: the layer alone holds it open
        [InlineData(true, "L1", false, true, true)]
        [InlineData(true, "L1", true, true, true)]
        [InlineData(true, "L1", false, false, false)]
        // flag on with nothing to hold: surface path, there is no exit
        [InlineData(true, "", false, true, false)]
        [InlineData(true, "Base", false, true, false)]
        [InlineData(true, "", true, true, true)]
        public void ComputeSurfaceActive_TruthTable(bool holds, string mask, bool physical, bool layerOk, bool expected)
        {
            var def = new MenuDefinitionEntry { LayerMask = mask, LayerHoldsOpen = holds };
            Assert.Equal(expected, MenuEvaluator.ComputeSurfaceActive(def, physical, layerOk));
        }

        [Fact]
        public void ComputeSurfaceActive_NullDefinitionIsInactive()
            => Assert.False(MenuEvaluator.ComputeSurfaceActive(null, true, true));

        // ── UpdateLayerEngaged: resting hover and firing ─────────────────

        private static MenuDefinitionEntry Held(MenuFireType fire, MenuKind kind = MenuKind.Radial, bool hasCenter = false)
            => new()
            {
                Kind = kind, CellCount = 4, HasCenter = hasCenter,
                FireType = fire, LayerMask = "L1", LayerHoldsOpen = true,
                EngageDeadzonePercent = 25,
            };

        private static void Tick(MenuRuntimeState st, MenuDefinitionEntry def,
            bool active, bool physical, bool clicked, bool centerAtRest, double dx, double dy, long nowMs)
            => MenuEvaluator.UpdateLayerEngaged(st, def, active, physical, clicked, centerAtRest,
                dx, dy, (dx + 1) / 2, (dy + 1) / 2, nowMs);

        [Fact]
        public void AtRest_StickRadialShowsItsCenter_OnlyWhenItHasOne()
        {
            var st = new MenuRuntimeState();
            Tick(st, Held(MenuFireType.Click, hasCenter: true), active: true, physical: false, clicked: false,
                centerAtRest: true, 0, 0, 1000);
            Assert.Equal(0, st.HoveredIndex);
            Assert.True(st.Engaged);
            Assert.False(st.PhysicalEngaged);

            st = new MenuRuntimeState();
            Tick(st, Held(MenuFireType.Click, hasCenter: false), true, false, false, true, 0, 0, 1000);
            Assert.Equal(-1, st.HoveredIndex);
        }

        [Fact]
        public void AtRest_GridAndUntouchedPadHoverNothing()
        {
            // GridIndexFromPosition clamps every position into a cell, so a
            // synthetic center would light the middle cell with no finger
            // down. The stay-open path refuses to compute a grid hover at
            // rest at all.
            var st = new MenuRuntimeState();
            Tick(st, Held(MenuFireType.Click, MenuKind.Grid), true, false, false, true, 0, 0, 1000);
            Assert.Equal(-1, st.HoveredIndex);

            // A touchpad host never reports centerAtRest, so a radial with a
            // center still hovers nothing until touched.
            st = new MenuRuntimeState();
            Tick(st, Held(MenuFireType.Click, hasCenter: true), true, false, false, centerAtRest: false, 0, 0, 1000);
            Assert.Equal(-1, st.HoveredIndex);
        }

        [Fact]
        public void TouchRelease_CommitsOnPhysicalRelease_WhileTheMenuStaysOpen_AndNotAgainOnLayerExit()
        {
            var def = Held(MenuFireType.TouchRelease);
            var st = new MenuRuntimeState();

            Tick(st, def, true, false, false, true, 0, 0, 1000);      // layer opens at rest
            Assert.False(MenuEvaluator.IsItemFired(st, 2, 1000));
            Tick(st, def, true, true, false, true, 1.0, 0.0, 1010);  // deflect to slot 2
            Assert.Equal(2, st.HoveredIndex);
            Assert.False(MenuEvaluator.IsItemFired(st, 2, 1010));   // held, not fired
            Tick(st, def, true, false, false, true, 0, 0, 1020);     // re-center: the release
            Assert.True(MenuEvaluator.IsItemFired(st, 2, 1020));
            Assert.True(st.Engaged, "the menu stays open after the commit");

            // The layer ending later must not re-fire that interaction.
            Tick(st, def, true, false, false, true, 0, 0, 1200);     // pulse expired
            Assert.False(MenuEvaluator.IsItemFired(st, 2, 1200));
            Tick(st, def, false, false, false, true, 0, 0, 1210);    // layer exits
            for (int i = 0; i <= 4; i++)
                Assert.False(MenuEvaluator.IsItemFired(st, i, 1210));
        }

        [Fact]
        public void TouchRelease_LayerExitCommitsAnInteractionStillInProgress()
        {
            var def = Held(MenuFireType.TouchRelease);
            var st = new MenuRuntimeState();
            Tick(st, def, true, false, false, true, 0, 0, 1000);
            Tick(st, def, true, true, false, true, 0.0, -1.0, 1010); // hover slot 1, still deflected
            Tick(st, def, false, true, false, true, 0.0, -1.0, 1020); // layer ends mid-deflection
            Assert.True(MenuEvaluator.IsItemFired(st, 1, 1020));
        }

        [Fact]
        public void TouchRelease_OpeningAndClosingAtRest_ArmsNothing()
        {
            var def = Held(MenuFireType.TouchRelease, hasCenter: true);
            var st = new MenuRuntimeState();
            Tick(st, def, true, false, false, true, 0, 0, 1000);   // opens, center hovered
            Assert.Equal(0, st.HoveredIndex);
            Tick(st, def, false, false, false, true, 0, 0, 1010);  // closes, never touched
            Assert.False(MenuEvaluator.IsItemFired(st, 0, 1010));
        }

        [Fact]
        public void ClickRelease_SimultaneousReleaseAndRecenter_CommitsTheRingCellNotTheNewCenter()
        {
            var def = Held(MenuFireType.ClickRelease, hasCenter: true);
            var st = new MenuRuntimeState();
            Tick(st, def, true, false, false, true, 0, 0, 1000);
            Tick(st, def, true, true, true, true, 1.0, 0.0, 1010);   // slot 2, clicked
            Assert.Equal(2, st.HoveredIndex);
            // Click releases AND the stick snaps back in the same frame: the
            // resting center (0) appears this frame, but the user was on 2.
            Tick(st, def, true, false, false, true, 0, 0, 1020);
            Assert.True(MenuEvaluator.IsItemFired(st, 2, 1020));
            Assert.False(MenuEvaluator.IsItemFired(st, 0, 1020));
        }

        [Fact]
        public void Always_AssertsAResidentCenterTheMomentTheLayerOpens()
        {
            var def = Held(MenuFireType.Always, hasCenter: true);
            var st = new MenuRuntimeState();
            Tick(st, def, true, false, false, true, 0, 0, 1000);
            Assert.True(MenuEvaluator.IsItemFired(st, 0, 1000));
            Tick(st, def, false, false, false, true, 0, 0, 1010);
            Assert.False(MenuEvaluator.IsItemFired(st, 0, 1010));
        }

        [Fact]
        public void Click_AssertsOnlyWhileClicked_CenterIncluded()
        {
            var def = Held(MenuFireType.Click, hasCenter: true);
            var st = new MenuRuntimeState();
            Tick(st, def, true, false, false, true, 0, 0, 1000);
            Assert.False(MenuEvaluator.IsItemFired(st, 0, 1000));
            Tick(st, def, true, false, true, true, 0, 0, 1010);   // click the resting center
            Assert.True(MenuEvaluator.IsItemFired(st, 0, 1010));
            Tick(st, def, true, false, false, true, 0, 0, 1020);
            Assert.False(MenuEvaluator.IsItemFired(st, 0, 1020));
        }

        [Fact]
        public void Reset_ClearsPhysicalEngaged()
        {
            var st = new MenuRuntimeState { PhysicalEngaged = true, Engaged = true };
            st.Reset();
            Assert.False(st.PhysicalEngaged);
        }

        // ── Editor: the layer gate ───────────────────────────────────────

        private static MenuEditorItem Editor(MenuDefinitionEntry entry = null)
        {
            entry ??= new MenuDefinitionEntry { MenuId = 1, CellCount = 4 };
            var vm = new MenuEditorItem(entry)
            {
                LayerChoicesProvider = () => new[]
                {
                    new ShiftLayerInfo { LayerMask = "", LayerName = Strings.Instance.Macro_Layer_Any },
                    new ShiftLayerInfo { LayerMask = "Base", LayerName = "Base" },
                    new ShiftLayerInfo { LayerMask = "L1", LayerName = "Radial" },
                },
            };
            vm.RefreshLayerChoices();
            return vm;
        }

        [Fact]
        public void LayerMask_IgnoresANullWrite_AndDoesNotDirty()
        {
            var vm = Editor(new MenuDefinitionEntry { LayerMask = "L1" });
            int edits = 0;
            vm.Changed += () => edits++;
            vm.LayerMask = null;
            Assert.Equal("L1", vm.LayerMask);
            Assert.Equal(0, edits);
        }

        [Fact]
        public void LayerMask_PreservesAnAuthoredBase_Exactly()
        {
            var vm = Editor();
            vm.LayerMask = "Base";
            Assert.Equal("Base", vm.Entry.LayerMask);
            Assert.True(vm.HasLayerScope);
            Assert.False(vm.HasNamedLayer);
            Assert.False(vm.ShowLayerHold);
        }

        [Fact]
        public void LayerHoldsOpen_NeedsARealLayer_AndDropsWhenTheGateClears()
        {
            var vm = Editor();
            vm.LayerMask = "Base";
            vm.LayerHoldsOpen = true;
            Assert.False(vm.LayerHoldsOpen, "Base cannot hold a menu open");

            vm.LayerMask = "L1";
            vm.LayerHoldsOpen = true;
            Assert.True(vm.LayerHoldsOpen);
            Assert.True(vm.EffectiveLayerHoldsOpen);
            Assert.Equal(Strings.Instance.Menu_Host_SteerWith, vm.HostInputLabel);

            vm.LayerMask = "";
            Assert.False(vm.Entry.LayerHoldsOpen, "clearing the gate clears the flag");
            Assert.Equal(Strings.Instance.Menu_HostInput, vm.HostInputLabel);
        }

        [Fact]
        public void LoadedFlagWithNoLayer_StaysVisibleAndResettable_ButIsNotEffective()
        {
            var vm = Editor(new MenuDefinitionEntry { LayerMask = "", LayerHoldsOpen = true });
            Assert.True(vm.ShowLayerHold);
            Assert.True(vm.ShowsLayerRow);
            Assert.False(vm.EffectiveLayerHoldsOpen);
            vm.ResetLayerCommand.Execute(null);
            Assert.False(vm.Entry.LayerHoldsOpen);
        }

        [Fact]
        public void LayerChoices_CarryAMarkedEntryForAMaskNotOnTheSlot()
        {
            var vm = Editor(new MenuDefinitionEntry { LayerMask = "Ghost" });
            var ghost = vm.LayerChoices.FirstOrDefault(c => c.LayerMask == "Ghost");
            Assert.NotNull(ghost);
            Assert.Equal(string.Format(Strings.Instance.Menu_Layer_Missing_Format, "Ghost"), ghost.LayerName);
            Assert.Contains(vm.LayerChoices, c => c.LayerMask == "");
            Assert.Contains(vm.LayerChoices, c => c.LayerMask == "Base");
        }

        [Fact]
        public void LayerChoices_ReconcileInPlace_KeepingTheSelectedInstance()
        {
            var vm = Editor(new MenuDefinitionEntry { LayerMask = "L1" });
            var before = vm.LayerChoices.First(c => c.LayerMask == "L1");
            int version = vm.LayerChoicesVersion;
            vm.RefreshLayerChoices();
            Assert.Same(before, vm.LayerChoices.First(c => c.LayerMask == "L1"));
            Assert.Equal(version + 1, vm.LayerChoicesVersion);
        }

        [Fact]
        public void FireDescriptions_FollowTheMode_AndTheHotbarHost()
        {
            var vm = Editor();
            vm.FireTypeIndex = (int)MenuFireType.TouchRelease;
            Assert.Equal(Strings.Instance.Menu_Fire_TouchRelease_Desc, vm.SelectedFireDescription);

            vm.LayerMask = "L1";
            vm.LayerHoldsOpen = true;
            Assert.Equal(Strings.Instance.Menu_Fire_TouchRelease_LayerHold_Desc, vm.SelectedFireDescription);

            vm.KindIndex = 1; // Grid
            vm.SelectedHost = vm.HostOptions.First(h => h.Descriptor == "Gamepad RightStick");
            Assert.False(vm.IsButtonPairGrid);
            vm.Entry.HostDescriptor = "Gamepad DPad";
            vm.RefreshHostOptions();
            Assert.True(vm.IsButtonPairGrid);
            Assert.Equal(Strings.Instance.Menu_Fire_ButtonPairGrid_Desc, vm.SelectedFireDescription);
        }

        // ── Editor: per-cell icon size ───────────────────────────────────

        private static MenuCellItem Cell(MenuItemDefinition seed = null)
        {
            var entry = new MenuDefinitionEntry { MenuId = 1, CellCount = 4 };
            if (seed != null) entry.Items.Add(seed);
            return new MenuEditorItem(entry).Cells.First(c => c.Index == (seed?.Index ?? 1));
        }

        [Fact]
        public void IconScale_OnACellWithNoIcon_AuthorsNothing()
        {
            var cell = Cell();
            cell.IconScalePercent = 150;
            Assert.Equal(100, cell.IconScalePercent);
            Assert.False(cell.HasIcon);
        }

        [Fact]
        public void IconScale_ClampsBothWays()
        {
            var cell = Cell(new MenuItemDefinition { Index = 1, Icon = "a.png" });
            cell.IconScalePercent = 500;
            Assert.Equal(200, cell.IconScalePercent);
            cell.IconScalePercent = 3;
            Assert.Equal(25, cell.IconScalePercent);
            // A loaded out-of-range value reads clamped without being rewritten.
            var wild = Cell(new MenuItemDefinition { Index = 1, Icon = "a.png", IconScalePercent = 999 });
            Assert.Equal(200, wild.IconScalePercent);
        }

        [Fact]
        public void SetIcon_ReplacementKeepsTheSize_ClearResetsIt()
        {
            var seed = new MenuItemDefinition { Index = 1, Icon = "a.png", IconScalePercent = 150 };
            var cell = Cell(seed);
            cell.SetIcon("b.png");
            Assert.Equal(150, seed.IconScalePercent);
            cell.SetIcon("");
            Assert.False(cell.HasIcon);
            Assert.Equal(100, cell.IconScalePercent);
        }

        [Fact]
        public void SetIcon_ClearingAnIconOnlyCellPrunesIt_SizeIncluded()
        {
            var entry = new MenuDefinitionEntry { MenuId = 1, CellCount = 4 };
            entry.Items.Add(new MenuItemDefinition { Index = 1, Icon = "a.png", IconScalePercent = 150 });
            var editor = new MenuEditorItem(entry);
            editor.Cells.First(c => c.Index == 1).ResetCellCommand.Execute(null);
            Assert.Empty(entry.Items);
        }

        // ── Sibling sites ────────────────────────────────────────────────

        [Fact]
        public void StarterRadials_AuthorRingSlotsOneToN_AndKeepSurfaceEngagement()
        {
            int radials = 0;
            foreach (var p in StarterProfileCatalog.All)
            {
                var built = p.Build();
                foreach (var set in built.SlotMappingSets)
                {
                    if (set?.Menus == null) continue;
                    foreach (var menu in set.Menus)
                    {
                        if (menu.Kind != MenuKind.Radial) continue;
                        radials++;
                        Assert.False(menu.HasCenter);
                        Assert.False(menu.LayerHoldsOpen, "a mask alone must not opt a starter into stay-open");
                        // Index 0 is the center this menu does not have; the
                        // ring is 1..N. Authoring from 0 put the first cell on
                        // an index the ring never hovers and left slot N empty.
                        Assert.Equal(Enumerable.Range(1, menu.CellCount), menu.Items.Select(i => i.Index).OrderBy(i => i));
                        Assert.All(menu.Items, i => Assert.Equal(100, i.IconScalePercent));
                    }
                }
            }
            Assert.True(radials > 0, "no starter radials found, the positive control failed");
        }

        [Fact]
        public void OverlaySignature_TracksThePerCellSize()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "PadForge.App", "Views", "MenuOverlayWindow.xaml.cs"));
            Assert.Contains(".Append('~').Append(it.IconScalePercent);", src);
            Assert.Contains("Math.Clamp(item.IconScalePercent, 25, 200) / 100.0", src);
        }

        [Fact]
        public void IconDecode_IsOneConstantAtAllThreeLoaders()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "PadForge.App", "Common", "MenuIconResolver.cs"));
            Assert.Contains("private const int IconDecodePixelWidth = 256;", src);
            Assert.Equal(3, Count(src, "img.DecodePixelWidth = IconDecodePixelWidth;"));
            Assert.DoesNotContain("DecodePixelWidth = 96", src);
        }

        [Fact]
        public void Translator_StatesSurfaceEngagementExplicitly()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "PadForge.SteamWorkshop", "Translation", "ConfigTranslator.cs"));
            Assert.Contains("LayerHoldsOpen = false,", src);
        }

        private static string RepoRoot()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static int Count(string hay, string needle)
        {
            int n = 0, i = 0;
            while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }
    }
}
