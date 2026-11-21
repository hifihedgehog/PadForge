using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Gyro On Buttons, on the gyro's Engage line, is a list of check boxes
    /// in the line's control column. The list drew each button inside the
    /// library's list item, 12 of padding on every side and a 1 edge, so a
    /// button took 58 where a check box takes 32, the list's 160 showed under
    /// three of them, and their boxes stood 24 right of the pickers over them
    /// (seen in the 6.0.0 screenshots).
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class GyroOnButtonsListTests
    {
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var below in Descendants<T>(child)) yield return below;
            }
        }

        private static bool Shows(DependencyObject part)
        {
            for (var at = part; at != null; at = VisualTreeHelper.GetParent(at))
                if (at is UIElement { Visibility: not Visibility.Visible }) return false;
            return true;
        }

        private static Rect On(FrameworkElement ground, FrameworkElement part)
            => part.TransformToAncestor(ground).TransformBounds(new Rect(part.RenderSize));

        [Fact]
        public void EachButtonIsACheckBoxAtTheLinesControlColumn()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var vm = new PadViewModel(0) { OutputType = PadForge.Engine.VirtualControllerType.Xbox };
                var page = new PadPage { DataContext = vm };
                var window = new Window
                {
                    Width = 1500, Height = 1400, ShowActivated = false, ShowInTaskbar = false,
                    Left = -10000, Top = -10000, Content = page,
                };
                var engage = (PadForge.Controls.FoldCard)page.FindName("GyroEngageCard");
                bool wasOpen = engage.IsExpanded;
                try
                {
                    window.Show();
                    vm.SelectedConfigTab = 8;
                    // A controller with no motion device keeps the Gyro card
                    // collapsed. The page render shows it the same way.
                    foreach (string name in new[] { "TabGyro", "GyroEngageCard" })
                        ((UIElement)page.FindName(name)).Visibility = Visibility.Visible;
                    engage.IsExpanded = true;
                    // Showing a layer fills the list. While Held reads buttons.
                    vm.GyroActivationLayer = "1";
                    vm.GyroActivationLayer = "Base";
                    vm.GyroActivationMode = "Hold";
                    window.UpdateLayout();
                    PageRenderDump.Settle(dispatcher);
                    window.UpdateLayout();

                    var list = Descendants<ListBox>(page).Single(l =>
                        BindingOperations.GetBinding(l, ItemsControl.ItemsSourceProperty)?.Path?.Path == "GyroActivationButtons");
                    Assert.True(Shows(list), "control: the Gyro On Buttons list does not show");
                    var stick = Descendants<ComboBox>(page).Single(c =>
                        BindingOperations.GetBinding(c, Selector.SelectedValueProperty)?.Path?.Path == "GyroEngageStickSide");
                    double column = On(page, stick).Left;

                    var items = Descendants<ListBoxItem>(list).Where(i => i.ActualHeight > 0).ToList();
                    Assert.True(items.Count >= 4, items.Count + " buttons drawn in the list's 160");
                    foreach (var item in items.Take(4))
                    {
                        var box = Descendants<CheckBox>(item).Single();
                        Assert.True(Math.Abs(item.ActualHeight - box.ActualHeight) < 0.5,
                            $"a button takes {item.ActualHeight}, its check box {box.ActualHeight}");
                        // The box is drawn the check box's left padding in.
                        double drawn = On(page, box).Left + box.Padding.Left;
                        Assert.True(Math.Abs(drawn - column) < 0.5, $"a button's box starts at {drawn}, the column at {column}");
                    }

                    // The first button stands in the row's first 44 as Engage
                    // Stick's picker stands in its row, the label's middle is
                    // its middle as Engage Stick's label's is its picker's,
                    // and the reset stands beside it. The label stood 2.7
                    // above the first box's middle, and the box at the row's
                    // top.
                    double Middle(Rect r) => r.Top + r.Height / 2;
                    TextBlock Label(string words) => Descendants<TextBlock>(page).Single(t => t.Text == words && Shows(t) && t.ActualWidth > 0);
                    FrameworkElement RowOf(DependencyObject part)
                    {
                        for (var at = VisualTreeHelper.GetParent(part); at != null; at = VisualTreeHelper.GetParent(at))
                            if (at is PadForge.Controls.SettingRow row) return row;
                        throw new InvalidOperationException("no row holds the part");
                    }
                    var first = Descendants<CheckBox>(items[0]).Single();
                    var strings = PadForge.Resources.Strings.Strings.Instance;
                    double inRow = Middle(On(page, first)) - On(page, RowOf(list)).Top;
                    double stickInRow = Middle(On(page, stick)) - On(page, RowOf(stick)).Top;
                    Assert.True(Math.Abs(inRow - stickInRow) < 0.5, $"the first button's middle is {inRow} into its row, Engage Stick's {stickInRow}");
                    double off = Middle(On(page, Label(strings.Pad_GyroActivation_Buttons))) - Middle(On(page, first));
                    double stickOff = Middle(On(page, Label(strings.Settings_GyroEngageStickSide))) - Middle(On(page, stick));
                    Assert.True(Math.Abs(off - stickOff) < 0.5, $"the label's middle is {off} from the first box's, Engage Stick's {stickOff}");
                    var reset = Descendants<Button>(page).Single(b =>
                        BindingOperations.GetBinding(b, ButtonBase.CommandProperty)?.Path?.Path == "ResetGyroActivationButtonsCommand");
                    Assert.True(Math.Abs(Middle(On(page, reset)) - Middle(On(page, first))) < 0.5, "the reset stands off the first button's middle");
                }
                finally
                {
                    engage.IsExpanded = wasOpen;
                    window.Close();
                }
            }, TimeSpan.FromMinutes(3), "The Gyro On Buttons test timed out.");
        }
    }
}
