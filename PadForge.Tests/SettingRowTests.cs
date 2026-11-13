using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The panels the controller page's rebuilt sections are laid out
    /// with (#574): a setting's row, which keeps its reset in view by
    /// letting one child give way, a card's head row, whose two parts
    /// stack in a narrow window, and the card that lists fold parts, which
    /// takes no room when none of its parts shows.
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

        /// <summary>A row with no reach of its own, for the tests of what a
        /// row does with its whole width.</summary>
        private static SettingRow Unbounded(SettingRow row)
        {
            SettingRow.SetReach(row, double.PositiveInfinity);
            return row;
        }

        /// <summary>A row as the page writes one: a label in its column, a
        /// slider that asks for 280, a value, and a reset docked right. It
        /// has no reach unless the test asks for the page's own.</summary>
        private static (SettingRow Row, TextBlock Label, Slider Slider, Border Value, Border Reset) SliderRow(bool withReach = false)
        {
            var row = new SettingRow { MinHeight = 44 };
            if (!withReach) Unbounded(row);
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

        // ── the row's reach ────────────────────────────────────────────

        [Theory]
        // Narrower than the reach: the row's own width.
        [InlineData(500, 660, 314, 500)]
        [InlineData(660, 660, 314, 660)]
        // Wider: the reach.
        [InlineData(1300, 660, 314, 660)]
        // Parts that need more than the reach have what they need, and
        // never more than the row.
        [InlineData(1300, 660, 760, 760)]
        [InlineData(700, 660, 760, 700)]
        // No reach, or no bound: the width.
        [InlineData(1300, double.PositiveInfinity, 314, 1300)]
        [InlineData(1300, 0, 314, 1300)]
        [InlineData(double.PositiveInfinity, 660, 314, double.PositiveInfinity)]
        public void ARowLaysItsPartsOutWithinItsReach(double width, double reach, double needed, double room)
            => Assert.Equal(room, SettingRow.RoomFor(width, reach, needed));

        /// <summary>On a wide card a row's reset stands beside its value,
        /// 660 from the row's start, and not at the card's far end: at
        /// 1300 it stood 746 from the value (the Rumble section on a wide
        /// window, 2026-11-13).</summary>
        [Fact]
        public void InAWideCard_TheResetStandsBesideTheValue() => RunSta(() =>
        {
            Assert.Equal(660.0, SettingRow.DefaultReach);
            var (row, label, slider, value, reset) = SliderRow(withReach: true);
            Assert.Equal(660.0, SettingRow.GetReach(row));
            Layout(row, 1300);
            Assert.Equal(0, LeftOf(label, row));
            Assert.Equal(242, LeftOf(slider, row));
            Assert.Equal(280, slider.RenderSize.Width);
            Assert.Equal(522, LeftOf(value, row));
            Assert.Equal(628, LeftOf(reset, row));

            // A slider that grows runs to the value at the reach's end.
            slider.MaxWidth = double.PositiveInfinity;
            row.Grow = true;
            Layout(row, 1300);
            Assert.Equal(660 - 242 - 40 - 32, slider.RenderSize.Width);
            Assert.Equal(588, LeftOf(value, row));
            Assert.Equal(628, LeftOf(reset, row));

            // A card narrower than the reach: the row's own width, as before.
            Layout(row, 500);
            Assert.Equal(468, LeftOf(reset, row));

            // A page sets the reach for every row inside it.
            var page = new Border { Child = row };
            SettingRow.SetReach(page, 820);
            row.ClearValue(SettingRow.ReachProperty);
            Assert.Equal(820.0, SettingRow.GetReach(row));
            Layout(page, 1300);
            Assert.Equal(788, LeftOf(reset, row));
            SettingRow.SetReach(page, double.PositiveInfinity);
            Layout(page, 1300);
            Assert.Equal(1268, LeftOf(reset, row));
        });

        /// <summary>A label that fills: it takes the reach's room, and the
        /// switch after it stands against the reset. A row whose parts
        /// need more than the reach has what they need.</summary>
        [Fact]
        public void WithinTheReach_AFillingLabelPutsTheSwitchAgainstTheReset() => RunSta(() =>
        {
            var row = new SettingRow { MinHeight = 44 };
            var label = new TextBlock { Text = "Hide from Games", TextWrapping = TextWrapping.Wrap };
            SettingRow.SetFills(label, true);
            var toggle = new Border { Width = 40, Height = 22 };
            var reset = new Border { Width = 32, Height = 32 };
            DockPanel.SetDock(reset, Dock.Right);
            row.Children.Add(label);
            row.Children.Add(toggle);
            row.Children.Add(reset);
            Layout(row, 1300);
            Assert.Equal(588, label.RenderSize.Width);
            Assert.Equal(588, LeftOf(toggle, row));
            Assert.Equal(628, LeftOf(reset, row));

            // Parts with no child that gives way, wider than the reach:
            // the reset follows them.
            var wide = new SettingRow { MinHeight = 44 };
            var name = new Border { Width = 242, Height = 20 };
            var boxes = new Border { Width = 430, Height = 30 };
            var end = new Border { Width = 32, Height = 32 };
            DockPanel.SetDock(end, Dock.Right);
            wide.Children.Add(name);
            wide.Children.Add(boxes);
            wide.Children.Add(end);
            Layout(wide, 1300);
            Assert.Equal(242, LeftOf(boxes, wide));
            Assert.Equal(672, LeftOf(end, wide));
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
            var row = Unbounded(new SettingRow { MinHeight = 44 });
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
            var row = Unbounded(new SettingRow { MinHeight = 44 });
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
            var row = Unbounded(new SettingRow { MinHeight = 44, Grow = true, ValueWidth = 66 });
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

        // ── a row whose controls stand at its end ──────────────────────

        /// <summary>What a line of text takes on one line, its margin included.</summary>
        private static double Natural(string text, Thickness margin)
        {
            var probe = new TextBlock { Text = text, Margin = margin };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return probe.DesiredSize.Width;
        }

        /// <summary>The height of one line of text.</summary>
        private static double OneLine()
        {
            var probe = new TextBlock { Text = "Ag" };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return probe.DesiredSize.Height;
        }

        /// <summary>The Settings board's row: the label takes the room the
        /// row has left, the control stands at the row's end and the reset
        /// after it, 12 on. The row takes its whole width whatever the
        /// reach is. A row with no reset ends on its control.</summary>
        [Fact]
        public void WithEndsSet_TheLabelTakesTheRoom_AndTheControlStandsAtTheRowsEnd() => RunSta(() =>
        {
            Assert.Equal(12.0, SettingRow.EndGap);
            var row = new SettingRow { MinHeight = 44 };
            Assert.False(SettingRow.GetEnds(row));
            // Set on what holds the row, every row inside reads it.
            var host = new StackPanel();
            host.Children.Add(row);
            SettingRow.SetEnds(host, true);
            Assert.True(SettingRow.GetEnds(row));

            var label = new TextBlock { Text = "Start at Login", Margin = new Thickness(0, 0, 12, 0), TextWrapping = TextWrapping.Wrap };
            var toggle = new Border { Width = 40, Height = 22 };
            var reset = new Border { Width = 32, Height = 32 };
            DockPanel.SetDock(reset, Dock.Right);
            row.Children.Add(label);
            row.Children.Add(toggle);
            row.Children.Add(reset);
            Layout(host, 780);
            Assert.Equal(780, row.RenderSize.Width);
            Assert.Equal(748, LeftOf(reset, row));
            Assert.Equal(748 - 12 - 40, LeftOf(toggle, row));
            Assert.Equal(new Rect(0, 0, 696, 44), LayoutInformation.GetLayoutSlot(label));

            // A narrow row: the label wraps in what the control and the
            // reset leave.
            Assert.Equal(OneLine(), label.DesiredSize.Height, 3);
            Layout(host, 40 + 12 + 32 + 50);
            Assert.Equal(50, LayoutInformation.GetLayoutSlot(label).Width);
            Assert.True(label.DesiredSize.Height > OneLine() + 1, "the label did not wrap: " + label.DesiredSize.Height);
            Assert.Equal(50, LeftOf(toggle, row));

            // No reset: the control ends the row, with no gap after it.
            row.Children.Remove(reset);
            Layout(host, 780);
            Assert.Equal(740, LeftOf(toggle, row));
            Assert.Equal(740, LayoutInformation.GetLayoutSlot(label).Width);

            // A part that is collapsed takes no room. Another width, since
            // a panel measured twice at one width is not measured again.
            row.Children.Add(reset);
            toggle.Visibility = Visibility.Collapsed;
            Layout(host, 800);
            Assert.Equal(768, LeftOf(reset, row));
            Assert.Equal(768, LayoutInformation.GetLayoutSlot(label).Width);
        });

        /// <summary>What a setting reports stands aside: before the switch
        /// it was written after, so the switches of a card keep one column
        /// whatever each row reports. It is the part that gives way in a
        /// narrow row, down to its least, and then the label wraps.</summary>
        [Fact]
        public void WithEndsSet_AStatusStandsBeforeTheSwitch_AndGivesWayFirst() => RunSta(() =>
        {
            Assert.Equal(96.0, SettingRow.FillLeast);
            const string LabelText = "Read Analog Keyboards", StatusText = "No supported analog keyboard found.";
            var margin = new Thickness(0, 0, 12, 0);
            var row = new SettingRow { MinHeight = 44 };
            SettingRow.SetEnds(row, true);
            var label = new TextBlock { Text = LabelText, Margin = margin, TextWrapping = TextWrapping.Wrap };
            var toggle = new Border { Width = 40, Height = 22 };
            var status = new TextBlock { Text = StatusText, Margin = margin, TextWrapping = TextWrapping.Wrap };
            SettingRow.SetFills(status, true);
            SettingRow.SetAside(status, true);
            var reset = new Border { Width = 32, Height = 32 };
            DockPanel.SetDock(reset, Dock.Right);
            foreach (UIElement part in new UIElement[] { label, toggle, status, reset }) row.Children.Add(part);
            double labelWidth = Natural(LabelText, margin), statusWidth = Natural(StatusText, margin);
            Assert.True(statusWidth > 160 && labelWidth > 80, "the status is " + statusWidth + " wide and the label " + labelWidth);

            // Room for all: the switch against the reset, the status
            // before it at the width of its words, the label the rest.
            Layout(row, 780);
            double switchAt = 780 - 32 - 12 - 40;
            Assert.Equal(switchAt, LeftOf(toggle, row), 3);
            Assert.Equal(switchAt - statusWidth, LeftOf(status, row), 3);
            Assert.Equal(switchAt - statusWidth, LayoutInformation.GetLayoutSlot(label).Width, 3);
            Assert.Equal(44, row.RenderSize.Height);

            // Written after the switch and not aside, it stands after it,
            // and the switch moves with its words.
            SettingRow.SetAside(status, false);
            Layout(row, 780);
            Assert.Equal(switchAt - statusWidth, LeftOf(toggle, row), 3);
            Assert.Equal(switchAt - statusWidth + 40, LeftOf(status, row), 3);
            SettingRow.SetAside(status, true);

            // Short of room by 60: the status gives it and wraps, and the
            // label keeps its one line.
            double others = 40 + 12 + 32;
            Layout(row, labelWidth + statusWidth - 60 + others);
            Assert.Equal(statusWidth - 60, LayoutInformation.GetLayoutSlot(status).Width, 3);
            Assert.Equal(labelWidth, LayoutInformation.GetLayoutSlot(label).Width, 3);
            Assert.True(status.DesiredSize.Height > OneLine() + 1, "the status did not wrap");
            Assert.Equal(OneLine(), label.DesiredSize.Height, 3);
            Assert.Equal(labelWidth + statusWidth - 60, LeftOf(toggle, row), 3);

            // At its least the label wraps in what is left.
            Layout(row, 96 + 70 + others);
            Assert.Equal(96, LayoutInformation.GetLayoutSlot(status).Width, 3);
            Assert.Equal(70, LayoutInformation.GetLayoutSlot(label).Width, 3);
            Assert.Equal(166, LeftOf(toggle, row), 3);
        });

        /// <summary>A part that fills with a width of its own to stop at (a
        /// path's field) takes that width at the row's end while there is
        /// room, and a picker with none is as wide as its content.</summary>
        [Fact]
        public void WithEndsSet_AFieldTakesItsMaxWidth_AndAPickerItsContent() => RunSta(() =>
        {
            var row = new SettingRow { MinHeight = 44 };
            SettingRow.SetEnds(row, true);
            var label = new Border { Width = 120, Height = 20 };
            var field = new Border { MaxWidth = 420, MinWidth = 200, Height = 30 };
            SettingRow.SetFills(field, true);
            var browse = new Border { Width = 80, Height = 30 };
            var reset = new Border { Width = 32, Height = 32 };
            DockPanel.SetDock(reset, Dock.Right);
            foreach (UIElement part in new UIElement[] { label, field, browse, reset }) row.Children.Add(part);

            Layout(row, 900);
            Assert.Equal(900 - 32 - 12 - 80, LeftOf(browse, row));
            Assert.Equal(420, LayoutInformation.GetLayoutSlot(field).Width);
            Assert.Equal(900 - 32 - 12 - 80 - 420, LeftOf(field, row));

            // Narrower: the field gives way, never under its own least.
            Layout(row, 120 + 300 + 80 + 12 + 32);
            Assert.Equal(300, LayoutInformation.GetLayoutSlot(field).Width);
            Layout(row, 120 + 150 + 80 + 12 + 32);
            Assert.Equal(200, LayoutInformation.GetLayoutSlot(field).Width);

            // A picker that fills with no width to stop at: its content's.
            var picker = new Border { MinWidth = 210, Height = 34, Child = new Border { Width = 236 } };
            SettingRow.SetFills(picker, true);
            row.Children.Clear();
            foreach (UIElement part in new UIElement[] { label, picker, reset }) row.Children.Add(part);
            Layout(row, 900);
            Assert.Equal(236, LayoutInformation.GetLayoutSlot(picker).Width);
            Assert.Equal(900 - 32 - 12 - 236, LeftOf(picker, row));
        });

        /// <summary>What a part draws, a pixel as 0xAARRGGBB.</summary>
        private static uint[] Pixels(Visual part, int width, int height)
        {
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(part);
            var pixels = new uint[width * height];
            bitmap.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }

        /// <summary>The line over a row is its item's: one pixel thick, the
        /// item's whole width, over the item's first row of pixels and not
        /// its second. An item with no rule draws none.</summary>
        [Fact]
        public void ASettingItem_DrawsItsHairlineOverItself_ItsWholeWidth() => RunSta(() =>
        {
            const uint Red = 0xFFFF0000, White = 0xFFFFFFFF;
            Assert.Equal(1.0, SettingItem.RuleHeight);
            var item = new SettingItem { Rule = Brushes.Red };
            item.Children.Add(new Border { Height = 20 });
            var host = new Border { Background = Brushes.White, Child = item };
            Layout(host, 60);
            var pixels = Pixels(host, 60, 20);
            for (int x = 0; x < 60; x++)
            {
                Assert.Equal(Red, pixels[x]);
                Assert.Equal(White, pixels[60 + x]);
            }

            item.Rule = null;
            item.UpdateLayout();
            Assert.All(Pixels(host, 60, 20), pixel => Assert.Equal(White, pixel));
        });

        // ── a card's head row ──────────────────────────────────────────

        [Theory]
        [InlineData(800, 340, 220, true)]
        [InlineData(560, 340, 220, true)]
        [InlineData(559, 340, 220, false)]
        [InlineData(300, 340, 220, false)]
        [InlineData(double.PositiveInfinity, 340, 220, true)]
        public void TheHeadsParts_ShareALine_WhileTheLeadingOneKeepsItsLeastWidth(double width, double trailing, double leadingMin, bool expected)
            => Assert.Equal(expected, HeadRowPanel.SharesLine(width, trailing, leadingMin));

        /// <summary>The Buttons card's head: the switch and what wraps
        /// with it lead, the list's tools and Map All trail. With room the
        /// trailing part stands at the row's end and the leading part wraps
        /// in the rest. Without it each has the whole row, the trailing
        /// part under the leading one.</summary>
        [Fact]
        public void TheHead_PutsItsTrailingPartAtTheEnd_AndStacksInANarrowRow() => RunSta(() =>
        {
            var head = new HeadRowPanel { LeadingMinWidth = 220, Gap = 8 };
            var leading = new WrapPanel { Orientation = Orientation.Horizontal };
            var toggle = new Border { Width = 190, Height = 36 };
            var hint = new Border { Width = 300, Height = 20 };
            leading.Children.Add(toggle);
            leading.Children.Add(hint);
            var trailing = new WrapPanel { Orientation = Orientation.Horizontal };
            trailing.Children.Add(new Border { Width = 240, Height = 40 });
            trailing.Children.Add(new Border { Width = 100, Height = 36 });
            head.Children.Add(leading);
            head.Children.Add(trailing);

            // Wide: one line. The leading part has the 660 left of 1000.
            Layout(head, 1000);
            Assert.True(head.IsOnLine);
            Assert.Equal(660, LeftOf(trailing, head));
            Assert.Equal(660, leading.RenderSize.Width);
            Assert.Equal(40, head.DesiredSize.Height);
            Assert.Equal(190, LeftOf(hint, head));

            // Less room: still one line, and the hint wraps under the
            // switch inside the leading part's 260.
            Layout(head, 600);
            Assert.True(head.IsOnLine);
            Assert.Equal(260, LeftOf(trailing, head));
            Assert.Equal(0, LeftOf(hint, head));
            Assert.Equal(36, TopOf(hint, head));

            // Narrow: 540 would leave the leading part 200, under its 220.
            // The parts stack, each with the whole row.
            Layout(head, 540);
            Assert.False(head.IsOnLine);
            Assert.Equal(0, LeftOf(trailing, head));
            Assert.Equal(36 + 8, TopOf(trailing, head));
            Assert.Equal(540, trailing.RenderSize.Width);
            Assert.Equal(36 + 8 + 40, head.DesiredSize.Height);
            Assert.Equal(190, LeftOf(hint, head));

            // With no trailing part showing, the leading one has the row.
            trailing.Visibility = Visibility.Collapsed;
            Layout(head, 540);
            Assert.True(head.IsOnLine);
            Assert.Equal(540, leading.RenderSize.Width);
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
