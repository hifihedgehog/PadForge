using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PadForge.Common;
using PadForge.Engine.Menus;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    [Collection("IconPackageRegistry")]
    public class MenuGridRenderTests
    {
        [Fact]
        public void A200PercentLabeledIconKeepsAllItsPixelsInTheWindow()
        {
            RunSta(() => WithIcon(() =>
            {
                var control = Render(200, false, 100);
                Assert.Equal(3600, CountMagenta(control.Bitmap));
                var labeled = Render(200, true, 100);
                Assert.Equal(CountMagenta(control.Bitmap), CountMagenta(labeled.Bitmap));
                Assert.All(labeled.Bounds, b => AssertInside(b, labeled.Width, labeled.Height));
                Assert.True(labeled.Height > 76);
            }));
        }

        [Theory]
        [InlineData(25, 10)]
        [InlineData(100, 10)]
        [InlineData(200, 10)]
        [InlineData(25, 100)]
        [InlineData(100, 100)]
        [InlineData(200, 100)]
        [InlineData(200, 400)]
        public void LabeledGridContentFitsAtTheSupportedSizes(int iconPercent, int menuPercent)
        {
            RunSta(() => WithIcon(() =>
            {
                var result = Render(iconPercent, true, menuPercent);
                Assert.True(CountMagenta(result.Bitmap) > 0, "the icon must actually render");
                Assert.All(result.Bounds, b => AssertInside(b, result.Width, result.Height));
                if (iconPercent == 100 && menuPercent == 100) Assert.Equal(76, result.Height);
            }));
        }

        [Theory]
        [InlineData(200, 200)]
        [InlineData(200, 400)]
        public void LargeLabeledRadialIconsKeepAllTheirPixels(int iconPercent, int menuPercent)
        {
            RunSta(() => WithIcon(() =>
            {
                var control = Render(iconPercent, false, menuPercent, MenuKind.Radial);
                var labeled = Render(iconPercent, true, menuPercent, MenuKind.Radial);
                Assert.True(CountMagenta(control.Bitmap) > 0);
                Assert.Equal(CountMagenta(control.Bitmap), CountMagenta(labeled.Bitmap));
                Assert.All(labeled.Bounds, bounds => AssertInside(bounds, labeled.Width, labeled.Height));
            }));
        }

        [Theory]
        [InlineData(100, 10)]
        [InlineData(200, 10)]
        [InlineData(100, 100)]
        public void RadialContentFitsAtSmallAndDefaultMenuScales(int iconPercent, int menuPercent)
        {
            RunSta(() => WithIcon(() =>
            {
                var rendered = Render(iconPercent, true, menuPercent, MenuKind.Radial);
                Assert.True(CountMagenta(rendered.Bitmap) > 0);
                Assert.All(rendered.Bounds, bounds => AssertInside(bounds, rendered.Width, rendered.Height));
                if (iconPercent == 100 && menuPercent == 100) Assert.Equal(348, rendered.Width);
            }));
        }

        private static void AssertInside(Rect bounds, double width, double height)
        {
            Assert.True(bounds.Left >= -0.01 && bounds.Top >= -0.01, bounds.ToString());
            Assert.True(bounds.Right <= width + 0.01 && bounds.Bottom <= height + 0.01, bounds.ToString());
        }

        private static (RenderTargetBitmap Bitmap, Rect[] Bounds, double Width, double Height)
            Render(int iconPercent, bool labels, int menuPercent, MenuKind kind = MenuKind.Grid)
        {
            var window = new MenuOverlayWindow();
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(MenuOverlayWindow).GetMethod("RefreshThemeBrushes", flags).Invoke(window, null);
                var menu = new MenuDefinitionEntry
                {
                    Kind = kind, CellCount = kind == MenuKind.Radial ? 8 : 1, ShowLabels = labels,
                    ScalePercent = menuPercent, OpacityPercent = 100,
                };
                menu.Items.Add(new MenuItemDefinition
                {
                    Index = kind == MenuKind.Radial ? 1 : 0, Label = "Menu label", Icon = "grid_render_test.png", IconScalePercent = iconPercent,
                });
                typeof(MenuOverlayWindow).GetMethod(kind == MenuKind.Radial ? "BuildRadial" : "BuildGrid", flags).Invoke(window, new object[] { menu });
                var canvas = (Canvas)window.FindName("MenuCanvas");
                var client = (FrameworkElement)window.Content;
                Assert.False(window.IsVisible);
                window.Content = null;
                var size = new Size(canvas.Width, canvas.Height);
                client.Measure(size);
                client.Arrange(new Rect(size));
                client.UpdateLayout();
                Assert.Equal(size, canvas.RenderSize);
                Assert.Single(canvas.Children.OfType<Image>());
                var bounds = canvas.Children.OfType<FrameworkElement>()
                    .Where(c => c is Image || c is TextBlock)
                    .Select(c => c.TransformToAncestor(client).TransformBounds(new Rect(c.RenderSize)))
                    .ToArray();
                if (labels) Assert.Equal(2, bounds.Length);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width),
                    (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
                // Render the window's detached content at its exact client extent.
                // No Show call is needed. Pixels outside that extent are clipped.
                bitmap.Render(client);
                return (bitmap, bounds, size.Width, size.Height);
            }
            finally { window.Close(); }
        }

        private static int CountMagenta(BitmapSource bitmap)
        {
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            int count = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                if (pixels[i] > 240 && pixels[i + 1] < 15 && pixels[i + 2] > 240 && pixels[i + 3] > 240)
                    count++;
            return count;
        }

        private static void WithIcon(Action action)
        {
            const string prefix = "padforge-grid-render-";
            string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            string root = Path.GetFullPath(Path.Combine(temp, prefix + Guid.NewGuid().ToString("N")));
            string directory = Path.Combine(root, "tenfoot", "resource", "images", "library", "controller", "binding_icons");
            var old = MenuIconResolver.SteamRootOverride;
            try
            {
                Directory.CreateDirectory(directory);
                byte[] pixels = Enumerable.Repeat(new byte[] { 255, 0, 255, 255 }, 64).SelectMany(p => p).ToArray();
                var source = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using (var stream = File.Create(Path.Combine(directory, "grid_render_test.png"))) encoder.Save(stream);
                MenuIconResolver.SteamRootOverride = root;
                Assert.NotNull(MenuIconResolver.Resolve("grid_render_test.png"));
                action();
            }
            finally
            {
                MenuIconResolver.SteamRootOverride = old;
                if (Directory.Exists(root))
                {
                    string resolved = Path.GetFullPath(root);
                    Assert.Equal(temp, Path.GetDirectoryName(resolved), ignoreCase: true);
                    Assert.StartsWith(prefix, Path.GetFileName(resolved), StringComparison.Ordinal);
                    Directory.Delete(resolved, recursive: true);
                }
            }
        }

        private static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "hidden render timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
