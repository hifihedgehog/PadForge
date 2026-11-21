using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Steam Deck's 2D art, which the Buttons picture and the web
    /// controller's Deck page both draw. A green line one pixel wide ran down
    /// the left edge of the Deck's screen: the screen's first column was
    /// green, where every other column of the screen is the screen's own
    /// dark color.
    /// </summary>
    public class SteamDeckArtTests
    {
        private static string ArtPath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "PadForge.App", "2DModels", "STEAMDECK", "SD_base.png");
        }

        [Fact]
        public void TheDecksScreenIsOneColorFromEdgeToEdge()
        {
            BitmapSource art;
            using (var file = File.OpenRead(ArtPath()))
                art = new FormatConvertedBitmap(BitmapFrame.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            int width = art.PixelWidth, height = art.PixelHeight;
            var pixels = new byte[width * height * 4];
            art.CopyPixels(pixels, width * 4, 0);
            uint At(int x, int y) => BitConverter.ToUInt32(pixels, (y * width + x) * 4);

            // The screen's color is the color at the art's middle. The screen
            // runs out from there, left, right, up and down, to the bezel.
            int cx = width / 2, cy = 500;
            uint screen = At(cx, cy);
            int left = cx, right = cx, top = cy, bottom = cy;
            while (left > 0 && At(left - 1, cy) == screen) left--;
            while (right < width - 1 && At(right + 1, cy) == screen) right++;
            while (top > 0 && At(cx, top - 1) == screen) top--;
            while (bottom < height - 1 && At(cx, bottom + 1) == screen) bottom++;

            // The bezel around the screen is as wide on the left as on the
            // right: the art is drawn about its middle.
            Assert.True(left + right == width - 1,
                $"the screen runs from column {left} to {right} of {width}, so it stands off center; column {left - 1} is #{At(left - 1, cy):X8}");

            // Every pixel of the screen is the screen's color, on every row.
            for (int y = top; y <= bottom; y++)
                for (int x = left; x <= right; x++)
                {
                    uint seen = At(x, y);
                    Assert.True(seen == screen, $"column {x}, row {y} is #{seen:X8} inside a screen of #{screen:X8}");
                }
        }
    }
}
