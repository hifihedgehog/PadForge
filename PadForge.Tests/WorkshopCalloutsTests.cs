using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The callouts on the pad in Get Profiles' What Comes Across. The
    /// whole drawing, words included, was scaled down to the 200-tall
    /// frame, so the words came out about 5 tall where 12 is the least text
    /// PadForge draws (typography-pass-2026-11-17). They are drawn at 12 now,
    /// beside the pad, at any size the frame takes, and none covers another,
    /// also on a side with more callouts than fit at the usual pitch.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class WorkshopCalloutsTests
    {
        // A Switch Pro config's bindings, as the frame workshop-manifest.png
        // shows them: every control of the pad called out.
        private static readonly (string Anchor, string Label)[] s_switchPro =
        {
            ("LeftTrigger", "Left Trigger"), ("LeftShoulder", "Left Bumper"), ("ButtonBack", "View"),
            ("LeftThumbRing", "Left Stick, Left Stick Click"), ("DPadUp", "D-Pad Up"), ("DPadLeft", "D-Pad Left"),
            ("DPadRight", "D-Pad Right"), ("DPadDown", "D-Pad Down"), ("ButtonShare", "Share"),
            ("RightTrigger", "Right Trigger"), ("RightShoulder", "Right Bumper"), ("ButtonStart", "Menu"),
            ("ButtonX", "X"), ("ButtonY", "Y"), ("ButtonA", "A"), ("ButtonB", "B"), ("ButtonGuide", "Guide"),
            ("RightThumbRing", "Right Stick, Right Stick Click"),
        };

        private static void Settle()
            => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));

        private static IEnumerable<DependencyObject> Below(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var deeper in Below(child)) yield return deeper;
            }
        }

        /// <summary>The preview in its frame as Get Profiles holds it: 200
        /// tall with 8 of padding, on a card.</summary>
        private static (Border Frame, WorkshopControllerPreview Preview) Frame(App app, string tag, IEnumerable<(string, string)> callouts)
        {
            var preview = new WorkshopControllerPreview();
            var frame = new Border
            {
                Height = 200, Padding = new Thickness(8), BorderThickness = new Thickness(1),
                Child = preview,
            };
            frame.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            frame.SetResourceReference(Border.BorderBrushProperty, "SteelLineSoftBrush");
            preview.Render(tag, callouts.Select(c => new WorkshopControllerPreview.Callout(c.Item1, c.Item2)));
            return (frame, preview);
        }

        private static void Lay(FrameworkElement frame, double width)
        {
            frame.Width = width;
            frame.Measure(new Size(width, 200));
            frame.Arrange(new Rect(0, 0, width, 200));
            frame.UpdateLayout();
            Settle();
            frame.UpdateLayout();
        }

        private static void Picture(FrameworkElement frame, string name)
        {
            string dir = Environment.GetEnvironmentVariable("PADFORGE_PAGE_RENDER_DIR");
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            var bitmap = new RenderTargetBitmap((int)Math.Round(frame.ActualWidth * 1.5), (int)Math.Round(frame.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
            bitmap.Render(frame);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(dir, name));
            png.Save(file);
        }

        /// <summary>Every word the preview draws, with where it is drawn on
        /// the frame and at what size.</summary>
        private static List<(string Text, Rect At, double Size)> Words(Border frame, WorkshopControllerPreview preview)
        {
            var words = new List<(string, Rect, double)>();
            foreach (var text in Below(preview).OfType<TextBlock>())
            {
                if (text.Visibility != Visibility.Visible || string.IsNullOrEmpty(text.Text)) continue;
                var toFrame = text.TransformToAncestor(frame);
                Rect at = toFrame.TransformBounds(new Rect(text.RenderSize));
                Point o = toFrame.Transform(new Point(0, 0)), down = toFrame.Transform(new Point(0, 1));
                double scale = down.Y - o.Y;
                words.Add((text.Text, at, text.FontSize * scale));
            }
            return words;
        }

        private static Rect Em((string Text, Rect At, double Size) word)
            => new Rect(word.At.Left, word.At.Top + (word.At.Height - word.Size) / 2, word.At.Width, word.Size);

        [Fact]
        public void TheCalloutsAreDrawnAtTwelveBesideThePadWithoutCoveringEachOther()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                // The frame's width in the owner's window (workshop-manifest.png,
                // 617 wide inside the card), and a narrow window's.
                // A Steam Deck config that binds every control the Deck's art
                // draws, 14 on each side: more than fit at the usual pitch.
                var deck = PadForge.Models2D.SteamDeckLayout.Overlays
                    .Select(o => o.TargetName).Where(t => !t.EndsWith("Base", StringComparison.Ordinal)).Distinct()
                    .Select(t => (t, t)).ToArray();
                foreach (var (tag, callouts, width) in new[]
                {
                    ("controller_switch_pro", s_switchPro, 618.0), ("controller_switch_pro", s_switchPro, 460.0),
                    ("controller_neptune", deck, 618.0),
                })
                {
                    var (frame, preview) = Frame(app, tag, callouts);
                    Lay(frame, width);
                    Picture(frame, $"workshop-callouts-{tag}-{width:0}.png");
                    var words = Words(frame, preview);
                    Assert.Equal(callouts.Length, words.Count);
                    foreach (var (text, at, size) in words)
                    {
                        string where = $"{width:0} wide: \"{text}\" is drawn {size:0.##} tall at {at}";
                        Assert.True(size >= 12 - 0.01, where);
                        Assert.True(at.Left >= 1 - 0.01 && at.Right <= width - 1 + 0.01 && at.Top >= 1 - 0.01 && at.Bottom <= 199 + 0.01, where + ", outside the frame");
                    }
                    for (int i = 0; i < words.Count; i++)
                        for (int j = i + 1; j < words.Count; j++)
                        {
                            // Each word's em box: its size tall, about the
                            // middle of its line.
                            Rect a = Em(words[i]), b = Em(words[j]);
                            Rect both = Rect.Intersect(a, b);
                            Assert.True(both.IsEmpty || both.Width * both.Height < 0.01,
                                $"{width:0} wide: \"{words[i].Text}\" at {a} covers \"{words[j].Text}\" at {b}");
                        }
                }

                // A picture in the light theme, when pictures are asked for.
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PADFORGE_PAGE_RENDER_DIR")))
                {
                    PageRenderDump.ApplyTheme(Wpf.Ui.Appearance.ApplicationTheme.Light);
                    PadForge.Common.EmberTheme.ApplyAccent();
                    Settle();
                    try
                    {
                        var (frame, _) = Frame(app, "controller_switch_pro", s_switchPro);
                        Lay(frame, 618);
                        Picture(frame, "workshop-callouts-controller_switch_pro-618-light.png");
                    }
                    finally
                    {
                        PageRenderDump.ApplyTheme(Wpf.Ui.Appearance.ApplicationTheme.Dark);
                        PadForge.Common.EmberTheme.ApplyAccent();
                    }
                }
            }, TimeSpan.FromMinutes(2), "the Workshop callout test timed out");
        }

        /// <summary>The Deck's back paddles stand in the margin every leader
        /// on their side crosses to reach the pad. A leader runs under the
        /// lit controls, as it did before the callouts moved out of the
        /// scaled art, so the touchpad's leader never draws over a lit
        /// paddle. Its run over the pad is drawn 1 wide at the frame's
        /// size.</summary>
        [Fact]
        public void ALeaderRunsUnderTheLitControlsItCrosses()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var (frame, preview) = Frame(app, "controller_neptune",
                    new[] { ("Paddle2", "Back Left Upper"), ("LeftTouchpadClick", "Left Trackpad Click") });
                Lay(frame, 618);
                Picture(frame, "workshop-callouts-neptune-paddle-and-pad.png");
                var model = (Canvas)preview.FindName("ModelCanvas");
                var over = (Canvas)preview.FindName("CalloutCanvas");

                var lit = model.Children.OfType<System.Windows.Shapes.Rectangle>()
                    .Where(r => Panel.GetZIndex(r) == 5)
                    .Select(r => r.TransformToVisual(over).TransformBounds(new Rect(r.RenderSize)))
                    .ToList();
                Assert.Equal(2, lit.Count);

                foreach (var line in over.Children.OfType<System.Windows.Shapes.Polyline>())
                {
                    var pts = line.Points;
                    for (int i = 1; i < pts.Count; i++)
                        for (int k = 0; k <= 200; k++)
                        {
                            Point p = pts[i - 1] + (pts[i] - pts[i - 1]) * (k / 200.0);
                            foreach (var at in lit)
                            {
                                var inside = at;
                                inside.Inflate(-1, -1);
                                Assert.False(!inside.IsEmpty && inside.Contains(p),
                                    $"a leader drawn over the lit controls crosses {at} at {p}");
                            }
                        }
                }

                // Each callout's run over the pad is under the lit controls,
                // over the body, and 1 wide where it is drawn.
                var under = model.Children.OfType<System.Windows.Shapes.Polyline>().ToList();
                Assert.Equal(2, under.Count);
                var toOver = model.TransformToVisual(over);
                double scale = toOver.Transform(new Point(0, 1)).Y - toOver.Transform(new Point(0, 0)).Y;
                foreach (var line in under)
                {
                    Assert.Equal(3, Panel.GetZIndex(line));
                    Assert.True(Math.Abs(line.StrokeThickness * scale - 1) < 0.01,
                        $"a leader over the pad is drawn {line.StrokeThickness * scale:0.###} wide");
                }
            }, TimeSpan.FromMinutes(2), "the Workshop callout test timed out");
        }
    }
}
