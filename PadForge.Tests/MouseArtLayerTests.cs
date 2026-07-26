using System.Windows.Media;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The KBM preview builds its keyboard AND its mouse inside one
    /// RebuildLayout, and only sets <c>_layoutBuilt</c> after BOTH succeed. So
    /// anything that throws while building the mouse silently stops the whole
    /// preview updating: no mouse button lights, and no KEYBOARD key lights
    /// either, because the per-frame loop returns early on that flag.
    ///
    /// <para>That failure mode is invisible to the compiler and produces no
    /// error a user can see, only a dead preview. These assert the two things
    /// the mouse build does that can throw at runtime.</para></summary>
    public class MouseArtLayerTests
    {
        public static TheoryData<string> HitGeometry() => new()
        {
            MouseArt.LmbHit,
            MouseArt.RmbHit,
            MouseArt.WheelHit,
            MouseArt.SideUpperHit,
            MouseArt.SideLowerHit,
        };

        /// <summary>Hit shapes are parsed with Geometry.Parse at build time.
        /// Malformed path data throws FormatException, which kills the build.</summary>
        [Theory]
        [MemberData(nameof(HitGeometry))]
        public void EveryHitGeometryParses(string data)
        {
            Assert.False(string.IsNullOrWhiteSpace(data));
            var g = Geometry.Parse(data);
            Assert.NotNull(g);
            Assert.False(g.Bounds.IsEmpty);
            Assert.True(g.Bounds.Width > 0 && g.Bounds.Height > 0);
        }

        public static TheoryData<string> LayerFiles() => new()
        {
            MouseArt.Line,
            MouseArt.Body,
            MouseArt.Lmb,
            MouseArt.Rmb,
            MouseArt.Wheel,
            MouseArt.SideUpper,
            MouseArt.SideLower,
        };

        /// <summary>Every layer must actually resolve as an embedded resource.
        /// A miss does not throw, which is worse: the layer keeps its Fill with
        /// NO OpacityMask, so it paints as a solid rectangle instead of the
        /// control's shape.</summary>
        [Theory]
        [MemberData(nameof(LayerFiles))]
        public void EveryLayerResourceLoads(string file)
        {
            var bmp = EmbeddedBitmaps.Load(MouseArt.Dir + file);
            Assert.NotNull(bmp);
            Assert.True(bmp.PixelWidth > 0);
            Assert.True(bmp.PixelHeight > 0);
        }

        /// <summary>Each control's mask must sit inside the canvas and cover a
        /// real area. A zero or out-of-bounds rect means the layer is invisible
        /// or clipped away.</summary>
        [Fact]
        public void EveryLayerRectIsInsideTheCanvas()
        {
            foreach (var r in new[]
            {
                MouseArt.BodyRect, MouseArt.LmbRect, MouseArt.RmbRect,
                MouseArt.WheelRect, MouseArt.SideUpperRect, MouseArt.SideLowerRect,
            })
            {
                Assert.True(r.Width > 0 && r.Height > 0);
                Assert.InRange(r.X, -1, MouseArt.W);
                Assert.InRange(r.Y, -1, MouseArt.H);
                Assert.True(r.X + r.Width <= MouseArt.W + 1);
                Assert.True(r.Y + r.Height <= MouseArt.H + 1);
            }
        }

        /// <summary>The hit shape has to cover the control it speaks for, or
        /// the pointer misses. Compared against the mask's own rect.</summary>
        [Theory]
        [InlineData("Lmb")]
        [InlineData("Rmb")]
        [InlineData("Wheel")]
        [InlineData("SideUpper")]
        [InlineData("SideLower")]
        public void HitGeometryCoversItsControl(string which)
        {
            var (hit, rect) = which switch
            {
                "Lmb" => (MouseArt.LmbHit, MouseArt.LmbRect),
                "Rmb" => (MouseArt.RmbHit, MouseArt.RmbRect),
                "Wheel" => (MouseArt.WheelHit, MouseArt.WheelRect),
                "SideUpper" => (MouseArt.SideUpperHit, MouseArt.SideUpperRect),
                _ => (MouseArt.SideLowerHit, MouseArt.SideLowerRect),
            };
            var b = Geometry.Parse(hit).Bounds;
            // Within a couple of art units of the mask it stands in for.
            Assert.InRange(b.X, rect.X - 3, rect.X + 3);
            Assert.InRange(b.Y, rect.Y - 3, rect.Y + 3);
            Assert.InRange(b.Width, rect.Width - 4, rect.Width + 4);
            Assert.InRange(b.Height, rect.Height - 4, rect.Height + 4);
        }
    }
}
