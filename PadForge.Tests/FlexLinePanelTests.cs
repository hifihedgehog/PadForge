using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The panel that lays the bar under the controller's picture out
    /// (#574): parts with a basis width that share a line while they fit,
    /// take the line's leftover by their grow shares, and start a new line
    /// when they do not fit.
    /// </summary>
    public class FlexLinePanelTests
    {
        /// <summary>Parts fill a line while their bases fit with the gap
        /// between them. The bar's three (170, 420 and 220 with 28 between)
        /// need 866.</summary>
        [Theory]
        [InlineData(866, new[] { 3 })]
        [InlineData(1400, new[] { 3 })]
        [InlineData(865, new[] { 2, 1 })]
        [InlineData(618, new[] { 2, 1 })]
        [InlineData(617, new[] { 1, 1, 1 })]
        [InlineData(100, new[] { 1, 1, 1 })]
        [InlineData(double.PositiveInfinity, new[] { 3 })]
        public void PartsFillALineWhileTheirBasesFit(double width, int[] lines)
            => Assert.Equal(lines, FlexLinePanel.Lines(new double[] { 170, 420, 220 }, width, 28));

        [Fact]
        public void NoPartsMakeNoLines()
            => Assert.Empty(FlexLinePanel.Lines(Array.Empty<double>(), 500, 28));

        /// <summary>What a line has left over goes to its parts by their
        /// shares. With 999 against 1 the middle part takes nearly all of
        /// it, and a part with no share keeps its basis.</summary>
        [Fact]
        public void ALinesLeftoverGoesByShares()
        {
            double[] widths = FlexLinePanel.Share(new double[] { 170, 420, 220 }, new double[] { 0, 999, 1 }, 1866, 28);
            Assert.Equal(170, widths[0], 6);
            Assert.Equal(420 + 999, widths[1], 6);
            Assert.Equal(220 + 1, widths[2], 6);
            Assert.Equal(1866, widths[0] + widths[1] + widths[2] + 56, 6);

            // Nothing left over, or nobody to take it: the bases.
            Assert.Equal(new double[] { 170, 420, 220 }, FlexLinePanel.Share(new double[] { 170, 420, 220 }, new double[] { 0, 999, 1 }, 866, 28));
            Assert.Equal(new double[] { 100, 100 }, FlexLinePanel.Share(new double[] { 100, 100 }, new double[] { 0, 0 }, 500, 20));
            // No bound: the bases.
            Assert.Equal(new double[] { 100, 100 }, FlexLinePanel.Share(new double[] { 100, 100 }, new double[] { 1, 1 }, double.PositiveInfinity, 20));
        }

        /// <summary>A part alone on a line narrower than its basis gets the
        /// line's width, so nothing is cut off.</summary>
        [Fact]
        public void APartAloneOnANarrowLineGetsTheLinesWidth()
        {
            Assert.Equal(new double[] { 300 }, FlexLinePanel.Share(new double[] { 420 }, new double[] { 999 }, 300, 28));
            Assert.Equal(new double[] { 0 }, FlexLinePanel.Share(new double[] { 420 }, new double[] { 0 }, -5, 28));
        }

        /// <summary>A part that asks for a height and fills the place it is
        /// given, so where it ends up is the place itself. One with a width
        /// of its own asks for that width and stands at its place's start.</summary>
        private static Border Part(double height, double basis = double.NaN, double grow = 0, double width = double.NaN)
        {
            var part = new Border { MinHeight = height };
            if (!double.IsNaN(width))
            {
                part.MinWidth = width;
                part.HorizontalAlignment = HorizontalAlignment.Left;
            }
            if (!double.IsNaN(basis)) FlexLinePanel.SetBasis(part, basis);
            if (grow > 0) FlexLinePanel.SetGrow(part, grow);
            return part;
        }

        private static Rect Place(UIElement child, UIElement within)
            => new Rect(child.TranslatePoint(new Point(0, 0), within), child.RenderSize);

        /// <summary>On a wide card the bar's three parts share one line:
        /// the name at its 170, the value near its 220, and the blocks
        /// with what is left. Each is as tall as the tallest.</summary>
        [Fact]
        public void OneLine_ThePartsShareTheWidth()
        {
            RunSta(() =>
            {
                var panel = new FlexLinePanel { Gap = 28, LineGap = 14 };
                var name = Part(30, 170);
                var blocks = Part(60, 420, 999);
                var value = Part(40, 220, 1);
                foreach (var part in new[] { name, blocks, value }) panel.Children.Add(part);
                panel.Measure(new Size(1366, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize));

                Assert.Equal(new Size(1366, 60), panel.DesiredSize);
                Assert.Equal(new Rect(0, 0, 170, 60), Place(name, panel));
                Rect middle = Place(blocks, panel), end = Place(value, panel);
                Assert.Equal(198, middle.X, 6);
                Assert.Equal(420 + 500 * 999 / 1000.0, middle.Width, 6);
                Assert.Equal(220 + 500 / 1000.0, end.Width, 6);
                Assert.Equal(1366, end.Right, 6);
                Assert.Equal(60, end.Height, 6);
            });
        }

        /// <summary>On a narrow card a part that does not fit starts a new
        /// line under the last, with the line gap between, and takes that
        /// line's leftover.</summary>
        [Fact]
        public void TheLinesWrap_AndEachIsAsTallAsItsTallest()
        {
            RunSta(() =>
            {
                var panel = new FlexLinePanel { Gap = 28, LineGap = 14 };
                var name = Part(30, 170);
                var blocks = Part(60, 420, 999);
                var value = Part(40, 220, 1);
                foreach (var part in new[] { name, blocks, value }) panel.Children.Add(part);
                panel.Measure(new Size(700, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize));

                // The name and the blocks on the first line, the value on
                // the second with the whole width.
                Assert.Equal(new Size(700, 60 + 14 + 40), panel.DesiredSize);
                Assert.Equal(new Rect(0, 0, 170, 60), Place(name, panel));
                Assert.Equal(new Rect(198, 0, 502, 60), Place(blocks, panel));
                Assert.Equal(new Rect(0, 74, 700, 40), Place(value, panel));

                // Narrower than the blocks' basis: every part has a line,
                // at the line's width where it grows or would not fit.
                panel.Measure(new Size(300, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize));
                Assert.Equal(new Size(300, 30 + 14 + 60 + 14 + 40), panel.DesiredSize);
                Assert.Equal(new Rect(0, 0, 170, 30), Place(name, panel));
                Assert.Equal(new Rect(0, 44, 300, 60), Place(blocks, panel));
                Assert.Equal(new Rect(0, 118, 300, 40), Place(value, panel));
            });
        }

        /// <summary>A part with no basis takes the width it asks for, and
        /// a collapsed part takes no place.</summary>
        [Fact]
        public void APartWithNoBasisIsAsWideAsItAsks_AndACollapsedOneIsNotThere()
        {
            RunSta(() =>
            {
                var panel = new FlexLinePanel { Gap = 10, LineGap = 6 };
                var asked = Part(20, width: 120);
                var gone = Part(50, 300);
                gone.Visibility = Visibility.Collapsed;
                var rest = Part(24, 100, 1);
                foreach (var part in new[] { asked, gone, rest }) panel.Children.Add(part);
                panel.Measure(new Size(400, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize));

                Assert.Equal(new Size(400, 24), panel.DesiredSize);
                Assert.Equal(new Rect(0, 0, 120, 24), Place(asked, panel));
                Assert.Equal(new Rect(130, 0, 270, 24), Place(rest, panel));

                // With no bound the panel is as wide as its parts.
                panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.Equal(new Size(230, 24), panel.DesiredSize);

                // No parts, no size.
                panel.Children.Clear();
                panel.Measure(new Size(400, 400));
                Assert.Equal(new Size(0, 0), panel.DesiredSize);
            });
        }

        private static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "the layout run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
