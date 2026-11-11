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
    /// by the same tool. Its margin is 32 a side, and the sidebar is 248
    /// wide and always open, so a line holds 588 at that minimum. Worst
    /// language for each piece: the title 338.7, the Preset 603.3 at its
    /// combo's cap and 223.3 without the combo, the power button 396.7
    /// with its state word whole, the six sections on one line 1079.3, the
    /// widest single section 251.3, and a device's pill 328 at its cap. So
    /// the sections wrap, the power button's word is capped, the title
    /// trims, the Preset's combo gives way and the device line wraps.
    /// Nothing runs past the edge.</para>
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
        public void TheHeadersLinesWrap()
        {
            string xaml = PadPage();
            Assert.DoesNotContain("<DockPanel LastChildFill=\"False\" VerticalAlignment=\"Center\">", xaml);

            // The title's line is a WrapPanel in the first column of line
            // 1's grid. The device's line is a WrapPanel of the header
            // itself, under that grid.
            var page = XDocument.Parse(xaml);
            var titleLine = Named(page, "PadTitle").Ancestors().First(a => a.Name.LocalName == "WrapPanel");
            Assert.Equal("Horizontal", (string)titleLine.Attribute("Orientation"));
            Assert.Equal("Grid", titleLine.Parent!.Name.LocalName);
            Assert.Null(titleLine.Attribute("Grid.Column"));
            var deviceLine = Named(page, "DrivenByLabel").Parent!;
            Assert.Equal("WrapPanel", deviceLine.Name.LocalName);
            Assert.Equal("Horizontal", (string)deviceLine.Attribute("Orientation"));
            Assert.Equal("StackPanel", deviceLine.Parent!.Name.LocalName);
            Assert.Equal("0", (string)deviceLine.Parent.Attribute("Grid.Row"));
            Assert.Same(titleLine.Parent, deviceLine.ElementsBeforeSelf().Last());

            // The pills wrap inside their list as well, so a controller
            // with many devices takes more lines and no more width.
            var pills = Named(page, "DrivenByList");
            Assert.Same(deviceLine, pills.Parent);
            var panel = Assert.Single(Assert.Single(pills.Descendants(), e => e.Name.LocalName == "ItemsPanelTemplate").Elements());
            Assert.Equal("WrapPanel", panel.Name.LocalName);
            Assert.Equal("Horizontal", (string)panel.Attribute("Orientation"));
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
        /// against the 588 a line holds at the 900 minimum beside the
        /// sidebar, and English needs more than 588 too, so in
        /// a horizontal stack the last sections would run past the edge, as
        /// the last of the six tabs once did (#505). The top row of Buttons
        /// is the head of its card. Its left part wraps: the Picture and
        /// List switch, which needs 176 in Ukrainian, the layer chips, Add
        /// Layer and the hint. Its right part, the list's tools and Map
        /// All, keeps its width at the row's end, and the left part wraps
        /// in what that leaves. When that is less than the switch needs,
        /// the two parts stand one under the other, each with the whole
        /// row (HeadRowPanel).</summary>
        [Fact]
        public void TheTabGroupWrapsItsTabs()
        {
            var page = XDocument.Parse(PadPage());
            foreach (string button in new[] { "SectionButtons", "SectionMacros", "TabMacros", "TabMenus" })
            {
                var strip = Named(page, button).Parent!;
                Assert.Equal("WrapPanel", strip.Name.LocalName);
                Assert.Equal("Horizontal", (string)strip.Attribute("Orientation"));
            }
            var left = Named(page, "ButtonsTopLeft");
            Assert.Equal("WrapPanel", left.Name.LocalName);
            Assert.Equal("Horizontal", (string)left.Attribute("Orientation"));
            // The switch's two segments stand in one track, which is a
            // child of the wrapping panel, and so are the layers' track,
            // Add Layer and the hint.
            foreach (string segment in new[] { "TabController", "TabMappings" })
            {
                var track = Named(page, segment).Parent!.Parent!;
                Assert.Equal("{StaticResource SegmentTrack}", (string)track.Attribute("Style"));
                Assert.Same(left, track.Parent);
            }
            var layers = Named(page, "ShiftLayerTrack");
            Assert.Same(left, layers.Parent);
            Assert.Same(layers, Named(page, "ShiftLayerTabStrip").Parent);
            foreach (string child in new[] { "AddShiftLayerButton", "PictureHint" })
                Assert.Same(left, Named(page, child).Parent);
            // The row: the tools and Map All take their own width at the
            // row's end and the wrapping part the rest, and the two stack
            // when the rest is under 220, which holds the switch's 176
            // with its track. The trailing part wraps too, for the line it
            // has to itself then.
            var top = Named(page, "ButtonsTopRow");
            Assert.Equal("HeadRowPanel", top.Name.LocalName);
            Assert.Equal("220", (string)top.Attribute("LeadingMinWidth"));
            var right = Named(page, "ButtonsTopRight");
            Assert.Equal(new[] { left, right }, top.Elements());
            Assert.Equal("WrapPanel", right.Name.LocalName);
            Assert.Same(right, Named(page, "ListTools").Parent);
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

        /// <summary>A device's pill is as wide as its name asks and 320 at
        /// most, and the name in it trims at 220. A device's name has no
        /// limit of its own, so without the two a long one would take a
        /// line to itself and run past the edge. The glyphs and the
        /// battery beside a name at its 220 fit inside the 320.</summary>
        [Fact]
        public void ADevicesPillHasACeiling()
        {
            var page = XDocument.Parse(PadPage());
            var pills = Named(page, "DrivenByList");
            var pill = Assert.Single(pills.Descendants(), e => (string)e.Attribute(X + "Name") == "Pill");
            Assert.Equal("320", (string)pill.Attribute("MaxWidth"));
            var name = Assert.Single(pills.Descendants(), e => e.Name.LocalName == "TextBlock"
                && (string)e.Attribute("Text") == "{Binding Name}");
            Assert.Equal("220", (string)name.Attribute("MaxWidth"));
            Assert.Equal("CharacterEllipsis", (string)name.Attribute("TextTrimming"));
        }

        /// <summary>The power button and the More button sit at the right
        /// of the title's line and take what they need first, so the power
        /// button's width is bounded: the state word is capped at 204, which
        /// makes the button 264 at most and, with More's 42, leaves the
        /// title's column 282 at the minimum. Uncapped, "Virtual controller
        /// failed" in Tamil made the button 397 and left the title's column
        /// 149.</summary>
        [Fact]
        public void ThePowerButtonsWordIsCapped()
        {
            var page = XDocument.Parse(PadPage());
            var word = Named(page, "SlotStateWord");
            Assert.Equal("204", (string)word.Attribute("MaxWidth"));
            Assert.Equal("CharacterEllipsis", (string)word.Attribute("TextTrimming"));
            var host = Named(page, "SlotPowerHost");
            Assert.Equal("1", (string)host.Attribute("Grid.Column"));
            var columns = host.Parent!.Element(host.Parent.Name.Namespace + "Grid.ColumnDefinitions")!.Elements().ToList();
            Assert.Equal(new[] { "*", "Auto", "Auto" }, columns.Select(c => (string)c.Attribute("Width")));
            // More is the third column's, 34 wide in an 8 left margin.
            var more = Named(page, "PadMoreButton");
            Assert.Same(host.Parent, more.Parent);
            Assert.Equal("2", (string)more.Attribute("Grid.Column"));
            Assert.Equal("34", (string)more.Attribute("Width"));
            Assert.Equal("8,0,0,0", (string)more.Attribute("Margin"));
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

        /// <summary>The Preset is a grid whose middle column holds the
        /// combo, so on a short line the combo narrows and the reset beside
        /// it stays in view. In a horizontal stack the combo took up to its
        /// 380 cap whatever the line held: 603 for the Preset in Swahili,
        /// against a column of 282. Its two resets sit alike, with the
        /// margin their style gives them.</summary>
        [Fact]
        public void ThePresetsComboGivesWay()
        {
            var page = XDocument.Parse(PadPage());
            var preset = Named(page, "HMaestroProfileBar");
            var grid = Assert.Single(preset.Elements());
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
            Assert.All(resets, r => Assert.Null(r.Attribute("Margin")));
        }

        /// <summary>The measurement harness carries the header, so the next
        /// change to it can be measured rather than estimated.</summary>
        [Fact]
        public void TheHarnessMeasuresTheHeader()
        {
            string prog = File.ReadAllText(Path.Combine(
                RepoRoot(), "tools", "rowmeasure", "Program.cs"));

            Assert.Contains("BuildPadHeader();", prog);
            Assert.Contains("hdr-title", prog);
            Assert.Contains("hdr-preset", prog);
            Assert.Contains("hdr-preset-floor", prog);
            Assert.Contains("hdr-power", prog);
            Assert.Contains("hdr-driven", prog);
            Assert.Contains("hdr-add", prog);
            Assert.Contains("hdr-section-part", prog);
            Assert.Contains("hdr-parts-buttons", prog);
            Assert.Contains("Pad page header", prog);
            // The cap the page gives the state word is the one the harness
            // reports against.
            Assert.Contains("const double wordCap = 204,", prog);
            // And the line it reports against is the header's: the window
            // less the sidebar and the header's margin of 32 a side.
            Assert.Contains("(\"a 900 window\", 900.0 - 248 - 64)", prog);
            Assert.Contains("<StackPanel Grid.Row=\"0\" Margin=\"32,24,32,0\">", PadPage());
        }
    }
}
