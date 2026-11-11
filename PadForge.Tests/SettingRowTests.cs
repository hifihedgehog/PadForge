using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The two panels the controller page's rebuilt sections are laid out
    /// with (#574): a setting's row, which keeps its reset in view by
    /// letting one child give way, and the card that lists fold parts,
    /// which takes no room when none of its parts shows.
    /// </summary>
    public class SettingRowTests
    {
        /// <summary>WPF elements need an STA thread.</summary>
        private static void RunSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(30000), "the row test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Layout(FrameworkElement element, double width)
        {
            element.Measure(new Size(width, double.PositiveInfinity));
            element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height));
        }

        private static double LeftOf(UIElement child, UIElement panel) => child.TranslatePoint(new Point(0, 0), panel).X;

        private static double TopOf(UIElement child, UIElement panel) => child.TranslatePoint(new Point(0, 0), panel).Y;

        /// <summary>A row as the page writes one: a label in its column, a
        /// slider that asks for 280, a value, and a reset docked right.</summary>
        private static (SettingRow Row, TextBlock Label, Slider Slider, Border Value, Border Reset) SliderRow()
        {
            var row = new SettingRow { MinHeight = 44 };
            var label = new TextBlock { Width = 230, Margin = new Thickness(0, 0, 12, 0) };
            var slider = new Slider { MaxWidth = 280 };
            var value = new Border { Width = 40, Height = 20 };
            var reset = new Border { Width = 32, Height = 32 };
            DockPanel.SetDock(reset, Dock.Right);
            row.Children.Add(label);
            row.Children.Add(slider);
            row.Children.Add(value);
            row.Children.Add(reset);
            return (row, label, slider, value, reset);
        }

        // ── the width of the child that gives way ──────────────────────

        [Theory]
        [InlineData(280, 500, false, 280)]
        [InlineData(280, 120, false, 120)]
        [InlineData(280, -40, false, 0)]
        [InlineData(280, 500, true, 500)]
        [InlineData(280, -40, true, 0)]
        [InlineData(280, double.PositiveInfinity, false, 280)]
        [InlineData(280, double.PositiveInfinity, true, 280)]
        public void TheChildThatGivesWay_TakesWhatItWantsOrWhatIsLeft(double wanted, double room, bool grow, double expected)
            => Assert.Equal(expected, SettingRow.FlexWidth(wanted, room, grow));

        // ── a row with a slider ────────────────────────────────────────

        [Fact]
        public void AWideRow_GivesTheSliderItsWidth_AndPutsTheResetAtTheFarRight() => RunSta(() =>
        {
            var (row, label, slider, value, reset) = SliderRow();
            Layout(row, 800);

            Assert.Equal(0, LeftOf(label, row));
            Assert.Equal(242, LeftOf(slider, row));
            Assert.Equal(280, slider.RenderSize.Width);
            // The value follows the slider, and the reset is at the edge
            // whatever room is left between them.
            Assert.Equal(522, LeftOf(value, row));
            Assert.Equal(768, LeftOf(reset, row));
            Assert.Equal(44, row.DesiredSize.Height);
        });

        /// <summary>The label, the value and the reset need 314 of a row
        /// 500 wide, so the slider takes the 186 that is left. A DockPanel
        /// gave the slider its 280 and the reset nothing.</summary>
        [Fact]
        public void ANarrowRow_NarrowsTheSlider_AndKeepsTheResetInView() => RunSta(() =>
        {
            var (row, _, slider, value, reset) = SliderRow();
            Layout(row, 500);

            Assert.Equal(186, slider.RenderSize.Width);
            Assert.Equal(428, LeftOf(value, row));
            Assert.Equal(468, LeftOf(reset, row));
            Assert.Equal(32, reset.RenderSize.Width);
            Assert.Equal(500, row.DesiredSize.Width);
        });

        [Fact]
        public void ARowWithNoBound_AsksForEveryChildsWidth() => RunSta(() =>
        {
            var (row, _, _, _, _) = SliderRow();
            row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.Equal(242 + 280 + 40 + 32, row.DesiredSize.Width);
        });

        [Fact]
        public void ASliderWithAWidthOfItsOwn_NeverGivesWay() => RunSta(() =>
        {
            var (row, _, slider, _, reset) = SliderRow();
            slider.Width = 200;
            Layout(row, 400);

            Assert.Equal(200, slider.RenderSize.Width);
            // The reset still stands at the edge.
            Assert.Equal(368, LeftOf(reset, row));
        });

        [Fact]
        public void AGrowingRow_HandsTheSliderAllTheRoom() => RunSta(() =>
        {
            var (row, _, slider, value, reset) = SliderRow();
            slider.MaxWidth = double.PositiveInfinity;
            row.Grow = true;
            Layout(row, 800);

            // 800 less the label's 242, the value's 40 and the reset's 32.
            Assert.Equal(486, slider.RenderSize.Width);
            Assert.Equal(728, LeftOf(value, row));
            Assert.Equal(768, LeftOf(reset, row));
        });

        // ── the value's column ─────────────────────────────────────────

        [Theory]
        [InlineData(66, 40, 26)]
        [InlineData(66, 66, 0)]
        [InlineData(66, 90, 0)]
        [InlineData(66, 0, 0)]
        [InlineData(0, 40, 0)]
        public void AValueNarrowerThanItsColumn_LeavesTheDifferenceBeforeIt(double valueWidth, double value, double expected)
            => Assert.Equal(expected, SettingRow.ValuePad(valueWidth, value));

        /// <summary>Two rows of one card, a short number and a long one:
        /// both sliders end at one line and both numbers end against the
        /// reset, as the Motion and Lights boards draw a card's rows.</summary>
        [Fact]
        public void RowsWithAValueWidth_EndTheirSlidersAtOneLine_AndTheirValuesAtTheRight() => RunSta(() =>
        {
            var (shortRow, _, shortSlider, shortValue, shortReset) = SliderRow();
            var (longRow, _, longSlider, longValue, _) = SliderRow();
            longValue.Width = 60;
            foreach (var (row, slider) in new[] { (shortRow, shortSlider), (longRow, longSlider) })
            {
                slider.MaxWidth = double.PositiveInfinity;
                row.Grow = true;
                row.ValueWidth = 66;
                Layout(row, 800);
            }

            // 800 less the label's 242, the value column's 66 and the reset's 32.
            Assert.Equal(460, shortSlider.RenderSize.Width);
            Assert.Equal(460, longSlider.RenderSize.Width);
            // Each value stands at the right of the column.
            Assert.Equal(768 - 40, LeftOf(shortValue, shortRow));
            Assert.Equal(768 - 60, LeftOf(longValue, longRow));
            Assert.Equal(768, LeftOf(shortReset, shortRow));
        });

        [Fact]
        public void AValueWiderThanItsColumn_TakesItsOwnWidth() => RunSta(() =>
        {
            var (row, _, slider, value, reset) = SliderRow();
            slider.MaxWidth = double.PositiveInfinity;
            value.Width = 90;
            row.Grow = true;
            row.ValueWidth = 66;
            Layout(row, 800);

            Assert.Equal(800 - 242 - 90 - 32, slider.RenderSize.Width);
            Assert.Equal(768 - 90, LeftOf(value, row));
            Assert.Equal(768, LeftOf(reset, row));
        });

        /// <summary>A slider with no number after it runs to the reset: the
        /// column is the value's, and there is none.</summary>
        [Fact]
        public void ARowWithNoValue_LeavesNoColumnForOne() => RunSta(() =>
        {
            var (row, _, slider, value, _) = SliderRow();
            slider.MaxWidth = double.PositiveInfinity;
            value.Visibility = Visibility.Collapsed;
            row.Grow = true;
            row.ValueWidth = 66;
            Layout(row, 800);

            Assert.Equal(800 - 242 - 32, slider.RenderSize.Width);
        });

        /// <summary>A slider that stops at its own width keeps the column
        /// too: the value stands the difference away from it.</summary>
        [Fact]
        public void AValueWidth_HoldsForASliderThatDoesNotGrow() => RunSta(() =>
        {
            var (row, _, slider, value, _) = SliderRow();
            row.ValueWidth = 66;
            Layout(row, 800);

            Assert.Equal(280, slider.RenderSize.Width);
            Assert.Equal(242 + 280 + 26, LeftOf(value, row));

            // With no bound the row asks for the column as well.
            row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.Equal(242 + 280 + 66 + 32, row.DesiredSize.Width);
        });

        [Fact]
        public void ACollapsedChild_TakesNoRoom() => RunSta(() =>
        {
            var (row, _, slider, value, reset) = SliderRow();
            value.Visibility = Visibility.Collapsed;
            Layout(row, 500);

            Assert.Equal(226, slider.RenderSize.Width);
            Assert.Equal(468, LeftOf(reset, row));
        });

        /// <summary>Two children docked right: the first is outermost, as
        /// a DockPanel places them, so a reset written before a Test button
        /// stays at the edge with Test beside it.</summary>
        [Fact]
        public void TheFirstChildDockedRight_IsOutermost() => RunSta(() =>
        {
            var (row, _, _, _, reset) = SliderRow();
            var test = new Border { Width = 50, Height = 30 };
            DockPanel.SetDock(test, Dock.Right);
            row.Children.Add(test);
            Layout(row, 800);

            Assert.Equal(768, LeftOf(reset, row));
            Assert.Equal(718, LeftOf(test, row));
        });

        // ── a child marked to fill ─────────────────────────────────────

        /// <summary>A line of text beside a switch: it takes the room the
        /// label, the switch and the reset leave, wraps inside it, and the
        /// row grows as tall as the wrapped text.</summary>
        [Fact]
        public void AChildThatFills_TakesTheRoom_AndWrapsInsideIt() => RunSta(() =>
        {
            var row = new SettingRow { MinHeight = 44 };
            var label = new Border { Width = 242, Height = 20 };
            var toggle = new Border { Width = 40, Height = 22 };
            var hint = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = string.Join(" ", System.Linq.Enumerable.Repeat("routes the rumble to an audio output", 12)),
            };
            SettingRow.SetFills(hint, true);
            var reset = new Border { Width = 32, Height = 32 };
            DockPanel.SetDock(reset, Dock.Right);
            row.Children.Add(label);
            row.Children.Add(toggle);
            row.Children.Add(hint);
            row.Children.Add(reset);
            Layout(row, 600);

            Assert.Equal(282, LeftOf(hint, row));
            Assert.Equal(286, hint.RenderSize.Width);
            Assert.Equal(568, LeftOf(reset, row));
            Assert.True(row.DesiredSize.Height > 44, "the text did not wrap: the row is " + row.DesiredSize.Height + " tall");
        });

        /// <summary>A pair of meters that fills up to 420: in a wide row
        /// it stops at the 420, and the button written after it follows it
        /// and is not pushed to the row's end.</summary>
        [Fact]
        public void AChildThatFills_StopsAtItsMaxWidth_AndWhatFollowsItComesNext() => RunSta(() =>
        {
            var row = new SettingRow { MinHeight = 44 };
            var label = new Border { Width = 242, Height = 20 };
            var meters = new Border { MaxWidth = 420, Height = 6 };
            SettingRow.SetFills(meters, true);
            var test = new Border { Width = 100, Height = 36 };
            row.Children.Add(label);
            row.Children.Add(meters);
            row.Children.Add(test);

            Layout(row, 1000);
            Assert.Equal(420, meters.RenderSize.Width);
            Assert.Equal(662, LeftOf(test, row));

            // A narrower row narrows the meters and keeps the button.
            Layout(row, 500);
            Assert.Equal(158, meters.RenderSize.Width);
            Assert.Equal(400, LeftOf(test, row));

            // A row that grows its slider leaves a filling child at its
            // MaxWidth: growing is the slider's.
            row.Grow = true;
            Layout(row, 1000);
            Assert.Equal(420, meters.RenderSize.Width);
            Assert.Equal(662, LeftOf(test, row));
        });

        /// <summary>A picker in a row that grows: 260 wide while the row
        /// has the room, narrower when it has not, with the reset in view
        /// either way.</summary>
        [Fact]
        public void APickerThatFills_StaysAtItsWidth_AndNarrowsInANarrowRow() => RunSta(() =>
        {
            var row = new SettingRow { MinHeight = 44, Grow = true, ValueWidth = 66 };
            var label = new Border { Width = 182, Height = 20 };
            var picker = new Border { MaxWidth = 260, Height = 32 };
            SettingRow.SetFills(picker, true);
            var reset = new Border { Width = 32, Height = 32 };
            DockPanel.SetDock(reset, Dock.Right);
            row.Children.Add(label);
            row.Children.Add(picker);
            row.Children.Add(reset);

            Layout(row, 800);
            Assert.Equal(182, LeftOf(picker, row));
            Assert.Equal(260, picker.RenderSize.Width);
            Assert.Equal(768, LeftOf(reset, row));

            Layout(row, 388);
            Assert.Equal(174, picker.RenderSize.Width);
            Assert.Equal(356, LeftOf(reset, row));
        });

        // ── the card that lists fold parts ─────────────────────────────

        [Fact]
        public void TheListsHeight_IsItsParts_TheirHairlines_ItsEdgeAndItsGap()
        {
            Assert.Equal(0, FoldList.HeightFor(Array.Empty<double>(), 16));
            Assert.Equal(0, FoldList.HeightFor(null, 16));
            // One part: its 48, the edge over and under it, the gap.
            Assert.Equal(48 + 2 + 16, FoldList.HeightFor(new[] { 48.0 }, 16));
            // Three parts: two hairlines between them.
            Assert.Equal(48 + 48 + 120 + 2 + 2 + 16, FoldList.HeightFor(new[] { 48.0, 48.0, 120.0 }, 16));
        }

        [Fact]
        public void TheList_StacksTheShownParts_OneHairlineApart_InsideItsEdge() => RunSta(() =>
        {
            var list = new FoldList { Gap = 16 };
            var first = new Border { Height = 48 };
            var hidden = new Border { Height = 48, Visibility = Visibility.Collapsed };
            var last = new Border { Height = 60 };
            list.Children.Add(first);
            list.Children.Add(hidden);
            list.Children.Add(last);
            Layout(list, 400);

            // The hidden part takes no row and no hairline.
            Assert.Equal(48 + 60 + 1 + 2 + 16, list.DesiredSize.Height);
            Assert.Equal(1, LeftOf(first, list));
            Assert.Equal(1, TopOf(first, list));
            Assert.Equal(398, first.RenderSize.Width);
            Assert.Equal(1 + 48 + 1, TopOf(last, list));
        });

        /// <summary>With no part showing the list takes no room, its gap
        /// included, so a group whose folded features the device lacks
        /// leaves no empty card and no hole between cards.</summary>
        [Fact]
        public void AListWithNothingShowing_TakesNoRoomAtAll() => RunSta(() =>
        {
            var list = new FoldList { Gap = 16 };
            list.Children.Add(new Border { Height = 48, Visibility = Visibility.Collapsed });
            list.Children.Add(new Border { Height = 48, Visibility = Visibility.Collapsed });
            Layout(list, 400);
            Assert.Equal(new Size(0, 0), list.DesiredSize);

            // A part that shows again brings the card back.
            ((Border)list.Children[1]).Visibility = Visibility.Visible;
            Layout(list, 400);
            Assert.Equal(48 + 2 + 16, list.DesiredSize.Height);
        });
    }
}
