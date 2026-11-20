using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PadForge.Common;
using PadForge.Controls;
using PadForge.Engine;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A forging card on the Home page itself moves with what is
    /// held: the page's own card template binds the pose to the moving
    /// picture, hides the picture under it and the ember marks over it, and
    /// a held stick and trigger change the card's controller and nothing
    /// beside it. In 2D and in 3D.</summary>
    [Collection("WpfApplicationSingleton")]
    public class HomePageMovingCardTests
    {
        private const int Width = 1176, Height = 760;

        private static IEnumerable<T> Under<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T found) yield return found;
                foreach (var deeper in Under<T>(child)) yield return deeper;
            }
        }

        private static byte[] Shot(FrameworkElement ground)
        {
            var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(ground);
            var pixels = new byte[Width * Height * 4];
            bitmap.CopyPixels(pixels, Width * 4, 0);
            return pixels;
        }

        /// <summary>Saves a frame beside the test host's other temporary
        /// files and returns where, for a failure's message: the pixels
        /// that differed say where to look, and the two frames say what was
        /// there.</summary>
        private static string Keep(byte[] pixels, string name)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PadForge.Tests-movingcard-" + name + ".png");
            var frame = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Pbgra32, null, pixels, Width * 4);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(frame));
            using var file = System.IO.File.Create(path);
            png.Save(file);
            return path;
        }

        private static void Lay(FrameworkElement ground)
        {
            PageRenderDump.Flush(ground);
            ground.Measure(new Size(Width, Height));
            ground.Arrange(new Rect(0, 0, Width, Height));
            ground.UpdateLayout();
        }

        /// <summary>Gives the card what is held, lets the page settle and
        /// returns the frame.</summary>
        private static byte[] Hold(SlotSummary slot, PadViewModel input, System.Windows.Threading.Dispatcher dispatcher, FrameworkElement ground)
        {
            slot.ReadHeld(input);
            PageRenderDump.Settle(dispatcher);
            Lay(ground);
            return Shot(ground);
        }

        /// <summary>Whether two frames differ at a pixel by more than a
        /// trace.</summary>
        private static bool Apart(byte[] a, byte[] b, int i)
            => Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) + Math.Abs(a[i + 3] - b[i + 3]) > 24;

        /// <summary>What an element last drew. WPF keeps it in a private
        /// field and replaces it each time the element draws anew.</summary>
        private static readonly System.Reflection.FieldInfo Drawn =
            typeof(UIElement).GetField("_drawingContent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("WPF's UIElement._drawingContent moved. What a part last drew cannot be read.");

        /// <summary>Each part of the page outside the picture, with what it
        /// last drew, where it sits, its size, whether it shows and its
        /// opacity.</summary>
        private static Dictionary<UIElement, (object drawn, Point at, Size size, Visibility shows, double opacity)> Parts(FrameworkElement ground, FrameworkElement stage)
        {
            var parts = new Dictionary<UIElement, (object, Point, Size, Visibility, double)>();
            foreach (var part in Under<UIElement>(ground))
                if (!ReferenceEquals(part, stage) && !stage.IsAncestorOf(part))
                    parts[part] = (Drawn.GetValue(part), part.TransformToAncestor(ground).Transform(default), part.RenderSize, part.Visibility, part.Opacity);
            return parts;
        }

        /// <summary>The parts outside the picture that drew anew, moved,
        /// resized, showed, hid or faded between two readings, or an empty
        /// string when none did.</summary>
        private static string Moved(Dictionary<UIElement, (object drawn, Point at, Size size, Visibility shows, double opacity)> before,
            Dictionary<UIElement, (object drawn, Point at, Size size, Visibility shows, double opacity)> after, string what)
        {
            static string Name(UIElement part) => part is TextBlock line ? "TextBlock \"" + line.Text + "\"" : part.GetType().Name;
            var changed = new List<string>();
            foreach (var (part, was) in before)
            {
                if (!after.TryGetValue(part, out var now)) { changed.Add(Name(part) + " left the page"); continue; }
                if (!ReferenceEquals(was.drawn, now.drawn)) changed.Add(Name(part) + " drew anew");
                if (was.at != now.at || was.size != now.size) changed.Add(Name(part) + " moved from " + was.at + " " + was.size + " to " + now.at + " " + now.size);
                if (was.shows != now.shows || was.opacity != now.opacity) changed.Add(Name(part) + " went from " + was.shows + " " + was.opacity + " to " + now.shows + " " + now.opacity);
            }
            foreach (var part in after.Keys)
                if (!before.ContainsKey(part)) changed.Add(Name(part) + " came onto the page");
            return changed.Count == 0 ? string.Empty : what + "changed parts beside the card's picture: " + string.Join("; ", changed.Take(12));
        }

        [Fact]
        public void AForgingCardOnTheHomePageMovesWithAHeldStick_In2DAndIn3D()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                foreach (bool still in new[] { false, true })
                {
                    string kind = still ? "3D" : "2D";
                    var dash = new DashboardViewModel { RemoteLink = new SettingsViewModel() };
                    var slot = new SlotSummary(0)
                    {
                        OutputType = VirtualControllerType.Xbox, PresetName = "Xbox Series X|S Controller",
                        TypeInstanceLabel = "1", SlotNumber = 1,
                    };
                    slot.MappedDevices = new ObservableCollection<PadViewModel.MappedDeviceInfo>
                    {
                        new() { Name = "DualSense Wireless Controller", IsOnline = true, IsSending = true },
                    };
                    slot.MappedDeviceCount = 1;
                    slot.ConnectedDeviceCount = 1;
                    if (still)
                    {
                        StagePictures.MakeStill("XboxSeries", "Carbon");
                        Assert.True(StagePictures.TryGetStill("XboxSeries", "Carbon", out var color, out var gray));
                        slot.SetPicture("xbox-series-xs", VirtualControllerType.Xbox, string.Empty, true, color, gray, false,
                            StagePictures.StillMarksFor("XboxSeries", "Carbon", "xbox-series-xs"), StagePictures.LiveModelFor("XboxSeries", "Carbon"));
                    }
                    else
                    {
                        string art = StagePictures.PathFor("xbox-series-xs", VirtualControllerType.Xbox, _ => null);
                        slot.SetPicture("xbox-series-xs", VirtualControllerType.Xbox, string.Empty, false,
                            StagePictures.Color(art), StagePictures.Gray(art), false, StagePictures.ArtMarks(art), null, StagePictures.LiveArtFor(art));
                    }
                    dash.EngineStateKey = "Running";
                    dash.SlotSummaries.Add(slot);
                    slot.EngineStopped = false;
                    slot.IsVirtualControllerConnected = true;
                    Assert.Equal(SlotSummary.HeatForging, slot.Heat);
                    Assert.True(slot.PictureMoves, kind + ": the card's picture does not move");

                    var page = new HomePage { DataContext = dash };
                    var ground = new Border { Background = Brushes.Black, Child = page };
                    Lay(ground);
                    PageRenderDump.Settle(dispatcher);
                    Lay(ground);

                    // A page in no window is never IsVisible, so each part is
                    // read by the visibility its style's triggers give it.
                    FrameworkElement stage = still
                        ? Under<LiveStage>(ground).Single(s => s.Visibility == Visibility.Visible)
                        : Under<LiveArtStage>(ground).Single(s => s.Visibility == Visibility.Visible);
                    var colorStyle = (Style)page.FindResource("SlotPictureColor");
                    var under = Under<Image>(ground).Where(i => ReferenceEquals(i.Style, colorStyle)).ToList();
                    Assert.NotEmpty(under);
                    Assert.All(under, i => Assert.Equal(Visibility.Hidden, i.Visibility));
                    var glows = Under<HeldGlow>(ground).ToList();
                    Assert.NotEmpty(glows);
                    Assert.All(glows, g => Assert.True(g.Visibility != Visibility.Visible, kind + ": the ember marks show over a moving picture"));
                    Rect at = stage.TransformToAncestor(ground).TransformBounds(new Rect(stage.RenderSize));

                    // Each frame draws every line of text on the page anew:
                    // a bitmap render puts the page on a render channel and
                    // takes it off again. In loaded runs of the suite one such
                    // frame drew two glyph edges of unchanged text a little
                    // differently from every other frame, the same two pixels
                    // on 2026-11-19 and 2026-11-20, so no two frames are
                    // compared bit for bit beside the picture. What a held
                    // input may change beside it is read two ways. No part of
                    // the page outside the picture draws anew, moves or
                    // resizes. And a pixel beside the picture counts as
                    // changed only when both held frames differ from both
                    // rest frames, taken rest, held, rest, held: a frame drawn
                    // differently, or a run of them in a row, cannot do that.
                    var parts = Parts(ground, stage);
                    var input = new PadViewModel(0) { RawThumbLX = short.MaxValue, LeftTrigger = 1 };
                    byte[] rest = Shot(ground);
                    byte[] held = Hold(slot, input, dispatcher, ground);
                    Assert.NotEqual(StagePose.Rest, slot.StagePose);
                    string moved = Moved(parts, Parts(ground, stage), kind + ": a held stick and trigger ");
                    Assert.True(moved.Length == 0, moved);
                    byte[] restAgain = Hold(slot, new PadViewModel(0), dispatcher, ground);
                    Assert.Equal(StagePose.Rest, slot.StagePose);
                    moved = Moved(parts, Parts(ground, stage), kind + ": letting go ");
                    Assert.True(moved.Length == 0, moved);
                    byte[] heldAgain = Hold(slot, input, dispatcher, ground);
                    moved = Moved(parts, Parts(ground, stage), kind + ": a stick and trigger held again ");
                    Assert.True(moved.Length == 0, moved);

                    int inside = 0, outside = 0, back = 0;
                    var strays = new List<string>();
                    for (int y = 0; y < Height; y++)
                        for (int x = 0; x < Width; x++)
                        {
                            int i = (y * Width + x) * 4;
                            if (at.Contains(x + 0.5, y + 0.5))
                            {
                                if (Apart(held, rest, i)) inside++;
                                if (restAgain[i] != rest[i] || restAgain[i + 1] != rest[i + 1] || restAgain[i + 2] != rest[i + 2] || restAgain[i + 3] != rest[i + 3]) back++;
                            }
                            else if (Apart(held, rest, i) && Apart(held, restAgain, i) && Apart(heldAgain, rest, i) && Apart(heldAgain, restAgain, i))
                            {
                                outside++;
                                if (strays.Count < 8)
                                    strays.Add($"({x},{y}) from {rest[i + 2]},{rest[i + 1]},{rest[i]},{rest[i + 3]} to {held[i + 2]},{held[i + 1]},{held[i]},{held[i + 3]}");
                            }
                        }
                    Assert.True(inside > 100, $"{kind}: a held stick and trigger changed {inside} pixels of the card's picture at {at}");
                    Assert.True(outside == 0, $"{kind}: a held stick and trigger changed {outside} pixels beside the card's picture at {at}: "
                        + $"{string.Join(", ", strays)}. Frames: {(outside == 0 ? string.Empty : string.Join(", ", Keep(rest, kind + "-rest"), Keep(held, kind + "-held"), Keep(restAgain, kind + "-rest-again"), Keep(heldAgain, kind + "-held-again")))}");
                    // Let go, and the card's picture is as it was.
                    Assert.True(back == 0, $"{kind}: after letting go {back} pixels of the card's picture at {at} differ from the picture at rest");
                    ground.Child = null;
                }
            }, TimeSpan.FromMinutes(3), "The Home page test timed out.");
        }
    }
}
