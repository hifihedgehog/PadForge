using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Themes;
using PadForge.Controls;
using PadForge.Services;
using PadForge.SteamWorkshop.Cache;
using PadForge.ViewModels;
using PadForge.Views;
using PadForge.Views.Controls;
using Wpf.Ui.Appearance;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Custom themes (#591): the color math, the file, the ten starters,
    /// the engine's floors, the list and its Dark fallback, the picture and
    /// the colors taken from it, a Steam profile's game look, the web
    /// controller's sheet, and the credits. They share the one WPF
    /// application of a test run, so the class sits in its collection, which
    /// also keeps it apart from every test that sets a profile active.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class ThemeTests
    {
        private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

        // The ten starters as the issue lists them: slug, name, look, dark, background, accent, secondary, attention.
        private static readonly (string Slug, string Name, string Look, bool Dark, string Background, string Accent, string Secondary, string Attention)[] StarterTable =
        {
            ("sonic-blue-blur", "Sonic the Hedgehog", "Blue Blur", true, "#07114A", "#FFC700", "#9FD3FF", "#FF8A3D"),
            ("sonic-night-run", "Sonic the Hedgehog", "Night Run", true, "#060B26", "#2B7BFF", "#FFD23F", "#FF9A3D"),
            ("rings-the-one-ring", "The Lord of the Rings", "The One Ring", true, "#0E0A06", "#F5B041", "#C9D6E8", "#FF8A4D"),
            ("rings-map-of-middle-earth", "The Lord of the Rings", "Map of Middle-earth", false, "#E9DDC3", "#A23B2A", "#2F5D7C", "#8A6A1F"),
            ("potter-gryffindor", "Harry Potter", "Gryffindor", true, "#170609", "#EEBA30", "#E8D9B0", "#FF7A5C"),
            ("potter-four-houses", "Harry Potter", "Four Houses", true, "#090C22", "#E0344A", "#7FA8FF", "#FFD84D"),
            ("starwars-opening-crawl", "Star Wars", "Opening Crawl", true, "#02040A", "#FFE81F", "#4BD5EE", "#FF8A3D"),
            ("starwars-dark-side", "Star Wars", "Dark Side", true, "#070303", "#FF2B2B", "#C9D1DA", "#FFB347"),
            ("startrek-lcars", "Star Trek", "LCARS", true, "#000000", "#FF9900", "#BBA6FF", "#FFEE99"),
            ("startrek-starfleet", "Star Trek", "Starfleet", true, "#0C1014", "#F2C230", "#5FA4FF", "#FF6F59"),
        };

        private static Color Hex(string text)
        {
            Assert.True(ThemeColor.TryParse(text, out Color color), text + " is no color");
            return color;
        }

        private static string RepoRoot()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(System.IO.Path.Combine(root.FullName, "PadForge.App"))) root = root.Parent;
            Assert.NotNull(root);
            return root.FullName;
        }

        /// <summary>Runs what the dispatcher has waiting: bindings, layout, a queued refresh.</summary>
        private static void Pump(Dispatcher dispatcher) => dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        /// <summary>Runs the dispatcher until a condition holds: work a click
        /// started that finishes on another thread and comes back.</summary>
        private static void Until(Dispatcher dispatcher, Func<bool> done, string what)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                Pump(dispatcher);
                if (done()) return;
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), what + " did not happen");
                Thread.Sleep(10);
            }
        }

        private static T Wait<T>(Task<T> task, Dispatcher dispatcher)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            if (!task.IsCompleted) Dispatcher.PushFrame(frame);
            return task.GetAwaiter().GetResult();
        }

        /// <summary>Puts the stock dark look back and clears what a test
        /// left in the theme manager, which every other test stands in.</summary>
        private static void BackToStock()
        {
            ThemeManager.SetPreview(null);
            ThemeManager.SetGameLook(null);
            ThemeManager.LoadRegistry(null);
            ThemeManager.Apply(2, null);
            PageRenderDump.ApplyTheme(ApplicationTheme.Dark);
            EmberTheme.ApplyAccent();
            WebThemeSheet.Refresh();
        }

        private static ThemeDefinition Custom(bool dark, string background, string accent, string secondary, string attention)
            => new ThemeDefinition
            {
                Kind = ThemeKind.File, Name = "Test", Dark = dark,
                Background = background == null ? null : Hex(background), Accent = accent == null ? null : Hex(accent),
                Secondary = secondary == null ? null : Hex(secondary), Attention = attention == null ? null : Hex(attention),
            };

        private static BitmapSource Blocks(int width, int height, params (double Share, Color Color)[] blocks)
        {
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    double at = (x + 0.5) / width, sum = 0;
                    Color color = blocks[^1].Color;
                    foreach (var (share, block) in blocks)
                    {
                        sum += share;
                        if (at < sum) { color = block; break; }
                    }
                    int i = (y * width + x) * 4;
                    pixels[i] = color.B;
                    pixels[i + 1] = color.G;
                    pixels[i + 2] = color.R;
                    pixels[i + 3] = 255;
                }
            }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        private static byte[] Png(BitmapSource picture)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(picture));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            return buffer.ToArray();
        }

        private static byte[] Jpeg(BitmapSource picture)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(picture));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            return buffer.ToArray();
        }

        /// <summary>One pixel as straight color and its alpha.</summary>
        private static Color Pixel(BitmapSource picture, int x, int y)
        {
            var one = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            one.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
        }

        private static string TempFolder()
        {
            string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PadForgeThemeTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static void Drop(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ─────────────────────────────────────────────
        //  The color math
        // ─────────────────────────────────────────────

        [Fact]
        public void EveryStoredColorComesBackFromOklch()
        {
            var off = new List<string>();
            for (int r = 0; r < 256; r += 17)
                for (int g = 0; g < 256; g += 17)
                    for (int b = 0; b < 256; b += 17)
                    {
                        var color = Color.FromRgb((byte)r, (byte)g, (byte)b);
                        Color back = Lch.From(color).ToColor();
                        if (Math.Abs(back.R - r) > 1 || Math.Abs(back.G - g) > 1 || Math.Abs(back.B - b) > 1) off.Add(color + " came back " + back);
                    }
            Assert.Empty(off);
        }

        [Fact]
        public void AColorIsSixHexDigitsAndContrastReadsTheKnownCases()
        {
            Assert.True(ThemeColor.TryParse("#FF6B2C", out Color ember));
            Assert.Equal(Color.FromRgb(0xFF, 0x6B, 0x2C), ember);
            Assert.True(ThemeColor.TryParse(" 0a0E15 ", out Color ground));
            Assert.Equal(Color.FromRgb(0x0A, 0x0E, 0x15), ground);
            Assert.Equal("#0A0E15", ThemeColor.Format(ground));
            foreach (string bad in new[] { null, "", "   ", "#FFF", "#GGGGGG", "#FF6B2C80", "red", "#FF6B2", "FF6B2C0" })
                Assert.False(ThemeColor.TryParse(bad, out _), bad + " was read as a color");

            Assert.Equal(21.0, ThemeColor.Contrast(Colors.Black, Colors.White), 6);
            Assert.Equal(1.0, ThemeColor.Contrast(ember, ember), 6);
            Assert.Equal(ThemeContrastTests.Contrast(ember, Colors.White), ThemeColor.Contrast(ember, Colors.White), 2);
        }

        [Fact]
        public void AColorIsMovedUntilItReadsAndLeftAloneWhenItDoes()
        {
            Color card = Color.FromRgb(0x12, 0x19, 0x25);
            foreach (double ratio in new[] { 3.0, 4.5, 7.0 })
            {
                foreach (Color on in new[] { card, Colors.White, Colors.Black, Color.FromRgb(0xE9, 0xDD, 0xC3) })
                {
                    foreach (Color color in new[] { on, Colors.Black, Colors.White, Color.FromRgb(0x80, 0x80, 0x80), Color.FromRgb(0xFF, 0x00, 0x00), Color.FromRgb(0x00, 0x00, 0xFF) })
                    {
                        Color moved = ThemeColor.Reads(color, on, ratio);
                        Assert.True(ThemeColor.Contrast(moved, on) >= ratio, color + " on " + on + " reads " + ThemeColor.Contrast(moved, on) + ", under " + ratio);
                        if (ThemeColor.Contrast(color, on) >= ratio) Assert.Equal(color, moved);
                    }
                }
            }
        }

        [Fact]
        public void AStoredLightnessIsReachedAtAHueAndChroma()
        {
            foreach (double target in new[] { 0.08, 0.2, 0.4, 0.6, 0.8, 0.94 })
                foreach (double hue in new[] { 30.0, 140.0, 260.0 })
                    Assert.InRange(ThemeColor.Luma(ThemeColor.WithLuma(target, 0.08, hue)), target - 0.02, target + 0.02);
        }

        // ─────────────────────────────────────────────
        //  The file
        // ─────────────────────────────────────────────

        [Fact]
        public void AThemeWithColorsOnlyIsPlainJsonThatReadsBack()
        {
            var theme = Custom(true, "#140A26", "#FF4FD8", "#29E0FF", "#FFD166");
            theme.Name = "Synthwave";
            theme.Author = "You";
            byte[] file = ThemeFile.Serialize(theme);
            Assert.Equal((byte)'{', file[0]);
            string json = Encoding.UTF8.GetString(file);
            Assert.Contains("\"name\": \"Synthwave\"", json);
            Assert.Contains("\"base\": \"dark\"", json);
            Assert.Contains("\"background\": \"#140A26\"", json);
            Assert.DoesNotContain("picture", json);

            var back = ThemeFile.Parse(file, "fallback");
            Assert.NotNull(back);
            Assert.Equal("Synthwave", back.Name);
            Assert.Equal("You", back.Author);
            Assert.True(back.Dark);
            Assert.Equal(Hex("#140A26"), back.Background);
            Assert.Equal(Hex("#FF4FD8"), back.Accent);
            Assert.Equal(Hex("#29E0FF"), back.Secondary);
            Assert.Equal(Hex("#FFD166"), back.Attention);
            Assert.Null(back.PictureBytes);
            Assert.Null(back.Picture);
        }

        [Fact]
        public void AThemeWithAPictureIsAPackageThatReadsBack()
        {
            byte[] picture = Png(Blocks(64, 16, (0.5, Colors.Red), (0.5, Colors.Blue)));
            var theme = Custom(false, "#E9DDC3", "#A23B2A", "#2F5D7C", "#8A6A1F");
            theme.Name = "Paper";
            theme.Look = "Ink";
            theme.Credits = "A test picture.";
            theme.PictureBytes = picture;
            theme.TileAt = 0.25;
            byte[] file = ThemeFile.Serialize(theme);
            Assert.Equal(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, file.Take(4).ToArray());
            using (var archive = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read))
                Assert.Equal(new[] { "picture.png", "theme.json" }, archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray());

            var back = ThemeFile.Parse(file, "fallback");
            Assert.NotNull(back);
            Assert.Equal("Paper", back.Name);
            Assert.Equal("Ink", back.Look);
            Assert.Equal("A test picture.", back.Credits);
            Assert.False(back.Dark);
            Assert.Equal(picture, back.PictureBytes);
            Assert.Equal("picture.png", back.PictureName);
            Assert.Equal(0.25, back.TileAt, 3);
            Assert.Equal(64, back.Picture.PixelWidth);
            Assert.Equal(16, back.Picture.PixelHeight);
        }

        [Fact]
        public void AFileWrittenByHandIsReadWithItsQuirksAndABadOneIsNoTheme()
        {
            // A byte order mark, comments, a trailing comma, capitalized names, a color left out.
            byte[] byHand = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(
                "{ // mine\n \"Name\": \"  By\\tHand  \", \"Base\": \"LIGHT\", \"colors\": { \"Accent\": \"ff0000\", }, }")).ToArray();
            var theme = ThemeFile.Parse(byHand, "fallback");
            Assert.NotNull(theme);
            Assert.Equal("By Hand", theme.Name);
            Assert.False(theme.Dark);
            Assert.Equal(Colors.Red, theme.Accent);
            Assert.Null(theme.Background);

            // No name in the file: the file's own name.
            Assert.Equal("fallback", ThemeFile.Parse(Encoding.UTF8.GetBytes("{}"), "fallback").Name);
            // A name longer than a label holds is cut.
            Assert.Equal(80, ThemeFile.Parse(Encoding.UTF8.GetBytes("{\"name\":\"" + new string('n', 200) + "\"}"), "f").Name.Length);
            // The tile's window stays inside the picture.
            Assert.Equal(1 - ThemeDefinition.TileShare, ThemeFile.Parse(Encoding.UTF8.GetBytes("{\"name\":\"t\",\"tile\":7}"), "f").TileAt, 6);

            foreach (string bad in new[]
                     {
                         "", "not json", "[1,2]", "\"text\"", "{\"name\":\"x\",\"base\":\"sepia\"}", "{\"name\":\"x\",\"base\":3}",
                         "{\"name\":\"x\",\"colors\":[]}", "{\"name\":\"x\",\"colors\":{\"accent\":\"#12\"}}",
                         "{\"name\":\"x\",\"colors\":{\"accent\":7}}", "{\"name\":\"x\"",
                         // Half of a surrogate pair, in a name and in a key: text no string can hold.
                         "{\"name\":\"\\uD800\"}", "{\"\\uD800x\":1,\"name\":\"x\"}",
                     })
                Assert.True(ThemeFile.Parse(Encoding.UTF8.GetBytes(bad), null) == null, bad + " was read as a theme");
            Assert.Null(ThemeFile.Parse(null, "f"));
            Assert.Null(ThemeFile.Parse(Encoding.UTF8.GetBytes("{}"), null));
        }

        [Fact]
        public void TheFileKeepsToItsLimits()
        {
            static byte[] Package(Action<ZipArchive> fill)
            {
                using var buffer = new MemoryStream();
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true)) fill(archive);
                return buffer.ToArray();
            }
            static void Put(ZipArchive archive, string name, byte[] data, CompressionLevel level = CompressionLevel.Fastest)
            {
                using var entry = archive.CreateEntry(name, level).Open();
                entry.Write(data, 0, data.Length);
            }
            byte[] manifest = Encoding.UTF8.GetBytes("{\"name\":\"Limits\",\"picture\":\"art/picture.png\"}");
            byte[] small = Png(Blocks(8, 8, (1, Colors.Green)));

            // A package with no theme.json is no theme.
            Assert.Null(ThemeFile.Parse(Package(a => Put(a, "picture.png", small)), "f"));
            // The picture the settings name is found by its path, and by its name alone.
            Assert.Equal(small, ThemeFile.Parse(Package(a => { Put(a, "theme.json", manifest); Put(a, "art/picture.png", small); }), "f").PictureBytes);
            Assert.Equal(small, ThemeFile.Parse(Package(a => { Put(a, "theme.json", manifest); Put(a, "picture.png", small); }), "f").PictureBytes);
            // Settings larger than the limit: no theme. The zeros pack to almost nothing, so the limit is on what is read.
            byte[] longManifest = Encoding.UTF8.GetBytes("{\"name\":\"x\",\"credits\":\"" + new string(' ', ThemeFile.MaxManifestBytes) + "\"}");
            Assert.Null(ThemeFile.Parse(Package(a => Put(a, "theme.json", longManifest, CompressionLevel.Optimal)), "f"));
            Assert.Null(ThemeFile.Parse(longManifest, "f"));
            // A picture larger than the limit is left out and the theme still reads.
            var huge = new byte[ThemeFile.MaxPictureBytes + 1];
            var withHuge = ThemeFile.Parse(Package(a => { Put(a, "theme.json", manifest); Put(a, "art/picture.png", huge, CompressionLevel.Optimal); }), "f");
            Assert.NotNull(withHuge);
            Assert.Null(withHuge.PictureBytes);
            // A file larger than the limit is not read at all.
            Assert.Null(ThemeFile.Parse(new byte[ThemeFile.MaxFileBytes + 1], "f"));
        }

        [Fact]
        public void AFileThatIsGoneOrBrokenKeepsItsListedNameAndSaysWhich()
        {
            string folder = TempFolder();
            try
            {
                string path = System.IO.Path.Combine(folder, "gone.pftheme");
                var missing = ThemeFile.Read(path, "Listed Name");
                Assert.Equal(ThemeFault.Missing, missing.Fault);
                Assert.Equal("Listed Name", missing.Name);
                Assert.Equal(ThemeKind.File, missing.Kind);

                File.WriteAllText(path, "not a theme");
                var broken = ThemeFile.Read(path, null);
                Assert.Equal(ThemeFault.Unreadable, broken.Fault);
                Assert.Equal("gone", broken.Name);

                var good = Custom(true, "#101010", "#FF00FF", null, null);
                good.Name = "Good";
                ThemeFile.Write(path, good);
                var read = ThemeFile.Read(path);
                Assert.Equal(ThemeFault.None, read.Fault);
                Assert.Equal("Good", read.Name);
                Assert.Equal(path, read.Path);
                // The write leaves no file beside it.
                Assert.Equal(new[] { "gone.pftheme" }, Directory.GetFiles(folder).Select(System.IO.Path.GetFileName).ToArray());
            }
            finally
            {
                Drop(folder);
            }
        }

        // ─────────────────────────────────────────────
        //  The starters
        // ─────────────────────────────────────────────

        [Fact]
        public void TheTenStartersLoadFromPadForgeWithTheirColorsAndPictures()
        {
            var starters = ThemeManager.Starters;
            Assert.Equal(StarterTable.Select(s => ThemeManager.StarterPrefix + s.Slug), starters.Select(t => t.Id));
            foreach (var (row, theme) in StarterTable.Zip(starters))
            {
                Assert.Equal(ThemeKind.Starter, theme.Kind);
                Assert.Equal(row.Name, theme.Name);
                Assert.Equal(row.Look, theme.Look);
                Assert.Equal(row.Dark, theme.Dark);
                Assert.Equal(Hex(row.Background), theme.Background);
                Assert.Equal(Hex(row.Accent), theme.Accent);
                Assert.Equal(Hex(row.Secondary), theme.Secondary);
                Assert.Equal(Hex(row.Attention), theme.Attention);
                Assert.Equal("PadForge", theme.Author);
                Assert.False(string.IsNullOrWhiteSpace(theme.Credits), row.Slug + " has no credits");
                Assert.StartsWith("Picture: ", theme.Credits);
                Assert.Equal(ThemeFault.None, theme.Fault);
                // The picture: a PNG, 1176 by 240 units at twice the size.
                Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, theme.PictureBytes.Take(4).ToArray());
                Assert.Equal(ThemeDefinition.PictureWidth, theme.Picture.PixelWidth);
                Assert.Equal(ThemeDefinition.PictureHeight, theme.Picture.PixelHeight);
                Assert.InRange(theme.TileAt, 0, 1 - ThemeDefinition.TileShare);
                // Save a Copy writes the starter as it is inside PadForge.
                Assert.Equal(theme.Name, ThemeFile.Parse(ThemeManager.StarterBytes(row.Slug), row.Slug).Name);
                Assert.Same(theme, ThemeManager.Find(theme.Id));
            }
            Assert.Null(ThemeManager.StarterBytes("no-such-theme"));
            Assert.Null(ThemeManager.Find("Starter:no-such-theme"));
        }

        [Fact]
        public void TheGalleryListsPadForgesLooksThenTheStarters()
        {
            ThemeManager.LoadRegistry(null);
            var tiles = ThemeGallery.BuildTiles();
            Assert.Equal(13, tiles.Count);
            Assert.Equal(new[] { 0, 1, 2 }, tiles.Take(3).Select(t => t.StockIndex));
            Assert.All(tiles.Take(3), t => Assert.Equal(string.Empty, t.Id));
            // System shows both grounds across one tile.
            Assert.NotNull(tiles[0].OtherFace);
            Assert.Null(tiles[1].OtherFace);
            foreach (var (row, tile) in StarterTable.Zip(tiles.Skip(3)))
            {
                Assert.Equal(-1, tile.StockIndex);
                Assert.Equal(ThemeManager.StarterPrefix + row.Slug, tile.Id);
                Assert.Equal(row.Name, tile.Name);
                Assert.Equal(row.Look, tile.Look);
                Assert.Equal(row.Name + ", " + row.Look, tile.SpokenName);
                Assert.Equal(tile.SpokenName, tile.ToString());
                Assert.False(tile.IsFile);
                Assert.NotNull(tile.Face.Picture);
                Assert.Null(tile.FaultText);
                // A window into a picture of any shape fills the tile and stretches nothing.
                Assert.Equal(Stretch.UniformToFill, ((ImageBrush)tile.Face.Picture).Stretch);
            }
            // A starter's tile says where its picture came from in the words About uses, which every language has.
            var s = PadForge.Resources.Strings.Strings.Instance;
            Assert.Equal(new[]
            {
                s.About_ThemeArtSonicKronosDesc, s.About_ThemeArtSonicRunningDesc, s.About_ThemeArtHobbitonDesc, s.About_ThemeArtMiddleEarthMapDesc,
                s.About_ThemeArtHogwartsDesc, s.About_ThemeArtHouseCrestsDesc, s.About_ThemeArtFalconDesc, s.About_ThemeArtVaderHelmetDesc,
                s.About_ThemeArtEnterpriseModelDesc, s.About_ThemeArtEnterpriseDDesc,
            }, tiles.Skip(3).Select(t => t.Tip).ToArray());

            // Save a Copy writes a starter as it is inside PadForge, and PadForge's own look as its four colors.
            var blueBlur = ThemeManager.Find("Starter:sonic-blue-blur");
            Assert.Equal(ThemeManager.StarterBytes("sonic-blue-blur"), ThemeGallery.CopyBytes(blueBlur, "unused"));
            var stock = ThemeFile.Parse(ThemeGallery.CopyBytes(null, "Dark"), "f");
            Assert.Equal("Dark", stock.Name);
            Assert.Equal(ThemeEngine.StockAccent, stock.Accent);
            Assert.Null(stock.PictureBytes);
        }

        // ─────────────────────────────────────────────
        //  The engine
        // ─────────────────────────────────────────────

        private static readonly (string Key, double Ratio)[] Floors =
        {
            ("EmberBrush", ThemeEngine.MarkContrast),
            ("EmberTextBrush", ThemeEngine.TextContrast), ("ColdBrush", ThemeEngine.TextContrast), ("WaitBrush", ThemeEngine.TextContrast),
            ("DangerInkBrush", ThemeEngine.TextContrast), ("LinkHoverBrush", ThemeEngine.TextContrast),
            ("InputChipTextBrush", ThemeEngine.TextContrast),
            ("TextFillColorPrimaryBrush", 7.0), ("TextFillColorSecondaryBrush", 5.5), ("TextFillColorTertiaryBrush", 4.5),
        };

        [Fact]
        public void NoThemeMakesTextUnreadable()
        {
            var themes = new List<(string Name, ThemeDefinition Theme)>();
            foreach (var starter in ThemeManager.Starters) themes.Add((starter.Id, starter));
            // Colors a file can hold that fight the base they stand on.
            foreach (bool dark in new[] { true, false })
            {
                string on = dark ? "dark" : "light";
                themes.Add((on + ", all black", Custom(dark, "#000000", "#000000", "#000000", "#000000")));
                themes.Add((on + ", all white", Custom(dark, "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF")));
                themes.Add((on + ", all gray", Custom(dark, "#808080", "#808080", "#808080", "#808080")));
                themes.Add((on + ", all red", Custom(dark, "#FF0000", "#FF0000", "#FF0000", "#FF0000")));
                themes.Add((on + ", vivid ground", Custom(dark, "#FF00FF", "#00FF00", "#FFFF00", "#00FFFF")));
                themes.Add((on + ", accent only", Custom(dark, null, dark ? "#0B1018" : "#FAFAFA", null, null)));
                themes.Add((on + ", ground only", Custom(dark, dark ? "#3A2A00" : "#C8E6C9", null, null, null)));
            }

            var unread = new List<string>();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    foreach (var (name, theme) in themes)
                    {
                        ThemeManager.SetPreview(theme);
                        Assert.Same(theme, ThemeManager.Current);
                        Color Of(string key) => ((SolidColorBrush)app.FindResource(key)).Color;
                        Color page = ThemeContrastTests.Over(Of("ApplicationBackgroundBrush"), theme.Dark ? Colors.Black : Colors.White);
                        Color card = ThemeContrastTests.Over(Of("CardBackgroundFillColorDefaultBrush"), page);
                        foreach (var (key, ratio) in Floors)
                        {
                            foreach (var (ground, where) in new[] { (card, "a card"), (page, "the page") })
                            {
                                Color text = ThemeContrastTests.Over(Of(key), ground);
                                double reads = ThemeColor.Contrast(text, ground);
                                if (reads < ratio - 0.05)
                                    unread.Add(name + ": " + key + " " + text + " on " + where + " " + ground + " reads " + reads.ToString("0.00") + ", under " + ratio);
                            }
                        }
                    }
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the theme floor test timed out");
            Assert.True(unread.Count == 0, string.Join("\n", unread));
        }

        [Fact]
        public void ABackgroundIsHeldToItsBaseAndAStartersIsKept()
        {
            // A dark base keeps a ground white reads on, on its page and on its card, and a light base one black
            // reads on, whatever the file holds.
            foreach (Color asked in new[] { Colors.White, Color.FromRgb(0x80, 0x80, 0x80), Colors.Red, Colors.Magenta, Colors.Yellow, Colors.Cyan })
            {
                var dark = new ThemeEngine(new ThemeSeeds { Dark = true, Background = asked });
                Assert.True(ThemeColor.Contrast(Colors.White, dark.Card) >= ThemeEngine.GroundTextContrast, asked + " as a dark ground leaves the card " + dark.Card);
                Assert.True(ThemeColor.Contrast(Colors.White, dark.Ground) >= ThemeEngine.GroundTextContrast, asked + " as a dark ground leaves the page " + dark.Ground);
            }
            foreach (Color asked in new[] { Colors.Black, Color.FromRgb(0x80, 0x80, 0x80), Colors.Red, Colors.Blue, Colors.Magenta })
            {
                var light = new ThemeEngine(new ThemeSeeds { Dark = false, Background = asked });
                Assert.True(ThemeColor.Contrast(Colors.Black, light.Ground) >= ThemeEngine.GroundTextContrast, asked + " as a light ground leaves the page " + light.Ground);
                Assert.True(ThemeColor.Contrast(Colors.Black, light.Card) >= ThemeEngine.GroundTextContrast, asked + " as a light ground leaves the card " + light.Card);
            }
            // The starters' grounds are inside their base's range, so each stays as its file has it.
            foreach (var row in StarterTable)
            {
                Color ground = new ThemeEngine(new ThemeSeeds { Dark = row.Dark, Background = Hex(row.Background) }).Ground, file = Hex(row.Background);
                Assert.True(Math.Abs(ground.R - file.R) <= 2 && Math.Abs(ground.G - file.G) <= 2 && Math.Abs(ground.B - file.B) <= 2,
                    row.Slug + " draws its ground " + ground + ", and its file has " + file);
            }
        }

        private static Dictionary<string, string> Snapshot(Application app)
        {
            var held = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (object key in app.Resources.Keys)
            {
                if (key is not string name) continue;
                held[name] = app.Resources[key] switch
                {
                    SolidColorBrush brush => "brush " + brush.Color + " " + brush.Opacity.ToString("0.###"),
                    Color color => "color " + color,
                    GradientBrush gradient => "gradient " + string.Join(",", gradient.GradientStops.Select(s => s.Color.ToString())),
                    object other => other.GetType().Name,
                    null => "null",
                };
            }
            return held;
        }

        [Fact]
        public void PadForgesOwnLooksKeepTheirTablesAfterATheme()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    Color Ember() => ((SolidColorBrush)app.FindResource("EmberBrush")).Color;
                    ThemeManager.Apply(2, null);
                    Assert.Null(ThemeManager.Current);
                    var dark = Snapshot(app);
                    Assert.Equal(EmberTheme.Accent, Ember());
                    ThemeManager.Apply(1, null);
                    var light = Snapshot(app);
                    Assert.NotEqual(dark["WaitBrush"], light["WaitBrush"]);

                    foreach (var theme in ThemeManager.Starters)
                    {
                        ThemeManager.Apply(2, theme.Id);
                        Assert.Same(theme, ThemeManager.Current);
                        Assert.Equal(theme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light, ApplicationThemeManager.GetAppTheme());
                        // The engine ran: the accent is the theme's, moved to read on a card.
                        Assert.Equal(new ThemeEngine(theme.Seeds).Accent, Ember());
                        Assert.NotEqual(EmberTheme.Accent, Ember());
                        // What the theme set is held over the stock tables, key by key.
                        Assert.NotEqual(dark, Snapshot(app));
                    }

                    ThemeManager.Apply(2, null);
                    Assert.Null(ThemeManager.Current);
                    Assert.Null(ThemeManager.Picture);
                    Assert.Equal(dark, Snapshot(app));
                    Assert.Equal(EmberTheme.Accent, Ember());
                    ThemeManager.Apply(1, null);
                    Assert.Equal(light, Snapshot(app));
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the stock table test timed out");
        }

        [Fact]
        public void AThemeFileIsListedPickedLostAndFound()
        {
            string folder = TempFolder();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    string path = System.IO.Path.Combine(folder, "mine.pftheme");
                    var mine = Custom(false, "#F0F4FF", "#1144CC", "#227755", "#885500");
                    mine.Name = "Mine";
                    ThemeFile.Write(path, mine);

                    int listEdits = 0, listChanges = 0;
                    EventHandler edited = (_, _) => listEdits++, changed = (_, _) => listChanges++;
                    ThemeManager.ListEdited += edited;
                    ThemeManager.ListChanged += changed;
                    try
                    {
                        Assert.Null(ThemeManager.Register(System.IO.Path.Combine(folder, "none.pftheme")));
                        Assert.Equal(0, listEdits);
                        var listed = ThemeManager.Register(path);
                        Assert.NotNull(listed);
                        Assert.Equal(ThemeManager.FilePrefix + path, listed.Id);
                        Assert.Equal(1, listEdits);
                        // Listing it again reads it again and lists it once.
                        ThemeManager.Register(path);
                        Assert.Single(ThemeManager.Files);
                        Assert.Equal(new[] { ("Mine", path) }, ThemeManager.SaveRegistry());

                        // Picked: its own base, whatever stock look is set.
                        ThemeManager.Apply(2, listed.Id);
                        Assert.Equal("Mine", ThemeManager.Current.Name);
                        Assert.Equal(ApplicationTheme.Light, ApplicationThemeManager.GetAppTheme());

                        // The file goes: Dark, though the stock look set is Light, and the tile keeps its name.
                        ThemeManager.Apply(1, listed.Id);
                        File.Delete(path);
                        ThemeManager.Reload(listed.Id);
                        Assert.Null(ThemeManager.Current);
                        Assert.Equal(ApplicationTheme.Dark, ApplicationThemeManager.GetAppTheme());
                        var gone = ThemeManager.Find(listed.Id);
                        Assert.Equal(ThemeFault.Missing, gone.Fault);
                        Assert.Equal("Mine", gone.Name);
                        Assert.Equal(Strings_ThemeMissing(), ThemeGallery.BuildTiles().Single(t => t.Id == listed.Id).FaultText);

                        // The file is back: the theme is.
                        ThemeFile.Write(path, mine);
                        ThemeManager.Reload(listed.Id);
                        Assert.Equal("Mine", ThemeManager.Current.Name);

                        // The saved list loads again, and loading tells no one the settings changed.
                        var saved = ThemeManager.SaveRegistry();
                        int editsBefore = listEdits;
                        ThemeManager.LoadRegistry(null);
                        Assert.Empty(ThemeManager.Files);
                        ThemeManager.LoadRegistry(saved);
                        Assert.Equal("Mine", Assert.Single(ThemeManager.Files).Name);
                        Assert.Equal(editsBefore, listEdits);
                        Assert.True(listChanges > 0);

                        // Off the list: the file stays.
                        ThemeManager.Unregister(listed.Id);
                        Assert.Empty(ThemeManager.Files);
                        Assert.True(File.Exists(path));
                        Assert.Null(ThemeManager.Find(listed.Id));
                    }
                    finally
                    {
                        ThemeManager.ListEdited -= edited;
                        ThemeManager.ListChanged -= changed;
                    }
                }
                finally
                {
                    BackToStock();
                    Drop(folder);
                }
            }, Limit, "the theme list test timed out");
        }

        private static string Strings_ThemeMissing() => PadForge.Resources.Strings.Strings.Instance.Settings_ThemeMissing;

        [Fact]
        public void AThemeFileUnderPadForgesFolderIsStoredByItsRelativePath()
        {
            string folder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ThemeTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string path = System.IO.Path.Combine(folder, "near.pftheme");
                var near = Custom(true, "#101820", "#33CC99", null, null);
                near.Name = "Near";
                ThemeFile.Write(path, near);
                ThemeManager.LoadRegistry(null);
                var listed = ThemeManager.Register(path);
                string stored = Assert.Single(ThemeManager.SaveRegistry()).Path;
                Assert.False(System.IO.Path.IsPathRooted(stored), stored + " is a full path");
                Assert.Equal(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(ThemeManager.ResolvePath(stored)));
                Assert.Equal(ThemeManager.FilePrefix + stored, listed.Id);
            }
            finally
            {
                ThemeManager.LoadRegistry(null);
                Drop(folder);
            }
        }

        [Fact]
        public void ThePickedFileIsReadAgainWhenItChangesEvenUnderAProfilesLook()
        {
            string folder = TempFolder();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    string path = System.IO.Path.Combine(folder, "live.pftheme");
                    var live = Custom(true, "#101418", "#22CC88", null, null);
                    live.Name = "Live";
                    ThemeFile.Write(path, live);
                    var listed = ThemeManager.Register(path);
                    ThemeManager.Apply(2, listed.Id);
                    Assert.Equal(Hex("#22CC88"), ThemeManager.Current.Accent);

                    // Saved again, as an editor saves it: the look follows by itself.
                    live.Accent = Hex("#CC2288");
                    ThemeFile.Write(path, live);
                    Until(dispatcher, () => ThemeManager.Current?.Accent == Hex("#CC2288"), "the picked file's edit");

                    // A profile's look stands over the pick. The picked file is still watched, so its edit is
                    // there when the look comes off.
                    var look = new ThemeDefinition { Kind = ThemeKind.GameLook, Id = ThemeManager.GamePrefix + "620", Name = "Portal 2", Accent = Hex("#3399FF") };
                    ThemeManager.SetGameLook(look);
                    Assert.Same(look, ThemeManager.Current);
                    live.Accent = Hex("#2288CC");
                    ThemeFile.Write(path, live);
                    Until(dispatcher, () => ThemeManager.Find(listed.Id)?.Accent == Hex("#2288CC"), "the picked file's edit under a look");
                    Assert.Same(look, ThemeManager.Current);
                    ThemeManager.SetGameLook(null);
                    Assert.Equal(Hex("#2288CC"), ThemeManager.Current.Accent);
                }
                finally
                {
                    BackToStock();
                    Drop(folder);
                }
            }, Limit, "the watched file test timed out");
        }

        [Fact]
        public void APictureOfAnotherShapeKeepsItsShape()
        {
            string folder = TempFolder();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                Window window = null;
                try
                {
                    // The page area is wider than the picture's box, which is its right 1176 units.
                    const int across = 1400, down = 300;
                    var backdrop = new ThemeBackdrop { IsWhole = true };
                    var host = new Grid { Width = across, Height = down, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                    host.Children.Add(backdrop);
                    window = new Window
                    {
                        Width = across + 40, Height = down + 80, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, Content = host,
                    };
                    window.Show();
                    Pump(dispatcher);

                    RenderTargetBitmap Drawn(string name, BitmapSource picture)
                    {
                        string path = System.IO.Path.Combine(folder, name + ".pftheme");
                        var theme = Custom(true, "#101418", "#22CC88", null, null);
                        theme.Name = name;
                        theme.PictureBytes = Png(picture);
                        theme.PictureName = "picture.png";
                        ThemeFile.Write(path, theme);
                        ThemeManager.Apply(2, ThemeManager.Register(path).Id);
                        Assert.Equal(picture.PixelWidth, ThemeManager.Picture.PixelWidth);
                        Pump(dispatcher);
                        var bitmap = new RenderTargetBitmap(across, down, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(host);
                        return bitmap;
                    }

                    // A square picture: as tall as the box, at its right, and stretched across nothing.
                    var square = Drawn("square", Blocks(480, 480, (1, Color.FromRgb(0xE0, 0x20, 0x20))));
                    Assert.True(Pixel(square, across - 120, 120).A > 250, "the square is not at the box's right");
                    Assert.Equal(0, Pixel(square, across - 300, 120).A);
                    Assert.Equal(0, Pixel(square, across - 120, 260).A);

                    // A picture wider than the box: as tall as the box, its right part, and nothing left of the box.
                    var wide = Drawn("wide", Blocks(2000, 200, (0.9, Color.FromRgb(0x20, 0x20, 0xE0)), (0.1, Color.FromRgb(0x20, 0xE0, 0x20))));
                    Color right = Pixel(wide, across - 60, 120), inside = Pixel(wide, across - 1100, 120);
                    Assert.True(right.G > right.B + 80, "the wide picture's right end is not at the box's right: " + right);
                    Assert.True(inside.B > inside.G + 80, "the wide picture does not fill the box: " + inside);
                    Assert.Equal(0, Pixel(wide, 100, 120).A);

                    // A picture of the box's own shape fills the box, as the starters' and the builder's do.
                    var exact = Drawn("exact", Blocks(1176, 240, (1, Color.FromRgb(0xE0, 0xC0, 0x20))));
                    Assert.True(Pixel(exact, across - 1170, 120).A > 250, "a picture of the box's shape does not fill it");
                    Assert.True(Pixel(exact, across - 6, 236).A > 250);
                    Assert.Equal(0, Pixel(exact, across - 1182, 120).A);
                }
                finally
                {
                    window?.Close();
                    BackToStock();
                    Drop(folder);
                }
            }, Limit, "the picture shape test timed out");
        }

        [Fact]
        public void TheSettingsKeepThePickedThemeAndTheListedFiles()
        {
            var settings = new AppSettingsData
            {
                ThemeIndex = 2,
                ThemeId = "Starter:sonic-blue-blur",
                ThemeFiles = new[] { new ThemeFileData { Name = "Mine", Path = "themes\\mine.pftheme" } },
            };
            var serializer = new XmlSerializer(typeof(AppSettingsData));
            using var buffer = new StringWriter();
            serializer.Serialize(buffer, settings);
            var back = (AppSettingsData)serializer.Deserialize(new StringReader(buffer.ToString()));
            Assert.Equal(2, back.ThemeIndex);
            Assert.Equal("Starter:sonic-blue-blur", back.ThemeId);
            Assert.Equal("Mine", Assert.Single(back.ThemeFiles).Name);
            Assert.Equal("themes\\mine.pftheme", back.ThemeFiles[0].Path);

            // The reset returns to System and lets the theme go.
            var vm = new SettingsViewModel();
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            vm.SetTheme(1, "File:abc");
            Assert.Equal(1, vm.SelectedThemeIndex);
            Assert.Equal("File:abc", vm.SelectedThemeId);
            Assert.Contains(nameof(SettingsViewModel.SelectedThemeId), changed);
            vm.ResetSettingCommand.Execute(nameof(SettingsViewModel.SelectedThemeIndex));
            Assert.Equal(0, vm.SelectedThemeIndex);
            Assert.True(string.IsNullOrEmpty(vm.SelectedThemeId));
        }

        // ─────────────────────────────────────────────
        //  A page already built follows a theme
        // ─────────────────────────────────────────────

        [Fact]
        public void APageAlreadyBuiltFollowsAThemeAndComesBack()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                Window window = null;
                try
                {
                    ThemeManager.Apply(2, null);
                    // A brush by name, a gradient stop by name, the accent button's own brushes, and the picture.
                    var byName = new Border { Width = 60, Height = 30 };
                    byName.SetResourceReference(Border.BackgroundProperty, "EmberBrush");
                    var stops = (Rectangle)XamlReader.Parse(
                        "<Rectangle xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='60' Height='30'><Rectangle.Fill>"
                        + "<LinearGradientBrush><GradientStop Color='{DynamicResource EmberColor}' Offset='0'/>"
                        + "<GradientStop Color='{DynamicResource EmberColor}' Offset='1'/></LinearGradientBrush></Rectangle.Fill></Rectangle>");
                    var button = new Button { Width = 120, Height = 36, Content = string.Empty, Style = (Style)app.FindResource("EmberAccentButton") };
                    var backdrop = new ThemeBackdrop { IsWhole = true, Width = 1176, Height = 240 };
                    var panel = new StackPanel { Width = 1176 };
                    foreach (UIElement part in new UIElement[] { byName, stops, button, backdrop }) panel.Children.Add(part);
                    window = new Window
                    {
                        Width = 1200, Height = 420, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, Content = panel,
                    };
                    window.Show();
                    Pump(dispatcher);

                    // One picture of the panel, read at a place inside a part: a part drawn alone is drawn at its offset.
                    Color At(FrameworkElement part, double x, double y)
                    {
                        Pump(dispatcher);
                        var bitmap = new RenderTargetBitmap((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(panel);
                        Point place = part.TranslatePoint(new Point(x, y), panel);
                        return Pixel(bitmap, (int)place.X, (int)place.Y);
                    }
                    Color Middle(FrameworkElement part) => At(part, part.ActualWidth / 2, part.ActualHeight / 2);
                    var night = ThemeManager.Find("Starter:sonic-night-run");
                    // A place where the picture is solid, in units: the middle of the first run of 60 solid pixels on
                    // its middle row. A lone solid pixel is a letter's edge of the logo.
                    var row = new byte[night.Picture.PixelWidth * 4];
                    new FormatConvertedBitmap(night.Picture, PixelFormats.Bgra32, null, 0)
                        .CopyPixels(new Int32Rect(0, night.Picture.PixelHeight / 2, night.Picture.PixelWidth, 1), row, row.Length, 0);
                    int solidAt = -1;
                    for (int x = 0, run = 0; x < night.Picture.PixelWidth && solidAt < 0; x++)
                    {
                        run = row[x * 4 + 3] == 255 ? run + 1 : 0;
                        if (run >= 60) solidAt = x - 30;
                    }
                    Assert.True(solidAt > 0, "the picture has no solid run on its middle row");
                    double pictureX = solidAt / 2.0, pictureY = ThemeBackdrop.PictureHeight / 2;
                    Color ember = EmberTheme.Accent;
                    Assert.Equal(ember, Middle(byName));
                    Assert.Equal(ember, Middle(stops));
                    Color buttonBefore = Middle(button);
                    Assert.True(buttonBefore.R > buttonBefore.B + 80, "the accent button is not ember under Dark: " + buttonBefore);
                    Assert.Equal(0, At(backdrop, pictureX, pictureY).A);

                    ThemeManager.Apply(2, night.Id);
                    Pump(dispatcher);
                    Color accent = new ThemeEngine(night.Seeds).Accent;
                    Assert.True(accent.B > accent.R + 80, "Night Run's accent is not blue: " + accent);
                    Assert.Equal(accent, Middle(byName));
                    Assert.Equal(accent, Middle(stops));
                    Color buttonAfter = Middle(button);
                    Assert.True(buttonAfter.B > buttonAfter.R + 60, "the accent button kept its ember under Night Run: " + buttonAfter);
                    Assert.True(At(backdrop, pictureX, pictureY).A > 200, "the picture is not behind the page");

                    ThemeManager.Apply(2, null);
                    Pump(dispatcher);
                    Assert.Equal(ember, Middle(byName));
                    Assert.Equal(ember, Middle(stops));
                    Assert.Equal(buttonBefore, Middle(button));
                    Assert.Equal(0, At(backdrop, pictureX, pictureY).A);
                }
                finally
                {
                    window?.Close();
                    BackToStock();
                }
            }, Limit, "the live theme test timed out");
        }

        // ─────────────────────────────────────────────
        //  The picture and the colors taken from it
        // ─────────────────────────────────────────────

        [Fact]
        public void APictureIsCutFadedTonedAndGivenItsLogo()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var photo = Blocks(1920, 620, (1, Color.FromRgb(0xFF, 0x20, 0x20)));
                Color ground = Color.FromRgb(0x10, 0x20, 0xC0);
                var picture = ThemePicture.Compose(photo, null, ground, dark: true, focus: 0.5);
                Assert.Equal(ThemePicture.Width, picture.PixelWidth);
                Assert.Equal(ThemePicture.Height, picture.PixelHeight);
                Assert.True(picture.IsFrozen);
                int boxLeft = (int)(ThemePicture.Width * (1 - ThemePicture.PhotoShare));
                // Clear at the left of the photo's box, faint where it fades in, solid past the fade, clear again at the foot.
                Assert.Equal(0, Pixel(picture, boxLeft / 2, 200).A);
                Assert.InRange(Pixel(picture, boxLeft + 40, 200).A, 1, 60);
                Color solid = Pixel(picture, 1900, 200);
                Assert.True(solid.A >= 250, "the photo is not solid past its fade: " + solid);
                Assert.InRange(Pixel(picture, 1900, ThemePicture.Height - 2).A, 0, 24);
                // Toned toward the ground: the red is less than the source's and some of the ground's blue is in it.
                Assert.InRange(solid.R, 0xC0, 0xF0);
                Assert.True(solid.B > 0x30, "the photo took none of the ground's color: " + solid);
                // A light base tones more.
                Color onLight = Pixel(ThemePicture.Compose(photo, null, Colors.White, dark: false, focus: 0.5), 1900, 200);
                Assert.True(onLight.G > solid.G + 60, "a light base did not tone the photo more: " + onLight);

                // No logo: the logo's place is the photo's faint fade. With one: the logo, whole.
                int x = (int)ThemePicture.LogoCenterX, y = (int)ThemePicture.LogoCenterY;
                Assert.True(Pixel(picture, x, y).A < 250);
                var logo = Blocks(600, 200, (1, Colors.White));
                Color mark = Pixel(ThemePicture.Compose(photo, logo, ground, dark: true, focus: 0.5), x, y);
                Assert.Equal(Color.FromArgb(255, 255, 255, 255), mark);

                // The picture is kept as a PNG, which holds its clear ground.
                byte[] png = ThemePicture.EncodePng(picture);
                Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
                var back = BoundedBitmap.FromBytes(png, ThemePicture.Width, ThemePicture.Height, scaleUp: false);
                Assert.Equal(0, Pixel(back, boxLeft / 2, 200).A);
                Assert.Null(ThemePicture.Compose(null, logo, ground, true, 0.5));
                Assert.Null(ThemePicture.DecodeSource(new byte[] { 1, 2, 3, 4 }));
                Assert.Null(ThemePicture.EncodePng(null));

                // Position: a tall source shows its top at 0 and its foot at 1.
                var tall = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                    new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 255, 0, 0, 255, 255, 0, 0, 255 }, 8);
                Color top = Pixel(ThemePicture.Compose(tall, null, ground, true, 0), 1900, 200), foot = Pixel(ThemePicture.Compose(tall, null, ground, true, 1), 1900, 200);
                Assert.True(top.R > top.B + 60, "position 0 does not show the source's top: " + top);
                Assert.True(foot.B > foot.R + 60, "position 1 does not show the source's foot: " + foot);
            }, Limit, "the picture test timed out");
        }

        private static double HueApart(double a, double b)
        {
            double d = Math.Abs(a - b) % 360;
            return d > 180 ? 360 - d : d;
        }

        [Fact]
        public void TheFourColorsAreTakenFromThePicture()
        {
            Color navy = Color.FromRgb(0x10, 0x18, 0x30), red = Color.FromRgb(0xE0, 0x20, 0x20), cyan = Color.FromRgb(0x20, 0xC0, 0xE0);
            var picture = Blocks(400, 200, (0.6, navy), (0.25, red), (0.15, cyan));
            var dark = ThemePalette.Suggest(picture, dark: true);
            // The accent is the strongest hue, the secondary the next one apart from it, the ground dark and of the quiet tones' hue.
            Assert.True(HueApart(Lch.From(dark.Accent).H, Lch.From(red).H) <= 15, "the accent is not the picture's red: " + dark.Accent);
            Assert.True(HueApart(Lch.From(dark.Secondary).H, Lch.From(cyan).H) <= 15, "the secondary is not the picture's cyan: " + dark.Secondary);
            Assert.True(ThemeColor.Luma(dark.Background) < 0.2, "the ground is not dark: " + dark.Background);
            Assert.True(HueApart(Lch.From(dark.Background).H, Lch.From(navy).H) <= 25, "the ground is not the navy's hue: " + dark.Background);
            Assert.True(HueApart(Lch.From(dark.Attention).H, Lch.From(dark.Accent).H) >= 40);
            Assert.True(HueApart(Lch.From(dark.Attention).H, Lch.From(dark.Secondary).H) >= 40);
            // A light base: a light ground and darker colors of the same hues.
            var light = ThemePalette.Suggest(picture, dark: false);
            Assert.True(ThemeColor.Luma(light.Background) > 0.8, "the light ground is not light: " + light.Background);
            Assert.True(HueApart(Lch.From(light.Accent).H, Lch.From(red).H) <= 15);
            Assert.True(ThemeColor.Luma(light.Accent) < ThemeColor.Luma(dark.Accent));

            // A picture of one gray still gives four colors, and no picture gives PadForge's own.
            var gray = ThemePalette.Suggest(Blocks(40, 20, (1, Color.FromRgb(0x70, 0x70, 0x70))), dark: true);
            Assert.True(Lch.From(gray.Accent).C > 0.08, "a gray picture gave a gray accent: " + gray.Accent);
            var none = ThemePalette.Suggest(null, dark: true);
            Assert.Equal(ThemeEngine.StockAccent, none.Accent);
            Assert.Equal(ThemeEngine.StockGround(true), none.Background);
            // A clear pixel is no sample: a cutout on a clear ground gives its own colors.
            var clear = new byte[40 * 20 * 4];
            for (int i = 0; i < 40 * 20; i++)
            {
                bool cut = i % 40 < 10;
                clear[i * 4] = cut ? (byte)0x20 : (byte)0xFF;
                clear[i * 4 + 1] = cut ? (byte)0x20 : (byte)0xFF;
                clear[i * 4 + 2] = cut ? (byte)0xE0 : (byte)0xFF;
                clear[i * 4 + 3] = cut ? (byte)255 : (byte)0;
            }
            var cutout = ThemePalette.Suggest(BitmapSource.Create(40, 20, 96, 96, PixelFormats.Bgra32, null, clear, 160), dark: true);
            Assert.True(HueApart(Lch.From(cutout.Accent).H, Lch.From(red).H) <= 15, "the cutout's accent is not its own red: " + cutout.Accent);
            // A picture of four pixels, none as dark as a dark ground or as light as a light one, still gives a ground.
            Assert.True(ThemeColor.Luma(ThemePalette.Suggest(Blocks(2, 2, (1, Colors.White)), dark: true).Background) < 0.2);
            Assert.True(ThemeColor.Luma(ThemePalette.Suggest(Blocks(2, 2, (1, Colors.Black)), dark: false).Background) > 0.8);
        }

        // ─────────────────────────────────────────────
        //  The builder
        // ─────────────────────────────────────────────

        [Fact]
        public void TheBuilderStartsFromTheLookInForceAndOffersTheSteamSwitchWhileItIsOff()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    var settings = new SettingsViewModel { EnableCommunityConfigLookup = false };
                    var dialog = new ThemeBuilderDialog(null, settings);
                    var s = PadForge.Resources.Strings.Strings.Instance;
                    Assert.Equal(s.ThemeBuilder_NewTitle, dialog.Title);
                    Assert.Equal(s.ThemeBuilder_DefaultName, dialog.NameBox.Text);
                    Assert.True(dialog.SaveButton.IsEnabled);
                    Assert.Equal(0, dialog.BaseBox.SelectedIndex);
                    // Four colors, each with a swatch and a box that holds PadForge's own color.
                    Assert.Equal(4, dialog.ColorRows.Children.Count);
                    var boxes = dialog.ColorRows.Children.OfType<SettingRow>()
                        .Select(r => r.Children.OfType<StackPanel>().Single().Children.OfType<TextBox>().Single()).ToList();
                    Assert.Equal(new[] { ThemeEngine.StockGround(true), ThemeEngine.StockAccent, ThemeEngine.StockSecondary(true), ThemeEngine.StockAttention(true) }
                        .Select(ThemeColor.Format), boxes.Select(b => b.Text));
                    Assert.Equal(Visibility.Collapsed, dialog.Picker.Visibility);
                    Assert.False(dialog.MatchButton.IsEnabled);
                    Assert.Equal(Visibility.Collapsed, dialog.PictureFrame.Visibility);

                    // A name that is empty cannot be saved.
                    dialog.NameBox.Text = "  ";
                    Assert.False(dialog.SaveButton.IsEnabled);
                    dialog.NameBox.Text = "Mine";
                    Assert.True(dialog.SaveButton.IsEnabled);

                    // A color typed into its box is the theme's, and the app wears it once the change settles.
                    boxes[1].Text = "#00CC88";
                    dialog.BaseBox.SelectedIndex = 1;

                    // From a Steam Game with the switch off: the line that says what Steam is asked, and the switch.
                    dialog.FromGameButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(Visibility.Visible, dialog.GamePanel.Visibility);
                    Assert.Equal(Visibility.Visible, dialog.LookupOffPanel.Visibility);
                    Assert.Equal(Visibility.Collapsed, dialog.SearchPanel.Visibility);
                    var enable = dialog.LookupOffPanel.Children.OfType<Button>().Single();
                    enable.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.True(settings.EnableCommunityConfigLookup);
                    Assert.Equal(Visibility.Collapsed, dialog.LookupOffPanel.Visibility);
                    Assert.Equal(Visibility.Visible, dialog.SearchPanel.Visibility);

                    // Closed without a save: nothing saved, and no preview left on.
                    dialog.Close();
                    Assert.Null(dialog.Saved);
                    Assert.Null(ThemeManager.Current);
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the builder test timed out");
        }

        [Fact]
        public void TheBuilderEditsAThemeFileInPlaceAndKeepsItsPicture()
        {
            string folder = TempFolder();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    string path = System.IO.Path.Combine(folder, "edit.pftheme");
                    byte[] picture = Png(Blocks(64, 16, (1, Colors.Purple)));
                    var theme = Custom(true, "#101020", "#FF4FD8", "#29E0FF", "#FFD166");
                    theme.Name = "Edit Me";
                    theme.Look = "Kept Look";
                    theme.Author = "Kept Author";
                    theme.Credits = "Kept credits.";
                    theme.PictureBytes = picture;
                    theme.TileAt = 0.2;
                    ThemeFile.Write(path, theme);
                    var listed = ThemeManager.Register(path);

                    var dialog = new ThemeBuilderDialog(listed, new SettingsViewModel());
                    Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Settings_ThemeEdit, dialog.Title);
                    Assert.Equal("Edit Me", dialog.NameBox.Text);
                    var boxes = dialog.ColorRows.Children.OfType<SettingRow>()
                        .Select(r => r.Children.OfType<StackPanel>().Single().Children.OfType<TextBox>().Single()).ToList();
                    Assert.Equal("#FF4FD8", boxes[1].Text);
                    boxes[1].Text = "#00CC88";
                    dialog.NameBox.Text = "Edited";
                    // Save writes the file it was opened from: no file picker.
                    Assert.True(dialog.TrySave());
                    dialog.Close();

                    Assert.NotNull(dialog.Saved);
                    var read = ThemeFile.Read(path);
                    Assert.Equal("Edited", read.Name);
                    Assert.Equal(Hex("#00CC88"), read.Accent);
                    Assert.Equal(Hex("#101020"), read.Background);
                    Assert.Equal("Kept Look", read.Look);
                    Assert.Equal("Kept Author", read.Author);
                    Assert.Equal("Kept credits.", read.Credits);
                    Assert.Equal(picture, read.PictureBytes);
                    Assert.Equal(0.2, read.TileAt, 3);
                    Assert.Equal("Edited", Assert.Single(ThemeManager.Files).Name);
                }
                finally
                {
                    BackToStock();
                    Drop(folder);
                }
            }, Limit, "the builder edit test timed out");
        }

        // ─────────────────────────────────────────────
        //  A Steam profile's game look
        // ─────────────────────────────────────────────

        private static ProfileGameLook Look(int appId, string game = "Portal 2") => new ProfileGameLook
        {
            AppId = appId, Game = game, Background = "#0A1020", Accent = "#3399FF", Secondary = "#FFAA33", Attention = "#FFE066",
        };

        [Fact]
        public void TheStoreKeepsOnePicturePerGame()
        {
            string folder = TempFolder();
            try
            {
                var store = new GameLookStore(System.IO.Path.Combine(folder, "looks"));
                byte[] png = Png(Blocks(8, 8, (1, Colors.Teal)));
                Assert.Null(store.Stamp(620));
                Assert.Null(store.Read(620));
                Assert.True(store.Write(620, png));
                Assert.Equal(png, store.Read(620));
                Assert.NotNull(store.Stamp(620));
                Assert.Equal("620.png", System.IO.Path.GetFileName(store.PathOf(620)));
                Assert.Equal(new[] { "620.png" }, Directory.GetFiles(store.Folder).Select(System.IO.Path.GetFileName).ToArray());
                // No game, no picture.
                Assert.False(store.Write(0, png));
                Assert.False(store.Write(-4, png));
                Assert.False(store.Write(730, null));
                Assert.False(store.Write(730, Array.Empty<byte>()));
                Assert.Null(store.Read(0));
                store.Remove(620);
                Assert.Null(store.Read(620));
                store.Remove(620);
                // PadForge's own store is a folder beside PadForge, so the looks move with it.
                Assert.Equal(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameLooks")),
                    System.IO.Path.GetFullPath(new GameLookStore().Folder));
            }
            finally
            {
                Drop(folder);
            }
        }

        [Fact]
        public void AProfilesLookGoesOnWhileItIsActiveAndComesOffWhenAnotherIs()
        {
            string folder = TempFolder();
            var profilesBefore = SettingsManager.Profiles;
            string activeBefore = SettingsManager.ActiveProfileId;
            var storeBefore = GameLooks.Store;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    GameLooks.Store = new GameLookStore(folder);
                    byte[] png = ThemePicture.EncodePng(ThemePicture.Compose(Blocks(960, 310, (1, Colors.SteelBlue)), null, Hex("#0A1020"), true, 0.5));
                    var made = new GameLooks.Made { Look = Look(620), Picture = png };
                    GameLooks.Keep(made);
                    Assert.True(GameLooks.HasPicture(made.Look));

                    var wears = new ProfileData { Name = "Wears", GameLook = Look(620) };
                    var colorsOnly = new ProfileData { Name = "Colors Only", GameLook = Look(730, "No Picture") };
                    var plain = new ProfileData { Name = "Plain" };
                    SettingsManager.Profiles = new List<ProfileData> { wears, colorsOnly, plain };
                    ThemeManager.Apply(2, "Starter:startrek-lcars");
                    var picked = ThemeManager.Current;
                    Assert.Equal(ThemeKind.Starter, picked.Kind);

                    // The profile becomes active: its game's look, over the user's pick.
                    SettingsManager.ActiveProfileId = wears.Id;
                    GameLooks.Refresh();
                    var look = ThemeManager.Current;
                    Assert.Equal(ThemeKind.GameLook, look.Kind);
                    Assert.Same(look, ThemeManager.GameLook);
                    Assert.Equal("Portal 2", look.Name);
                    Assert.Equal(ThemeManager.GamePrefix + "620", look.Id);
                    Assert.True(look.Dark);
                    Assert.Equal(Hex("#3399FF"), look.Accent);
                    Assert.NotNull(look.Picture);
                    Assert.Same(look.Picture, ThemeManager.Picture);
                    Assert.Equal(new ThemeEngine(look.Seeds).Accent, ((SolidColorBrush)app.FindResource("EmberBrush")).Color);
                    // The pick is still the user's.
                    Assert.Equal("Starter:startrek-lcars", ThemeManager.SelectedId);
                    // The same look again is the same theme: nothing is read or drawn twice.
                    GameLooks.Refresh();
                    Assert.Same(look, ThemeManager.Current);

                    // A look whose picture is not on this PC: its colors alone.
                    SettingsManager.ActiveProfileId = colorsOnly.Id;
                    GameLooks.Refresh();
                    Assert.Equal(ThemeKind.GameLook, ThemeManager.Current.Kind);
                    Assert.Equal("No Picture", ThemeManager.Current.Name);
                    Assert.Null(ThemeManager.Picture);
                    Assert.False(GameLooks.HasPicture(colorsOnly.GameLook));

                    // A profile with no look, then the default profile: the user's pick is back.
                    SettingsManager.ActiveProfileId = plain.Id;
                    GameLooks.Refresh();
                    Assert.Same(picked, ThemeManager.Current);
                    Assert.Null(ThemeManager.GameLook);
                    SettingsManager.ActiveProfileId = wears.Id;
                    GameLooks.Refresh();
                    Assert.Equal(ThemeKind.GameLook, ThemeManager.Current.Kind);
                    SettingsManager.ActiveProfileId = null;
                    GameLooks.Refresh();
                    Assert.Same(picked, ThemeManager.Current);

                    // The builder's preview stands over a look.
                    SettingsManager.ActiveProfileId = wears.Id;
                    GameLooks.Refresh();
                    var preview = Custom(true, "#201010", "#FF3355", null, null);
                    ThemeManager.SetPreview(preview);
                    Assert.Same(preview, ThemeManager.Current);
                    ThemeManager.SetPreview(null);
                    Assert.Equal(ThemeKind.GameLook, ThemeManager.Current.Kind);

                    // The picture stays while a profile wears the game's look and goes when none does.
                    GameLooks.Release(620);
                    Assert.True(GameLooks.HasPicture(wears.GameLook));
                    wears.GameLook = null;
                    GameLooks.Release(620);
                    Assert.False(File.Exists(GameLooks.Store.PathOf(620)));
                }
                finally
                {
                    SettingsManager.Profiles = profilesBefore;
                    SettingsManager.ActiveProfileId = activeBefore;
                    GameLooks.Store = storeBefore;
                    BackToStock();
                    Drop(folder);
                }
            }, Limit, "the game look test timed out");
        }

        [Fact]
        public void TheLookFollowsTheActiveProfileByItself()
        {
            string folder = TempFolder();
            var profilesBefore = SettingsManager.Profiles;
            string activeBefore = SettingsManager.ActiveProfileId;
            var storeBefore = GameLooks.Store;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    GameLooks.Store = new GameLookStore(folder);
                    var wears = new ProfileData { Name = "Wears", GameLook = Look(620) };
                    SettingsManager.Profiles = new List<ProfileData> { wears };
                    SettingsManager.ActiveProfileId = null;
                    ThemeManager.Apply(2, null);

                    int raised = 0;
                    Action count = () => raised++;
                    SettingsManager.ActiveProfileChanged += count;
                    try
                    {
                        GameLooks.Start(dispatcher);
                        Pump(dispatcher);
                        Assert.Null(ThemeManager.Current);

                        SettingsManager.ActiveProfileId = wears.Id;
                        Assert.Equal(1, raised);
                        // The switch that raised it is still under way: the look goes on once it is done.
                        Assert.Null(ThemeManager.Current);
                        Pump(dispatcher);
                        Assert.Equal(ThemeKind.GameLook, ThemeManager.Current.Kind);

                        // The same profile set again changes nothing and tells no one.
                        SettingsManager.ActiveProfileId = wears.Id;
                        Assert.Equal(1, raised);

                        SettingsManager.ActiveProfileId = null;
                        Assert.Equal(2, raised);
                        Pump(dispatcher);
                        Assert.Null(ThemeManager.Current);
                    }
                    finally
                    {
                        SettingsManager.ActiveProfileChanged -= count;
                        GameLooks.Start(null);
                    }
                    // Stopped: a switch moves no look.
                    SettingsManager.ActiveProfileId = wears.Id;
                    Pump(dispatcher);
                    Assert.Null(ThemeManager.Current);
                }
                finally
                {
                    SettingsManager.Profiles = profilesBefore;
                    SettingsManager.ActiveProfileId = activeBefore;
                    GameLooks.Store = storeBefore;
                    BackToStock();
                    Drop(folder);
                }
            }, Limit, "the game look follow test timed out");
        }

        [Fact]
        public void ALookIsMadeFromArtThatIsOnThisPcAndAsksNothingOfSteam()
        {
            string folder = TempFolder();
            var storeBefore = GameLooks.Store;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    GameLooks.Store = new GameLookStore(System.IO.Path.Combine(folder, "looks"));
                    var cache = new SteamWorkshopCache(System.IO.Path.Combine(folder, "cache"));
                    // The switch is off for the whole test: nothing here may reach the network.
                    var art = new SteamGameArt(() => false, cache);
                    Assert.False(art.IsOn);
                    Assert.Empty(Wait(art.SearchAsync("portal", CancellationToken.None), dispatcher));

                    // No art on this PC and the switch off: no look, and the reason.
                    var none = Wait(GameLooks.MakeAsync(art, 620, "Portal 2", CancellationToken.None), dispatcher);
                    Assert.Null(none.Look);
                    Assert.Equal(GameLookFault.LookupOff, none.Fault);
                    Assert.Equal(GameLookFault.NoArt, Wait(GameLooks.MakeAsync(art, 0, "Nothing", CancellationToken.None), dispatcher).Fault);

                    // The art the Community tab cached: the look is made from it.
                    cache.PutBytes(CacheCategory.Art, "620_" + SteamGameArt.HeroFile, Jpeg(Blocks(960, 310, (0.7, Color.FromRgb(0x10, 0x18, 0x30)), (0.3, Color.FromRgb(0xE0, 0x60, 0x10)))));
                    cache.PutBytes(CacheCategory.Art, "620_" + SteamGameArt.LogoFile, Png(Blocks(300, 100, (1, Colors.White))));
                    var made = Wait(GameLooks.MakeAsync(art, 620, "  Portal 2 ", CancellationToken.None), dispatcher);
                    Assert.Equal(GameLookFault.None, made.Fault);
                    Assert.Equal(620, made.Look.AppId);
                    Assert.Equal("Portal 2", made.Look.Game);
                    foreach (string color in new[] { made.Look.Background, made.Look.Accent, made.Look.Secondary, made.Look.Attention })
                        Assert.Matches("^#[0-9A-F]{6}$", color);
                    Assert.True(ThemeColor.Luma(Hex(made.Look.Background)) < 0.2);
                    Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, made.Picture.Take(4).ToArray());
                    // Made is not kept: a look the user backs out of leaves nothing behind.
                    Assert.False(GameLooks.HasPicture(made.Look));
                    GameLooks.Keep(made);
                    Assert.True(GameLooks.HasPicture(made.Look));
                    var theme = GameLooks.ThemeOf(made.Look);
                    Assert.Equal(ThemePicture.Width, theme.Picture.PixelWidth);
                    Assert.Equal(GameLooks.TileAt, theme.TileAt);

                    // A game whose wide picture Steam lacks was cached as its store header.
                    cache.PutBytes(CacheCategory.Art, "730_" + SteamGameArt.HeaderFile, Jpeg(Blocks(460, 215, (1, Colors.DarkOliveGreen))));
                    Assert.NotNull(Wait(GameLooks.MakeAsync(art, 730, "Header Only", CancellationToken.None), dispatcher).Look);

                    // A color the profile holds that is no color is left to PadForge's own.
                    var odd = GameLooks.ThemeOf(new ProfileGameLook { AppId = 999, Game = null, Accent = "nope", Background = "#000000" });
                    Assert.Null(odd.Accent);
                    Assert.Equal(Colors.Black, odd.Background);
                    Assert.Equal(string.Empty, odd.Name);
                }
                finally
                {
                    GameLooks.Store = storeBefore;
                    BackToStock();
                    Drop(folder);
                }
            }, Limit, "the game look making test timed out");
        }

        [Fact]
        public void AProfileKeepsItsLookInTheSettingsFile()
        {
            var serializer = new XmlSerializer(typeof(ProfileData));
            var profile = new ProfileData { Name = "Steam", GameLook = Look(620) };
            using var buffer = new StringWriter();
            serializer.Serialize(buffer, profile);
            string xml = buffer.ToString();
            Assert.Contains("<GameLook AppId=\"620\" Game=\"Portal 2\" Background=\"#0A1020\" Accent=\"#3399FF\" Secondary=\"#FFAA33\" Attention=\"#FFE066\" />", xml);
            var back = (ProfileData)serializer.Deserialize(new StringReader(xml));
            Assert.Equal(620, back.GameLook.AppId);
            Assert.Equal("Portal 2", back.GameLook.Game);
            Assert.Equal("#3399FF", back.GameLook.Accent);
            // A copy for another profile is its own object.
            var copy = back.GameLook.Clone();
            Assert.NotSame(back.GameLook, copy);
            Assert.Equal(back.GameLook.Attention, copy.Attention);

            using var plain = new StringWriter();
            serializer.Serialize(plain, new ProfileData { Name = "Plain" });
            Assert.DoesNotContain("GameLook", plain.ToString());
            Assert.Null(((ProfileData)serializer.Deserialize(new StringReader(plain.ToString()))).GameLook);
        }

        [Fact]
        public void TheProfileDialogOffersTheLookOnlyForAProfileThatKnowsItsGame()
        {
            string folder = TempFolder();
            var storeBefore = GameLooks.Store;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    GameLooks.Store = new GameLookStore(System.IO.Path.Combine(folder, "looks"));
                    var cache = new SteamWorkshopCache(System.IO.Path.Combine(folder, "cache"));
                    var art = new SteamGameArt(() => false, cache);
                    var s = PadForge.Resources.Strings.Strings.Instance;

                    // No game: no switch.
                    var plain = new ProfileDialog();
                    plain.LoadForEdit("Plain", Array.Empty<string>());
                    Assert.False(plain.GameLookOffered);
                    Assert.Equal(Visibility.Collapsed, plain.GameLookPanel.Visibility);
                    plain.OfferGameLook(0, "Nothing", null, art);
                    Assert.False(plain.GameLookOffered);
                    plain.Close();

                    // A game, no look yet, no art and the switch off: turning it on says why it stays off.
                    var dialog = new ProfileDialog();
                    dialog.LoadForEdit("Steam", Array.Empty<string>());
                    dialog.OfferGameLook(620, "Portal 2", null, art);
                    Assert.True(dialog.GameLookOffered);
                    Assert.Equal(Visibility.Visible, dialog.GameLookPanel.Visibility);
                    Assert.False(dialog.GameLookSwitch.IsChecked == true);
                    Assert.Equal(Visibility.Collapsed, dialog.GameLookLine.Visibility);
                    dialog.GameLookSwitch.IsChecked = true;
                    Until(dispatcher, () => dialog.SaveButton.IsEnabled, "the look's answer");
                    Assert.False(dialog.GameLookSwitch.IsChecked == true);
                    Assert.Null(dialog.GameLook);
                    Assert.Equal(s.Profile_GameLook_LookupOff, dialog.GameLookStatus.Text);
                    Assert.True(dialog.SaveButton.IsEnabled);

                    // The art is on this PC: the switch stays on and the dialog holds a made look.
                    cache.PutBytes(CacheCategory.Art, "620_" + SteamGameArt.HeroFile, Jpeg(Blocks(960, 310, (1, Colors.Crimson))));
                    dialog.GameLookSwitch.IsChecked = true;
                    Until(dispatcher, () => dialog.SaveButton.IsEnabled, "the look");
                    Assert.True(dialog.GameLookSwitch.IsChecked == true);
                    Assert.Equal(620, dialog.GameLook.AppId);
                    Assert.NotNull(dialog.MadeGameLook);
                    Assert.Equal(Visibility.Collapsed, dialog.GameLookLine.Visibility);
                    // Nothing is kept until the profile takes the look.
                    Assert.False(GameLooks.HasPicture(dialog.GameLook));
                    // Off again: no look.
                    dialog.GameLookSwitch.IsChecked = false;
                    Assert.Null(dialog.GameLook);
                    Assert.Null(dialog.MadeGameLook);
                    dialog.Close();

                    // A look the profile wears whose picture is not on this PC: the line says so and offers to get it.
                    var worn = new ProfileDialog();
                    worn.LoadForEdit("Worn", Array.Empty<string>());
                    var look = Look(620);
                    worn.OfferGameLook(620, "Portal 2", look, art);
                    Assert.True(worn.GameLookSwitch.IsChecked == true);
                    Assert.Same(look, worn.GameLook);
                    Assert.Null(worn.MadeGameLook);
                    Assert.Equal(s.Profile_GameLook_NoPicture, worn.GameLookStatus.Text);
                    Assert.Equal(Visibility.Visible, worn.GameLookGetButton.Visibility);
                    worn.GameLookGetButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Until(dispatcher, () => worn.SaveButton.IsEnabled, "the look's picture");
                    Assert.NotNull(worn.MadeGameLook);
                    Assert.Equal(Visibility.Collapsed, worn.GameLookGetButton.Visibility);
                    worn.Close();
                }
                finally
                {
                    GameLooks.Store = storeBefore;
                    BackToStock();
                    Drop(folder);
                }
            }, Limit, "the profile dialog look test timed out");
        }

        // ─────────────────────────────────────────────
        //  The web controller's pages
        // ─────────────────────────────────────────────

        private static string WebFile(params string[] parts)
            => File.ReadAllText(System.IO.Path.Combine(new[] { RepoRoot(), "PadForge.App", "WebAssets" }.Concat(parts).ToArray()));

        [Fact]
        public void EveryColorOfTheSheetHasItsBrushOrIsMadeFromOne()
        {
            string sheet = WebFile("css", "padforge.css");
            string root = Regex.Match(sheet, @":root\s*\{(.*?)\n\}", RegexOptions.Singleline).Groups[1].Value;
            var named = Regex.Matches(root, @"(--[a-z0-9-]+)\s*:").Select(m => m.Groups[1].Value).Where(n => n != "--font").ToList();
            var rows = WebThemeSheet.Rows(sheet);
            Assert.Equal(27, named.Count);
            Assert.Equal(named, rows.Select(r => r.Name));
            Assert.Equal(new[] { "--ember-glow", "--veil" }, rows.Where(r => r.Brush == null).Select(r => r.Name));
        }

        [Fact]
        public void ThePagesTakeTheThemeInForceAndPadForgesOwnLooksLeaveThemAlone()
        {
            byte[] sheet = Encoding.UTF8.GetBytes(WebFile("css", "padforge.css")), page = Encoding.UTF8.GetBytes(WebFile("index.html")),
                script = Encoding.UTF8.GetBytes("var ground = '#0a0e15';");
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    ThemeManager.Apply(2, null);
                    WebThemeSheet.Refresh();
                    Assert.Null(WebThemeSheet.Current);
                    Assert.Same(sheet, WebThemeSheet.Dress("/css/padforge.css", sheet));
                    Assert.Same(page, WebThemeSheet.Dress("/index.html", page));

                    foreach (string id in new[] { "Starter:sonic-night-run", "Starter:rings-map-of-middle-earth" })
                    {
                        var theme = ThemeManager.Find(id);
                        ThemeManager.Apply(2, id);
                        WebThemeSheet.Refresh();
                        var dressing = WebThemeSheet.Current;
                        Assert.NotNull(dressing);
                        string dressed = Encoding.UTF8.GetString(WebThemeSheet.Dress("/css/padforge.css", sheet));
                        // The sheet as it is, then the theme's rule.
                        Assert.StartsWith(Encoding.UTF8.GetString(sheet), dressed);
                        string rule = dressed.Substring(Encoding.UTF8.GetString(sheet).Length);
                        Assert.Contains("color-scheme: " + (theme.Dark ? "dark" : "light") + ";", rule);
                        Color ember = ((SolidColorBrush)app.FindResource("EmberBrush")).Color;
                        Assert.Contains("--ember: #" + ember.R.ToString("x2") + ember.G.ToString("x2") + ember.B.ToString("x2") + ";", rule);
                        Assert.Contains("--ground: " + dressing.Ground + ";", rule);
                        Assert.Contains("--ember-glow: rgba(" + ember.R + ", " + ember.G + ", " + ember.B + ", 0.3);", rule);
                        Assert.Matches(@"--veil: rgba\(\d+, \d+, \d+, 0\.82\);", rule);
                        // Every color of the sheet is in the rule, each once, solid where the sheet's is solid.
                        foreach (var row in WebThemeSheet.Rows(Encoding.UTF8.GetString(sheet)))
                        {
                            var value = Regex.Matches(rule, Regex.Escape(row.Name) + @": ([^;]+);");
                            Assert.True(value.Count == 1, row.Name + " is in the theme's rule " + value.Count + " times");
                            Assert.Equal(row.StockValue.StartsWith("#"), value[0].Groups[1].Value.StartsWith("#"));
                        }
                        // A page names the theme's ground, and a file that is neither is as it was.
                        string html = Encoding.UTF8.GetString(WebThemeSheet.Dress("/index.html", page));
                        Assert.Contains("<meta name=\"theme-color\" content=\"" + dressing.Ground + "\">", html);
                        Assert.DoesNotContain("content=\"#0a0e15\"", html);
                        Assert.Same(script, WebThemeSheet.Dress("/js/any.js", script));
                    }

                    ThemeManager.Apply(1, null);
                    WebThemeSheet.Refresh();
                    Assert.Null(WebThemeSheet.Current);
                    Assert.Same(page, WebThemeSheet.Dress("/index.html", page));
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the web sheet test timed out");
        }

        // ─────────────────────────────────────────────
        //  What a theme must be able to reach
        // ─────────────────────────────────────────────

        // The accent's, the secondary's and the attention color's own tones, as PadForge's stock looks write them.
        private const string ThemedTones = "FF6B2C|C43D0C|FFA24D|FFD2B8|FFA500|FF8A57|FF8450|E85F25|FF8C3C|F2652A|FF9D5D|FF8228|E87A2E"
                                           + "|F2B84B|7CC7EA|2E6A8F|58B6E4";

        [Fact]
        public void NoPageWritesAThemeColorAsALiteral()
        {
            string app = System.IO.Path.Combine(RepoRoot(), "PadForge.App");
            var literal = new Regex("#(?:[0-9A-Fa-f]{2})?(?:" + ThemedTones + ")\\b", RegexOptions.IgnoreCase);
            var found = new List<string>();
            int swatches = 0;
            foreach (string path in Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories))
            {
                string relative = System.IO.Path.GetRelativePath(app, path).Replace('\\', '/');
                if (relative.StartsWith("bin/") || relative.StartsWith("obj/") || relative == "App.xaml") continue;
                foreach (var part in XDocument.Load(path, LoadOptions.SetLineInfo).Descendants())
                {
                    foreach (var attribute in part.Attributes())
                    {
                        if (!literal.IsMatch(attribute.Value)) continue;
                        // A swatch offers the user a color to pick for a lightbar or a key. It is the user's, never the theme's.
                        bool swatch = part.AncestorsAndSelf().Any(a => a.Attributes().Any(v => v.Value.Contains("Swatch", StringComparison.Ordinal)));
                        if (swatch) swatches++;
                        else found.Add(relative + ":" + ((System.Xml.IXmlLineInfo)part).LineNumber + " " + attribute.Name.LocalName + "=" + attribute.Value);
                    }
                }
            }
            Assert.Empty(found);
            // Positive controls: the walk reads colors, the swatches' among them, and the tokens themselves are written, in App.xaml.
            Assert.True(swatches >= 2, "the walk found " + swatches + " swatch colors");
            Assert.Matches(literal, File.ReadAllText(System.IO.Path.Combine(app, "App.xaml")));
        }

        [Fact]
        public void NoCodeKeepsAThemeColorAsAConstant()
        {
            string app = System.IO.Path.Combine(RepoRoot(), "PadForge.App");
            string tones = string.Join("|", ThemedTones.Split('|').Select(t =>
                "0x" + t.Substring(0, 2) + @"\s*,\s*0x" + t.Substring(2, 2) + @"\s*,\s*0x" + t.Substring(4, 2)));
            var literal = new Regex("(?:" + tones + ")|" + "\"" + "#(?:[0-9A-Fa-f]{2})?(?:" + ThemedTones + ")" + "\"", RegexOptions.IgnoreCase);
            // The stock tables, the engine's reference colors and the fallbacks of the look's reader.
            var keepers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Common/EmberTheme.cs", "Common/Themes/ThemeEngine.cs", "Common/Themes/ThemeLook.cs",
            };
            // A lightbar's preview draws the device's own light: its unlit blue and the wash that stands for a screen.
            const string DeviceLight = "Views/PadPage.xaml.cs";
            var found = new List<string>();
            int kept = 0, deviceLight = 0;
            foreach (string path in Directory.GetFiles(app, "*.cs", SearchOption.AllDirectories))
            {
                string relative = System.IO.Path.GetRelativePath(app, path).Replace('\\', '/');
                if (relative.StartsWith("bin/") || relative.StartsWith("obj/")) continue;
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!literal.IsMatch(lines[i])) continue;
                    if (keepers.Contains(relative)) kept++;
                    else if (relative == DeviceLight && lines[i].Contains("0x58, 0xB6, 0xE4", StringComparison.Ordinal)) deviceLight++;
                    else found.Add(relative + ":" + (i + 1) + " " + lines[i].Trim());
                }
            }
            Assert.Empty(found);
            Assert.True(kept >= 10, "the pattern found only " + kept + " of the stock tables' own colors");
            Assert.Equal(5, deviceLight);
        }

        // ─────────────────────────────────────────────
        //  The credits
        // ─────────────────────────────────────────────

        private static readonly string[] PictureSources =
        {
            "https://commons.wikimedia.org/wiki/File:Sonic_Frontiers_-_Sonic_on_Kronos_Island.jpg",
            "https://sonic.fandom.com/wiki/File:Early_render_sonic_lost_world.png",
            "https://commons.wikimedia.org/wiki/File:Hobbit_holes_reflected_in_water.jpg",
            "https://lotr.fandom.com/wiki/File:Middle_earth_map.jpg",
            "https://commons.wikimedia.org/wiki/File:Hogwarts_school_of_magic_in_late_1800s_according_to_Hogwarts_Legacy.jpg",
            "https://harrypotter.fandom.com/wiki/File:Gryffindor_ClearBG.png",
            "https://commons.wikimedia.org/wiki/File:Millennium_Falcon_Star_Wars_Galaxy%27s_Edge_Disneyland_Resort_in_Anaheim,_California_(48512627247).jpg",
            "https://starwars.fandom.com/wiki/File:Vader_Helmet.jpg",
            "https://airandspace.si.edu/collection-objects/model-star-trek-starship-enterprise-television-show/nasm_A19740668000",
            "https://memory-alpha.fandom.com/wiki/File:USS_Enterprise-D,_2364.jpg",
            "https://commons.wikimedia.org/wiki/File:Sonic_the_Hedgehog_logo.png",
            "https://commons.wikimedia.org/wiki/File:The_Lord_of_the_Rings_logo.svg",
            "https://commons.wikimedia.org/wiki/File:Harry_Potter_wordmark.svg",
            "https://commons.wikimedia.org/wiki/File:Star_Wars_Logo.svg",
            "https://commons.wikimedia.org/wiki/File:Star_Trek_-_The_Original_Series_-_official_logo.svg",
            "https://commons.wikimedia.org/wiki/File:Star_Trek_The_Next_Generation_official_logo.svg",
        };

        [Fact]
        public void EveryStarterPictureIsCreditedOnEverySurface()
        {
            string root = RepoRoot();
            string readme = File.ReadAllText(System.IO.Path.Combine(root, "README.md")), license = File.ReadAllText(System.IO.Path.Combine(root, "LICENSE"));
            string row = Assert.Single(readme.Split('\n'), l => l.StartsWith("| Theme pictures |", StringComparison.Ordinal));
            Assert.EndsWith("| Several licenses |", row.TrimEnd('\r'));
            string note = Assert.Single(readme.Split('\n'), l => l.StartsWith("- **Theme pictures**", StringComparison.Ordinal));
            int at = license.IndexOf("\nTheme pictures\n", StringComparison.Ordinal);
            if (at < 0) at = license.IndexOf("\nTheme pictures\r\n", StringComparison.Ordinal);
            Assert.True(at >= 0, "LICENSE has no Theme pictures entry");
            string entry = Regex.Replace(license.Substring(at, license.IndexOf("TriggerEffectGenerator", at, StringComparison.Ordinal) - at), "\\s+", " ");
            foreach (string source in PictureSources)
            {
                Assert.True(note.Contains(source, StringComparison.Ordinal), "README does not link " + source);
                Assert.True(entry.Contains(source, StringComparison.Ordinal), "LICENSE does not give " + source);
            }
            // The licenses that ask for their name, and the names they ask for.
            foreach (string credit in new[] { "CC BY 3.0", "CC BY 2.0", "CC BY-SA 2.0", "Jackie.lck", "Tomás Del Coro", "Atomhawk Design", "Smithsonian National Air and Space Museum", "A19740668000" })
            {
                Assert.True(note.Contains(credit, StringComparison.Ordinal), "README's note lacks " + credit);
                Assert.True(entry.Contains(credit, StringComparison.Ordinal), "LICENSE's entry lacks " + credit);
            }
            // Every starter names its picture's source in its own file.
            foreach (var theme in ThemeManager.Starters)
                Assert.Matches("found on|photographed by", theme.Credits);
        }
    }
}
