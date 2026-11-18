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

                    byte[] rest = Shot(ground);
                    slot.ReadHeld(new PadViewModel(0) { RawThumbLX = short.MaxValue, LeftTrigger = 1 });
                    Assert.NotEqual(StagePose.Rest, slot.StagePose);
                    PageRenderDump.Settle(dispatcher);
                    Lay(ground);
                    byte[] held = Shot(ground);

                    int inside = 0, outside = 0;
                    var strays = new System.Collections.Generic.List<string>();
                    for (int y = 0; y < Height; y++)
                        for (int x = 0; x < Width; x++)
                        {
                            int i = (y * Width + x) * 4;
                            int d = Math.Abs(held[i] - rest[i]) + Math.Abs(held[i + 1] - rest[i + 1])
                                + Math.Abs(held[i + 2] - rest[i + 2]) + Math.Abs(held[i + 3] - rest[i + 3]);
                            if (d <= 24) continue;
                            if (at.Contains(x + 0.5, y + 0.5)) inside++;
                            else
                            {
                                outside++;
                                if (strays.Count < 8)
                                    strays.Add($"({x},{y}) from {rest[i + 2]},{rest[i + 1]},{rest[i]},{rest[i + 3]} to {held[i + 2]},{held[i + 1]},{held[i]},{held[i + 3]}");
                            }
                        }
                    Assert.True(inside > 100, $"{kind}: a held stick and trigger changed {inside} pixels of the card's picture at {at}");
                    // Seen once, in the 2D pass, as one pixel in a full run of
                    // the suite (2026-11-17), and in no run of the test alone.
                    // A held input redraws the picture alone, clipped to its
                    // own box, so the pixel came from something else that
                    // changed between the two frames. The frames are kept
                    // when it comes again.
                    Assert.True(outside == 0, $"{kind}: a held stick and trigger changed {outside} pixels beside the card's picture at {at}: "
                        + $"{string.Join(", ", strays)}. Frames: {(outside == 0 ? string.Empty : Keep(rest, kind + "-rest") + " and " + Keep(held, kind + "-held"))}");

                    // Let go, and the card is as it was.
                    slot.ReadHeld(new PadViewModel(0));
                    PageRenderDump.Settle(dispatcher);
                    Lay(ground);
                    Assert.Equal(rest, Shot(ground));
                    ground.Child = null;
                }
            }, TimeSpan.FromMinutes(3), "The Home page test timed out.");
        }
    }
}
