using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A picker of one input says what it holds inside its own box, as the
    /// page's other pickers do ("None (Off)" in Trigger Routing's Source).
    /// With nothing saved the box read blank and "Unassigned" stood under
    /// it, and a gate read "Button 5" beside a picker that always read blank
    /// (seen in the 6.0.0 screenshots: Aim Engage Button, the trigger
    /// Activators, Only While and And While). One mark for one state: the
    /// box reads "Unassigned" or the saved input, and the line under it
    /// names a picked input's device only.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class InputPickerWordsTests
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

        private static bool Inside(Rect outer, Rect inner)
            => inner.Left >= outer.Left - 0.5 && inner.Right <= outer.Right + 0.5
               && inner.Top >= outer.Top - 0.5 && inner.Bottom <= outer.Bottom + 0.5;

        /// <summary>The words drawn on the ground, outside any picker's
        /// dropdown, that read <paramref name="words"/>.</summary>
        private static List<TextBlock> Drawn(FrameworkElement ground, string words)
            => Descendants<TextBlock>(ground).Where(t => t.Text == words && Shows(t) && t.ActualWidth > 0).ToList();

        [Fact]
        public void AnInputPickerSaysWhatItHoldsInsideItsBox()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                string unassigned = Strings.Instance.Common_Unassigned;
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
                    // A controller with no motion device keeps the Gyro
                    // card collapsed. The page render shows it the same way.
                    foreach (string name in new[] { "TabGyro", "GyroEngageCard" })
                        ((UIElement)page.FindName(name)).Visibility = Visibility.Visible;
                    engage.IsExpanded = true;
                    window.UpdateLayout();
                    PageRenderDump.Settle(dispatcher);
                    window.UpdateLayout();

                    var pickers = Descendants<ComboBox>(page).ToList();
                    var picker = pickers.Single(c =>
                        System.Windows.Data.BindingOperations.GetBinding(c, System.Windows.Controls.Primitives.Selector.SelectedItemProperty)?.Path?.Path == "GyroAimEngageSelectedInput");
                    Assert.True(Shows(picker), "control: the Aim Engage Button picker does not show");
                    var box = On(page, picker);

                    // The row is drawn as Engage Stick's over it is: the box
                    // starts where that picker does, and the label's middle is
                    // the box's middle, as that row's label is its box's. The
                    // labels stood above their box's middle and the trigger
                    // Activators' boxes 12 left of their column (seen in the
                    // 6.0.0 screenshots).
                    var stick = pickers.Single(c =>
                        System.Windows.Data.BindingOperations.GetBinding(c, System.Windows.Controls.Primitives.Selector.SelectedValueProperty)?.Path?.Path == "GyroEngageStickSide");
                    var stickBox = On(page, stick);
                    double Middle(Rect r) => r.Top + r.Height / 2;
                    Rect LabelOf(string words) => On(page, Drawn(page, words).Single());
                    Assert.True(Math.Abs(box.Left - stickBox.Left) < 0.5, $"the box starts at {box.Left}, Engage Stick's at {stickBox.Left}");
                    double stickOff = Middle(LabelOf(Strings.Instance.Settings_GyroEngageStickSide)) - Middle(stickBox);
                    double off = Middle(LabelOf(Strings.Instance.Settings_GyroAimEngageButton)) - Middle(box);
                    Assert.True(Math.Abs(off - stickOff) < 0.5, $"the label's middle is {off} from its box's, Engage Stick's {stickOff}");

                    // Nothing saved: the box reads Unassigned, and no line
                    // under it says so again.
                    var said = Drawn(page, unassigned).Where(t => Math.Abs(On(page, t).Top - box.Top) < 120).ToList();
                    Assert.True(said.Count == 1, $"Unassigned is drawn {said.Count} times by the picker");
                    Assert.True(Inside(box, On(page, said[0])), $"Unassigned stands at {On(page, said[0])}, outside the box at {box}");

                    // A saved input the list does not hold (its device is
                    // away): the box reads the input, not Unassigned.
                    vm.GyroAimEngageButton = "Button 5";
                    window.UpdateLayout();
                    PageRenderDump.Settle(dispatcher);
                    window.UpdateLayout();
                    Assert.Null(vm.GyroAimEngageSelectedInput);
                    Assert.DoesNotContain(Drawn(page, unassigned), t => Math.Abs(On(page, t).Top - box.Top) < 120);
                    var saved = Descendants<TextBlock>(picker.Parent as DependencyObject ?? picker)
                        .Where(t => Shows(t) && t.Text.Length > 0 && Inside(box, On(page, t))).Select(t => t.Text).ToList();
                    Assert.Contains(saved, t => t.Contains('5'));
                }
                finally
                {
                    engage.IsExpanded = wasOpen;
                    window.Close();
                }
            }, TimeSpan.FromMinutes(3), "The input picker test timed out.");
        }

        [Fact]
        public void AGateReadsItsInputInsideItsPicker()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var vm = new PadViewModel(0);
                var page = new PadPage { DataContext = vm };
                var template = (DataTemplate)page.FindResource("SourceShaping");
                var source = new MappingSourceItem { Descriptor = "Axis 3", GateDescriptor = "Button 4" };
                var line = new ContentControl { ContentTemplate = template, Content = source };
                var host = new UserControl { Content = line, DataContext = vm, Resources = page.Resources };
                var window = new Window
                {
                    Width = 1200, Height = 400, ShowActivated = false, ShowInTaskbar = false,
                    Left = -10000, Top = -10000, Content = host,
                };
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    PageRenderDump.Settle(dispatcher);
                    window.UpdateLayout();
                    var picker = Descendants<ComboBox>(line).First(c =>
                        System.Windows.Data.BindingOperations.GetBinding(c, System.Windows.Controls.Primitives.Selector.SelectedItemProperty)?.Path?.Path == "GatePick");
                    Assert.True(Shows(picker), "control: the gate's picker does not show");
                    var box = On(host, picker);
                    var words = Descendants<TextBlock>(line).Where(t => t.Text == source.GateText && Shows(t) && t.ActualWidth > 0).ToList();
                    Assert.True(words.Count == 1, $"{source.GateText} is drawn {words.Count} times");
                    Assert.True(Inside(box, On(host, words[0])), $"{source.GateText} stands at {On(host, words[0])}, outside the picker at {box}");
                }
                finally
                {
                    window.Close();
                }
            }, TimeSpan.FromMinutes(3), "The gate picker test timed out.");
        }
    }
}
