using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Serialization;
using PadForge.Common.Crosshairs;
using PadForge.Common.Input;
using PadForge.Controls;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using InputManager = PadForge.Common.Input.InputManager;

namespace PadForge.Tests
{
    [CollectionDefinition("CrosshairStatics", DisableParallelization = true)]
    public class CrosshairStaticsCollection { }

    /// <summary>
    /// The crosshairs and the magnifier (#585): the shapes against the CS:GO
    /// crosshair generator's math and Crosshair-Overlay's, the window placed on
    /// whole pixels of its display, the drawn pixels, the magnifier's three
    /// modes against Microsoft's two Magnification samples, the settings, the
    /// macros in both dispatch loops, and the service that keeps the windows in
    /// line. Windows are made on displays far off the desktop
    /// (<see cref="DisplayBounds.Seam"/>), and Windows' full-screen zoom is set
    /// only through a recorder (<see cref="MagnifierNative.FullScreenSeam"/>).
    /// </summary>
    [Collection("CrosshairStatics")]
    public class CrosshairTests
    {
        // ── The harness ──

        private static void OnSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception e) { failure = e; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Pump(int ms)
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

        /// <summary>A display far off the desktop, so a window on it shows on
        /// no monitor.</summary>
        private static readonly Int32Rect Away = new(-3000, -3000, 800, 600);

        private static void OnDisplay(Int32Rect display, Action body)
        {
            DisplayBounds.Seam = _ => display;
            try { body(); }
            finally { DisplayBounds.Seam = null; }
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        private static int CountOf(string text, string part) => Regex.Matches(text, Regex.Escape(part)).Count;

        private static object GetPrivate(object target, string field)
            => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        private static void SetPrivate(object target, string field, object value)
            => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        private static void Invoke(object target, string method, params object[] args)
            => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(target, args);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const uint GW_CHILD = 5;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);

        [DllImport("user32.dll")]
        private static extern int GetWindowRgn(IntPtr hWnd, IntPtr region);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint key, out byte alpha, out uint flags);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PtInRegion(IntPtr region, int x, int y);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr obj);

        private static IntPtr Handle(Window window) => new WindowInteropHelper(window).EnsureHandle();

        private static Int32Rect RectOf(IntPtr hwnd)
        {
            Assert.True(GetWindowRect(hwnd, out var r));
            return new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        /// <summary>Whether a window's region holds a point of the window.</summary>
        private static bool RegionHolds(IntPtr hwnd, int x, int y)
        {
            var region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                Assert.NotEqual(0, GetWindowRgn(hwnd, region));
                return PtInRegion(region, x, y);
            }
            finally { DeleteObject(region); }
        }

        private static byte[] Png(int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixel)
        {
            var bgra = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    var (b, g, r, a) = pixel(x, y);
                    int i = (y * width + x) * 4;
                    bgra[i] = b;
                    bgra[i + 1] = g;
                    bgra[i + 2] = r;
                    bgra[i + 3] = a;
                }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4)));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        /// <summary>A picture file, deleted when the test ends.</summary>
        private static void WithPicture(int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixel, Action<string> test)
        {
            string path = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Crosshair." + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                File.WriteAllBytes(path, Png(width, height, pixel));
                test(path);
            }
            finally { File.Delete(path); }
        }

        /// <summary>Whether a shape holds a point. Geometry.FillContains(Point)
        /// counts a point within its flattening tolerance of an edge, a quarter
        /// pixel, as inside, so the shapes are asked at a thousandth.</summary>
        private static bool Inside(Geometry shape, double x, double y)
            => shape.FillContains(new Point(x, y), 0.001, ToleranceType.Absolute);

        private static void AssertRect(Rect expected, Rect actual, double within = 0.01)
        {
            Assert.InRange(actual.X, expected.X - within, expected.X + within);
            Assert.InRange(actual.Y, expected.Y - within, expected.Y + within);
            Assert.InRange(actual.Width, expected.Width - within, expected.Width + within);
            Assert.InRange(actual.Height, expected.Height - within, expected.Height + within);
        }

        // ── The shapes ──

        /// <summary>The Cross is the CS:GO generator's classic crosshair
        /// (javascript.js updateCrosshair): each arm a thickness wide and a
        /// length long, offset from the center by half its thickness plus the
        /// gap, and the dot a thickness square.</summary>
        [Fact]
        public void TheCross_IsTheGeneratorsClassicCrosshair()
            => OnSta(() =>
            {
                var look = new CrosshairData { Thickness = 2, Length = 10, Gap = 4, Outline = false };
                var shape = CrosshairShape.Of(look);
                Assert.False(shape.IsImage);
                Assert.Equal(0, shape.OutlinePenWidth);
                AssertRect(new Rect(-15, -15, 30, 30), shape.Bounds);
                Assert.True(Inside(shape.Body, 5.5, 0), "an arm begins half its thickness plus the gap out");
                Assert.False(Inside(shape.Body, 4.5, 0), "the gap is clear");
                Assert.True(Inside(shape.Body, 14.5, 0.9));
                Assert.False(Inside(shape.Body, 14.5, 1.1), "an arm is a thickness wide");
                Assert.False(Inside(shape.Body, 15.5, 0), "and a length long");
                Assert.True(Inside(shape.Body, 0, -6));
                Assert.True(Inside(shape.Body, 0, 6));
                Assert.True(Inside(shape.Body, -6, 0));
                Assert.False(Inside(shape.Body, 0, 0), "no dot unless asked");

                look.CenterDot = true;
                var dotted = CrosshairShape.Of(look);
                Assert.True(Inside(dotted.Body, 0.9, 0.9));
                Assert.False(Inside(dotted.Body, 1.1, 0), "the dot is a thickness square");

                look.TShape = true;
                var t = CrosshairShape.Of(look);
                Assert.False(Inside(t.Body, 0, -6), "a T has no top arm");
                Assert.True(Inside(t.Body, 0, 6));
                AssertRect(new Rect(-15, -1, 30, 16), t.Bounds);

                // A negative gap overlaps the arms into Crosshair-Overlay's plain cross.
                var plain = CrosshairShape.Of(new CrosshairData { Thickness = 2, Length = 10, Gap = -1, Outline = false });
                Assert.True(Inside(plain.Body, 0.5, 0));
                AssertRect(new Rect(-10, -10, 20, 20), plain.Bounds);
            });

        /// <summary>An outline grows the shape by its thickness on every side, as
        /// Crosshair-Overlay's outlined styles draw quads that much larger
        /// first. The pen is twice as wide, centered on the edge, and the fill
        /// covers its inner half.</summary>
        [Fact]
        public void TheOutline_GrowsTheShapeByItsThickness()
            => OnSta(() =>
            {
                var shape = CrosshairShape.Of(new CrosshairData { Thickness = 2, Length = 10, Gap = 4, Outline = true, OutlineThickness = 2 });
                Assert.Equal(4, shape.OutlinePenWidth);
                AssertRect(new Rect(-17, -17, 34, 34), shape.Bounds);
                var pen = CrosshairShape.OutlinePen(4);
                Assert.Equal(PenLineJoin.Miter, pen.LineJoin);
                Assert.True(pen.IsFrozen);
            });

        /// <summary>The Circle is Crosshair-Overlay's polygon (Drawing.cpp
        /// STYLE_POLYGON): its sides on the radius, the first vertex straight
        /// above the center, round at the most sides.</summary>
        [Fact]
        public void TheCircle_IsAPolygonOnItsRadius_RoundAtTheMostSides()
            => OnSta(() =>
            {
                var round = CrosshairShape.Of(new CrosshairData { Style = CrosshairStyle.Circle, Radius = 12, Thickness = 2, Sides = 64, Outline = false });
                AssertRect(new Rect(-13, -13, 26, 26), round.Bounds, 0.05);
                Assert.True(Inside(round.Body, 12, 0));
                Assert.True(Inside(round.Body, 0, 12.9));
                Assert.False(Inside(round.Body, 10.5, 0), "a ring, not a disc");
                Assert.False(Inside(round.Body, 0, 0));

                var diamond = CrosshairShape.Of(new CrosshairData { Style = CrosshairStyle.Circle, Radius = 12, Thickness = 2, Sides = 4, Outline = false });
                Assert.True(Inside(diamond.Body, 0, -12), "the first vertex is straight above the center");
                Assert.True(Inside(diamond.Body, 6, -6), "the side runs vertex to vertex");
                Assert.False(Inside(diamond.Body, 8.5, -8.5), "a square on its point, not a circle");

                var dotted = CrosshairShape.Of(new CrosshairData { Style = CrosshairStyle.Circle, Radius = 12, Thickness = 2, CenterDot = true, Outline = false });
                Assert.True(Inside(dotted.Body, 0, 0));
            });

        [Fact]
        public void TheDot_IsItsSize()
            => OnSta(() =>
            {
                var shape = CrosshairShape.Of(new CrosshairData { Style = CrosshairStyle.Dot, DotSize = 6, Outline = false });
                AssertRect(new Rect(-3, -3, 6, 6), shape.Bounds, 0.05);
                Assert.True(Inside(shape.Body, 2.5, 0));
                Assert.False(Inside(shape.Body, 3.5, 0));
            });

        /// <summary>A picture sits centered at the width and height it is given,
        /// as Crosshair-Overlay draws its image at the center less half its
        /// dimensions, and turns about the center.</summary>
        [Fact]
        public void APicture_IsCenteredAtItsSize_AndTurnsAboutTheCenter()
            => OnSta(() =>
            {
                var shape = CrosshairShape.Of(new CrosshairData { Style = CrosshairStyle.Image, ImageWidth = 64, ImageHeight = 32 });
                Assert.True(shape.IsImage);
                Assert.Null(shape.Body);
                Assert.Equal(new Rect(-32, -16, 64, 32), shape.ImageRect);
                AssertRect(new Rect(-32, -16, 64, 32), shape.Bounds);

                var turned = CrosshairShape.Of(new CrosshairData { Style = CrosshairStyle.Image, ImageWidth = 64, ImageHeight = 32, Rotation = 90 });
                AssertRect(new Rect(-16, -32, 32, 64), turned.Bounds);
            });

        [Fact]
        public void TheRotation_TurnsTheShapeAboutItsCenter()
            => OnSta(() =>
            {
                var turned = CrosshairShape.Of(new CrosshairData { Thickness = 2, Length = 10, Gap = 4, Outline = false, Rotation = 45 });
                double d = 10 / Math.Sqrt(2);
                Assert.True(Inside(turned.Body, d, d), "the right arm turns clockwise toward the bottom right");
                Assert.False(Inside(turned.Body, 10, 0));
            });

        /// <summary>A file edited by hand or written by a later version comes
        /// back inside every range.</summary>
        [Fact]
        public void Normalize_HoldsEveryValueToItsRange()
        {
            var d = new CrosshairData
            {
                Style = (CrosshairStyle)42, Display = null, OffsetX = double.NaN, OffsetY = 1e9, Opacity = 7,
                OutlineThickness = 0, Rotation = 500, Length = -5, Thickness = double.PositiveInfinity, Gap = -999,
                Radius = 0, Sides = 1000, DotSize = 0, ImagePath = null, ImageWidth = 0, ImageHeight = 1e7,
            }.Normalize();
            Assert.Equal(CrosshairStyle.Cross, d.Style);
            Assert.Equal(string.Empty, d.Display);
            Assert.Equal(0, d.OffsetX);
            Assert.Equal(CrosshairData.MaxOffset, d.OffsetY);
            Assert.Equal(1, d.Opacity);
            Assert.Equal(CrosshairData.MinOutline, d.OutlineThickness);
            Assert.Equal(359, d.Rotation);
            Assert.Equal(CrosshairData.MinLength, d.Length);
            Assert.Equal(2, d.Thickness);
            Assert.Equal(CrosshairData.MinGap, d.Gap);
            Assert.Equal(CrosshairData.MinRadius, d.Radius);
            Assert.Equal(CrosshairData.MaxSides, d.Sides);
            Assert.Equal(CrosshairData.MinDotSize, d.DotSize);
            Assert.Equal(string.Empty, d.ImagePath);
            Assert.Equal(CrosshairData.MinImageSize, d.ImageWidth);
            Assert.Equal(CrosshairData.MaxImageSize, d.ImageHeight);

            var m = new MagnifierData
            {
                Mode = (MagnifierMode)9, Follow = (MagnifierFollow)9, LensShape = (LensShape)9, Display = null,
                Zoom = 99, LensWidth = 1, LensHeight = double.NaN, BorderThickness = 0, SpotlightSize = 1, SpotlightDim = 2,
            }.Normalize();
            Assert.Equal(MagnifierMode.Lens, m.Mode);
            Assert.Equal(MagnifierFollow.ScreenCenter, m.Follow);
            Assert.Equal(LensShape.Circle, m.LensShape);
            Assert.Equal(string.Empty, m.Display);
            Assert.Equal(16, m.Zoom);
            Assert.Equal(MagnifierData.MinLensSize, m.LensWidth);
            Assert.Equal(400, m.LensHeight);
            Assert.Equal(MagnifierData.MinBorder, m.BorderThickness);
            Assert.Equal(MagnifierData.MinSpotlightSize, m.SpotlightSize);
            Assert.Equal(MagnifierData.MaxDim, m.SpotlightDim);
        }

        // ── The place on the display ──

        /// <summary>Crosshair-Overlay rounds its window's center to a whole pixel
        /// (Window.cpp GetOverlayCenter) and floors the offset (Drawing.cpp
        /// DrawOverlay).</summary>
        [Fact]
        public void TheCenter_IsTheDisplaysCenterRounded_PlusTheOffsetFloored()
        {
            Assert.Equal((960, 540), CrosshairPlace.Center(new Int32Rect(0, 0, 1920, 1080), 0, 0));
            Assert.Equal((683, 384), CrosshairPlace.Center(new Int32Rect(0, 0, 1365, 767), 0, 0));
            Assert.Equal((962, 537), CrosshairPlace.Center(new Int32Rect(0, 0, 1920, 1080), 2.7, -2.2));
            Assert.Equal((-960, 540), CrosshairPlace.Center(new Int32Rect(-1920, 0, 1920, 1080), 0, 0));
        }

        [Fact]
        public void TheWindow_CoversTheCrosshair_CutToItsDisplay()
        {
            var display = new Int32Rect(0, 0, 1920, 1080);
            var bounds = new Rect(-17, -17, 34, 34);
            Assert.Equal(new Int32Rect(942, 522, 36, 36), CrosshairPlace.Window(display, (960, 540), bounds, out var origin));
            Assert.Equal(new Point(18, 18), origin);

            Assert.Equal(new Int32Rect(0, 522, 23, 36), CrosshairPlace.Window(display, (5, 540), bounds, out origin));
            Assert.Equal(new Point(5, 18), origin);

            var off = CrosshairPlace.Window(display, (-100, 540), bounds, out _);
            Assert.Equal(0, off.Width);
            Assert.Equal(0, CrosshairPlace.Window(default, (0, 0), bounds, out _).Width);
        }

        [Fact]
        public void ThePreview_ShrinksOnlyWhatDoesNotFit()
        {
            Assert.Equal(1, CrosshairView.FitScale(new Rect(-15, -15, 30, 30), 176, 112));
            Assert.Equal(0.44, CrosshairView.FitScale(new Rect(-200, -10, 400, 20), 176, 112), 6);
            Assert.Equal(0.28, CrosshairView.FitScale(new Rect(-10, -200, 20, 400), 176, 112), 6);
            Assert.Equal(1, CrosshairView.FitScale(Rect.Empty, 176, 112));
        }

        // ── The pixels ──

        /// <summary>The view draws in the display's pixels: a 96 dpi bitmap of a
        /// view at the display's scale holds the crosshair on whole
        /// pixels.</summary>
        private static BitmapSource Rendered(CrosshairData look, Point origin, int side)
        {
            var view = new CrosshairView { Look = look, Origin = origin };
            var dpi = VisualTreeHelper.GetDpi(view);
            view.Measure(new Size(side / dpi.DpiScaleX, side / dpi.DpiScaleY));
            view.Arrange(new Rect(0, 0, side / dpi.DpiScaleX, side / dpi.DpiScaleY));
            view.UpdateLayout();
            var target = new RenderTargetBitmap(side, side, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            target.Render(view);
            return target;
        }

        private static (byte B, byte G, byte R, byte A) PixelOf(BitmapSource bitmap, int x, int y)
        {
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return (pixel[0], pixel[1], pixel[2], pixel[3]);
        }

        [Fact]
        public void TheView_DrawsTheCross_OnWholePixels()
            => OnSta(() =>
            {
                var look = new CrosshairData { Thickness = 2, Length = 10, Gap = 4, Outline = false, ColorR = 0, ColorG = 255, ColorB = 0 };
                var bitmap = Rendered(look, new Point(20, 20), 40);
                // The right arm: x 25 to 34, y 19 and 20.
                Assert.Equal((0, 255, 0, 255), PixelOf(bitmap, 25, 19));
                Assert.Equal((0, 255, 0, 255), PixelOf(bitmap, 34, 20));
                Assert.Equal(0, PixelOf(bitmap, 24, 19).A);
                Assert.Equal(0, PixelOf(bitmap, 35, 19).A);
                Assert.Equal(0, PixelOf(bitmap, 25, 21).A);
                // The top arm: y 5 to 14.
                Assert.Equal((0, 255, 0, 255), PixelOf(bitmap, 19, 5));
                Assert.Equal(0, PixelOf(bitmap, 19, 15).A);
                Assert.Equal(0, PixelOf(bitmap, 20, 20).A);

                look.Outline = true;
                look.OutlineThickness = 1;
                var outlined = Rendered(look, new Point(20, 20), 40);
                Assert.Equal((0, 0, 0, 255), PixelOf(outlined, 25, 18));
                Assert.Equal((0, 0, 0, 255), PixelOf(outlined, 24, 19));
                Assert.Equal((0, 255, 0, 255), PixelOf(outlined, 25, 19));
                Assert.Equal(0, PixelOf(outlined, 25, 17).A);

                // Half the opacity halves the crosshair, the outline under it unseen.
                look.Opacity = 0.5;
                var faded = Rendered(look, new Point(20, 20), 40);
                var arm = PixelOf(faded, 27, 19);
                Assert.InRange(arm.A, 126, 129);
                Assert.Equal(0, arm.R);
                Assert.InRange(arm.G, 126, 129);
            });

        /// <summary>A picture draws stretched to its width and height, its
        /// transparent pixels clear, scaled by its nearest pixels while Smooth
        /// Edges is off.</summary>
        [Fact]
        public void APicture_DrawsAtItsSize_WithItsTransparency()
            => OnSta(() => WithPicture(2, 2, (x, y) => (x, y) switch
            {
                (0, 0) => (0, 0, 255, 255),
                (1, 0) => (255, 0, 0, 255),
                (0, 1) => (0, 255, 0, 255),
                _ => (0, 0, 0, 0),
            }, path =>
            {
                var look = new CrosshairData { Style = CrosshairStyle.Image, ImagePath = path, ImageWidth = 20, ImageHeight = 10, KeepProportions = false };
                var bitmap = Rendered(look, new Point(20, 20), 40);
                // The picture covers x 10 to 29 and y 15 to 24.
                Assert.Equal((0, 0, 255, 255), PixelOf(bitmap, 12, 16));
                Assert.Equal((255, 0, 0, 255), PixelOf(bitmap, 27, 16));
                Assert.Equal((0, 255, 0, 255), PixelOf(bitmap, 12, 23));
                Assert.Equal(0, PixelOf(bitmap, 27, 23).A);
                Assert.Equal(0, PixelOf(bitmap, 8, 20).A);

                // No file, no drawing.
                var missing = Rendered(new CrosshairData { Style = CrosshairStyle.Image, ImagePath = path + ".gone" }, new Point(20, 20), 40);
                Assert.Equal(0, PixelOf(missing, 20, 20).A);
            }));

        [Fact]
        public void APictureFile_IsReadOnce_AndAgainWhenItChanges()
            => OnSta(() => WithPicture(4, 3, (_, _) => (0, 0, 255, 255), path =>
            {
                var first = CrosshairPictures.Load(path);
                Assert.Equal(4, first.PixelWidth);
                Assert.True(first.IsFrozen);
                Assert.Same(first, CrosshairPictures.Load(path));
                File.WriteAllBytes(path, Png(6, 5, (_, _) => (0, 0, 255, 255)));
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
                Assert.Equal(6, CrosshairPictures.Load(path).PixelWidth);
                Assert.Null(CrosshairPictures.Load(path + ".gone"));
                Assert.Null(CrosshairPictures.Load(""));
                File.WriteAllText(path, "not a picture");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
                Assert.Null(CrosshairPictures.Load(path));
            }));

        /// <summary>The cache keeps the pictures read last, one for each
        /// crosshair there can be, so a picture tried and dropped does not
        /// stay in memory while PadForge runs.</summary>
        [Fact]
        public void ThePictureCache_KeepsOnlyThePicturesReadLast()
            => OnSta(() =>
            {
                var paths = new List<string>();
                try
                {
                    for (int i = 0; i <= CrosshairData.MaxCrosshairs; i++)
                    {
                        string path = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Crosshair." + Guid.NewGuid().ToString("N") + ".png");
                        File.WriteAllBytes(path, Png(2, 2, (_, _) => (0, 0, 255, 255)));
                        paths.Add(path);
                    }
                    var first = CrosshairPictures.Load(paths[0]);
                    for (int i = 1; i < CrosshairData.MaxCrosshairs; i++) Assert.NotNull(CrosshairPictures.Load(paths[i]));
                    // Read again, the first is no longer the one read longest ago.
                    Assert.Same(first, CrosshairPictures.Load(paths[0]));
                    Assert.NotNull(CrosshairPictures.Load(paths[CrosshairData.MaxCrosshairs]));
                    var cache = (System.Collections.IDictionary)typeof(CrosshairPictures)
                        .GetField("s_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
                    Assert.Equal(CrosshairData.MaxCrosshairs, cache.Count);
                    Assert.True(cache.Contains(paths[0]));
                    Assert.False(cache.Contains(paths[1]));
                    Assert.True(cache.Contains(paths[CrosshairData.MaxCrosshairs]));
                }
                finally
                {
                    foreach (var path in paths) File.Delete(path);
                }
            });

        // ── The window ──

        [Fact]
        public void TheWindow_CarriesItsStyles_BeforeItFirstShows()
            => OnSta(() =>
            {
                var window = new CrosshairOverlay("Crosshair 1");
                try
                {
                    Assert.False(window.ShowActivated);
                    Assert.False(window.Focusable);
                    Assert.False(window.ShowInTaskbar);
                    Assert.True(window.Topmost);
                    Assert.True(window.AllowsTransparency);
                    Assert.Equal(WindowStyle.None, window.WindowStyle);
                    Assert.Equal(FlowDirection.LeftToRight, window.FlowDirection);
                    int style = GetWindowLong(Handle(window), GWL_EXSTYLE);
                    Assert.NotEqual(0, style & WS_EX_NOACTIVATE);
                    Assert.NotEqual(0, style & WS_EX_TOOLWINDOW);
                    Assert.NotEqual(0, style & WS_EX_LAYERED);
                    Assert.NotEqual(0, style & WS_EX_TRANSPARENT);
                    Assert.Equal((IntPtr)3, SendMessage(Handle(window), WM_MOUSEACTIVATE, IntPtr.Zero, IntPtr.Zero));
                    Assert.False(window.IsVisible);
                }
                finally { window.Close(); }
            });

        /// <summary>The window lands where CrosshairPlace puts it, in pixels, on
        /// the display its crosshair names, and hides while none of the
        /// crosshair is on that display.</summary>
        [Fact]
        public void TheWindow_LandsOnTheCrosshairsPixels()
            => OnSta(() => OnDisplay(Away, () =>
            {
                var window = new CrosshairOverlay("Crosshair 1");
                try
                {
                    var look = new CrosshairData { Style = CrosshairStyle.Dot, DotSize = 6, OffsetX = 10, OffsetY = -4 };
                    window.ShowLook(look);
                    Pump(50);
                    Assert.True(window.IsVisible);
                    var center = CrosshairPlace.Center(Away, 10, -4);
                    Assert.Equal((-2590, -2704), center);
                    var expected = CrosshairPlace.Window(Away, center, CrosshairShape.Of(look).Bounds, out _);
                    Assert.Equal(expected, RectOf(Handle(window)));

                    // Offset off its display: down.
                    window.ShowLook(new CrosshairData { Style = CrosshairStyle.Dot, OffsetX = 3000 });
                    Assert.False(window.IsVisible);

                    // Back, and an Image crosshair with no picture is down too.
                    window.ShowLook(look);
                    Assert.True(window.IsVisible);
                    window.ShowLook(new CrosshairData { Style = CrosshairStyle.Image, ImagePath = "" });
                    Assert.False(window.IsVisible);
                }
                finally { window.Close(); }
            }));

        /// <summary>Each show reads an Image crosshair's picture again through
        /// the cache, so a file that could not be read at one show is drawn
        /// at the next, the same look or not.</summary>
        [Fact]
        public void AShow_ReadsThePictureAgain_ThatCouldNotBeReadBefore()
            => OnSta(() => OnDisplay(Away, () =>
            {
                string path = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Crosshair." + Guid.NewGuid().ToString("N") + ".png");
                var window = new CrosshairOverlay("Crosshair 1");
                try
                {
                    var look = new CrosshairData { Style = CrosshairStyle.Image, ImagePath = path, ImageWidth = 20, ImageHeight = 20 };
                    window.ShowLook(look);
                    Assert.False(window.IsVisible);
                    File.WriteAllBytes(path, Png(4, 4, (_, _) => (0, 0, 255, 255)));
                    window.ShowLook(look);
                    Assert.True(window.IsVisible);
                }
                finally
                {
                    window.Close();
                    File.Delete(path);
                }
            }));

        /// <summary>A display change queued while the window was up and run
        /// after it closed, as the engine stopped or the crosshair was
        /// removed, places nothing: a closed window cannot show again.</summary>
        [Fact]
        public void ADisplayChange_AfterTheWindowClosed_PlacesNothing()
            => OnSta(() => OnDisplay(Away, () =>
            {
                var window = new CrosshairOverlay("Crosshair 1");
                window.ShowLook(new CrosshairData { Style = CrosshairStyle.Dot, DotSize = 6 });
                Assert.True(window.IsVisible);
                SendMessage(Handle(window), 0x007E, IntPtr.Zero, IntPtr.Zero);
                window.Close();
                Pump(100);
                Assert.False(window.IsVisible);
            }));

        /// <summary>A click goes through the crosshair to the window beneath. The
        /// positive control is the same window without WS_EX_TRANSPARENT, which
        /// takes the click at the same point.</summary>
        [Fact]
        public void AClick_GoesThroughTheCrosshair()
            => OnSta(() =>
            {
                var beneath = new Window
                {
                    Left = -3000, Top = -3000, Width = 300, Height = 300, WindowStyle = WindowStyle.None,
                    ShowActivated = false, ShowInTaskbar = false,
                };
                CrosshairOverlay crosshair = null;
                try
                {
                    beneath.Show();
                    Pump(100);
                    var under = RectOf(Handle(beneath));
                    OnDisplay(under, () =>
                    {
                        crosshair = new CrosshairOverlay("Crosshair 1");
                        crosshair.ShowLook(new CrosshairData { Style = CrosshairStyle.Dot, DotSize = 80, Outline = false });
                        Pump(300);
                        var point = new POINT { X = under.X + under.Width / 2, Y = under.Y + under.Height / 2 };
                        Assert.Equal(Handle(beneath), WindowFromPoint(point));

                        IntPtr hwnd = Handle(crosshair);
                        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
                        SetWindowLong(hwnd, GWL_EXSTYLE, style & ~WS_EX_TRANSPARENT);
                        Pump(100);
                        Assert.Equal(hwnd, WindowFromPoint(point));

                        SetWindowLong(hwnd, GWL_EXSTYLE, style);
                        Pump(100);
                        Assert.Equal(Handle(beneath), WindowFromPoint(point));
                    });
                }
                finally
                {
                    crosshair?.Close();
                    beneath.Close();
                }
            });

        // ── The page ──

        [Fact]
        public void ThePage_StartsWithOneCrosshair_Off_InTheGeneratorsGreen()
        {
            var dash = new DashboardViewModel();
            var one = Assert.Single(dash.Crosshairs);
            Assert.Equal(1, one.Number);
            Assert.False(one.Enabled);
            Assert.Equal(CrosshairStyle.Cross, one.Style);
            Assert.Equal("#2EFA2E", one.ColorHex);
            Assert.Equal("#000000", one.OutlineHex);
            Assert.True(one.Outline);
            Assert.Equal(string.Format(Strings.Instance.Extras_Crosshair_Number_Format, 1), one.Title);
            Assert.False(one.OneOfSeveral);
            Assert.False(dash.CrosshairsAnyEnabled);
            Assert.False(dash.Magnifier.Enabled);
            Assert.Equal(200, dash.Magnifier.ZoomPercent);
        }

        [Fact]
        public void ThePage_AddsAndRemovesCrosshairs_AndNumbersStay()
        {
            var dash = new DashboardViewModel();
            for (int i = 0; i < 20; i++) dash.AddCrosshair();
            Assert.Equal(CrosshairData.MaxCrosshairs, dash.Crosshairs.Count);
            Assert.False(dash.CanAddCrosshair);
            Assert.Equal(Enumerable.Range(1, 10), dash.Crosshairs.Select(c => c.Number));
            dash.FindCrosshair(3).RemoveCommand.Execute(null);
            Assert.Null(dash.FindCrosshair(3));
            Assert.True(dash.CanAddCrosshair);
            dash.AddCrosshair();
            Assert.Equal(3, dash.Crosshairs[2].Number);
            while (dash.Crosshairs.Count > 1) dash.RemoveCrosshair(dash.Crosshairs[^1]);
            dash.RemoveCrosshair(dash.Crosshairs[0]);
            Assert.Single(dash.Crosshairs);
        }

        [Fact]
        public void TheTileSwitch_TurnsTheCrosshairsOff_AndBackTheOnesThatWereOn()
        {
            var dash = new DashboardViewModel();
            dash.AddCrosshair();
            dash.AddCrosshair();
            dash.FindCrosshair(2).Enabled = true;
            dash.FindCrosshair(3).Enabled = true;
            Assert.True(dash.CrosshairsAnyEnabled);
            dash.CrosshairsAnyEnabled = false;
            Assert.All(dash.Crosshairs, c => Assert.False(c.Enabled));
            Assert.Equal(0b110, dash.CrosshairsLastOn);
            dash.CrosshairsAnyEnabled = true;
            Assert.Equal(new[] { false, true, true }, dash.Crosshairs.Select(c => c.Enabled));

            var fresh = new DashboardViewModel();
            fresh.CrosshairsAnyEnabled = true;
            Assert.True(fresh.Crosshairs[0].Enabled);
        }

        /// <summary>Keep Proportions holds the picture's own shape. Off, the
        /// picture stretches to both sizes.</summary>
        [Fact]
        public void KeepProportions_MovesWidthAndHeightTogether()
            => OnSta(() => WithPicture(40, 20, (_, _) => (0, 0, 255, 255), path =>
            {
                var item = new DashboardViewModel().Crosshairs[0];
                item.Style = CrosshairStyle.Image;
                item.UsePicture(path);
                Assert.Equal((40, 20), (item.ImageWidth, item.ImageHeight));
                Assert.Equal(Path.GetFileName(path), item.ImageName);
                item.ImageWidth = 100;
                Assert.Equal(50, item.ImageHeight);
                item.ImageHeight = 10;
                Assert.Equal(20, item.ImageWidth);
                item.KeepProportions = false;
                item.ImageWidth = 300;
                Assert.Equal(10, item.ImageHeight);
                item.OwnSizeCommand.Execute(null);
                Assert.Equal((40, 20), (item.ImageWidth, item.ImageHeight));
                item.ResetSettingCommand.Execute(nameof(CrosshairItem.ImagePath));
                Assert.Equal(Strings.Instance.Extras_Crosshair_NoPictureChosen, item.ImageName);
            }));

        [Fact]
        public void TheColorBoxes_TakeAColor_AndLeaveTextThatIsNone()
        {
            var item = new DashboardViewModel().Crosshairs[0];
            item.ColorHex = "ff8000";
            Assert.Equal((255, 128, 0), (item.ColorR, item.ColorG, item.ColorB));
            Assert.Equal("#FF8000", item.ColorHex);
            item.ColorHex = "nope";
            Assert.Equal("#FF8000", item.ColorHex);
            item.OutlineHex = "#123456";
            Assert.Equal((0x12, 0x34, 0x56), (item.OutlineR, item.OutlineG, item.OutlineB));
            var magnifier = new DashboardViewModel().Magnifier;
            magnifier.BorderHex = "#00ff00";
            Assert.Equal((0, 255, 0), (magnifier.BorderR, magnifier.BorderG, magnifier.BorderB));
        }

        /// <summary>Every reset the card's rows name puts its value back, and
        /// Reset All puts them all back and turns the crosshair off.</summary>
        [Fact]
        public void EveryReset_PutsItsValueBack()
        {
            var defaults = new CrosshairData();
            var item = new DashboardViewModel().Crosshairs[0];
            item.Enabled = true;
            item.Style = CrosshairStyle.Circle;
            item.Display = @"\\.\DISPLAY2";
            item.OffsetX = 5;
            item.OffsetY = -5;
            item.ColorHex = "#010203";
            item.OpacityPercent = 30;
            item.Outline = false;
            item.OutlineThickness = 4;
            item.OutlineHex = "#040506";
            item.Rotation = 45;
            item.SmoothEdges = true;
            item.Length = 50;
            item.Thickness = 7;
            item.Gap = 9;
            item.CenterDot = true;
            item.TShape = true;
            item.Radius = 40;
            item.Sides = 6;
            item.DotSize = 20;
            item.KeepProportions = false;
            foreach (string name in new[] { "Enabled", "StyleIndex", "Display", "OffsetX", "ColorHex", "OpacityPercent", "Outline", "OutlineThickness",
                "OutlineHex", "Rotation", "SmoothEdges", "Length", "Thickness", "Gap", "CenterDot", "TShape", "Radius", "Sides", "DotSize",
                "ImagePath", "ImageWidth", "KeepProportions" })
            {
                Assert.True(CrosshairItem.CanResetSetting(name), name);
                item.ResetSettingCommand.Execute(name);
            }
            AssertDefaults(defaults, item.ToData());

            item.Enabled = true;
            item.Length = 120;
            item.ResetAllCommand.Execute(null);
            AssertDefaults(defaults, item.ToData());
        }

        private static void AssertDefaults(CrosshairData expected, CrosshairData actual)
        {
            foreach (var property in typeof(CrosshairData).GetProperties().Where(p => p.Name != nameof(CrosshairData.Number)))
                Assert.True(Equals(property.GetValue(expected), property.GetValue(actual)), property.Name);
        }

        [Fact]
        public void TheList_LoadsAndDropsWhatCannotBe()
        {
            var dash = new DashboardViewModel();
            dash.LoadCrosshairs(null);
            var one = Assert.Single(dash.Crosshairs);
            Assert.Equal(1, one.Number);
            dash.LoadCrosshairs(new[]
            {
                new CrosshairData { Number = 4, Thickness = 999, Opacity = -1, Style = CrosshairStyle.Dot },
                null,
                new CrosshairData { Number = 0 },
                new CrosshairData { Number = CrosshairData.MaxCrosshairs + 1 },
                new CrosshairData { Number = 4, Length = 1 },
                new CrosshairData { Number = 2, ImageWidth = 30, ImageHeight = 90, KeepProportions = true },
            });
            Assert.Equal(new[] { 2, 4 }, dash.Crosshairs.Select(c => c.Number));
            var four = dash.FindCrosshair(4);
            Assert.Equal((int)CrosshairData.MaxThickness, four.Thickness);
            Assert.Equal(0, four.Opacity);
            Assert.Equal(CrosshairStyle.Dot, four.Style);
            Assert.Equal((30, 90), (dash.FindCrosshair(2).ImageWidth, dash.FindCrosshair(2).ImageHeight));
            Assert.All(dash.Crosshairs, c => Assert.True(c.OneOfSeveral));
            Assert.Equal(new[] { 2, 4 }, dash.SnapshotCrosshairs().Select(c => c.Number));
        }

        [Fact]
        public void TheMagnifier_HoldsItsValuesToTheirRanges()
        {
            var item = new DashboardViewModel().Magnifier;
            item.ZoomPercent = 50;
            Assert.Equal(125, item.ZoomPercent);
            item.ZoomPercent = 5000;
            Assert.Equal(1600, item.ZoomPercent);
            item.LensWidth = 300;
            Assert.Equal(300, item.LensHeight);
            item.LensShape = LensShape.Rectangle;
            item.LensHeight = 120;
            item.LensWidth = 500;
            Assert.Equal(120, item.LensHeight);
            item.SpotlightDimPercent = 100;
            Assert.Equal(95, item.SpotlightDimPercent);
            item.ModeIndex = -1;
            Assert.Equal(MagnifierMode.Lens, item.Mode);
            item.ModeIndex = 2;
            Assert.True(item.IsSpotlight);
            Assert.False(item.HasZoom);
            item.FollowIndex = 2;
            Assert.Equal(MagnifierFollow.Crosshair, item.Follow);
            Assert.False(item.FollowsCenter);

            item.Load(new MagnifierData { LensShape = LensShape.Rectangle, LensWidth = 900, LensHeight = 200 });
            Assert.Equal((900, 200), (item.LensWidth, item.LensHeight));
            item.Load(null);
            Assert.False(item.Enabled);
            Assert.Equal(MagnifierMode.Lens, item.Mode);
            Assert.Equal((400, 400), (item.LensWidth, item.LensHeight));

            foreach (string name in new[] { "Enabled", "ModeIndex", "FollowIndex", "Display", "ZoomPercent", "LensShapeIndex", "LensWidth",
                "LensHeight", "Border", "BorderHex", "BorderThickness", "SpotlightSize", "SpotlightDimPercent" })
                Assert.True(MagnifierItem.CanResetSetting(name), name);
        }

        /// <summary>A round lens has one size, its width, and the page hides
        /// the height. A rectangle of 600 x 300 turned round is a circle of
        /// 600, never an ellipse 600 wide and 300 high.</summary>
        [Fact]
        public void ARectangleTurnedRound_TakesItsWidthAsItsHeight()
        {
            var item = new DashboardViewModel().Magnifier;
            item.LensShape = LensShape.Rectangle;
            item.LensWidth = 600;
            item.LensHeight = 300;
            var raised = new List<string>();
            item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            item.LensShapeIndex = (int)LensShape.Circle;
            Assert.Equal((600, 600), (item.LensWidth, item.LensHeight));
            Assert.Equal(600, item.ToData().LensHeight);
            Assert.Contains(nameof(MagnifierItem.LensHeight), raised);
        }

        /// <summary>A round lens's size row reads Size, and its slider, its
        /// box and its reset say Size with it. A rectangle's says Width.</summary>
        [Fact]
        public void TheLensSizeRow_NamesItsControls_AsItsLabelReads()
        {
            var item = new DashboardViewModel().Magnifier;
            var name = typeof(MagnifierItem).GetProperty("LensWidthName");
            Assert.NotNull(name);
            item.LensShape = LensShape.Circle;
            Assert.Equal(Strings.Instance.Extras_Magnifier_Size, name.GetValue(item));
            var raised = new List<string>();
            item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            item.LensShape = LensShape.Rectangle;
            Assert.Equal(Strings.Instance.Extras_Magnifier_Width, name.GetValue(item));
            Assert.Contains("LensWidthName", raised);

            string page = RepoText("PadForge.App", "Views", "ExtrasPage.xaml");
            int slider = page.IndexOf("AutomationProperties.AutomationId=\"Magnifier_LensWidth\"", StringComparison.Ordinal);
            Assert.True(slider > 0);
            int start = page.LastIndexOf("<controls:SettingRow", slider, StringComparison.Ordinal);
            int end = page.IndexOf("</controls:SettingRow>", slider, StringComparison.Ordinal);
            string row = page.Substring(start, end - start);
            Assert.Equal(3, CountOf(row, "\"{Binding LensWidthName"));
            // The width's own string names only the label a rectangle shows.
            Assert.Equal(1, CountOf(row, "Extras_Magnifier_Width"));
        }

        /// <summary>The Display box lists the Lighting tab's displays. A new list
        /// clears the box's selection, which writes null: the display stays,
        /// and goes back to the box after the list. A display unplugged stays
        /// listed, as gone, and a list that says nothing new is not
        /// swapped.</summary>
        [Fact]
        public void TheDisplayBox_ListsTheDisplays_AndAClearedSelectionWritesNothing()
        {
            ScreenDisplayChoice.DisplaysSeam = () => new[] { (@"\\.\DISPLAY1", 1920, 1080), (@"\\.\DISPLAY2", 2560, 1440) };
            try
            {
                var crosshair = new DashboardViewModel().Crosshairs[0];
                Assert.Equal(new[] { "", @"\\.\DISPLAY1", @"\\.\DISPLAY2" }, crosshair.DisplayChoices.Select(c => c.Id));
                crosshair.Display = @"\\.\DISPLAY2";
                crosshair.Display = null;
                Assert.Equal(@"\\.\DISPLAY2", crosshair.Display);

                ScreenDisplayChoice.DisplaysSeam = () => new[] { (@"\\.\DISPLAY1", 1920, 1080) };
                var raised = new List<string>();
                crosshair.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
                crosshair.RefreshDisplays();
                Assert.Equal(new[] { "", @"\\.\DISPLAY1", @"\\.\DISPLAY2" }, crosshair.DisplayChoices.Select(c => c.Id));
                Assert.Equal(string.Format(Strings.Instance.Pad_Lighting_ScreenDisplayGone, "2"), crosshair.DisplayChoices[^1].Label);
                Assert.Equal(new[] { nameof(CrosshairItem.DisplayChoices), nameof(CrosshairItem.Display) }, raised);
                raised.Clear();
                crosshair.RefreshDisplays();
                Assert.Empty(raised);

                var magnifier = new DashboardViewModel().Magnifier;
                Assert.Equal(2, magnifier.DisplayChoices.Count);
                magnifier.Display = @"\\.\DISPLAY1";
                magnifier.Display = null;
                Assert.Equal(@"\\.\DISPLAY1", magnifier.Display);
            }
            finally { ScreenDisplayChoice.DisplaysSeam = null; }
        }

        /// <summary>A language change swaps a box's lines, which clears its
        /// selection and writes -1: the setting stays.</summary>
        [Fact]
        public void AClearedSelection_LeavesTheSetting()
        {
            var item = new DashboardViewModel().Crosshairs[0];
            item.Style = CrosshairStyle.Image;
            item.StyleIndex = -1;
            Assert.Equal(CrosshairStyle.Image, item.Style);
            var magnifier = new DashboardViewModel().Magnifier;
            magnifier.Mode = MagnifierMode.FullScreen;
            magnifier.ModeIndex = -1;
            magnifier.FollowIndex = -1;
            magnifier.LensShapeIndex = -1;
            Assert.Equal(MagnifierMode.FullScreen, magnifier.Mode);
        }

        // ── The settings ──

        [Fact]
        public void EverySettingSite_CarriesTheCrosshairsAndTheMagnifier()
        {
            string settings = RepoText("PadForge.App", "Services", "SettingsService.cs");
            string window = RepoText("PadForge.App", "MainWindow.xaml.cs");
            string service = RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Equal(2, CountOf(settings, "public CrosshairData[] Crosshairs { get; set; }"));
            Assert.Equal(2, CountOf(settings, "public MagnifierData Magnifier { get; set; }"));
            Assert.Equal(1, CountOf(settings, "_mainVm.Dashboard.LoadCrosshairs(appSettings.Crosshairs);"));
            Assert.Equal(1, CountOf(settings, "_mainVm.Dashboard.LoadMagnifier(appSettings.Magnifier);"));
            Assert.Equal(1, CountOf(settings, "_mainVm.Dashboard.LoadCrosshairs(active.Crosshairs);"));
            Assert.Equal(1, CountOf(settings, "_mainVm.Dashboard.LoadMagnifier(active.Magnifier);"));
            Assert.Equal(2, CountOf(settings, "Crosshairs = _mainVm.Dashboard.SnapshotCrosshairs()"));
            Assert.Equal(2, CountOf(settings, "Magnifier = _mainVm.Dashboard.SnapshotMagnifier()"));
            Assert.Equal(1, CountOf(settings, "_mainVm.Dashboard.CrosshairsLastOn = appSettings.CrosshairsLastOn;"));
            Assert.Equal(1, CountOf(settings, "CrosshairsLastOn = _mainVm.Dashboard.CrosshairsLastOn,"));
            Assert.Equal(2, CountOf(settings, "CrosshairNumber = "));
            Assert.Equal(1, CountOf(window, "nameof(DashboardViewModel.Crosshairs)"));
            Assert.Equal(1, CountOf(window, "nameof(DashboardViewModel.Magnifier)"));
            Assert.Equal(1, CountOf(window, "nameof(DashboardViewModel.CrosshairsLastOn)"));
            Assert.Equal(1, CountOf(service, "Crosshairs = _mainVm.Dashboard.SnapshotCrosshairs(),"));
            Assert.Equal(1, CountOf(service, "Magnifier = _mainVm.Dashboard.SnapshotMagnifier(),"));
            Assert.Equal(1, CountOf(service, "_mainVm.Dashboard.LoadCrosshairs(profile.Crosshairs);"));
            Assert.Equal(1, CountOf(service, "_mainVm.Dashboard.LoadMagnifier(profile.Magnifier);"));
            Assert.Equal(1, CountOf(service, "profile.Crosshairs = snapshot.Crosshairs;"));
            Assert.Equal(1, CountOf(service, "profile.Magnifier = snapshot.Magnifier;"));
        }

        private static T RoundTrip<T>(T data)
        {
            var serializer = new XmlSerializer(typeof(T));
            using var writer = new StringWriter();
            serializer.Serialize(writer, data);
            return (T)serializer.Deserialize(new StringReader(writer.ToString()));
        }

        [Fact]
        public void TheSettings_RoundTrip()
        {
            var empty = RoundTrip(new AppSettingsData());
            Assert.Null(empty.Crosshairs);
            Assert.Null(empty.Magnifier);
            Assert.Equal(0, empty.CrosshairsLastOn);

            var app = RoundTrip(new AppSettingsData
            {
                Crosshairs = new[]
                {
                    new CrosshairData { Number = 2, Enabled = true, Style = CrosshairStyle.Image, Display = @"\\.\DISPLAY3", OffsetX = -12, OffsetY = 7,
                        ImagePath = @"C:\Pictures\scope.png", ImageWidth = 1920, ImageHeight = 1080, KeepProportions = false, SmoothEdges = true, Rotation = 30 },
                    new CrosshairData { Number = 5, Style = CrosshairStyle.Circle, Sides = 6, ColorR = 1, ColorG = 2, ColorB = 3 },
                },
                Magnifier = new MagnifierData { Enabled = true, Mode = MagnifierMode.Spotlight, Follow = MagnifierFollow.Crosshair, Zoom = 3.5, SpotlightDim = 0.4 },
                CrosshairsLastOn = 0b10010,
            });
            Assert.Equal(new[] { 2, 5 }, app.Crosshairs.Select(c => c.Number));
            var two = app.Crosshairs[0];
            Assert.True(two.Enabled);
            Assert.Equal(CrosshairStyle.Image, two.Style);
            Assert.Equal(@"\\.\DISPLAY3", two.Display);
            Assert.Equal((-12, 7), (two.OffsetX, two.OffsetY));
            Assert.Equal(@"C:\Pictures\scope.png", two.ImagePath);
            Assert.Equal((1920, 1080), (two.ImageWidth, two.ImageHeight));
            Assert.False(two.KeepProportions);
            Assert.True(two.SmoothEdges);
            Assert.Equal(30, two.Rotation);
            Assert.Equal(6, app.Crosshairs[1].Sides);
            Assert.Equal((1, 2, 3), (app.Crosshairs[1].ColorR, app.Crosshairs[1].ColorG, app.Crosshairs[1].ColorB));
            Assert.Equal(MagnifierMode.Spotlight, app.Magnifier.Mode);
            Assert.Equal(MagnifierFollow.Crosshair, app.Magnifier.Follow);
            Assert.Equal(3.5, app.Magnifier.Zoom);
            Assert.Equal(0.4, app.Magnifier.SpotlightDim);
            Assert.Equal(0b10010, app.CrosshairsLastOn);

            var profile = RoundTrip(new ProfileData { Id = "p", Crosshairs = new[] { new CrosshairData { Number = 3, Gap = -2 } }, Magnifier = new MagnifierData { Zoom = 8 } });
            Assert.Equal(-2, profile.Crosshairs[0].Gap);
            Assert.Equal(8, profile.Magnifier.Zoom);

            var old = (ProfileData)new XmlSerializer(typeof(ProfileData)).Deserialize(new StringReader("<ProfileData Id=\"old\"><Name>Old</Name></ProfileData>"));
            Assert.Null(old.Crosshairs);
            Assert.Null(old.Magnifier);
        }

        private sealed class SavedStatics : IDisposable
        {
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly List<ProfileData> _profiles = SettingsManager.Profiles;
            private readonly string _active = SettingsManager.ActiveProfileId;
            private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
            private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
            private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();
            private readonly List<int> _xbox = SettingsManager.XboxSlotOrder;
            private readonly List<int> _playStation = SettingsManager.PlayStationSlotOrder;
            private readonly List<int> _extended = SettingsManager.ExtendedSlotOrder;
            private readonly List<int> _kbm = SettingsManager.KeyboardMouseSlotOrder;
            private readonly List<int> _midi = SettingsManager.MidiSlotOrder;

            public SavedStatics()
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.Profiles = new List<ProfileData>();
                SettingsManager.ActiveProfileId = null;
                SettingsManager.PendingDefaultSnapshot = null;
                Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
                Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
                SettingsManager.SlotCreated[0] = true;
                SettingsManager.SlotEnabled[0] = true;
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                SettingsManager.XboxSlotOrder = new List<int> { 0 };
                SettingsManager.PlayStationSlotOrder = new List<int>();
                SettingsManager.ExtendedSlotOrder = new List<int>();
                SettingsManager.KeyboardMouseSlotOrder = new List<int>();
                SettingsManager.MidiSlotOrder = new List<int>();
            }

            public void Dispose()
            {
                SettingsManager.UserDevices = _devices;
                SettingsManager.UserSettings = _settings;
                SettingsManager.Profiles = _profiles;
                SettingsManager.ActiveProfileId = _active;
                SettingsManager.PendingDefaultSnapshot = null;
                SettingsManager.SlotMappingSets = _sets;
                Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
                Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
                SettingsManager.XboxSlotOrder = _xbox;
                SettingsManager.PlayStationSlotOrder = _playStation;
                SettingsManager.ExtendedSlotOrder = _extended;
                SettingsManager.KeyboardMouseSlotOrder = _kbm;
                SettingsManager.MidiSlotOrder = _midi;
            }
        }

        private static void SetValues(DashboardViewModel dash)
        {
            dash.LoadCrosshairs(new[]
            {
                new CrosshairData { Number = 1, Enabled = true, Style = CrosshairStyle.Dot, DotSize = 9, OffsetX = -30 },
                new CrosshairData { Number = 4, Style = CrosshairStyle.Image, ImagePath = "scope.png", ImageWidth = 640, ImageHeight = 360 },
            });
            dash.LoadMagnifier(new MagnifierData { Mode = MagnifierMode.FullScreen, Zoom = 4, Follow = MagnifierFollow.Pointer });
            dash.CrosshairsLastOn = 0b1001;
        }

        private static void AssertValues(DashboardViewModel dash)
        {
            Assert.Equal(new[] { 1, 4 }, dash.Crosshairs.Select(c => c.Number));
            var one = dash.FindCrosshair(1);
            Assert.True(one.Enabled);
            Assert.Equal(CrosshairStyle.Dot, one.Style);
            Assert.Equal(9, one.DotSize);
            Assert.Equal(-30, one.OffsetX);
            var four = dash.FindCrosshair(4);
            Assert.Equal("scope.png", four.ImagePath);
            Assert.Equal((640, 360), (four.ImageWidth, four.ImageHeight));
            Assert.Equal(MagnifierMode.FullScreen, dash.Magnifier.Mode);
            Assert.Equal(400, dash.Magnifier.ZoomPercent);
            Assert.Equal(MagnifierFollow.Pointer, dash.Magnifier.Follow);
        }

        [Fact]
        public void TheGlobalSettings_SurviveTheActualSaveAndLoad()
            => OnSta(() =>
            {
                using var statics = new SavedStatics();
                var vm = new MainViewModel();
                var settings = new SettingsService(vm);
                SetValues(vm.Dashboard);
                var built = (AppSettingsData)typeof(SettingsService)
                    .GetMethod("BuildAppSettings", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(settings, null);
                var loaded = RoundTrip(built);
                var fresh = new MainViewModel();
                typeof(SettingsService).GetMethod("LoadAppSettings", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(new SettingsService(fresh), new object[] { loaded });
                AssertValues(fresh.Dashboard);
                Assert.Equal(0b1001, fresh.Dashboard.CrosshairsLastOn);
            });

        [Fact]
        public void AProfile_CarriesThem_ThroughBothServices()
            => OnSta(() =>
            {
                using var statics = new SavedStatics();
                var vm = new MainViewModel();
                var settings = new SettingsService(vm);
                var input = new InputService(vm) { SettingsService = settings };
                SetValues(vm.Dashboard);

                var snapshot = input.SnapshotCurrentProfile();
                Assert.Equal(new[] { 1, 4 }, snapshot.Crosshairs.Select(c => c.Number));
                Assert.Equal(MagnifierMode.FullScreen, snapshot.Magnifier.Mode);

                var fresh = new MainViewModel();
                var freshInput = new InputService(fresh) { SettingsService = new SettingsService(fresh) };
                freshInput.ApplyProfile(snapshot);
                AssertValues(fresh.Dashboard);

                // A profile from before them has one crosshair, off, and the
                // magnifier's defaults.
                freshInput.ApplyProfile(new ProfileData { Id = "old", Name = "Old" });
                Assert.False(Assert.Single(fresh.Dashboard.Crosshairs).Enabled);
                Assert.Equal(MagnifierMode.Lens, fresh.Dashboard.Magnifier.Mode);

                var active = new ProfileData { Id = "p1", Name = "Game" };
                SettingsManager.Profiles.Add(active);
                SettingsManager.ActiveProfileId = "p1";
                settings.UpdateActiveProfileSnapshot();
                Assert.Equal(new[] { 1, 4 }, active.Crosshairs.Select(c => c.Number));
                Assert.Equal(4, active.Magnifier.Zoom);

                SetValues(fresh.Dashboard);
                SettingsManager.Profiles.Clear();
                var running = new ProfileData { Id = "p2", Name = "Other" };
                SettingsManager.Profiles.Add(running);
                SettingsManager.ActiveProfileId = "p2";
                freshInput.SaveActiveProfileState();
                Assert.Equal(new[] { 1, 4 }, running.Crosshairs.Select(c => c.Number));
                Assert.Equal(MagnifierFollow.Pointer, running.Magnifier.Follow);
            });

        // ── The macros ──

        [Fact]
        public void TheActions_SitInBothLoops_AndTheEditor()
        {
            string loops = RepoText("PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs");
            Assert.Equal(2, CountOf(loops, "RequestCrosshairToggle(action.CrosshairNumber);"));
            Assert.Equal(2, CountOf(loops, "RequestMagnifierToggle();"));
            Assert.Equal(2, CountOf(loops, "NoteMagnifierHeld();"));
            Assert.Contains("or MacroActionType.MagnifierWhileHeld;", loops);
            string catalog = RepoText("PadForge.App", "ViewModels", "MacroTypeCatalog.cs");
            Assert.Contains("ToggleCrosshair, S.MacroAction_ToggleCrosshair", catalog);
            Assert.Contains("ToggleMagnifier, S.MacroAction_ToggleMagnifier", catalog);
            Assert.Contains("MagnifierWhileHeld, S.MacroAction_Type_MagnifierWhileHeld", catalog);

            Assert.Equal(Strings.Instance.MacroAction_ToggleCrosshair, new MacroAction { Type = MacroActionType.ToggleCrosshair }.DisplayText);
            Assert.Equal(string.Format(Strings.Instance.MacroAction_ToggleCrosshair_Format, 7),
                new MacroAction { Type = MacroActionType.ToggleCrosshair, CrosshairNumber = 7 }.DisplayText);
            Assert.Equal(Strings.Instance.MacroAction_ToggleMagnifier, new MacroAction { Type = MacroActionType.ToggleMagnifier }.DisplayText);
            Assert.Equal(Strings.Instance.MacroAction_Type_MagnifierWhileHeld, new MacroAction { Type = MacroActionType.MagnifierWhileHeld }.DisplayText);

            var action = new MacroAction { Type = MacroActionType.ToggleCrosshair, CrosshairNumber = 99 };
            Assert.True(action.IsCrosshairTargetType);
            Assert.False(action.IsScreenOverlayTargetType);
            Assert.Equal(CrosshairData.MaxCrosshairs, action.CrosshairNumber);
            Assert.Equal(11, action.CrosshairChoices.Count);
            Assert.Equal(0, action.CrosshairChoices[0].Value);
            Assert.Equal(Strings.Instance.Macro_EveryCrosshair, action.CrosshairChoices[0].Display);
            action.ResetSettingCommand.Execute(nameof(MacroAction.CrosshairNumber));
            Assert.Equal(0, action.CrosshairNumber);

            // The number survives a save and a load.
            var macro = new MacroItem { Name = "C" };
            macro.Actions.Add(new MacroAction { Type = MacroActionType.ToggleCrosshair, CrosshairNumber = 4 });
            var back = SettingsService.LoadMacroFromData(SettingsService.BuildMacroDataForMacro(macro, 0), VirtualControllerType.Xbox, null);
            Assert.Equal(4, Assert.Single(back.Actions).CrosshairNumber);
        }

        private static MacroItem PressMacro(MacroAction action, bool raw)
        {
            var m = new MacroItem
            {
                Name = "585",
                IsEnabled = true,
                PadIndex = 0,
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
            };
            if (raw) m.TriggerCustomButtons = "00000001,00000000,00000000,00000000";
            else m.TriggerButtons = Gamepad.A;
            m.Actions.Add(action);
            return m;
        }

        private static void Press(InputManager im, MacroItem[] macros, bool raw, bool down)
        {
            if (raw)
            {
                var state = RawHidState.Create(8, 32, 1);
                if (down) state.Buttons[0] = 1;
                im.EvaluateSlotMacrosExtended(ref state, macros);
            }
            else
            {
                var gp = down ? new Gamepad { Buttons = Gamepad.A } : new Gamepad();
                im.EvaluateSlotMacros(ref gp, macros);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheToggles_RaiseTheirRequests_OnEverySlot(bool raw)
        {
            var im = new InputManager();
            var macros = new[]
            {
                PressMacro(new MacroAction { Type = MacroActionType.ToggleCrosshair, CrosshairNumber = 3 }, raw),
            };
            Assert.Equal(0, im.ToggleCrosshairMask);
            Press(im, macros, raw, true);
            Assert.Equal(1 << 3, im.ToggleCrosshairMask);

            var all = new InputManager();
            Press(all, new[] { PressMacro(new MacroAction { Type = MacroActionType.ToggleCrosshair }, raw) }, raw, true);
            Assert.Equal(1, all.ToggleCrosshairMask);

            var magnifier = new InputManager();
            Press(magnifier, new[] { PressMacro(new MacroAction { Type = MacroActionType.ToggleMagnifier }, raw) }, raw, true);
            Assert.Equal(1, magnifier.ToggleMagnifierRequested);
            Assert.Equal(0, magnifier.ToggleCrosshairMask);
        }

        /// <summary>Magnifier While Held beats each frame while held, and the
        /// hold lapses a moment after release, Voice Listen While Held's
        /// contract.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheHold_LastsWhileHeld_AndLapsesAfter(bool raw)
        {
            var im = new InputManager();
            var macros = new[] { PressMacro(new MacroAction { Type = MacroActionType.MagnifierWhileHeld }, raw) };
            Assert.False(im.MagnifierHeld);
            Press(im, macros, raw, true);
            Assert.True(im.MagnifierHeld);
            Press(im, macros, raw, true);
            Assert.True(im.MagnifierHeld);
            Press(im, macros, raw, false);
            Thread.Sleep(InputManager.MagnifierHoldMs * 3);
            Press(im, macros, raw, false);
            Assert.False(im.MagnifierHeld);
        }

        // ── The magnifier ──

        private static IntPtr Field(MagnifierService service, string name) => (IntPtr)GetPrivate(service, name);

        /// <summary>The lens is Microsoft's windowed magnifier: a topmost layered
        /// host the mouse goes through with a magnifier control in it, here cut
        /// to a circle with a border around it, centered on its display.</summary>
        [Fact]
        public void TheLens_IsAMagnifierControl_InAHostTheMouseGoesThrough()
            => OnSta(() => OnDisplay(Away, () =>
            {
                var service = new MagnifierService(null);
                try
                {
                    service.Show(new MagnifierData { Enabled = true, Mode = MagnifierMode.Lens, LensWidth = 200, LensHeight = 200, Zoom = 2, BorderThickness = 4 });
                    Assert.Null(service.Failure);
                    Assert.True(service.IsShowing);
                    IntPtr host = Field(service, "_host");
                    Assert.True(IsWindow(host));
                    Assert.True(IsWindowVisible(host));
                    int style = GetWindowLong(host, GWL_EXSTYLE);
                    foreach (int bit in new[] { WS_EX_TOPMOST, WS_EX_LAYERED, WS_EX_TRANSPARENT, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW })
                        Assert.NotEqual(0, style & bit);
                    Assert.True(GetLayeredWindowAttributes(host, out _, out byte alpha, out _));
                    Assert.Equal(255, alpha);
                    IntPtr view = GetWindow(host, GW_CHILD);
                    Assert.Equal(Field(service, "_view"), view);
                    var name = new StringBuilder(64);
                    GetClassName(view, name, name.Capacity);
                    Assert.Equal("Magnifier", name.ToString());

                    Assert.Equal(new Int32Rect(-2700, -2800, 200, 200), RectOf(host));
                    Assert.True(RegionHolds(host, 100, 100), "the lens's middle shows");
                    Assert.False(RegionHolds(host, 3, 3), "its corner is cut away");
                    var frame = (PixelOverlay)GetPrivate(service, "_frame");
                    Assert.Equal(new Int32Rect(-2704, -2804, 208, 208), RectOf(frame.Handle));

                    // A new zoom and a new size keep the windows, and a rectangle
                    // drops the circle's region.
                    service.Show(new MagnifierData { Enabled = true, Mode = MagnifierMode.Lens, LensShape = LensShape.Rectangle, LensWidth = 300, LensHeight = 100, Zoom = 4, Border = false });
                    Assert.Equal(host, Field(service, "_host"));
                    Assert.Equal(new Int32Rect(-2750, -2750, 300, 100), RectOf(host));
                    var none = CreateRectRgn(0, 0, 0, 0);
                    Assert.Equal(0, GetWindowRgn(host, none));
                    DeleteObject(none);
                    Assert.Null(GetPrivate(service, "_frame"));

                    service.Hide();
                    Assert.False(service.IsShowing);
                    Assert.False(IsWindow(host));
                }
                finally { service.Close(); }
            }));

        /// <summary>What the lens shows is its size over the zoom, centered on
        /// the point and kept on the display, the sample's UpdateMagWindow
        /// clamps.</summary>
        [Fact]
        public void TheLensSource_IsItsSizeOverTheZoom_KeptOnTheDisplay()
        {
            var display = new Int32Rect(0, 0, 1920, 1080);
            Assert.Equal(new Int32Rect(860, 440, 200, 200), MagnifierMath.Source(display, 960, 540, 400, 400, 2));
            Assert.Equal(new Int32Rect(0, 440, 200, 200), MagnifierMath.Source(display, 10, 540, 400, 400, 2));
            Assert.Equal(new Int32Rect(1720, 880, 200, 200), MagnifierMath.Source(display, 1919, 1079, 400, 400, 2));
            // A second display: kept on it, at its own edge.
            Assert.Equal(new Int32Rect(1920, 300, 120, 160), MagnifierMath.Source(new Int32Rect(1920, 0, 1920, 1080), 1930, 380, 300, 400, 2.5));
            Assert.Equal(new Int32Rect(760, 340, 400, 400), MagnifierMath.Lens(display, 960, 540, 400, 400));
            Assert.Equal(new Int32Rect(0, 0, 400, 400), MagnifierMath.Lens(display, 5, 5, 400, 400));
            Assert.Equal(new Int32Rect(0, 0, 1920, 1080), MagnifierMath.Lens(display, 960, 540, 4000, 4000));
        }

        /// <summary>Windows' full-screen zoom shows S at (S − offset) × zoom:
        /// the primary display's center at its center is Microsoft's SetZoom,
        /// size × (1 − 1/zoom) / 2.</summary>
        [Fact]
        public void TheFullScreenOffsets_PutThePointAtTheDisplaysCenter()
        {
            var primary = new Int32Rect(0, 0, 1920, 1080);
            Assert.Equal(((int)(1920 * (1 - 1 / 2.0) / 2), (int)(1080 * (1 - 1 / 2.0) / 2)), MagnifierMath.FullScreenOffset(primary, 960, 540, 2));
            Assert.Equal((720, 405), MagnifierMath.FullScreenOffset(primary, 960, 540, 4));
            Assert.Equal((0, 0), MagnifierMath.FullScreenOffset(primary, 0, 0, 2));
            Assert.Equal((960, 540), MagnifierMath.FullScreenOffset(primary, 1919, 1079, 2));
            // A display beside the primary: its own center, and only itself.
            var right = new Int32Rect(1920, 0, 1920, 1080);
            Assert.Equal((1440, 270), MagnifierMath.FullScreenOffset(right, 2880, 540, 2));
            Assert.Equal((960, 0), MagnifierMath.FullScreenOffset(right, 1920, 0, 2));
            var left = new Int32Rect(-1920, 0, 1920, 1080);
            Assert.Equal((-480, 270), MagnifierMath.FullScreenOffset(left, -960, 540, 2));
        }

        [Fact]
        public void TheFullScreenZoom_IsSetThroughItsFence_AndGoesBackTo1()
            => OnSta(() => OnDisplay(new Int32Rect(0, 0, 1920, 1080), () =>
            {
                var calls = new List<(float Zoom, int X, int Y)>();
                MagnifierNative.FullScreenSeam = (zoom, x, y) =>
                {
                    calls.Add((zoom, x, y));
                    return true;
                };
                var service = new MagnifierService(null);
                try
                {
                    Assert.True(MagnifierNative.OffLimitsForTest);
                    service.Show(new MagnifierData { Enabled = true, Mode = MagnifierMode.FullScreen, Zoom = 2 });
                    Assert.Equal(new[] { (2f, 480, 270) }, calls);
                    Assert.False((bool)GetPrivate(service, "_framing"), "a zoom that follows no pointer runs no frames");
                    service.Show(new MagnifierData { Enabled = true, Mode = MagnifierMode.FullScreen, Zoom = 4 });
                    Assert.Equal((4f, 720, 405), calls[^1]);
                    service.Show(new MagnifierData { Enabled = true, Mode = MagnifierMode.FullScreen, Zoom = 4 });
                    Assert.Equal(2, calls.Count);
                    service.Hide();
                    Assert.Equal((1f, 0, 0), calls[^1]);
                    Assert.False(service.IsShowing);

                    // Windows refuses: nothing stays up, and the page says so.
                    MagnifierNative.FullScreenSeam = (_, _, _) => false;
                    service.Show(new MagnifierData { Enabled = true, Mode = MagnifierMode.FullScreen, Zoom = 2 });
                    Assert.False(service.IsShowing);
                    Assert.Equal(Strings.Instance.Extras_Magnifier_Failed, service.Failure);
                }
                finally
                {
                    service.Close();
                    MagnifierNative.FullScreenSeam = null;
                }
            }));

        /// <summary>The spotlight shades its display outside a square, and the
        /// square's own window draws the circle's edge.</summary>
        [Fact]
        public void TheSpotlight_ShadesTheDisplay_AroundItsCircle()
            => OnSta(() => OnDisplay(Away, () =>
            {
                var service = new MagnifierService(null);
                try
                {
                    service.Show(new MagnifierData { Enabled = true, Mode = MagnifierMode.Spotlight, SpotlightSize = 200, SpotlightDim = 0.6 });
                    Assert.True(service.IsShowing);
                    IntPtr shade = Field(service, "_shade");
                    Assert.Equal(Away, RectOf(shade));
                    Assert.True(GetLayeredWindowAttributes(shade, out _, out byte alpha, out _));
                    Assert.Equal(SpotlightHoleView.ShadeAlpha(0.6), alpha);
                    Assert.Equal(153, alpha);
                    Assert.NotEqual(0, GetWindowLong(shade, GWL_EXSTYLE) & WS_EX_TRANSPARENT);
                    Assert.False(RegionHolds(shade, 400, 300), "the middle is cut out");
                    Assert.True(RegionHolds(shade, 10, 10), "the rest is shaded");
                    var hole = (PixelOverlay)GetPrivate(service, "_hole");
                    Assert.Equal(new Int32Rect(-2700, -2800, 200, 200), RectOf(hole.Handle));

                    service.Show(new MagnifierData { Enabled = true, Mode = MagnifierMode.Spotlight, SpotlightSize = 100, SpotlightDim = 0.9 });
                    Assert.Equal(shade, Field(service, "_shade"));
                    Assert.True(GetLayeredWindowAttributes(shade, out _, out alpha, out _));
                    Assert.Equal(230, alpha);
                    Assert.Equal(new Int32Rect(-2650, -2750, 100, 100), RectOf(hole.Handle));
                    service.Hide();
                    Assert.False(IsWindow(shade));
                }
                finally { service.Close(); }
            }));

        /// <summary>Following the crosshair, the magnifier centers on the first
        /// crosshair that is on, its offset included, as reWASD's magnifier
        /// centers on its crosshair.</summary>
        [Fact]
        public void FollowingTheCrosshair_CentersOnItsPoint()
            => OnSta(() => OnDisplay(new Int32Rect(0, 0, 1920, 1080), () =>
            {
                var calls = new List<(float Zoom, int X, int Y)>();
                MagnifierNative.FullScreenSeam = (zoom, x, y) =>
                {
                    calls.Add((zoom, x, y));
                    return true;
                };
                var service = new MagnifierService(null) { FirstCrosshair = () => (string.Empty, 100, -40) };
                try
                {
                    service.Show(new MagnifierData { Enabled = true, Mode = MagnifierMode.FullScreen, Follow = MagnifierFollow.Crosshair, Zoom = 2 });
                    Assert.Equal(MagnifierMath.FullScreenOffset(new Int32Rect(0, 0, 1920, 1080), 1060, 500, 2), (calls[^1].X, calls[^1].Y));
                    service.FirstCrosshair = () => null;
                    service.Tend();
                    Assert.Equal((480, 270), (calls[^1].X, calls[^1].Y));
                }
                finally
                {
                    service.Close();
                    MagnifierNative.FullScreenSeam = null;
                }
            }));

        // ── The service ──

        [Fact]
        public void TheService_BringsTheCrosshairsIntoLine()
            => OnSta(() => OnDisplay(Away, () =>
            {
                using var statics = new SavedStatics();
                var vm = new MainViewModel();
                var input = new InputService(vm) { SettingsService = new SettingsService(vm) };
                var dash = vm.Dashboard;
                var windows = (Dictionary<int, CrosshairOverlay>)GetPrivate(input, "_crosshairs");
                try
                {
                    Invoke(input, "SyncCrosshairs");
                    Assert.Empty(windows);

                    var one = dash.Crosshairs[0];
                    one.Enabled = true;
                    Invoke(input, "SyncCrosshairs");
                    Assert.True(windows[1].IsVisible);
                    Assert.True(one.IsRunning);
                    Assert.Equal(Strings.Instance.Common_Running, one.Status);

                    // Running it again keeps the window.
                    var window = windows[1];
                    Invoke(input, "SyncCrosshairs");
                    Assert.Same(window, windows[1]);

                    // A picture that is not there, and an offset off the display.
                    one.Style = CrosshairStyle.Image;
                    one.ImagePath = "missing.png";
                    Invoke(input, "SyncCrosshairs");
                    Assert.False(window.IsVisible);
                    Assert.Equal(Strings.Instance.Extras_Crosshair_Status_NoPicture, one.Status);
                    one.Style = CrosshairStyle.Cross;
                    one.OffsetX = 3000;
                    Invoke(input, "SyncCrosshairs");
                    Assert.Equal(Strings.Instance.Extras_Crosshair_Status_OffDisplay, one.Status);
                    one.OffsetX = 0;

                    // Macros: one by its number, then every one.
                    dash.AddCrosshair();
                    input.ToggleCrosshairs(1 << 2);
                    Assert.True(dash.FindCrosshair(2).Enabled);
                    Assert.True(windows[2].IsVisible);
                    input.ToggleCrosshairs(1);
                    Assert.All(dash.Crosshairs, c => Assert.False(c.Enabled));
                    input.ToggleCrosshairs(1);
                    Assert.All(dash.Crosshairs, c => Assert.True(c.Enabled));

                    // Removed from the list, its window closes.
                    dash.RemoveCrosshair(dash.FindCrosshair(2));
                    Invoke(input, "SyncCrosshairs");
                    Assert.False(windows.ContainsKey(2));

                    // The engine stops: every window closes, and a sync queued
                    // as it stopped opens nothing.
                    Invoke(input, "CloseCrosshairs");
                    Assert.Empty(windows);
                    Assert.False(one.IsRunning);
                    Invoke(input, "SyncCrosshairs");
                    Assert.Empty(windows);
                    Invoke(input, "OpenCrosshairs");
                    Pump(100);
                    Assert.True(windows[1].IsVisible);
                }
                finally { Invoke(input, "CloseCrosshairs"); }
            }));

        /// <summary>A display change that hides or shows a crosshair's window
        /// reaches its card: the status follows the window, with no setting
        /// changed.</summary>
        [Fact]
        public void ADisplayChange_ThatHidesOrShowsTheWindow_ReachesTheStatus()
            => OnSta(() =>
            {
                var display = Away;
                DisplayBounds.Seam = _ => display;
                using var statics = new SavedStatics();
                var vm = new MainViewModel();
                var input = new InputService(vm) { SettingsService = new SettingsService(vm) };
                var windows = (Dictionary<int, CrosshairOverlay>)GetPrivate(input, "_crosshairs");
                try
                {
                    var one = vm.Dashboard.Crosshairs[0];
                    one.Enabled = true;
                    Invoke(input, "SyncCrosshairs");
                    var window = windows[1];
                    Assert.True(window.IsVisible);
                    Assert.Equal(Strings.Instance.Common_Running, one.Status);

                    // The display goes away: WM_DISPLAYCHANGE, and the window hides.
                    display = new Int32Rect(-3000, -3000, 0, 0);
                    SendMessage(Handle(window), 0x007E, IntPtr.Zero, IntPtr.Zero);
                    Pump(100);
                    Assert.False(window.IsVisible);
                    Assert.False(one.IsRunning);
                    Assert.Equal(Strings.Instance.Extras_Crosshair_Status_OffDisplay, one.Status);

                    // It comes back, and so does the crosshair.
                    display = Away;
                    SendMessage(Handle(window), 0x007E, IntPtr.Zero, IntPtr.Zero);
                    Pump(100);
                    Assert.True(window.IsVisible);
                    Assert.Equal(Strings.Instance.Common_Running, one.Status);
                }
                finally
                {
                    Invoke(input, "CloseCrosshairs");
                    DisplayBounds.Seam = null;
                }
            });

        [Fact]
        public void TheService_ShowsTheMagnifier_WhileOnOrHeld()
            => OnSta(() => OnDisplay(new Int32Rect(0, 0, 1920, 1080), () =>
            {
                using var statics = new SavedStatics();
                var calls = new List<(float Zoom, int X, int Y)>();
                MagnifierNative.FullScreenSeam = (zoom, x, y) =>
                {
                    calls.Add((zoom, x, y));
                    return true;
                };
                var vm = new MainViewModel();
                var input = new InputService(vm) { SettingsService = new SettingsService(vm) };
                var item = vm.Dashboard.Magnifier;
                try
                {
                    item.Mode = MagnifierMode.FullScreen;
                    Invoke(input, "SyncMagnifier");
                    Assert.Empty(calls);
                    Assert.False(item.IsRunning);

                    item.Enabled = true;
                    Invoke(input, "SyncMagnifier");
                    Assert.Equal((2f, 480, 270), calls[^1]);
                    Assert.True(item.IsRunning);
                    Assert.Equal(Strings.Instance.Common_Running, item.Status);

                    item.Enabled = false;
                    Invoke(input, "SyncMagnifier");
                    Assert.Equal((1f, 0, 0), calls[^1]);
                    Assert.False(item.IsRunning);

                    // Held: up while the switch is off.
                    SetPrivate(input, "_magnifierHeld", true);
                    Invoke(input, "SyncMagnifier");
                    Assert.True(item.IsRunning);
                    SetPrivate(input, "_magnifierHeld", false);
                    Invoke(input, "SyncMagnifier");
                    Assert.False(item.IsRunning);

                    // Refused: the status says so.
                    MagnifierNative.FullScreenSeam = (_, _, _) => false;
                    item.Enabled = true;
                    Invoke(input, "SyncMagnifier");
                    Assert.False(item.IsRunning);
                    Assert.Equal(Strings.Instance.Extras_Magnifier_Failed, item.Status);
                }
                finally
                {
                    Invoke(input, "CloseCrosshairs");
                    MagnifierNative.FullScreenSeam = null;
                }
            }));

        [Fact]
        public void TheService_TakesTheMacrosOnItsTick()
        {
            string service = RepoText("PadForge.App", "Services", "InputService.cs");
            int tick = service.IndexOf("private void UiTimer_Tick(", StringComparison.Ordinal);
            Assert.True(tick > 0);
            Assert.True(service.IndexOf("TickCrosshairs();", tick, StringComparison.Ordinal) > tick);
            Assert.Equal(1, CountOf(service, "OpenCrosshairs();"));
            Assert.Equal(1, CountOf(service, "CloseCrosshairs();"));
            string own = RepoText("PadForge.App", "Services", "InputService.Crosshairs.cs");
            Assert.Contains("ToggleCrosshairs(Interlocked.Exchange(ref _inputManager.ToggleCrosshairMask, 0));", own);
            Assert.Contains("Interlocked.Exchange(ref _inputManager.ToggleMagnifierRequested, 0) != 0", own);
        }
    }
}
