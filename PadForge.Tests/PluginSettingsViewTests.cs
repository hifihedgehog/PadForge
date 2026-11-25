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
