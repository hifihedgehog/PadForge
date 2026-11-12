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

        /// <summary>The parts of a line stand from its start, the gap
        /// between them. When the line's ends are apart, the end parts that
        /// close it stand against its end, the last flush with it. They
        /// stay in order when the line has nothing left over, when it has
        /// no bound, and when another part comes after them.</summary>
        [Fact]
        public void EndPartsStandAgainstTheLinesEnd_WhenItsEndsAreApart()
        {
            var widths = new double[] { 200, 138, 42 };
            var atEnd = new[] { false, false, true };
            Assert.Equal(new double[] { 0, 200, 338 }, FlexLinePanel.Starts(widths, atEnd, 500, 0, false));
            Assert.Equal(new double[] { 0, 200, 458 }, FlexLinePanel.Starts(widths, atEnd, 500, 0, true));
            Assert.Equal(new double[] { 0, 200, 338 }, FlexLinePanel.Starts(widths, atEnd, 380, 0, true));
            Assert.Equal(new double[] { 0, 200, 338 }, FlexLinePanel.Starts(widths, atEnd, 300, 0, true));
            Assert.Equal(new double[] { 0, 200, 338 }, FlexLinePanel.Starts(widths, atEnd, double.PositiveInfinity, 0, true));

            // Two end parts close the line, the gap between them.
            Assert.Equal(new double[] { 0, 110, 406, 458 },
                FlexLinePanel.Starts(new double[] { 100, 50, 42, 42 }, new[] { false, false, true, true }, 500, 10, true));
            // An end part with another part after it is in order.
            Assert.Equal(new double[] { 0, 110, 162 },
                FlexLinePanel.Starts(new double[] { 100, 42, 50 }, new[] { false, true, false }, 500, 10, true));
            Assert.Empty(FlexLinePanel.Starts(Array.Empty<double>(), Array.Empty<bool>(), 500, 10, true));
        }

        /// <summary>A row of a setting under the picture's bar: at least
        /// 44 tall, the label and the control from its start, and the reset
        /// at its end. A reset that does not fit the row's line stands at
        /// the end of the next. Where the row's ends are not apart, as in
        /// the list, the reset follows the control.</summary>
        [Fact]
        public void ARowOfASetting_IsAsTallAsItsLeast_AndItsResetStandsAtItsEnd()
        {
            RunSta(() =>
            {
                var row = new FlexLinePanel { Gap = 0, LineGap = 0, MinLineHeight = 44, EndsApart = true };
                var label = Part(20, width: 200);
                var control = Part(34, width: 138);
                var reset = Part(32, width: 42);
                FlexLinePanel.SetAtEnd(reset, true);
                foreach (var part in new[] { label, control, reset }) row.Children.Add(part);

                row.Measure(new Size(500, double.PositiveInfinity));
                row.Arrange(new Rect(row.DesiredSize));
                Assert.Equal(new Size(500, 44), row.DesiredSize);
                Assert.Equal(new Rect(0, 0, 200, 44), Place(label, row));
                Assert.Equal(new Rect(200, 0, 138, 44), Place(control, row));
                Assert.Equal(new Rect(458, 0, 42, 44), Place(reset, row));

                // The list's rows: 40 tall, the reset after the control.
                row.EndsApart = false;
                row.MinLineHeight = 40;
                row.Measure(new Size(500, double.PositiveInfinity));
                row.Arrange(new Rect(row.DesiredSize));
                Assert.Equal(new Size(500, 40), row.DesiredSize);
                Assert.Equal(new Rect(338, 0, 42, 40), Place(reset, row));

                // Too narrow for the three: the reset has the next line, at
                // its end.
                row.EndsApart = true;
                row.MinLineHeight = 44;
                row.Measure(new Size(360, double.PositiveInfinity));
                row.Arrange(new Rect(row.DesiredSize));
                Assert.Equal(new Size(360, 88), row.DesiredSize);
                Assert.Equal(new Rect(200, 0, 138, 44), Place(control, row));
                Assert.Equal(new Rect(318, 44, 42, 44), Place(reset, row));

                // A part that grows takes what the row has left over, and
                // the reset is at the end with nothing between: a slider
                // that fills its row, its number, and the reset.
                var filled = new FlexLinePanel { Gap = 0, LineGap = 0, MinLineHeight = 44, EndsApart = true };
                var name = Part(20, width: 200);
                var slider = Part(20, 60, 1);
                var number = Part(20, width: 30);
                var end = Part(32, width: 42);
                FlexLinePanel.SetAtEnd(end, true);
                foreach (var part in new[] { name, slider, number, end }) filled.Children.Add(part);
                filled.Measure(new Size(500, double.PositiveInfinity));
                filled.Arrange(new Rect(filled.DesiredSize));
                Assert.Equal(new Rect(200, 0, 228, 44), Place(slider, filled));
                Assert.Equal(new Rect(428, 0, 30, 44), Place(number, filled));
                Assert.Equal(new Rect(458, 0, 42, 44), Place(end, filled));
            });
        }

        /// <summary>A part that says how wide it was asked to be, and is
        /// cut short when that is less than its 54.5.</summary>
        private sealed class Text : FrameworkElement
        {
            public readonly System.Collections.Generic.List<double> Asked = new();

            protected override Size MeasureOverride(Size availableSize)
            {
                Asked.Add(availableSize.Width);
                return new Size(availableSize.Width >= 54.5 ? 54.5 : 30, 20);
            }
        }

        /// <summary>A part that keeps the width it asked for is measured
        /// once, with no bound. Layout rounding takes part of a pixel off
        /// the width a part asks for, and a text measured again at that
        /// width was cut short where it fits. A part that grows, and one
        /// alone on a line too narrow for it, are measured at the width
        /// they get.</summary>
        [Fact]
        public void APartThatKeepsItsOwnWidthIsMeasuredOnce_WithNoBound()
        {
            RunSta(() =>
            {
                var line = new FlexLinePanel { Gap = 8, LineGap = 8 };
                var text = new Text();
                var other = new Text();
                line.Children.Add(text);
                line.Children.Add(other);
                line.Measure(new Size(400, double.PositiveInfinity));
                line.Arrange(new Rect(line.DesiredSize));
                Assert.Equal(new[] { double.PositiveInfinity }, text.Asked);
                Assert.Equal(new[] { double.PositiveInfinity }, other.Asked);
                Assert.Equal(new Rect(0, 0, 54.5, 20), Place(text, line));
                Assert.Equal(new Rect(62.5, 0, 54.5, 20), Place(other, line));

                // One that grows is measured at its share.
                FlexLinePanel.SetGrow(other, 1);
                line.Measure(new Size(400, double.PositiveInfinity));
                Assert.Equal(new[] { double.PositiveInfinity, 337.5 }, other.Asked);
                Assert.Equal(new[] { double.PositiveInfinity }, text.Asked);

                // One alone on a line too narrow for it is measured at the
                // line's width, and is cut short.
                var narrow = new FlexLinePanel();
                var cut = new Text();
                narrow.Children.Add(cut);
                narrow.Measure(new Size(40, double.PositiveInfinity));
                Assert.Equal(new[] { double.PositiveInfinity, 40 }, cut.Asked);
                Assert.Equal(30, cut.DesiredSize.Width);
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
