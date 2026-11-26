using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Controls;
using PadForge.Services.Automation;
using PadForge.Services.Plugins;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A plugin's settings as PluginSettingsView draws them in the app's own
    /// resources (#600 items 17 and 19). Every value row shows its reset, at
    /// its default and changed, as every setting's reset in PadForge shows on
    /// its row, and a status or a button has none. The Test Controller's
    /// stick pads stay where they are let go, and after a restart they show
    /// where they were left.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public sealed class PluginSettingsViewTests
    {
        /// <summary>Runs the body beside the app's resources, on the STA
        /// thread the page renders use, which puts the process back with no
        /// Application after.</summary>
        private static void WithAppResources(Action<Dispatcher> body)
            => PageRenderDumpPages.WithApp((_, dispatcher) => body(dispatcher), TimeSpan.FromMinutes(2), "The plugin settings view test timed out.");

        /// <summary>Lets the bindings the last change set off run.</summary>
        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        /// <summary>Lets the bindings the last change set off run, then lays
        /// the view out at the Plugins page's width.</summary>
        private static void Lay(FrameworkElement view)
        {
            Pump();
            view.Measure(new Size(1100, 3000));
            view.Arrange(new Rect(0, 0, 1100, 3000));
            view.UpdateLayout();
        }

        private static IEnumerable<DependencyObject> Under(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var below in Under(child)) yield return below;
            }
        }

        /// <summary>Whether the part and everything that holds it, up to the
        /// view, is visible and has room: what IsVisible says in a window,
        /// which a view laid out on its own has none of.</summary>
        private static bool Shown(FrameworkElement part, FrameworkElement view)
        {
            if (part.ActualWidth <= 0 || part.ActualHeight <= 0) return false;
            for (DependencyObject at = part; at != null; at = VisualTreeHelper.GetParent(at))
            {
                if (at is UIElement element && element.Visibility != Visibility.Visible) return false;
                if (ReferenceEquals(at, view)) return true;
            }
            return false;
        }

        private static string Declare(PluginSettingSet set, string line) => PluginLines.Answer(set, AutomationLine.Parse(line));

        [Fact]
        public void EveryValueRow_ShowsItsReset_AtItsDefaultAndChanged_AndAStatusOrAButtonHasNone()
        {
            WithAppResources(dispatcher =>
            {
                var set = new PluginSettingSet();
                Declare(set, "setting key=dead kind=number name=\"Dead Zone\" min=0 max=20 default=3");
                Declare(set, "setting key=invert kind=switch name=Invert help=\"Turns the axis over.\"");
                Declare(set, "setting key=steer kind=pad name=Steer stay=1");
                Declare(set, "setting key=port kind=status name=Port");
                Declare(set, "setting key=cal kind=button name=Calibrate");
                var host = new PluginSettingsHost(set, null, "Test", dispatcher);
                var view = new PluginSettingsView { DataContext = host };
                Lay(view);
                SettingResetButton Reset(string key)
                    => Under(view).OfType<SettingResetButton>().Single(r => ReferenceEquals(r.DataContext, host.Item(key)));
                var values = new[] { "dead", "invert", "steer" };

                // At its default each value shows its reset, as every
                // setting's reset in PadForge shows on its row. A status and
                // a button hold no value and have none.
                foreach (string key in values) Assert.True(Shown(Reset(key), view), key + "'s reset is not drawn at its default");
                Assert.Equal(Visibility.Collapsed, Reset("port").Visibility);
                Assert.Equal(Visibility.Collapsed, Reset("cal").Visibility);

                // Changed, each still shows. Its reset puts the default back
                // and stays on its row.
                host.Item("invert").IsOn = true;
                host.Item("dead").Number = 5;
                host.Item("steer").Pad = new Point(20, 30);
                Lay(view);
                foreach (string key in values) Assert.True(Shown(Reset(key), view), key + "'s reset is not drawn once changed");
                foreach (string key in values) Reset(key).Command.Execute(null);
                Lay(view);
                Assert.Equal("3", set.ValueOf(null, "dead"));
                Assert.Equal("false", set.ValueOf(null, "invert"));
                Assert.Equal("0,0", set.ValueOf(null, "steer"));
                foreach (string key in values) Assert.True(Shown(Reset(key), view), key + "'s reset left its row");
            });
        }

        /// <summary>A setting whose value differs from its default shows its
        /// name in ember and says Changed to UI Automation, on a plugin's
        /// card and on a device's, in the full row and in a compact one. It
        /// turns back on its reset and on a value typed back to its default,
        /// since the set keeps only a value that differs from its default.
        /// Its reset stays on its row either way. Only kept settings take it
        /// (switch, number, choice, text and input): a pad, a button and a
        /// status never do.</summary>
        [Fact]
        public void AChangedValue_ShowsItsNameInEmber_AndSaysChanged_UntilItIsAtItsDefaultAgain()
        {
            WithAppResources(dispatcher =>
            {
                var set = new PluginSettingSet();
                Declare(set, "setting key=dead kind=number name=\"Dead Zone\" min=0 max=20 default=3");
                Declare(set, "setting key=curve kind=choice name=\"Brake Curve\" {\"choices\":[\"Linear\",\"Progressive\"],\"default\":\"Linear\"}");
                Declare(set, "setting key=port kind=status name=Port");
                Declare(set, "setting key=trigger kind=number name=Trigger min=0 max=100 default=0 look=compact");
                Declare(set, "setting key=stick kind=pad name=Stick look=compact");
                Declare(set, "setting key=hand kind=switch name=A look=compact default=true");
                Declare(set, "setting key=clutch kind=switch name=\"Clutch Inverted\" device=pedals");
                var card = new PluginSettingsHost(set, null, "Test", dispatcher);
                var device = new PluginSettingsHost(set, "pedals", "Device_Plugin_pedals", dispatcher);
                var cardView = new PluginSettingsView { DataContext = card };
                var deviceView = new PluginSettingsView { DataContext = device, LabelWidth = 170, CompactMargin = new Thickness(0) };
                var ember = ((SolidColorBrush)Application.Current.FindResource("EmberTextBrush")).Color;

                // The text that names the row, and the control a screen
                // reader is on.
                System.Windows.Controls.TextBlock Name(FrameworkElement view, PluginSettingItem item)
                    => Under(view).OfType<System.Windows.Controls.TextBlock>().First(t => ReferenceEquals(t.DataContext, item) && t.Text == item.Name);
                FrameworkElement Control(FrameworkElement view, PluginSettingItem item)
                    => Under(view).OfType<FrameworkElement>().First(e => System.Windows.Automation.AutomationProperties.GetAutomationId(e) == item.AutomationId);
                bool Ember(Brush brush) => brush is SolidColorBrush b && b.Color == ember;
                void Marked(FrameworkElement view, PluginSettingItem item, bool changed)
                {
                    Assert.Equal(changed, Ember(Name(view, item).Foreground));
                    Assert.Equal(changed ? "Changed" : string.Empty, System.Windows.Automation.AutomationProperties.GetItemStatus(Control(view, item)));
                }

                Lay(cardView);
                Lay(deviceView);
                foreach (string key in new[] { "dead", "curve", "trigger", "stick" }) Marked(cardView, card.Item(key), false);
                Marked(deviceView, device.Item("clutch"), false);
                // A status holds no value and is never marked.
                Assert.False(Ember(Name(cardView, card.Item("port")).Foreground));

                card.Item("dead").Number = 5;
                card.Item("curve").Choice = "Progressive";
                card.Item("trigger").Number = 70;
                card.Item("stick").Pad = new Point(20, 30);
                device.Item("clutch").IsOn = true;
                Lay(cardView);
                Lay(deviceView);
                foreach (string key in new[] { "dead", "curve", "trigger" }) Marked(cardView, card.Item(key), true);
                // A pad is moved, not kept as a setting: no mark.
                Marked(cardView, card.Item("stick"), false);
                Marked(deviceView, device.Item("clutch"), true);

                // A compact toggle off its default says so, and in ember while
                // it is off, since a lit toggle is ember already.
                card.Item("hand").IsOn = false;
                Lay(cardView);
                Assert.Equal("Changed", System.Windows.Automation.AutomationProperties.GetItemStatus(Control(cardView, card.Item("hand"))));
                Assert.True(Ember(((System.Windows.Controls.Primitives.ToggleButton)Control(cardView, card.Item("hand"))).Foreground));

                // Back by its reset, and by a value typed back to its default.
                card.Item("dead").ResetCommand.Execute(null);
                card.Item("curve").Choice = "Linear";
                device.Item("clutch").IsOn = false;
                Lay(cardView);
                Lay(deviceView);
                Marked(cardView, card.Item("dead"), false);
                Marked(cardView, card.Item("curve"), false);
                Marked(deviceView, device.Item("clutch"), false);
                Marked(cardView, card.Item("trigger"), true);
                Assert.True(card.Item("dead").CanReset);
                Assert.True(card.Item("curve").CanReset);
            });
        }

        /// <summary>The Test Controller's By Hand switch is a kept setting
        /// and turns ember when on. The sticks, triggers and buttons it shows
        /// are moved live with the mouse and take no mark.</summary>
        [Fact]
        public void TheTestControllersByHandControls_TakeNoMark_ButByHandItselfDoes()
        {
            WithAppResources(dispatcher =>
            {
                var set = new PluginSettingSet();
                TestControllerPattern.Declare(set);
                var host = new PluginSettingsHost(set, null, "Plugins_TestController", dispatcher);
                var view = new PluginSettingsView { DataContext = host };
                host.Item(TestControllerPattern.KeyHand).IsOn = true;
                host.Item(TestControllerPattern.KeyLeftStick).Pad = new Point(40, -60);
                host.Item(TestControllerPattern.KeyRightTrigger).Number = 70;
                host.Item("hand-a").IsOn = true;
                Lay(view);
                Assert.True(host.Item(TestControllerPattern.KeyHand).IsChanged);
                Assert.Equal("Changed", host.Item(TestControllerPattern.KeyHand).ChangedStatus);
                foreach (string key in new[] { TestControllerPattern.KeyLeftStick, TestControllerPattern.KeyRightTrigger, "hand-a" })
                {
                    Assert.True(set.Keeps(null, key), key + " is not kept");
                    Assert.False(host.Item(key).IsChanged, key + " is marked");
                    Assert.Equal(string.Empty, host.Item(key).ChangedStatus);
                }
                var ember = ((SolidColorBrush)Application.Current.FindResource("EmberTextBrush")).Color;
                var trigger = Under(view).OfType<System.Windows.Controls.TextBlock>()
                    .First(t => ReferenceEquals(t.DataContext, host.Item(TestControllerPattern.KeyRightTrigger)) && t.Text == host.Item(TestControllerPattern.KeyRightTrigger).Name);
                Assert.False(trigger.Foreground is SolidColorBrush b && b.Color == ember);
            });
        }

        [Fact]
        public void TheTestControllersStickPads_StayWhereTheyAreLetGo_AndShowWhereTheyWereLeftAfterARestart()
        {
            WithAppResources(dispatcher =>
            {
                // The first run: By Hand on, the left stick set by hand.
                var first = new PluginSettingSet();
                TestControllerPattern.Declare(first);
                first.Set(null, TestControllerPattern.KeyHand, "true");
                first.Set(null, TestControllerPattern.KeyLeftStick, "40,-60");
                var saved = first.Save();

                // The next run declares and then takes its values, as
                // BuiltInPlugins does.
                var next = new PluginSettingSet();
                TestControllerPattern.Declare(next);
                next.Load(saved);
                var host = new PluginSettingsHost(next, null, "Plugins_TestController", dispatcher);
                var view = new PluginSettingsView { DataContext = host };
                Lay(view);
                var pads = Under(view).OfType<PluginPad>().ToList();
                Assert.Equal(2, pads.Count);
                // Each pad stays where the user lets go of it (PluginPad
                // centers a pad on release only without Stay).
                Assert.All(pads, pad => Assert.True(pad.Stay));
                var left = pads.Single(p => ReferenceEquals(p.DataContext, host.Item(TestControllerPattern.KeyLeftStick)));
                var right = pads.Single(p => ReferenceEquals(p.DataContext, host.Item(TestControllerPattern.KeyRightStick)));
                Assert.Equal(new Point(40, -60), left.Position);
                Assert.Equal(new Point(0, 0), right.Position);
            });
        }
    }
}
