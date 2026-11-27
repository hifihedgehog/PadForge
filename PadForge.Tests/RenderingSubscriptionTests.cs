using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A per-frame view keeps its CompositionTarget.Rendering handler only
    /// while it is visible and the main window can render. Any handler on
    /// that event makes WPF draw every frame, so a view in a window hidden
    /// to the tray kept the UI thread waking hundreds of times a second.
    /// </summary>
    public class RenderingSubscriptionTests
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
            t.Start();
            t.Join();
            if (failure != null) throw new Exception("STA body failed", failure);
        }

        private sealed class Counted
        {
            public int Attached;
            public int Detached;
            public int Live => Attached - Detached;
        }

        private static RenderingSubscription Make(FrameworkElement view, AmbientMotionProbe probe, Counted c)
            => new RenderingSubscription(view, (s, e) => { }, probe, h => c.Attached++, h => c.Detached++);

        [Theory]
        [InlineData(true, true, false, true)]
        [InlineData(false, true, false, false)]
        [InlineData(true, false, false, false)]
        [InlineData(true, true, true, false)]
        public void ShouldAttach_OnlyWhenWantedVisibleAndRenderable(bool wanted, bool visible, bool cannotRender, bool expected)
            => Assert.Equal(expected, RenderingSubscription.ShouldAttach(wanted, visible, cannotRender));

        [Fact]
        public void HandlerFollowsTheWindow_HiddenToTrayAndMinimized() => RunSta(() =>
        {
            var probe = new AmbientMotionProbe();
            var view = new Border { Width = 20, Height = 20 };
            var c = new Counted();
            var frames = Make(view, probe, c);
            var window = new Window
            {
                Width = 100, Height = 100, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, Content = view,
            };
            try
            {
                frames.Wanted = true;
                Assert.False(frames.IsAttached);
                Assert.Equal(0, c.Attached);

                // Positive control: a shown view gets its frames.
                window.Show();
                Assert.True(view.IsVisible);
                Assert.True(frames.IsAttached);
                Assert.Equal(1, c.Live);

                // Hidden to the tray: Window.Hide, WindowState untouched.
                window.Hide();
                Assert.False(frames.IsAttached);
                Assert.Equal(0, c.Live);

                window.Show();
                Assert.True(frames.IsAttached);

                // Minimized: the view stays IsVisible, the probe says no.
                probe.IsWindowMinimized = true;
                Assert.True(view.IsVisible);
                Assert.False(frames.IsAttached);
                probe.IsWindowMinimized = false;
                Assert.True(frames.IsAttached);

                // Not wanted: nothing brings it back.
                frames.Wanted = false;
                Assert.False(frames.IsAttached);
                probe.IsWindowMinimized = true;
                probe.IsWindowMinimized = false;
                window.Hide();
                window.Show();
                Assert.False(frames.IsAttached);
                Assert.Equal(0, c.Live);
                Assert.Equal(c.Attached, c.Detached);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void ACollapsedPage_AttachesNothing() => RunSta(() =>
        {
            var probe = new AmbientMotionProbe();
            var view = new Border { Width = 20, Height = 20 };
            var host = new Grid();
            host.Children.Add(view);
            var c = new Counted();
            var frames = Make(view, probe, c);
            var window = new Window
            {
                Width = 100, Height = 100, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, Content = host,
            };
            try
            {
                window.Show();
                host.Visibility = Visibility.Collapsed;
                frames.Wanted = true;
                Assert.False(frames.IsAttached);
                host.Visibility = Visibility.Visible;
                Assert.True(frames.IsAttached);
                host.Visibility = Visibility.Collapsed;
                Assert.False(frames.IsAttached);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public void ControllerModelView_TakesItsFramesThroughTheSubscription()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string code = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "Views", "ControllerModelView.xaml.cs"));
            Assert.DoesNotContain("CompositionTarget.Rendering +=", code);
            Assert.Contains("new PadForge.Common.RenderingSubscription(this, OnRendering)", code);
        }
    }
}
