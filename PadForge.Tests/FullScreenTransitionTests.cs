using System.Windows;
using PadForge;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #342 (Xaklse): the Full Screen button did almost nothing
    /// from a maximized window. The icon changed, the window did not.
    ///
    /// <para>WPF ignores an assignment of the WindowState a window already
    /// holds, so setting Maximized on an already-maximized window never
    /// recomputes the frame under the newly borderless style. The owner
    /// measured the user-visible shape of that exactly: it took THREE
    /// presses to reach full screen. Press one flips the flag and the style
    /// and moves nothing, press two exits to Normal, press three finally
    /// performs a real Normal to Maximized transition.</para>
    ///
    /// <para>Startup restore never showed the bug because it runs with the
    /// window deliberately left Normal, so its transition was always real.
    /// That is why the feature looked fine every launch and only failed
    /// from the maximized state.</para>
    /// </summary>
    public class FullScreenTransitionTests
    {
        /// <summary>THE BUG. From maximized, the path must bounce through
        /// Normal so the final assignment is a real transition.</summary>
        [Fact]
        public void FromMaximized_BouncesThroughNormal()
        {
            Assert.Equal(
                new[] { WindowState.Normal, WindowState.Maximized },
                MainWindow.FullScreenEnterPath(WindowState.Maximized));
        }

        /// <summary>From anywhere else a single assignment is already a
        /// real transition. The extra bounce would be a visible flash, so
        /// it must not happen on the path that always worked.</summary>
        [Theory]
        [InlineData(WindowState.Normal)]
        [InlineData(WindowState.Minimized)]
        public void FromEveryOtherState_AssignsMaximizedOnce(WindowState from)
        {
            Assert.Equal(
                new[] { WindowState.Maximized },
                MainWindow.FullScreenEnterPath(from));
        }

        /// <summary>Whatever the path, it ends Maximized. A path ending
        /// anywhere else is not full screen at all.</summary>
        [Theory]
        [InlineData(WindowState.Normal)]
        [InlineData(WindowState.Maximized)]
        [InlineData(WindowState.Minimized)]
        public void EveryPath_EndsMaximized(WindowState from)
        {
            var path = MainWindow.FullScreenEnterPath(from);
            Assert.NotEmpty(path);
            Assert.Equal(WindowState.Maximized, path[^1]);
        }

        /// <summary>The last step is always a REAL transition: the state
        /// before it differs from the state it assigns. This is the actual
        /// invariant the bug violated, stated without reference to how many
        /// steps a particular path happens to take.</summary>
        [Theory]
        [InlineData(WindowState.Normal)]
        [InlineData(WindowState.Maximized)]
        [InlineData(WindowState.Minimized)]
        public void TheFinalAssignment_IsAlwaysAChange(WindowState from)
        {
            var path = MainWindow.FullScreenEnterPath(from);
            WindowState before = path.Length > 1 ? path[^2] : from;
            Assert.NotEqual(before, path[^1]);
        }
    }
}
