using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The slot cards' Preset name scrolls while the pointer is on its card.
    /// The slide repeats forever, so it is ambient motion: it rests while the
    /// app is not in front, as a halo's breath does. A pointer left on a card
    /// when the user switches away by keyboard sends no mouse leave, so the
    /// hover alone would keep the slide running behind a game.
    /// </summary>
    public class MarqueeLabelTests
    {
        private const string LongName = "Xbox Series X|S Controller (Bluetooth) with a long preset name";

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

        private static Window Host(UIElement content) => new Window
        {
            Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false,
            Left = -10000, Top = -10000, Content = content,
        };

        private static void Pump(Dispatcher dispatcher, Func<bool> until, string what)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!until())
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), what + " did not happen");
                dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(15);
            }
        }

        /// <summary>The probe's handlers that belong to this label. Only the
        /// label's own are called, so halos on other test threads are not
        /// touched from this one.</summary>
        private static Delegate[] Listeners(MarqueeLabel label)
        {
            var field = typeof(AmbientMotionProbe).GetField(nameof(AmbientMotionProbe.PropertyChanged),
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var all = (Delegate)field.GetValue(AmbientMotionProbe.Instance);
            return all == null
                ? Array.Empty<Delegate>()
                : all.GetInvocationList().Where(d => ReferenceEquals(d.Target, label)).ToArray();
        }

        private static void TellTheLabel(MarqueeLabel label)
        {
            var mine = Listeners(label);
            Assert.True(mine.Length > 0, "the label does not listen to the ambient motion probe");
            foreach (var d in mine)
                d.DynamicInvoke(AmbientMotionProbe.Instance, new PropertyChangedEventArgs(nameof(AmbientMotionProbe.IsAppActive)));
        }

        [Fact]
        public void TheSlideRests_WhileTheAppIsNotInFront() => RunSta(() =>
        {
            var motion = MarqueeLabel.MotionEnabled;
            var front = MarqueeLabel.AppInFront;
            bool inFront = true;
            MarqueeLabel.MotionEnabled = () => true;
            MarqueeLabel.AppInFront = () => inFront;
            var label = new MarqueeLabel { Text = LongName, Width = 120, Height = 20 };
            var window = Host(label);
            try
            {
                window.Show();
                var dispatcher = window.Dispatcher;
                Pump(dispatcher, () => label.IsLoaded && label.ActualWidth > 0, "the load");
                var rest = (TextBlock)VisualTreeHelper.GetChild(label, 0);
                var track = (Canvas)VisualTreeHelper.GetChild(label, 1);
                var shift = (TranslateTransform)((TextBlock)track.Children[0]).RenderTransform;

                // Positive control: in front, the hover starts the slide.
                label.Active = true;
                Assert.True(shift.HasAnimatedProperties, "the slide did not start in front: the checks below prove nothing");
                Assert.Equal(Visibility.Hidden, rest.Visibility);

                // The app leaves the front with the pointer still on the card.
                inFront = false;
                TellTheLabel(label);
                Assert.False(shift.HasAnimatedProperties, "the slide runs on while the app is behind");
                Assert.Equal(Visibility.Visible, rest.Visibility);

                // A hover while the app is away starts nothing.
                label.Active = false;
                label.Active = true;
                Assert.False(shift.HasAnimatedProperties, "a hover started the slide while the app is behind");

                // Back in front, the card still hovered: the slide comes back.
                inFront = true;
                TellTheLabel(label);
                Assert.True(shift.HasAnimatedProperties, "the slide did not come back with the app");

                // Out of the window, the label lets go of the probe.
                window.Content = null;
                Pump(dispatcher, () => !label.IsLoaded, "the unload");
                Assert.Empty(Listeners(label));
                Assert.False(shift.HasAnimatedProperties);
            }
            finally
            {
                MarqueeLabel.MotionEnabled = motion;
                MarqueeLabel.AppInFront = front;
                window.Close();
            }
        });
    }
}
