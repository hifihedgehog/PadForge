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
    /// beside the pad, at any size the frame takes, and none covers another.
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

        [Fact]
        public void TheCalloutsAreDrawnAtTwelveBesideThePadWithoutCoveringEachOther()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                // The frame's width in the owner's window (workshop-manifest.png,
                // 617 wide inside the card), and a narrow window's.
                foreach (double width in new[] { 618.0, 460.0 })
                {
                    var (frame, preview) = Frame(app, "controller_switch_pro", s_switchPro);
                    Lay(frame, width);
                    Picture(frame, $"workshop-callouts-{width:0}.png");
                    var words = Words(frame, preview);
                    Assert.Equal(s_switchPro.Length, words.Count);
                    foreach (var (text, at, size) in words)
                    {
                        string where = $"{width:0} wide: \"{text}\" is drawn {size:0.##} tall at {at}";
                        Assert.True(size >= 12 - 0.01, where);
                        Assert.True(at.Left >= 1 - 0.01 && at.Right <= width - 1 + 0.01 && at.Top >= 1 - 0.01 && at.Bottom <= 199 + 0.01, where + ", outside the frame");
                    }
                    for (int i = 0; i < words.Count; i++)
                        for (int j = i + 1; j < words.Count; j++)
                        {
                            Rect a = words[i].At, b = words[j].At;
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
                        Picture(frame, "workshop-callouts-618-light.png");
                    }
                    finally
                    {
                        PageRenderDump.ApplyTheme(Wpf.Ui.Appearance.ApplicationTheme.Dark);
                        PadForge.Common.EmberTheme.ApplyAccent();
                    }
                }
            }, TimeSpan.FromMinutes(2), "the Workshop callout test timed out");
        }
    }
}
