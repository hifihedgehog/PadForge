using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Gyro On pickers of the Gyro group's Engage card, hosted on the
    /// real page. Their lists are made anew for each read, so a raise of a
    /// list hands the picker records that are not the ones it had picked.
    /// A change of language did that, the picker's pick fell out, and the
    /// picker wrote null back: Needs went from All to Any in the saved
    /// activation, and Gyro On showed Always On while Hold stayed saved.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class GyroActivationPickerTests
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

        private static ComboBox Picker(DependencyObject page, string path)
            => Descendants<ComboBox>(page).Single(c =>
                BindingOperations.GetBinding(c, Selector.SelectedValueProperty)?.Path?.Path == path);

        /// <summary>Hosts the page with the Engage card open on a Hold, All
        /// activation and runs <paramref name="body"/> on it.</summary>
        private static void WithCard(string activation, Action<PadViewModel, PadPage, Window, System.Windows.Threading.Dispatcher> body)
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
                    foreach (string name in new[] { "TabGyro", "GyroEngageCard" })
                        ((UIElement)page.FindName(name)).Visibility = Visibility.Visible;
                    engage.IsExpanded = true;
                    vm.GyroActivation = activation;
                    window.UpdateLayout();
                    PageRenderDump.Settle(dispatcher);
                    window.UpdateLayout();
                    body(vm, page, window, dispatcher);
                }
                finally
                {
                    engage.IsExpanded = wasOpen;
                    window.Close();
                }
            }, TimeSpan.FromMinutes(3), "The Gyro On picker test timed out.");
        }

        [Fact]
        public void ALanguageChange_KeepsTheSavedActivationAndThePicks()
        {
            const string saved = "Base=Hold,All,Gamepad ButtonA";
            WithCard(saved, (vm, page, window, dispatcher) =>
            {
                var before = CultureInfo.CurrentUICulture;
                var mode = Picker(page, "GyroActivationMode");
                var needs = Picker(page, "GyroActivationNeeds");
                try
                {
                    Strings.ChangeCulture(CultureInfo.GetCultureInfo("en-US"));
                    PageRenderDump.Settle(dispatcher);
                    Assert.Equal("Hold", mode.SelectedValue);
                    Assert.Equal("All", needs.SelectedValue);
                    string english = Strings.Instance.Pad_GyroActivation_NeedsAll;

                    Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));
                    window.UpdateLayout();
                    PageRenderDump.Settle(dispatcher);
                    Assert.NotEqual(english, Strings.Instance.Pad_GyroActivation_NeedsAll);

                    Assert.Equal(saved, vm.GyroActivation);
                    Assert.Equal("Hold", vm.GyroActivationMode);
                    Assert.Equal("All", vm.GyroActivationNeeds);
                    Assert.Equal("Hold", mode.SelectedValue);
                    Assert.Equal("All", needs.SelectedValue);
                    // The picks are the new language's records.
                    Assert.Equal(Strings.Instance.Pad_GyroActivation_Hold, (mode.SelectedItem as GyroActivationChoice)?.Display);
                    Assert.Equal(Strings.Instance.Pad_GyroActivation_NeedsAll, (needs.SelectedItem as GyroActivationChoice)?.Display);
                }
                finally
                {
                    Strings.ChangeCulture(before);
                }
            });
        }

        /// <summary>Tab goes from the Gyro On reset into the Buttons list and
        /// lands on each button's check box in turn, which draws the focus
        /// ring, and Space on the focused box ticks its button. A list item
        /// took the focus and drew nothing, the next Tab left the list, and
        /// Space picked the item instead of ticking it.</summary>
        [Fact]
        public void TabReachesEachButtonsCheckBox_AndSpaceTicksIt()
        {
            WithCard("Base=Hold,Any,Gamepad ButtonA", (vm, page, window, dispatcher) =>
            {
                var reset = Descendants<Button>(page).Single(b =>
                    BindingOperations.GetBinding(b, ButtonBase.CommandProperty)?.Path?.Path == "ResetGyroActivationLayerCommand");
                reset.Focus();
                Assert.Same(reset, FocusManager.GetFocusedElement(window));

                var boxes = new List<CheckBox>();
                UIElement at = reset;
                for (int i = 0; i < 2; i++)
                {
                    at.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    at = (UIElement)FocusManager.GetFocusedElement(window);
                    var reached = Assert.IsType<CheckBox>(at);
                    Assert.NotNull(reached.FocusVisualStyle);
                    Assert.IsType<GyroActivationButton>(reached.DataContext);
                    boxes.Add(reached);
                }
                Assert.NotSame(boxes[0], boxes[1]);

                // The box Tab stands on, or the one before it when that one
                // is the button the activation already holds.
                var box = boxes.Last(b => !((GyroActivationButton)b.DataContext).IsOn);
                var button = (GyroActivationButton)box.DataContext;
                if (!ReferenceEquals(box, at))
                {
                    at.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
                    at = (UIElement)FocusManager.GetFocusedElement(window);
                    Assert.Same(box, at);
                }
                var source = PresentationSource.FromVisual(at);
                at.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
                at.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
                Assert.True(button.IsOn);
                Assert.Contains(button.Descriptor, vm.GyroActivation);
                Assert.Contains("Gamepad ButtonA", vm.GyroActivation);
            });
        }

        /// <summary>A shift layer's list is not Base's, so moving the shown
        /// layer replaces the Gyro On list too, also between two layers that
        /// both hold the gyro while a button is held. The picker shows each
        /// layer's mode and the saved activation is untouched.</summary>
        [Fact]
        public void MovingTheShownLayer_KeepsTheSavedActivationAndShowsEachMode()
        {
            const string saved = "Base=Hold,All,Gamepad ButtonA;Layer_1=Hold,Any,Gamepad ButtonX;Layer_2=Toggle,Any,Gamepad ButtonY";
            WithCard(saved, (vm, page, window, dispatcher) =>
            {
                var mode = Picker(page, "GyroActivationMode");
                var needs = Picker(page, "GyroActivationNeeds");
                foreach (var (layer, shown, need) in new[]
                {
                    ("Layer_1", "Hold", "Any"), ("Layer_2", "Toggle", "Any"), ("Base", "Hold", "All"), ("Layer_1", "Hold", "Any"),
                })
                {
                    vm.GyroActivationLayer = layer;
                    window.UpdateLayout();
                    PageRenderDump.Settle(dispatcher);
                    Assert.Equal(saved, vm.GyroActivation);
                    Assert.Equal(shown, vm.GyroActivationMode);
                    Assert.Equal(shown, mode.SelectedValue);
                    Assert.Equal(shown, (mode.SelectedItem as GyroActivationChoice)?.Value);
                    Assert.Equal(need, needs.SelectedValue);
                    Assert.Equal(need, (needs.SelectedItem as GyroActivationChoice)?.Value);
                }
            });
        }
    }
}
