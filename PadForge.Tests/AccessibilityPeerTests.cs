using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The automation peers that give PadForge's look-alike buttons a role
    /// and a state: SegmentButton reads as a radio button, StateToggleButton
    /// as a toggle, AvailabilityButton as unavailable when its choice is.
    /// Each keeps the Button style, and each acts through the button's own
    /// click, as ButtonAutomationPeer.Invoke does.
    /// </summary>
    public class AccessibilityPeerTests
    {
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

        /// <summary>Runs the dispatcher until the input-priority work queued
        /// so far is done: ButtonAutomationPeer.Invoke clicks through
        /// Dispatcher.BeginInvoke at Input priority.</summary>
        private static void Pump()
            => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        private static AutomationPeer PeerOf(UIElement element)
            => UIElementAutomationPeer.CreatePeerForElement(element);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetKeyboardState(byte[] keys);

        /// <summary>Clears this thread's key state before keys are sent.
        /// ClickBorder and CurveEditor read Keyboard.Modifiers, which is this
        /// thread's state (GetKeyState), and while it holds a modifier both
        /// leave Enter, Space and the arrows to the page. Both key tests failed
        /// together that way in one full run on 2026-11-07. SetKeyboardState
        /// writes this thread's table only and sends nothing to Windows.</summary>
        private static void NoKeysHeld() => Assert.True(SetKeyboardState(new byte[256]));

        [Fact]
        public void ASegmentButton_ReadsAsARadioButton_SelectedWhenItsChoiceIsCurrent() => RunSta(() =>
        {
            int clicks = 0;
            var tile = new SegmentButton();
            tile.Click += (s, e) => clicks++;
            var peer = PeerOf(tile);

            Assert.Equal(AutomationControlType.RadioButton, peer.GetAutomationControlType());
            Assert.Null(peer.GetPattern(PatternInterface.Invoke));
            var item = Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem));
            Assert.False(item.IsSelected);

            // Select clicks a choice that is not current, as a mouse click
            // does, and leaves the current one alone.
            item.Select();
            Pump();
            Assert.Equal(1, clicks);
            tile.IsSelected = true;
            Assert.True(item.IsSelected);
            item.Select();
            Pump();
            Assert.Equal(1, clicks);
            Assert.Throws<InvalidOperationException>(() => item.RemoveFromSelection());
        });

        [Fact]
        public void AStateToggleButton_ReadsAsAToggle_OnWhileItsThingIsOn() => RunSta(() =>
        {
            int clicks = 0;
            var power = new StateToggleButton();
            power.Click += (s, e) => clicks++;
            var peer = PeerOf(power);

            Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
            // Axe's ButtonInvokeAndTogglePatterns rejects a button with both.
            Assert.Null(peer.GetPattern(PatternInterface.Invoke));
            var toggle = Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle));
            Assert.Equal(ToggleState.Off, toggle.ToggleState);
            power.IsOn = true;
            Assert.Equal(ToggleState.On, toggle.ToggleState);

            toggle.Toggle();
            Pump();
            Assert.Equal(1, clicks);
        });

        [Fact]
        public void AnAvailabilityButton_ReadsUnavailable_WhileItsChoiceIs_AndStillLooksAndClicksTheSame() => RunSta(() =>
        {
            int clicks = 0;
            var entry = new AvailabilityButton();
            entry.Click += (s, e) => clicks++;
            var peer = PeerOf(entry);
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));

            Assert.True(peer.IsEnabled());
            invoke.Invoke();
            Pump();
            Assert.Equal(1, clicks);

            entry.IsAvailable = false;
            Assert.True(entry.IsEnabled);
            Assert.False(peer.IsEnabled());
            Assert.Throws<ElementNotEnabledException>(() => invoke.Invoke());
        });

        [Fact]
        public void ADecorativeImage_LeavesTheControlView_WhereAnImageStays() => RunSta(() =>
        {
            // Shown, since a peer counts only a visible element as a control
            // element (UIElementAutomationPeer.IsControlElementCore).
            var decorative = new DecorativeImage { Width = 10, Height = 10 };
            var plain = new Image { Width = 10, Height = 10 };
            var panel = new StackPanel();
            panel.Children.Add(decorative);
            panel.Children.Add(plain);
            var window = new Window
            {
                Width = 200, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, Content = panel,
            };
            try
            {
                window.Show();
                Assert.True(decorative.IsVisible);
                Assert.False(PeerOf(decorative).IsControlElement());
                Assert.False(PeerOf(decorative).IsContentElement());
                Assert.True(PeerOf(plain).IsControlElement());
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void AGlyphText_LeavesTheControlView_AndTakesTheTextBlockStyle() => RunSta(() =>
        {
            var marker = new SolidColorBrush(Colors.Tomato);
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, marker));
            var glyph = new GlyphText { Text = ((char)0xE7C8).ToString() };
            var plain = new TextBlock { Text = "Record" };
            var panel = new StackPanel();
            panel.Resources.Add(typeof(TextBlock), style);
            panel.Children.Add(glyph);
            panel.Children.Add(plain);
            var window = new Window
            {
                Width = 200, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, Content = panel,
            };
            try
            {
                window.Show();
                Assert.False(PeerOf(glyph).IsControlElement());
                Assert.False(PeerOf(glyph).IsContentElement());
                Assert.True(PeerOf(plain).IsControlElement());
                // It draws as the TextBlock beside it does.
                Assert.Same(style, glyph.Style);
                Assert.Same(marker, glyph.Foreground);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void ADecorativeIcon_HidesTheGlyphItDraws_WhereAWpfUiIconShowsIt() => RunSta(() =>
        {
            var plainButton = new System.Windows.Controls.Button { Content = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24 } };
            var quietButton = new System.Windows.Controls.Button { Content = new DecorativeSymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24 } };
            var fontButton = new System.Windows.Controls.Button { Content = new DecorativeFontIcon { Glyph = ((char)0xE713).ToString() } };
            var panel = new StackPanel();
            panel.Children.Add(plainButton);
            panel.Children.Add(quietButton);
            panel.Children.Add(fontButton);
            var window = new Window
            {
                Width = 200, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, Content = panel,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                // The plain icon has no peer, so its glyph TextBlock is the
                // button's child in the control view.
                var plainKids = PeerOf(plainButton).GetChildren() ?? new System.Collections.Generic.List<AutomationPeer>();
                Assert.Contains(plainKids, k => k.GetAutomationControlType() == AutomationControlType.Text && k.IsControlElement());
                foreach (var button in new[] { quietButton, fontButton })
                {
                    var kids = PeerOf(button).GetChildren() ?? new System.Collections.Generic.List<AutomationPeer>();
                    Assert.DoesNotContain(kids, k => k.IsControlElement() || k.IsContentElement() || (k.GetChildren()?.Count ?? 0) > 0);
                }
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void AltUpAndAltDown_MoveAMacroStepOnePlace_AndNothingElseDoes()
        {
            // With Alt held, an arrow arrives as Key.System.
            Assert.Equal(-1, PadForge.Views.PadPage.ReorderStep(System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.System, System.Windows.Input.Key.Up));
            Assert.Equal(1, PadForge.Views.PadPage.ReorderStep(System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.System, System.Windows.Input.Key.Down));
            Assert.Equal(0, PadForge.Views.PadPage.ReorderStep(System.Windows.Input.ModifierKeys.None, System.Windows.Input.Key.Up, System.Windows.Input.Key.None));
            Assert.Equal(0, PadForge.Views.PadPage.ReorderStep(System.Windows.Input.ModifierKeys.Alt | System.Windows.Input.ModifierKeys.Shift, System.Windows.Input.Key.System, System.Windows.Input.Key.Up));
            Assert.Equal(0, PadForge.Views.PadPage.ReorderStep(System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.System, System.Windows.Input.Key.Left));

            var steps = new System.Collections.ObjectModel.ObservableCollection<string> { "a", "b", "c" };
            Assert.True(PadForge.Views.PadPage.MoveStep(steps, "b", -1));
            Assert.Equal(new[] { "b", "a", "c" }, steps);
            Assert.False(PadForge.Views.PadPage.MoveStep(steps, "b", -1));
            Assert.True(PadForge.Views.PadPage.MoveStep(steps, "a", 1));
            Assert.Equal(new[] { "b", "c", "a" }, steps);
            Assert.False(PadForge.Views.PadPage.MoveStep(steps, "a", 1));
        }

        [Fact]
        public void AStatusLineMarkedLive_ReadsAsAPoliteLiveRegion_UntilUnmarked() => RunSta(() =>
        {
            var line = new TextBlock { Text = "Stopped" };
            var window = new Window
            {
                Width = 200, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, Content = line,
            };
            try
            {
                window.Show();
                Assert.Equal(AutomationLiveSetting.Off, PeerOf(line).GetLiveSetting());
                PadForge.Common.LiveText.SetPolite(line, true);
                Assert.Equal(AutomationLiveSetting.Polite, PeerOf(line).GetLiveSetting());
                // A change schedules the announcement, which needs a listener
                // and the active window, and neither exists here.
                line.Text = "Listening on port 8080";
                Pump();
                PadForge.Common.LiveText.SetPolite(line, false);
                Assert.Equal(AutomationLiveSetting.Off, PeerOf(line).GetLiveSetting());
            }
            finally
            {
                window.Close();
            }
        });

        private static T Find<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T hit)
                    return hit;
                if (Find<T>(child) is T deeper)
                    return deeper;
            }
            return null;
        }

        [Fact]
        public void AButtonMadeByADataTemplate_KeepsTheTemplatesStyle_OrTakesTheButtonStyle() => RunSta(() =>
        {
            // A template's values rank below a local value, so a style reference
            // set in the constructor once replaced the slot card's own tile style.
            var marker = new SolidColorBrush(Colors.Tomato);
            var implicitStyle = new Style(typeof(System.Windows.Controls.Button));
            implicitStyle.Setters.Add(new Setter(Control.BackgroundProperty, marker));
            var list = new ItemsControl
            {
                ItemsSource = new[] { "slot" },
                ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
                    "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"" +
                    " xmlns:c=\"clr-namespace:PadForge.Controls;assembly=PadForge\"><StackPanel>" +
                    "<c:SegmentButton><Button.Style><Style TargetType=\"Button\">" +
                    "<Setter Property=\"ToolTip\" Value=\"Tip\"/></Style></Button.Style></c:SegmentButton>" +
                    "<c:StateToggleButton/></StackPanel></DataTemplate>"),
            };
            list.Resources.Add(typeof(System.Windows.Controls.Button), implicitStyle);
            var window = new Window
            {
                Width = 200, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, Content = list,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var tile = Find<SegmentButton>(list);
                var power = Find<StateToggleButton>(list);
                Assert.NotNull(tile);
                Assert.NotNull(power);
                Assert.Equal("Tip", tile.ToolTip);
                Assert.NotSame(implicitStyle, tile.Style);
                Assert.Same(implicitStyle, power.Style);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void EachButton_TakesTheButtonStyle_WhenItHasNoneOfItsOwn() => RunSta(() =>
        {
            var marker = new SolidColorBrush(Colors.Tomato);
            var style = new Style(typeof(System.Windows.Controls.Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, marker));
            var panel = new StackPanel();
            panel.Resources.Add(typeof(System.Windows.Controls.Button), style);
            var buttons = new System.Windows.Controls.Button[] { new SegmentButton(), new StateToggleButton(), new AvailabilityButton() };
            foreach (var b in buttons)
                panel.Children.Add(b);
            var window = new Window
            {
                Width = 200, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, Content = panel,
            };
            try
            {
                window.Show();
                foreach (var b in buttons)
                {
                    Assert.Same(style, b.Style);
                    Assert.Same(marker, b.Background);
                }
            }
            finally
            {
                window.Close();
            }
        });

        private static Window Host(UIElement content) => new Window
        {
            Width = 300, Height = 300, ShowActivated = false, ShowInTaskbar = false,
            Left = -10000, Top = -10000, Content = content,
        };

        [Fact]
        public void AClickBorder_ClicksOnEnterOrSpace_AndOnTheMouseWhenItsOldHandlerRan() => RunSta(() =>
        {
            int clicks = 0;
            var release = new ClickBorder { Width = 40, Height = 20 };
            var press = new ClickBorder { Width = 40, Height = 20, ClickMode = System.Windows.Controls.ClickMode.Press };
            var label = new TextBlock { Text = "Chip" };
            var holder = new ClickBorder { Child = label };
            var handles = new ClickBorder();
            foreach (var b in new[] { release, press, holder })
                b.Click += (s, e) => clicks++;
            handles.Click += (s, e) => e.Handled = true;
            var panel = new StackPanel();
            foreach (var b in new[] { release, press, holder, handles })
                panel.Children.Add(b);
            var window = Host(panel);
            try
            {
                window.Show();
                NoKeysHeld();
                var source = PresentationSource.FromVisual(release);
                System.Windows.Input.KeyEventArgs KeyDown(System.Windows.Input.Key key)
                    => new(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent };
                System.Windows.Input.MouseButtonEventArgs Button(RoutedEvent ev)
                    => new(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = ev };

                // Tab reaches it.
                Assert.True(release.Focusable);
                Assert.True(System.Windows.Input.KeyboardNavigation.GetIsTabStop(release));

                var enter = KeyDown(System.Windows.Input.Key.Enter);
                release.RaiseEvent(enter);
                Assert.Equal(1, clicks);
                Assert.True(enter.Handled);
                release.RaiseEvent(KeyDown(System.Windows.Input.Key.Space));
                Assert.Equal(2, clicks);
                var other = KeyDown(System.Windows.Input.Key.A);
                release.RaiseEvent(other);
                Assert.Equal(2, clicks);
                Assert.False(other.Handled);

                // A key press that rose from inside it belongs to what it
                // rose from.
                label.RaiseEvent(KeyDown(System.Windows.Input.Key.Enter));
                Assert.Equal(2, clicks);

                // The default clicks on the left button's release, Press on
                // its press, each once.
                release.RaiseEvent(Button(UIElement.MouseLeftButtonDownEvent));
                Assert.Equal(2, clicks);
                var up = Button(UIElement.MouseLeftButtonUpEvent);
                release.RaiseEvent(up);
                Assert.Equal(3, clicks);
                Assert.False(up.Handled);
                press.RaiseEvent(Button(UIElement.MouseLeftButtonDownEvent));
                Assert.Equal(4, clicks);
                press.RaiseEvent(Button(UIElement.MouseLeftButtonUpEvent));
                Assert.Equal(4, clicks);

                // A handler that handles Click handles the mouse event, as it
                // did when it ran on that event.
                var handledUp = Button(UIElement.MouseLeftButtonUpEvent);
                handles.RaiseEvent(handledUp);
                Assert.True(handledUp.Handled);

                release.IsEnabled = false;
                release.RaiseEvent(Button(UIElement.MouseLeftButtonUpEvent));
                Assert.Equal(4, clicks);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void AClickBorder_ReadsAsAButton_ARadioButtonOrAToggle_ByTheStateItCarries() => RunSta(() =>
        {
            int clicks = 0;
            var chip = new ClickBorder();
            chip.Click += (s, e) => clicks++;
            var peer = PeerOf(chip);

            Assert.Equal(nameof(ClickBorder), peer.GetClassName());
            Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
            Assert.Null(peer.GetPattern(PatternInterface.SelectionItem));
            Assert.Null(peer.GetPattern(PatternInterface.Toggle));
            Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
            Assert.Equal(1, clicks);

            // One choice among several: a radio button, checked on the
            // current choice, that clicks only a choice that is not current.
            chip.IsSelected = false;
            Assert.Equal(AutomationControlType.RadioButton, peer.GetAutomationControlType());
            Assert.Null(peer.GetPattern(PatternInterface.Invoke));
            var item = Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem));
            Assert.False(item.IsSelected);
            item.Select();
            Assert.Equal(2, clicks);
            chip.IsSelected = true;
            Assert.True(item.IsSelected);
            item.Select();
            Assert.Equal(2, clicks);

            var preset = new ClickBorder { IsChecked = false };
            preset.Click += (s, e) => clicks++;
            var presetPeer = PeerOf(preset);
            Assert.Equal(AutomationControlType.Button, presetPeer.GetAutomationControlType());
            Assert.Null(presetPeer.GetPattern(PatternInterface.Invoke));
            var toggle = Assert.IsAssignableFrom<IToggleProvider>(presetPeer.GetPattern(PatternInterface.Toggle));
            Assert.Equal(ToggleState.Off, toggle.ToggleState);
            toggle.Toggle();
            Assert.Equal(3, clicks);
            preset.IsChecked = true;
            Assert.Equal(ToggleState.On, toggle.ToggleState);

            preset.IsEnabled = false;
            Assert.Throws<ElementNotEnabledException>(() => toggle.Toggle());
            Assert.Equal(3, clicks);
        });

        [Fact]
        public void AClickBorder_IsNamedByTheTextItShows_ElseByItsTooltip() => RunSta(() =>
        {
            // A letter-spaced token reads as its word.
            var spaced = new ClickBorder { Child = new TextBlock { Text = "A\u200AL\u200AL" } };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new GlyphText { Text = "←" });
            row.Children.Add(new TextBlock { Text = "" });
            row.Children.Add(new TextBlock { Text = "Back" });
            row.Children.Add(new TextBlock { Text = "Hidden", Visibility = Visibility.Collapsed });
            row.Children.Add(new TextBlock { Text = "to search" });
            var pieces = new ClickBorder { Child = row };
            var tipped = new ClickBorder { ToolTip = "Identify", Child = new GlyphText { Text = "" } };
            var named = new ClickBorder { Child = new TextBlock { Text = "Shown" } };
            AutomationProperties.SetName(named, "Given");
            var panel = new StackPanel();
            foreach (var b in new[] { spaced, pieces, tipped, named })
                panel.Children.Add(b);
            var window = Host(panel);
            try
            {
                window.Show();
                Assert.Equal("ALL", PeerOf(spaced).GetName());
                Assert.Equal("Back to search", PeerOf(pieces).GetName());
                Assert.Equal("Identify", PeerOf(tipped).GetName());
                Assert.Equal("Given", PeerOf(named).GetName());
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void TheCurveEditorKeys_SelectMoveAddAndRemovePoints_AsTheMouseDoes()
        {
            var points = new System.Collections.Generic.List<(double X, double Y)> { (0, 0), (0.5, 0.25), (1, 1) };
            int index = 0;
            var none = System.Windows.Input.ModifierKeys.None;
            var shift = System.Windows.Input.ModifierKeys.Shift;
            CurveEditor.KeyResult Press(System.Windows.Input.ModifierKeys m, System.Windows.Input.Key key)
                => CurveEditor.ApplyKey(points, ref index, m, key, x => x * x);

            Assert.Equal(CurveEditor.KeyResult.Selected, Press(none, System.Windows.Input.Key.Right));
            Assert.Equal(1, index);
            Assert.Equal(CurveEditor.KeyResult.Selected, Press(none, System.Windows.Input.Key.End));
            Assert.Equal(2, index);
            Assert.Equal(CurveEditor.KeyResult.Selected, Press(none, System.Windows.Input.Key.Right));
            Assert.Equal(2, index);
            Assert.Equal(CurveEditor.KeyResult.Selected, Press(none, System.Windows.Input.Key.Home));
            Assert.Equal(0, index);

            // Up and Down move the output a percent, the page keys ten, and
            // the output stays within 0 to 1.
            Assert.Equal(CurveEditor.KeyResult.Changed, Press(none, System.Windows.Input.Key.Up));
            Assert.Equal((0.0, 0.01), points[0]);
            Assert.Equal(CurveEditor.KeyResult.Changed, Press(none, System.Windows.Input.Key.Down));
            Assert.Equal(CurveEditor.KeyResult.Selected, Press(none, System.Windows.Input.Key.Down));
            Assert.Equal((0.0, 0.0), points[0]);
            index = 1;
            Assert.Equal(CurveEditor.KeyResult.Changed, Press(none, System.Windows.Input.Key.PageUp));
            Assert.Equal(0.35, points[1].Y, 6);

            // Shift moves an interior point's input, short of its neighbors.
            // An endpoint keeps its input.
            Assert.Equal(CurveEditor.KeyResult.Changed, Press(shift, System.Windows.Input.Key.Right));
            Assert.Equal(0.51, points[1].X, 6);
            points[1] = (0.985, points[1].Y);
            Assert.Equal(CurveEditor.KeyResult.Changed, Press(shift, System.Windows.Input.Key.Right));
            Assert.Equal(0.99, points[1].X, 6);
            Assert.Equal(CurveEditor.KeyResult.Selected, Press(shift, System.Windows.Input.Key.Right));
            points[1] = (0.5, 0.35);
            index = 0;
            Assert.Equal(CurveEditor.KeyResult.Selected, Press(shift, System.Windows.Input.Key.Right));
            Assert.Equal((0.0, 0.0), points[0]);

            // Insert adds a point on the curve halfway to the next one, and on
            // the last point halfway from the one before. Delete removes an
            // interior point and selects the one before it.
            Assert.Equal(CurveEditor.KeyResult.Changed, Press(none, System.Windows.Input.Key.Insert));
            Assert.Equal(4, points.Count);
            Assert.Equal(1, index);
            Assert.Equal((0.25, 0.0625), points[1]);
            index = 3;
            Assert.Equal(CurveEditor.KeyResult.Changed, Press(none, System.Windows.Input.Key.Insert));
            Assert.Equal(3, index);
            Assert.Equal((0.75, 0.5625), points[3]);
            Assert.Equal(CurveEditor.KeyResult.Changed, Press(none, System.Windows.Input.Key.Delete));
            Assert.Equal(2, index);
            Assert.Equal(4, points.Count);
            index = 0;
            Assert.Equal(CurveEditor.KeyResult.Selected, Press(none, System.Windows.Input.Key.Delete));
            index = 3;
            Assert.Equal(CurveEditor.KeyResult.Selected, Press(none, System.Windows.Input.Key.Delete));
            Assert.Equal(4, points.Count);

            // No room for a point halfway: nothing is added.
            var tight = new System.Collections.Generic.List<(double X, double Y)> { (0, 0), (0.015, 0.1), (1, 1) };
            int at = 0;
            Assert.Equal(CurveEditor.KeyResult.Selected, CurveEditor.ApplyKey(tight, ref at, none, System.Windows.Input.Key.Insert, x => x));
            Assert.Equal(3, tight.Count);

            // Other keys and modifiers belong to the page.
            Assert.Equal(CurveEditor.KeyResult.Ignored, Press(none, System.Windows.Input.Key.Tab));
            Assert.Equal(CurveEditor.KeyResult.Ignored, Press(System.Windows.Input.ModifierKeys.Control, System.Windows.Input.Key.Up));
        }

        [Fact]
        public void ACurveEditor_ReadsThePointTheKeysActOn_AsItsValue() => RunSta(() =>
        {
            var editor = new CurveEditor { CurveString = "0,0;0.5,0.25;1,1", Width = 200, Height = 200 };
            var window = Host(editor);
            try
            {
                window.Show();
                // The editor reads its points when it loads.
                Pump();
                Assert.True(editor.IsTabStop);
                var peer = PeerOf(editor);
                var value = Assert.IsAssignableFrom<IValueProvider>(peer.GetPattern(PatternInterface.Value));
                Assert.True(value.IsReadOnly);
                string Point(int n, int count, int x, int y) => string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    PadForge.Resources.Strings.Strings.Instance.CurveEditor_PointFormat, n, count, x, y);
                Assert.Equal(Point(1, 3, 0, 0), value.Value);

                NoKeysHeld();
                var source = PresentationSource.FromVisual(editor);
                System.Windows.Input.KeyEventArgs Key(System.Windows.Input.Key key)
                    => new(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent };
                var right = Key(System.Windows.Input.Key.Right);
                editor.RaiseEvent(right);
                Assert.True(right.Handled);
                Assert.Equal(Point(2, 3, 50, 25), value.Value);

                // A move goes through the same commit as a drag.
                editor.RaiseEvent(Key(System.Windows.Input.Key.Up));
                Assert.Equal("0.000,0.000;0.500,0.260;1.000,1.000", editor.CurveString);
                Assert.Equal(Point(2, 3, 50, 26), value.Value);

                var tab = Key(System.Windows.Input.Key.Tab);
                editor.RaiseEvent(tab);
                Assert.False(tab.Handled);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void ATemplatePartButton_IsNamedForWhatItDoes_AndDrawsWhatItsTemplateDraws() => RunSta(() =>
        {
            var strings = PadForge.Resources.Strings.Strings.Instance;
            var bar = (System.Windows.Controls.Primitives.ScrollBar)System.Windows.Markup.XamlReader.Parse(
                "<ScrollBar xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"" +
                " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"" +
                " xmlns:ui=\"clr-namespace:Wpf.Ui.Controls;assembly=Wpf.Ui\" Height=\"120\" Width=\"14\">" +
                "<ScrollBar.Template><ControlTemplate TargetType=\"ScrollBar\"><StackPanel>" +
                "<RepeatButton x:Name=\"Up\" Command=\"ScrollBar.LineUpCommand\" Content=\"{x:Static ui:SymbolRegular.CaretUp24}\" FontSize=\"11\" Width=\"12\" Height=\"12\">" +
                "<RepeatButton.Template><ControlTemplate TargetType=\"RepeatButton\"><Border Background=\"Transparent\">" +
                "<ui:SymbolIcon HorizontalAlignment=\"Center\" Filled=\"True\" FontSize=\"{TemplateBinding FontSize}\"" +
                " Symbol=\"{Binding Path=Content, RelativeSource={RelativeSource TemplatedParent}, TargetNullValue={x:Static ui:SymbolRegular.Empty}}\"/>" +
                "</Border></ControlTemplate></RepeatButton.Template></RepeatButton>" +
                "<RepeatButton x:Name=\"Page\" Command=\"ScrollBar.PageDownCommand\" Height=\"20\"/>" +
                "<RepeatButton x:Name=\"PART_InlineIncrementButton\" Width=\"12\" Height=\"12\">" +
                "<ui:SymbolIcon FontSize=\"9\" Symbol=\"ChevronUp24\"/></RepeatButton>" +
                "<RepeatButton x:Name=\"Other\" Width=\"12\" Height=\"12\"/>" +
                "</StackPanel></ControlTemplate></ScrollBar.Template></ScrollBar>");
            var window = Host(bar);
            try
            {
                window.Show();
                window.UpdateLayout();
                T Part<T>(string name) where T : class => (T)bar.Template.FindName(name, bar);
                var up = Part<System.Windows.Controls.Primitives.RepeatButton>("Up");
                var page = Part<System.Windows.Controls.Primitives.RepeatButton>("Page");
                var increment = Part<System.Windows.Controls.Primitives.RepeatButton>("PART_InlineIncrementButton");
                var other = Part<System.Windows.Controls.Primitives.RepeatButton>("Other");
                var border = (Border)VisualTreeHelper.GetChild(up, 0);
                var arrow = border.Child;
                var chevron = increment.Content;
                foreach (var b in new[] { up, page, increment, other })
                    PadForge.Common.AccessibleNames.NameButton(b);

                Assert.Equal(strings.Common_ScrollUp, AutomationProperties.GetName(up));
                Assert.Equal(strings.Common_PageDown, AutomationProperties.GetName(page));
                Assert.Equal(strings.Common_Increase, AutomationProperties.GetName(increment));
                Assert.Equal(string.Empty, AutomationProperties.GetName(other));

                // Naming sets the name and nothing else: each part keeps the
                // icon its template made, as wpf-ui draws it.
                Assert.IsType<Wpf.Ui.Controls.SymbolIcon>(arrow);
                Assert.Same(arrow, border.Child);
                Assert.IsType<Wpf.Ui.Controls.SymbolIcon>(chevron);
                Assert.Same(chevron, increment.Content);

                // Named again, nothing changes.
                PadForge.Common.AccessibleNames.NameButton(up);
                Assert.Same(arrow, border.Child);
                Assert.Equal(strings.Common_ScrollUp, AutomationProperties.GetName(up));
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void ATitleBarButton_IsNamedByItsType_AndMaximizeTurnsToRestore() => RunSta(() =>
        {
            var strings = PadForge.Resources.Strings.Strings.Instance;
            var button = new Wpf.Ui.Controls.TitleBarButton { ButtonType = Wpf.Ui.Controls.TitleBarButtonType.Maximize };
            PadForge.Common.AccessibleNames.NamePart(button);
            Assert.Equal(strings.Main_Maximize, AutomationProperties.GetName(button));
            button.ButtonType = Wpf.Ui.Controls.TitleBarButtonType.Restore;
            Assert.Equal(strings.Main_Restore, AutomationProperties.GetName(button));
            button.ButtonType = Wpf.Ui.Controls.TitleBarButtonType.Close;
            Assert.Equal(strings.Common_Close, AutomationProperties.GetName(button));
            button.ButtonType = Wpf.Ui.Controls.TitleBarButtonType.Minimize;
            Assert.Equal(strings.Main_Minimize, AutomationProperties.GetName(button));
            button.ButtonType = Wpf.Ui.Controls.TitleBarButtonType.Help;
            Assert.Equal(string.Empty, AutomationProperties.GetName(button));
        });

        public sealed class GridRow
        {
            public string Label { get; set; }

            public override string ToString() => "row " + Label;
        }

        [Fact]
        public void ARowOfATableWithNoColumns_ReadsAsItsItem_AndCanBeSelected() => RunSta(() =>
        {
            // The mapping table's shape (#574): it has no columns, its row
            // style draws the whole row, and a row opens when it is
            // selected. A screen reader reads each row as its item, by the
            // item's ToString (a mapping row's is its output and its
            // source), finds no cells in it, and selects it through the
            // row's own pattern. A table with template columns read WPF's
            // "Item: ..., Column Display Index: N" for every cell that was
            // not named (DataGridCellItemAutomationPeer.GetNameCore).
            var grid = (DataGrid)System.Windows.Markup.XamlReader.Parse(
                "<DataGrid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"" +
                " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" AutoGenerateColumns=\"False\"" +
                " CanUserAddRows=\"False\" HeadersVisibility=\"None\" SelectionMode=\"Single\" IsReadOnly=\"True\"" +
                " RowDetailsVisibilityMode=\"VisibleWhenSelected\">" +
                "<DataGrid.RowStyle><Style TargetType=\"DataGridRow\"><Setter Property=\"Template\"><Setter.Value>" +
                "<ControlTemplate TargetType=\"DataGridRow\"><StackPanel>" +
                "<TextBlock Text=\"{Binding Label}\" Height=\"30\"/>" +
                "<DataGridDetailsPresenter Visibility=\"{TemplateBinding DetailsVisibility}\"/>" +
                "</StackPanel></ControlTemplate></Setter.Value></Setter></Style></DataGrid.RowStyle>" +
                "<DataGrid.RowDetailsTemplate><DataTemplate><TextBlock Text=\"options\" Height=\"50\"/></DataTemplate>" +
                "</DataGrid.RowDetailsTemplate></DataGrid>");
            var items = new[] { new GridRow { Label = "B" }, new GridRow { Label = "A" } };
            grid.ItemsSource = items;
            var window = Host(grid);
            try
            {
                window.Show();
                window.UpdateLayout();
                Pump();
                Assert.Empty(grid.Columns);
                var rows = PeerOf(grid).GetChildren().OfType<DataGridItemAutomationPeer>().ToList();
                Assert.Equal(2, rows.Count);
                Assert.Equal("row B", rows[0].GetName());
                Assert.Equal("row A", rows[1].GetName());
                Assert.Empty(rows[0].GetChildren().OfType<DataGridCellItemAutomationPeer>());

                var select = (System.Windows.Automation.Provider.ISelectionItemProvider)rows[1].GetPattern(PatternInterface.SelectionItem);
                Assert.NotNull(select);
                select.Select();
                window.UpdateLayout();
                Pump();
                Assert.Same(items[1], grid.SelectedItem);
                var opened = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(items[1]);
                Assert.Equal(Visibility.Visible, opened.DetailsVisibility);
                Assert.Equal(Visibility.Collapsed, ((DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(items[0])).DetailsVisibility);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
