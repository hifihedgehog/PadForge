using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// One on/off setting is a switch, and a check box is for a pick from
    /// a list, as the boards draw them: every on/off setting of the
    /// controller boards is a switch (Controller-Motion.dc.html lines 108
    /// to 116, Controller-Rumble.dc.html lines 106 to 121), and the only
    /// check boxes are the items of a list (Dialogs-Links.dc.html lines
    /// 128 and 129). Allow Flick on Engage and Keep Controller Awake's
    /// motion setting were square check boxes among switches (seen in the
    /// 6.0.0 screenshots). The Gesture Buttons are a pick from a list and
    /// stay check boxes.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class OnOffSettingsAreSwitchesTests
    {
        private static IEnumerable<DependencyObject> Logical(DependencyObject root)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is not DependencyObject element) continue;
                yield return element;
                foreach (var below in Logical(element)) yield return below;
            }
        }

        private static string BoundTo(CheckBox box)
            => BindingOperations.GetBinding(box, ToggleButton_IsChecked)?.Path?.Path ?? string.Empty;

        private static readonly DependencyProperty ToggleButton_IsChecked = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

        [Fact]
        public void EveryLoneOnOffSettingOnTheControllerPageIsASwitch()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var vm = new PadViewModel(0);
                var page = new PadPage { DataContext = vm };
                var switchStyle = (Style)app.FindResource("SwitchCheckBox");

                var boxes = Logical(page).OfType<CheckBox>().ToList();
                Assert.True(boxes.Count > 40, boxes.Count + " check boxes on the page");
                var lone = boxes.Where(b => LogicalTreeHelper.GetParent(b) is Panel p
                    && p.Children.OfType<CheckBox>().Count() == 1).ToList();
                Assert.Contains(lone, b => BoundTo(b) == "FlickOnEngage");
                Assert.Contains(lone, b => BoundTo(b) == "KeepAwakeMotion");
                var squares = lone.Where(b => !ReferenceEquals(b.Style, switchStyle)
                    && (b.Style == null || !ReferenceEquals(b.Style.BasedOn, switchStyle)))
                    .Select(b => BoundTo(b)).ToList();
                Assert.True(squares.Count == 0, "on/off settings drawn as check boxes: " + string.Join(", ", squares));

                // A pick from a list stays check boxes: the Gesture Buttons.
                var gesture = boxes.Where(b => BoundTo(b).StartsWith("MouseGestureButton", StringComparison.Ordinal)).ToList();
                Assert.Equal(6, gesture.Count);
                Assert.All(gesture, b => Assert.NotSame(switchStyle, b.Style));
            }, TimeSpan.FromMinutes(3), "The on/off settings test timed out.");
        }
    }
}
