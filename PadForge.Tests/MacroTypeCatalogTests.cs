using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The grouped macro action-type catalog: every MacroActionType on
    /// exactly one shelf, shelves in a deliberate presentation order,
    /// and the editor's picker bound to the grouped view. The census is
    /// the load-bearing guard: a future action type appended to the enum
    /// fails here until it chooses a category, so the picker can never
    /// silently regrow an uncategorized tail.
    /// </summary>
    public class MacroTypeCatalogTests
    {
        /// <summary>Every enum member appears in the catalog exactly
        /// once, and the catalog carries nothing else.</summary>
        [Fact]
        public void Census_EveryActionTypeExactlyOnce()
        {
            var all = Enum.GetValues<MacroActionType>();
            var counts = MacroTypeCatalog.Choices
                .GroupBy(c => c.Type)
                .ToDictionary(g => g.Key, g => g.Count());

            foreach (var t in all)
            {
                Assert.True(counts.TryGetValue(t, out int n) && n == 1,
                    $"{t} appears {counts.GetValueOrDefault(t)} times in the catalog; every action type needs exactly one shelf");
            }
            Assert.Equal(all.Length, MacroTypeCatalog.Choices.Count);
        }

        /// <summary>Labels and categories are non-empty localized
        /// strings, so a missing resx key cannot ship a blank row.</summary>
        [Fact]
        public void EveryEntry_HasLabelAndCategory()
        {
            foreach (var c in MacroTypeCatalog.Choices)
            {
                Assert.False(string.IsNullOrWhiteSpace(c.Label), $"{c.Type} has no label");
                Assert.False(string.IsNullOrWhiteSpace(c.Category), $"{c.Type} has no category");
            }
        }

        /// <summary>Categories are contiguous blocks in presentation
        /// order: the grouped view renders headers in first-occurrence
        /// order, so a category split across the list would render
        /// twice.</summary>
        [Fact]
        public void Categories_AreContiguousAndEleven()
        {
            var seen = new List<string>();
            foreach (var c in MacroTypeCatalog.Choices)
            {
                if (seen.Count == 0 || seen[^1] != c.Category)
                {
                    Assert.DoesNotContain(c.Category, seen);
                    seen.Add(c.Category);
                }
            }
            Assert.Equal(11, seen.Count);
        }

        /// <summary>Spot pins for the shelving decisions that carry
        /// intent: buttons first, sequencing together, the #377 layer
        /// jump under layers, and CycleTapList in flow because its steps
        /// span keys, mouse, and virtual-pad outputs.</summary>
        [Fact]
        public void Shelving_SpotChecks()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            string CatOf(MacroActionType t) => MacroTypeCatalog.Choices.First(c => c.Type == t).Category;

            Assert.Equal(s.Macro_Cat_VcButtons, MacroTypeCatalog.Choices[0].Category);
            Assert.Equal(MacroActionType.ButtonPress, MacroTypeCatalog.Choices[0].Type);
            Assert.Equal(s.Macro_Cat_Flow, CatOf(MacroActionType.Delay));
            Assert.Equal(s.Macro_Cat_Flow, CatOf(MacroActionType.ComboBreak));
            Assert.Equal(s.Macro_Cat_Flow, CatOf(MacroActionType.CycleTapList));
            Assert.Equal(s.Macro_Cat_Layers, CatOf(MacroActionType.SwitchLayer));
            Assert.Equal(s.Macro_Cat_Mouse, CatOf(MacroActionType.MoveMouseToScreenPosition));
            Assert.Equal(s.Macro_Cat_System, CatOf(MacroActionType.RunProgram));
            Assert.Equal(s.Macro_Cat_VcAxes, CatOf(MacroActionType.ToggleWheel));
        }

        /// <summary>Tooltips that existed on the flat picker survive on
        /// the catalog entries (spot pins on both a tooltipped and a
        /// tooltip-less entry).</summary>
        [Fact]
        public void Tooltips_CarryOver()
        {
            var byType = MacroTypeCatalog.Choices.ToDictionary(c => c.Type);
            Assert.False(string.IsNullOrWhiteSpace(byType[MacroActionType.ComboBreak].Tooltip));
            Assert.False(string.IsNullOrWhiteSpace(byType[MacroActionType.SwitchLayer].Tooltip));
            Assert.Null(byType[MacroActionType.ButtonPress].Tooltip);
        }

        /// <summary>The picker's source contract: bound to the grouped
        /// view with Type as the selected value path, item tooltips on
        /// the container, and the flat fifty-six-item list gone.</summary>
        [Fact]
        public void PickerBindsTheGroupedView()
        {
            string page = RepoText("PadForge.App", "Views", "PadPage.xaml");
            int at = page.IndexOf("ItemsSource=\"{Binding Source={x:Static vm:MacroTypeCatalog.View}}\"", StringComparison.Ordinal);
            Assert.True(at > 0, "the picker no longer binds MacroTypeCatalog.View");
            string tail = page.Substring(Math.Max(0, at - 400), 3400);
            Assert.Contains("SelectedValuePath=\"Type\"", tail);
            Assert.Contains("DisplayMemberPath=\"Label\"", tail);
            Assert.Contains("ToolTip\" Value=\"{Binding Tooltip}", tail);
            Assert.Contains("GroupStyle", tail);
            // The old flat list is gone: no hardcoded action-type items
            // remain anywhere in the page.
            Assert.DoesNotContain("Tag=\"{x:Static vm:MacroActionType.", page);
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
