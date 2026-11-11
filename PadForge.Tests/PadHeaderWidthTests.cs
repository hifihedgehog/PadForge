using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The pad page's header has to fit at the supported minimum window
    /// width in every language. Its first tier once could not. Measured
    /// with tools/rowmeasure against the real controls, the
    /// real styles and the app font, the scope label, the identity chip, the
    /// preset chip and six tabs needed 1316.7 where the 900 minimum with the
    /// navigation pane open (244 wide) left 648, so the right-docked tab
    /// strip was handed a negative width and clipped away entirely.
    ///
    /// <para>The header that took the two tiers' place (#574) is measured
    /// by the same tool. Its margin is 12 a side, and the sidebar is 248
    /// wide and always open, so a line holds 628 at that minimum. Worst
    /// language for each piece: the title 362.0, the
    /// Preset chip 617.3 at its combo's cap and 237.3 without the combo,
    /// the power button 388.0 with its state word whole, the six sections
    /// on one line 1079.3, the widest single section 251.3. So the sections
    /// wrap, the power button's word is capped, the title trims, and the
    /// Preset chip's combo gives way. Nothing runs past the edge.</para>
    /// </summary>
    public class PadHeaderWidthTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string PadPage() => File.ReadAllText(Path.Combine(
            RepoRoot(), "PadForge.App", "Views", "PadPage.xaml"));

        private static XElement Named(XDocument page, string name)
            => Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Name") == name);

        /// <summary>The header's lines wrap. A panel that cannot wrap gives
        /// the last child whatever is left, which was nothing.</summary>
        [Fact]
        public void TheFirstTierWraps()
        {
            string xaml = PadPage();

            Assert.Contains("<WrapPanel Orientation=\"Horizontal\" VerticalAlignment=\"Center\">", xaml);
            Assert.DoesNotContain("<DockPanel LastChildFill=\"False\" VerticalAlignment=\"Center\">", xaml);

            // The title's line and the device's line are each a WrapPanel in
            // the first column of a two-column grid.
            var page = XDocument.Parse(xaml);
            foreach (string inLine in new[] { "PadTitle", "DrivenByLabel" })
            {
                var wrap = Named(page, inLine).Ancestors().First(a => a.Name.LocalName == "WrapPanel");
                Assert.Equal("Horizontal", (string)wrap.Attribute("Orientation"));
                Assert.Equal("Grid", wrap.Parent!.Name.LocalName);
                Assert.Null(wrap.Attribute("Grid.Column"));
            }
        }

        /// <summary>The tab strip is an ordinary wrapped child now. Left
        /// docked to the right edge it kept its position and lost its
        /// width.</summary>
        [Fact]
        public void TheTabStripNoLongerDocksRight()
        {
            Assert.DoesNotContain("<StackPanel DockPanel.Dock=\"Right\" Orientation=\"Horizontal\">", PadPage());
        }

        /// <summary>The strips wrap among themselves. Measured with
        /// tools/rowmeasure, the six sections need 1079 in Malayalam
        /// against the 628 a line holds at the 900 minimum beside the
        /// sidebar, and English needs more than 628 too, so in
        /// a horizontal stack the last sections would run past the edge, as
        /// the last of the six tabs once did (#505). The top row of Buttons
        /// holds its Picture and List switch, which needs 176 in Ukrainian,
        /// the layer chips, Map All and the list's tools, and wraps as
        /// well.</summary>
        [Fact]
        public void TheTabGroupWrapsItsTabs()
        {
            var page = XDocument.Parse(PadPage());
            foreach (string button in new[] { "SectionButtons", "SectionMacros", "TabController", "TabMappings", "TabMacros", "TabMenus" })
            {
                var strip = Named(page, button).Parent!;
                Assert.Equal("WrapPanel", strip.Name.LocalName);
                Assert.Equal("Horizontal", (string)strip.Attribute("Orientation"));
            }
            // Everything in the top row of Buttons is a child of the one
            // wrapping panel: the switch, the layer chips, the list's tools,
            // Map All and More.
            var top = Named(page, "ButtonsTopRow");
            Assert.Same(top, Named(page, "TabController").Parent);
            foreach (string child in new[] { "ShiftLayerTabStrip", "ListTools", "ListMoreButton" })
                Assert.Same(top, Named(page, child).Parent);
        }

        /// <summary>The raw hat strip on the Devices page wraps. Measured
        /// with tools/rowmeasure, four hats need 321 against the 298 the
        /// detail pane leaves them, so a fourth hat ran past the edge.</summary>
        [Fact]
        public void TheRawHatStripWraps()
        {
            string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Views", "DevicesPage.xaml"));
            int hats = xaml.IndexOf("ItemsSource=\"{Binding RawPovs}\"", StringComparison.Ordinal);
            Assert.True(hats > 0);
            int end = xaml.IndexOf("</ItemsControl.ItemsPanel>", hats, StringComparison.Ordinal);
            Assert.Contains("<WrapPanel Orientation=\"Horizontal\"/>", xaml.Substring(hats, end - hats));
        }

        /// <summary>The device selector had a floor and no ceiling in an Auto
        /// column, so one long device name grew the column without bound and
        /// took the width from the device tabs wrapping beside it.</summary>
        [Fact]
        public void TheDeviceSelectorHasACeilingAndAFloor()
        {
            Assert.Contains("MinWidth=\"200\" MaxWidth=\"320\" Margin=\"4,2\" VerticalAlignment=\"Center\">",
                PadPage());
        }

        /// <summary>The power button sits at the right of the title's line
        /// and takes what it needs first, so its width is bounded: the state
        /// word is capped at 196, which makes the button 254 at most and
        /// leaves the title's column 374 at the minimum. Uncapped, "Virtual
        /// controller failed" in Tamil made the button 388 and left the
        /// title 244 of the 362 it needs.</summary>
        [Fact]
        public void ThePowerButtonsWordIsCapped()
        {
            var page = XDocument.Parse(PadPage());
            var word = Named(page, "SlotStateWord");
            Assert.Equal("196", (string)word.Attribute("MaxWidth"));
            Assert.Equal("CharacterEllipsis", (string)word.Attribute("TextTrimming"));
            var host = Named(page, "SlotPowerHost");
            Assert.Equal("1", (string)host.Attribute("Grid.Column"));
            var columns = host.Parent!.Element(host.Parent.Name.Namespace + "Grid.ColumnDefinitions")!.Elements().ToList();
            Assert.Equal(new[] { "*", "Auto" }, columns.Select(c => (string)c.Attribute("Width")));
        }

        /// <summary>The title stays beside the type chip and ends in an
        /// ellipsis when its line is shorter than it. A DockPanel hands the
        /// title the width the chip leaves. A horizontal stack would hand it
        /// all it asks for, and the WrapPanel would then clip it.</summary>
        [Fact]
        public void TheTitleTrimsBesideItsChip()
        {
            var page = XDocument.Parse(PadPage());
            var title = Named(page, "PadTitle");
            Assert.Equal("CharacterEllipsis", (string)title.Attribute("TextTrimming"));
            Assert.Equal("DockPanel", title.Parent!.Name.LocalName);
            Assert.Same(title, title.Parent.Elements().Last());
            Assert.Null(title.Parent.Attribute("LastChildFill"));
            var chip = title.Parent.Elements().First();
            Assert.Equal("Left", chip.Attributes().Single(a => a.Name.LocalName == "DockPanel.Dock").Value);
            Assert.Equal("34", (string)chip.Attribute("Width"));
        }

        /// <summary>The Preset chip is a grid whose middle column holds the
        /// combo, so on a short line the combo narrows and the reset beside
        /// it stays in view. In a horizontal stack the combo took up to its
        /// 380 cap whatever the line held: 617 for the whole chip in
        /// Swahili, against a column of 378.</summary>
        [Fact]
        public void ThePresetChipsComboGivesWay()
        {
            var page = XDocument.Parse(PadPage());
            var chip = Named(page, "HMaestroProfileBar");
            var grid = Assert.Single(chip.Elements());
            Assert.Equal("Grid", grid.Name.LocalName);
            var columns = grid.Element(grid.Name.Namespace + "Grid.ColumnDefinitions")!.Elements().ToList();
            Assert.Equal(new[] { "Auto", "*", "Auto" }, columns.Select(c => (string)c.Attribute("Width")));
            foreach (string combo in new[] { "HMaestroProfileCombo", "KbmSurfacesCombo" })
            {
                var box = Named(page, combo);
                Assert.Same(grid, box.Parent);
                Assert.Equal("1", (string)box.Attribute("Grid.Column"));
                Assert.Equal("380", (string)box.Attribute("MaxWidth"));
            }
            var resets = grid.Elements().Where(e => e.Name.LocalName == "SettingResetButton").ToList();
            Assert.Equal(2, resets.Count);
            Assert.All(resets, r => Assert.Equal("2", (string)r.Attribute("Grid.Column")));
        }

        /// <summary>The measurement harness carries the header, so the next
        /// change to it can be measured rather than estimated.</summary>
        [Fact]
        public void TheHarnessMeasuresTheTier()
        {
            string prog = File.ReadAllText(Path.Combine(
                RepoRoot(), "tools", "rowmeasure", "Program.cs"));

            Assert.Contains("BuildPadHeader();", prog);
            Assert.Contains("hdr-title", prog);
            Assert.Contains("hdr-preset", prog);
            Assert.Contains("hdr-preset-floor", prog);
            Assert.Contains("hdr-power", prog);
            Assert.Contains("hdr-section-part", prog);
            Assert.Contains("hdr-parts-buttons", prog);
            Assert.Contains("Pad page header", prog);
            // The cap the page gives the state word is the one the harness
            // reports against.
            Assert.Contains("const double wordCap = 196,", prog);
        }
    }
}
