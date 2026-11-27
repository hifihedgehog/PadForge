using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using PadForge.Services;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The shift-layer flyout keeps a layer shown while its slide out runs
    /// (#600 item 42), the flyouts' fallback work area is in the monitor's
    /// pixels, and the copies older builds left in the user's temp folder
    /// are deleted as the user.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class ShiftLayerFlyoutSlideTests
    {
        private static void Pump(Dispatcher dispatcher, int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background, (_, _) => frame.Continue = false, dispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        /// <summary>A layer engaged while the flyout slides out after its
        /// two seconds keeps the window: the end of the old slide, which
        /// still arrives, hides nothing. The window is moved off every
        /// screen as soon as it is shown.</summary>
        [Fact]
        public void ALayerShownWhileTheFlyoutSlidesOut_KeepsTheWindow()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var flyout = new ShiftLayerFlyout();
                try
                {
                    flyout.ShowLayer("Base", "#FF8800", null);
                    flyout.Left = -32000;
                    flyout.Top = -32000;
                    Pump(dispatcher, 400);
                    Assert.True(flyout.IsVisible);

                    flyout.HideFlyout();
                    Pump(dispatcher, 60);
                    flyout.ShowLayer("Layer 1", "#00AAFF", null);
                    flyout.Left = -32000;
                    flyout.Top = -32000;
                    Pump(dispatcher, 400);
                    Assert.Equal(Visibility.Visible, flyout.Visibility);
                    Assert.Equal("Layer 1", flyout.LayerNameText.Text);
                    // A layer set to stay is kept alive, with no second slide in.
                    Assert.True(flyout.KeepAlive());
                }
                finally
                {
                    flyout.Close();
                }
            }, TimeSpan.FromMinutes(2), "The shift-layer flyout test timed out.");
        }
    }

    [Collection("FlyoutPlacement")]
    public class FlyoutFallbackAreaTests
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint winIni);

        private const uint SPI_GETWORKAREA = 0x0030;

        /// <summary>A monitor Windows cannot describe falls back to the
        /// primary monitor's work area in its own pixels, with the system's
        /// scale: what SetWindowPos takes.</summary>
        [Fact]
        public void TheFallbackWorkArea_IsInThePrimarysPixels_AtTheSystemScale()
        {
            try
            {
                FlyoutPlacement.MonitorUnreadSeam = true;
                FlyoutPlacement.SystemDpiSeam = () => 144;
                var work = new RECT();
                Assert.True(SystemParametersInfo(SPI_GETWORKAREA, 0, ref work, 0));
                var area = FlyoutPlacement.Of(IntPtr.Zero);
                Assert.Equal(new FlyoutPlacement.WorkArea(work.Left, work.Top, work.Right, work.Bottom, 1.5), area);
            }
            finally
            {
                FlyoutPlacement.MonitorUnreadSeam = false;
                FlyoutPlacement.SystemDpiSeam = null;
            }
        }
    }

}
