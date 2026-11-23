using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The flat pictures of the Buttons section stand in their picture with
    /// room around them, as the 3D controller does. The 2D pads and the VR
    /// hands filled the picture from edge to edge: the grips of a 2D pad
    /// lay on the picture's lower edge, its triggers and the tops of the VR
    /// hands on its upper edge, under the corner buttons. Each now keeps
    /// the room the 3D view's camera keeps (ControllerModelFraming): 8% of
    /// the height above and below, at least 46 above and 50 below, and 5%
    /// of the width at each side.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class FlatPictureRoomTests
    {
        // Every folder the 2D view draws.
        private static readonly string[] s_pads =
        {
            "XBOX360", "XBOXONE", "XBOXSERIES", "DS3", "DS4", "DualSense", "DUALSENSEEDGE",
            "SWITCHPRO", "SWITCH2PRO", "STEAMDECK", "STEAMCONTROLLER", "STEAMCONTROLLER2",
        };

        // The picture in the owner's window (ControllerModelFramingTests),
        // a smaller window's, one near square, a square one and a tall one.
        private static readonly (double W, double H)[] s_views =
        {
            (1393, 581), (1100, 420), (900, 700), (640, 640), (500, 900),
        };

        private static void Lay(FrameworkElement view, double width, double height)
        {
            var size = new Size(width, height);
            view.Measure(size);
            view.Arrange(new Rect(size));
            view.UpdateLayout();
        }

        /// <summary>Checks that the art stands inside the room and fills it
        /// one way: its height or its width meets the room.</summary>
        private static void StandsInTheRoom(string what, FrameworkElement view, FrameworkElement art, double width, double height)
        {
            Rect at = art.TransformToAncestor(view).TransformBounds(new Rect(art.RenderSize));
            double above = Math.Max(ControllerModelFraming.TopClear, ControllerModelFraming.ClearShare * height);
            double below = Math.Max(ControllerModelFraming.BottomClear, ControllerModelFraming.ClearShare * height);
            double side = ControllerModelFraming.SideShare * width;
            string where = $"{what} in {width} x {height}: the art lies at {at}";
            Assert.True(at.Top >= above - 0.01, where + $", {above:0.##} is kept above it");
            Assert.True(height - at.Bottom >= below - 0.01, where + $", {below:0.##} is kept below it");
            Assert.True(at.Left >= side - 0.01 && width - at.Right >= side - 0.01, where + $", {side:0.##} is kept at each side");
            bool fillsHeight = Math.Abs(at.Height - (height - above - below)) < 0.5;
            bool fillsWidth = Math.Abs(at.Width - (width - 2 * side)) < 0.5;
            Assert.True(fillsHeight || fillsWidth, where + ", and fills neither the room's height nor its width");
            // Centered in the room.
            Assert.Equal(above + (height - above - below - at.Height) / 2, at.Top, 1);
            Assert.Equal(width / 2, at.Left + at.Width / 2, 1);
        }

        [Fact]
        public void EveryTwoDPadStandsInItsPictureWithRoomAroundIt()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var build = typeof(ControllerModel2DView).GetMethod("BuildCanvas", BindingFlags.NonPublic | BindingFlags.Instance)!;
                foreach (string pad in s_pads)
                {
                    var view = new ControllerModel2DView();
                    build.Invoke(view, new object[] { pad });
                    var art = (FrameworkElement)view.FindName("ModelCanvas");
                    foreach (var (w, h) in s_views)
                    {
                        Lay(view, w, h);
                        StandsInTheRoom(pad, view, art, w, h);
                    }
                }
            }, TimeSpan.FromMinutes(2), "the 2D picture layout test timed out");
        }

        [Fact]
        public void TheVrHandsStandInTheirPictureWithRoomAroundThem()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var hands = new VRPreviewView();
                typeof(VRPreviewView).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(hands, null);
                var art = (FrameworkElement)hands.FindName("VrCanvas");
                foreach (var (w, h) in s_views)
                {
                    Lay(hands, w, h);
                    StandsInTheRoom("VR", hands, art, w, h);
                }
            }, TimeSpan.FromMinutes(2), "the VR picture layout test timed out");
        }

        /// <summary>The room around the art is set when the view is
        /// measured, from the size it is measured at, and arranging it at
        /// that size changes nothing. Set in the arrange alone, the measure
        /// ran with the last arrange's room, and the arrange then took the
        /// art's measure back and laid the view out a second time.</summary>
        [Fact]
        public void TheRoomAroundTheArtIsSetWhenTheViewIsMeasured()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var pad = new ControllerModel2DView();
                typeof(ControllerModel2DView).GetMethod("BuildCanvas", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(pad, new object[] { "DS4" });
                var hands = new VRPreviewView();
                typeof(VRPreviewView).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(hands, null);
                foreach (var (view, box) in new[] { ((FrameworkElement)pad, "ModelBox"), (hands, "VrBox") })
                {
                    var art = (FrameworkElement)view.FindName(box);
                    foreach (var (w, h) in s_views)
                    {
                        view.Measure(new Size(w, h));
                        Assert.Equal(ControllerModelFraming.PictureRoom(w, h), art.Margin);
                        view.Arrange(new Rect(0, 0, w, h));
                        Assert.True(art.IsMeasureValid, $"{box} in {w} x {h}: arranging the view took its measure back");
                    }
                }
            }, TimeSpan.FromMinutes(2), "the picture room measure test timed out");
        }
    }
}
