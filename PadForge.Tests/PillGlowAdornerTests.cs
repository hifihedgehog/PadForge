using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The sidebar's glow draws itself when its pill moves and at no
    /// other time. A redraw is a layout pass, so a glow that redraws on every layout
    /// pass keeps the interface thread busy for as long as the window is open.</summary>
    public class PillGlowAdornerTests
    {
        private static void OnSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception e) { failure = e; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        }

        /// <summary>Lets the window run for a time, with every layout and render
        /// pass that wants to run. The timer's priority is above theirs, since
        /// passes that never stop would starve an idle one and hang the test.</summary>
        private static void Run(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static void WithGlow(Action<Border, MainWindow.PillGlowAdorner, TranslateTransform> test)
        {
            OnSta(() =>
            {
                var lift = new TranslateTransform();
                var pill = new Border { Width = 80, Height = 30, RenderTransform = lift, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                var root = new Grid();
                root.Children.Add(pill);
                var window = new Window
                {
                    Width = 200, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None, Left = -3000, Top = -3000,
                    Content = new AdornerDecorator { Child = root },
                };
                try
                {
                    window.Show();
                    Run(50);
                    var glow = new MainWindow.PillGlowAdorner(root, pill, Colors.OrangeRed, 10, 2, 14, 0.4, () => true);
                    AdornerLayer.GetAdornerLayer(root).Add(glow);
                    Run(100);
                    test(pill, glow, lift);
                    glow.Detach();
                }
                finally { window.Close(); }
            });
        }

        [Fact]
        public void AGlowOnAStillPill_DoesNotKeepDrawingItself()
            => WithGlow((_, glow, _) =>
            {
                int settled = glow.Renders;
                Assert.True(settled > 0);
                Run(300);
                Assert.Equal(settled, glow.Renders);
            });

        [Fact]
        public void AGlow_FollowsAPillThatLayoutMoves()
            => WithGlow((pill, glow, _) =>
            {
                int settled = glow.Renders;
                pill.Margin = new Thickness(0, 40, 0, 0);
                Run(100);
                Assert.True(glow.Renders > settled);
            });

        /// <summary>The hover lift is a render transform, which raises no layout
        /// pass of its own.</summary>
        [Fact]
        public void AGlow_FollowsThePillsHoverLift()
            => WithGlow((_, glow, lift) =>
            {
                int settled = glow.Renders;
                lift.Y = -2;
                Run(100);
                Assert.True(glow.Renders > settled);
            });

        [Fact]
        public void ADetachedGlow_NoLongerFollowsThePill()
            => WithGlow((_, glow, lift) =>
            {
                glow.Detach();
                int settled = glow.Renders;
                lift.Y = -2;
                Run(100);
                Assert.Equal(settled, glow.Renders);
            });
    }
}
