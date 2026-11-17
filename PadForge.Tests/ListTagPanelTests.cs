using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// An input's line in a cell of the mappings list keeps the level's
    /// number whole in a column short of room.
    ///
    /// <para>The line's parts stood at fixed widths. On a list of four
    /// device columns, a name as long as "Right Stick X", an 84-wide bar
    /// and a four-character number ran past the column's edge, and the
    /// number was cut: "100" with half a percent sign, "-18" with none
    /// (owner, 2026-11-17). The bar now gives way first and then goes, the
    /// mode's word trims next, and only then does the name trim.</para>
    /// </summary>
    public class ListTagPanelTests
    {
        // A tag's name at 107, the bar's slot at 92 (84 and its margin) and
        // at least 32, the name's least 56, and the number's slot at 46.
        private static (double Name, double Bar, double Mode) Share(double room, double name = 107, double fixedParts = 46,
            double bar = 92, double mode = 0)
            => ListTagPanel.Share(room, new ListTagPanel.Wants(name, bar, bar > 0 ? 32 : 0, mode, Math.Min(mode, 40), 56, fixedParts));

        [Fact]
        public void WithRoomEveryPartHasItsOwnWidth()
        {
            Assert.Equal((107, 92, 0), Share(245));
            Assert.Equal((107, 92, 0), Share(400));
            Assert.Equal((107, 92, 0), Share(double.PositiveInfinity));
            Assert.Equal((107, 92, 60), Share(305, mode: 60));
        }

        /// <summary>The owner's column: 21 short of the name, the bar and
        /// the number. The bar gives the 21, and the number is whole.</summary>
        [Fact]
        public void ShortOfRoomTheBarGivesWayAndTheNumberKeepsItsWidth()
        {
            var (name, bar, _) = Share(224);
            Assert.Equal((107, 71), (name, bar));
            Assert.Equal(224, name + bar + 46);
        }

        [Fact]
        public void TheBarGoesWhenItWouldBeUnderItsLeast()
        {
            // 185 leaves the bar exactly its least.
            Assert.Equal((107, 32, 0), Share(185));
            // One less, and it goes: the name keeps its width.
            Assert.Equal((107, 0, 0), Share(184));
            Assert.Equal((107, 0, 0), Share(153));
        }

        [Fact]
        public void TheNameTrimsOnlyAfterTheBarIsGone_AndNeverUnderItsLeast()
        {
            Assert.Equal((100, 0, 0), Share(146));
            Assert.Equal((56, 0, 0), Share(102));
            Assert.Equal((56, 0, 0), Share(60));
            Assert.Equal((56, 0, 0), Share(0));
            // A name shorter than the least keeps its own width.
            Assert.Equal((40, 0, 0), Share(60, name: 40));
        }

        /// <summary>The widest number, "-100%", is a fixed part like any
        /// other: it takes its room first.</summary>
        [Fact]
        public void AWiderNumberTakesItsRoomFromTheBar()
        {
            Assert.Equal((107, 71, 0), Share(224));
            Assert.Equal((107, 63, 0), Share(224, fixedParts: 54));
        }

        /// <summary>A source's mode in a word gives way after the bar and
        /// before the name: a column at its least width cut the name of a
        /// modifier to six letters to keep "Invert On Hold" whole, and the
        /// open row names the mode anyway.</summary>
        [Fact]
        public void TheModeTrimsBeforeTheName_AndGoesUnderItsLeast()
        {
            // No bar on a button's line. The mode has its 100 while there is room.
            Assert.Equal((170, 0, 100), Share(270, name: 170, fixedParts: 0, bar: 0, mode: 100));
            // Short of room it takes what the name leaves, and trims.
            Assert.Equal((170, 0, 60), Share(230, name: 170, fixedParts: 0, bar: 0, mode: 100));
            Assert.Equal((170, 0, 40), Share(210, name: 170, fixedParts: 0, bar: 0, mode: 100));
            // Under its least it goes, and the name is whole.
            Assert.Equal((170, 0, 0), Share(209, name: 170, fixedParts: 0, bar: 0, mode: 100));
            // The name trims only then.
            Assert.Equal((152, 0, 0), Share(152, name: 170, fixedParts: 0, bar: 0, mode: 100));
        }

        /// <summary>An axis with a mode: the bar goes before the mode
        /// trims.</summary>
        [Fact]
        public void TheBarGoesBeforeTheModeTrims()
        {
            Assert.Equal((107, 92, 60), Share(305, mode: 60));
            Assert.Equal((107, 50, 60), Share(263, mode: 60));
            Assert.Equal((107, 0, 60), Share(244, mode: 60));
            Assert.Equal((107, 0, 60), Share(213, mode: 60));
            Assert.Equal((107, 0, 45), Share(198, mode: 60));
            Assert.Equal((107, 0, 0), Share(192, mode: 60));
        }

        [Fact]
        public void ALineWithNoBarSharesBetweenTheNameAndTheFixedParts()
        {
            Assert.Equal((107, 0, 0), Share(200, fixedParts: 0, bar: 0));
            Assert.Equal((90, 0, 0), Share(90, fixedParts: 0, bar: 0));
            Assert.Equal((56, 0, 0), Share(30, fixedParts: 0, bar: 0));
        }

        /// <summary>The panel itself, laid out: a name, a bar and a number
        /// in the owner's column. The number's right edge is the line's, and
        /// the line is no wider than its room.</summary>
        [Fact]
        public void ThePanelLaysTheNumberOutWholeInAShortColumn()
        {
            RunSta(() =>
            {
                var name = new Border { Width = 107, Height = 26 };
                ListTagPanel.SetPart(name, ListTagPart.Name);
                var bar = new LevelBar { Margin = new Thickness(8, 0, 0, 0) };
                ListTagPanel.SetPart(bar, ListTagPart.Bar);
                var number = new Border { Width = 38, Height = 16, Margin = new Thickness(8, 0, 0, 0) };
                var panel = new ListTagPanel();
                panel.Children.Add(name);
                panel.Children.Add(bar);
                panel.Children.Add(number);

                foreach (var (room, barWidth) in new[] { (400.0, 84.0), (224.0, 63.0), (184.0, 0.0) })
                {
                    panel.Measure(new Size(room, double.PositiveInfinity));
                    Assert.True(panel.DesiredSize.Width <= room, $"{panel.DesiredSize.Width} wide in a room of {room}");
                    panel.Arrange(new Rect(panel.DesiredSize));
                    Assert.Equal(barWidth, bar.RenderSize.Width, 3);
                    Assert.Equal(38, number.RenderSize.Width, 3);
                    double numberRight = number.TranslatePoint(new Point(number.RenderSize.Width, 0), panel).X;
                    Assert.Equal(panel.DesiredSize.Width, numberRight, 3);
                    Assert.Equal(107, name.RenderSize.Width, 3);
                }

                // Tighter still, the name trims and the number stays whole.
                panel.Measure(new Size(120, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize));
                Assert.Equal(120, panel.DesiredSize.Width, 3);
                Assert.Equal(38, number.RenderSize.Width, 3);
                Assert.Equal(0, bar.RenderSize.Width, 3);
            });
        }

        /// <summary>A button's line with a mode's word, laid out: the word
        /// trims in a column short of room, and the name is whole.</summary>
        [Fact]
        public void ThePanelTrimsTheModeAndKeepsTheName()
        {
            RunSta(() =>
            {
                var name = new Border { Width = 170, Height = 26 };
                ListTagPanel.SetPart(name, ListTagPart.Name);
                var mode = new TextBlock { Text = "Invert On Hold", FontSize = 13, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                ListTagPanel.SetPart(mode, ListTagPart.Mode);
                var panel = new ListTagPanel();
                panel.Children.Add(name);
                panel.Children.Add(mode);

                panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double whole = panel.DesiredSize.Width;
                Assert.True(whole > 170 + 40, "the mode's word has a width of its own");

                panel.Measure(new Size(whole - 20, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize));
                Assert.True(panel.DesiredSize.Width <= whole - 20 + 0.01);
                Assert.Equal(170, name.RenderSize.Width, 3);
                Assert.True(mode.RenderSize.Width > 0 && mode.RenderSize.Width < whole - 170 - 8);

                // No room for the mode at all: the name alone, whole.
                panel.Measure(new Size(175, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize));
                Assert.Equal(170, panel.DesiredSize.Width, 3);
                Assert.Equal(170, name.RenderSize.Width, 3);
            });
        }

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(30000), "STA test body timed out");
            if (failure != null) throw failure;
        }
    }
}
