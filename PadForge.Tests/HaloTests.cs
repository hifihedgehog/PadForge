using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The light around what is live (#574, the world-class pass): how the
    /// halo's light falls from its child's edge to its reach, that it draws
    /// only outside its child and takes no click, how it comes on, breathes
    /// and holds still, the sidebar pill's outline and light by state, and
    /// the theme's steps and tints the shell stands on.
    /// </summary>
    public class HaloTests
    {
        private static void RunSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the halo run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static int Count(string text, string needle) =>
            text.Split(new[] { needle }, StringSplitOptions.None).Length - 1;

        private static int At(string text, string needle)
        {
            int at = text.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(at >= 0, needle + " is not there");
            Assert.True(at == text.LastIndexOf(needle, StringComparison.Ordinal), needle + " is there twice");
            return at;
        }

        /// <summary>The light is whole at the child's edge and gone at the
        /// reach, and falls ever more slowly on the way. A brush's stops say
        /// the same in eight straight pieces, and a corner's brush is whole
        /// for the part of its radius that lies inside the child's own
        /// rounding.</summary>
        [Fact]
        public void TheLightFallsFromItsStrengthAtTheEdgeToNothingAtTheReach()
        {
            Assert.Equal(1.0, Halo.Fall(0));
            Assert.Equal(0.0, Halo.Fall(1));
            Assert.Equal(1.0, Halo.Fall(-3));
            Assert.Equal(0.0, Halo.Fall(7));
            Assert.Equal(0.25, Halo.Fall(0.5), 12);
            double last = 1, lastDrop = double.MaxValue;
            for (int i = 1; i <= 20; i++)
            {
                double now = Halo.Fall(i / 20.0), drop = last - now;
                Assert.True(now < last, "the light rose at " + i);
                Assert.True(drop < lastDrop, "the fall did not slow at " + i);
                last = now;
                lastDrop = drop;
            }

            var ember = Color.FromRgb(0xFF, 0x6B, 0x2C);
            var side = Halo.Stops(ember, 0.4, 0);
            Assert.True(side.IsFrozen);
            Assert.Equal(Halo.Steps + 1, side.Count);
            Assert.Equal(0.0, side[0].Offset);
            Assert.Equal((byte)102, side[0].Color.A);
            Assert.Equal(1.0, side[^1].Offset);
            Assert.Equal((byte)0, side[^1].Color.A);
            for (int i = 0; i < side.Count; i++)
            {
                Assert.Equal(i / (double)Halo.Steps, side[i].Offset, 12);
                Assert.Equal((byte)Math.Round(0.4 * Halo.Fall(side[i].Offset) * 255), side[i].Color.A);
                Assert.Equal(ember, Color.FromRgb(side[i].Color.R, side[i].Color.G, side[i].Color.B));
            }

            // A corner of radius 12 with a reach of 28: the first 12 of 40
            // are inside the child.
            var corner = Halo.Stops(ember, 0.4, 0.3);
            Assert.Equal(Halo.Steps + 2, corner.Count);
            Assert.Equal(0.0, corner[0].Offset);
            Assert.Equal(0.3, corner[1].Offset, 12);
            Assert.Equal((byte)102, corner[0].Color.A);
            Assert.Equal((byte)102, corner[1].Color.A);
            Assert.Equal(1.0, corner[^1].Offset, 12);
            Assert.Equal((byte)0, corner[^1].Color.A);
            Assert.Equal(0.3 + 0.7 * 0.5, corner[1 + Halo.Steps / 2].Offset, 12);
        }

        private const int W = 200, H = 120;

        private static (Grid Ground, Halo Halo, Border Child) Scene(double strength = 0.5)
        {
            var child = new Border { Width = 80, Height = 40, CornerRadius = new CornerRadius(12), Background = Brushes.Transparent };
            var halo = new Halo
            {
                Child = child, Radius = 28, Strength = strength, CornerRadius = 12,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var ground = new Grid { Width = W, Height = H };
            ground.Children.Add(halo);
            return (ground, halo, child);
        }

        private static byte[] Alphas(FrameworkElement element)
        {
            element.Measure(new Size(W, H));
            element.Arrange(new Rect(0, 0, W, H));
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var pixels = new byte[W * H * 4];
            bitmap.CopyPixels(pixels, W * 4, 0);
            var alphas = new byte[W * H];
            for (int i = 0; i < alphas.Length; i++) alphas[i] = pixels[i * 4 + 3];
            return alphas;
        }

        /// <summary>The light's opacity, as a byte, at a distance from the
        /// child's edge.</summary>
        private static double Expected(double strength, double distance)
            => strength * Halo.Fall(distance / 28.0) * 255;

        /// <summary>An unlit halo draws nothing. A lit one is as bright as
        /// its strength at the child's edge, falls to nothing at its reach
        /// the same way on every side and around a corner, and draws
        /// nothing inside the child, whose fill is clear here.</summary>
        [Fact]
        public void AnUnlitHaloDrawsNothing_AndALitOneDrawsOnlyOutsideItsChild() => RunSta(() =>
        {
            var (ground, halo, _) = Scene();
            Assert.Equal(0.0, halo.Level);
            Assert.All(Alphas(ground), a => Assert.Equal((byte)0, a));

            halo.IsLit = true;
            Assert.Equal(1.0, halo.Level);
            var lit = Alphas(ground);
            byte A(int x, int y) => lit[y * W + x];
            void Near(double expected, int x, int y, double within = 6)
                => Assert.True(Math.Abs(expected - A(x, y)) <= within, "at " + x + "," + y + ": " + A(x, y) + " where " + expected.ToString("F1") + " belongs");

            // The child is 80 by 40 in the middle: x 60 to 140, y 40 to 80.
            // Inside it, and one pixel inside each straight side: nothing.
            foreach (var (x, y) in new[] { (100, 60), (100, 40), (100, 79), (60, 60), (139, 60), (75, 45), (125, 75) })
                Assert.Equal((byte)0, A(x, y));

            // Above the top side's middle: at the edge, part way, and past the reach.
            Near(Expected(0.5, 0.5), 100, 39);
            Near(Expected(0.5, 7.5), 100, 32);
            Near(Expected(0.5, 13.5), 100, 26);
            Near(Expected(0.5, 21.5), 100, 18);
            Assert.Equal((byte)0, A(100, 11));
            Assert.Equal((byte)0, A(100, 5));
            Assert.True(A(100, 39) > A(100, 32) && A(100, 32) > A(100, 26) && A(100, 26) > A(100, 18) && A(100, 18) > 0);

            // The same distance from each side reads the same.
            foreach (int distance in new[] { 1, 8, 14, 22 })
            {
                byte top = A(100, 40 - distance), bottom = A(100, 79 + distance), left = A(60 - distance, 60), right = A(139 + distance, 60);
                Assert.True(Math.Abs(top - bottom) <= 2 && Math.Abs(top - left) <= 2 && Math.Abs(top - right) <= 2,
                    distance + " out: " + top + " " + bottom + " " + left + " " + right);
            }

            // Around the top left corner, whose rounding turns about (72, 52):
            // the notch between the child's rounding and its box is lit, and
            // the light falls along the diagonal as it does off a side.
            double Off(int x, int y) => Math.Sqrt(Math.Pow(72 - (x + 0.5), 2) + Math.Pow(52 - (y + 0.5), 2)) - 12;
            Near(Expected(0.5, Off(61, 41)), 61, 41);
            Near(Expected(0.5, Off(53, 33)), 53, 33);
            Near(Expected(0.5, Off(46, 26)), 46, 26);
            Assert.Equal((byte)0, A(34, 14));
            // And the four corners agree.
            byte tl = A(53, 33), tr = A(146, 33), bl = A(53, 86), br = A(146, 86);
            Assert.True(Math.Abs(tl - tr) <= 2 && Math.Abs(tl - bl) <= 2 && Math.Abs(tl - br) <= 2, "corners: " + tl + " " + tr + " " + bl + " " + br);
            // No seam where a side's piece meets a corner's: along the line
            // seven out from the top, the light is even across x = 72.
            for (int x = 69; x <= 75; x++)
                Assert.True(Math.Abs(A(x, 32) - A(72, 32)) <= 3, "a seam at x " + x + ": " + A(x, 32) + " by " + A(72, 32));

            // Unlit again: nothing.
            halo.IsLit = false;
            Assert.Equal(0.0, halo.Level);
            Assert.All(Alphas(ground), a => Assert.Equal((byte)0, a));
        });

        /// <summary>A stronger halo is brighter, one with no reach or no
        /// strength draws nothing, and a change of either is drawn.</summary>
        [Fact]
        public void TheLightFollowsItsStrengthItsReachAndItsColor() => RunSta(() =>
        {
            var (ground, halo, _) = Scene(0.2);
            halo.IsLit = true;
            var faint = Alphas(ground);
            Assert.True(Math.Abs(Expected(0.2, 0.5) - faint[39 * W + 100]) <= 4);
            halo.Strength = 0.8;
            var strong = Alphas(ground);
            Assert.True(Math.Abs(Expected(0.8, 0.5) - strong[39 * W + 100]) <= 8);

            // A shorter reach ends sooner.
            halo.Radius = 10;
            var near = Alphas(ground);
            Assert.True(near[35 * W + 100] > 0);
            Assert.Equal((byte)0, near[28 * W + 100]);

            // The color is the light's own.
            halo.Color = Color.FromRgb(0xF2, 0xB8, 0x4B);
            ground.Measure(new Size(W, H));
            ground.Arrange(new Rect(0, 0, W, H));
            ground.UpdateLayout();
            var bitmap = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(ground);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(100, 39, 1, 1), pixel, 4, 0);
            // Premultiplied blue, green, red, alpha: gold has more green than ember.
            Assert.True(pixel[3] > 0);
            Assert.True(Math.Abs(pixel[1] / (double)pixel[3] - 0xB8 / 255.0) < 0.03, "green " + pixel[1] + " of " + pixel[3]);
            Assert.True(Math.Abs(pixel[2] / (double)pixel[3] - 0xF2 / 255.0) < 0.03, "red " + pixel[2] + " of " + pixel[3]);

            halo.Strength = 0;
            Assert.All(Alphas(ground), a => Assert.Equal((byte)0, a));
            halo.Strength = 0.5;
            halo.Radius = 0;
            Assert.All(Alphas(ground), a => Assert.Equal((byte)0, a));
        });

        /// <summary>The light lies under the child, takes no click and has
        /// no part in what a screen reader is told. A halo with no child
        /// lights its own box, as a sidebar pill's does.</summary>
        [Fact]
        public void TheLightLiesUnderTheChild_AndTakesNoClick() => RunSta(() =>
        {
            var (ground, halo, child) = Scene();
            halo.IsLit = true;
            Alphas(ground);
            Assert.Equal(2, VisualTreeHelper.GetChildrenCount(halo));
            var light = VisualTreeHelper.GetChild(halo, 0);
            Assert.NotSame(child, light);
            Assert.Same(child, VisualTreeHelper.GetChild(halo, 1));
            Assert.False(((UIElement)light).IsHitTestVisible);
            Assert.Null(System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement((UIElement)light));
            Assert.Null(System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(halo));
            // The light is as large as the halo, so it follows the child.
            Assert.Equal(new Size(80, 40), ((FrameworkElement)light).RenderSize);

            // The pointer, in a window, since outside one it finds nothing
            // at all. The light is drawn above the child: a plain test of
            // the drawing finds it there. The pointer does not, since the
            // light takes no click, and on the child it finds the child.
            ground.HorizontalAlignment = HorizontalAlignment.Left;
            ground.VerticalAlignment = VerticalAlignment.Top;
            var window = Host(ground);
            try
            {
                window.Show();
                Pump(window.Dispatcher, () => ground.IsLoaded && ground.IsVisible && child.IsVisible, "the load");
                Assert.Same(light, VisualTreeHelper.HitTest(ground, new Point(100, 30))?.VisualHit);
                Assert.Same(light, VisualTreeHelper.HitTest(ground, new Point(52, 34))?.VisualHit);
                Assert.Null(ground.InputHitTest(new Point(100, 30)));
                Assert.Null(ground.InputHitTest(new Point(52, 34)));
                Assert.Same(child, ground.InputHitTest(new Point(100, 60)));
            }
            finally
            {
                window.Close();
            }

            var alone = new Halo { Width = 60, Height = 24, Radius = 14, Strength = 0.5, CornerRadius = 12, IsLit = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var room = new Grid { Width = W, Height = H };
            room.Children.Add(alone);
            var lit = Alphas(room);
            Assert.Equal(1, VisualTreeHelper.GetChildrenCount(alone));
            // The box is x 70 to 130, y 48 to 72: lit above it, clear inside.
            Assert.True(lit[46 * W + 100] > 60);
            Assert.Equal((byte)0, lit[60 * W + 100]);
            Assert.Equal((byte)0, lit[30 * W + 100]);
        });

        private static void Pump(Dispatcher dispatcher, Func<bool> until, string what)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!until())
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), what + " did not happen");
                dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(15);
            }
        }

        private static Window Host(UIElement content) => new Window
        {
            Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false,
            Left = -10000, Top = -10000, Content = content,
        };

        /// <summary>In a window, with Windows' animations on, the light
        /// fades on and off, and a breathing halo then rises and falls for
        /// as long as it is lit. One that is lit as its page comes back is
        /// there at once. Out of the window it holds still where its state
        /// says, with no clock left running.</summary>
        [Fact]
        public void InAWindowTheLightFadesAndBreathes_AndOutOfOneItHoldsStill() => RunSta(() =>
        {
            var motion = Halo.MotionEnabled;
            Halo.MotionEnabled = () => true;
            var plain = new Halo { Width = 40, Height = 20 };
            var ring = new Halo { Width = 40, Height = 20, Breathes = true };
            var panel = new StackPanel();
            panel.Children.Add(plain);
            panel.Children.Add(ring);
            var window = Host(panel);
            try
            {
                window.Show();
                var dispatcher = window.Dispatcher;
                Pump(dispatcher, () => plain.IsLoaded && ring.IsLoaded, "the load");
                Assert.False(plain.IsMoving);
                Assert.Equal(0.0, plain.Level);

                // On: a fade, then still at full.
                plain.IsLit = true;
                Assert.True(plain.IsMoving);
                Assert.True(plain.Level < 1.0);
                Pump(dispatcher, () => !plain.IsMoving, "the fade's end");
                Assert.Equal(1.0, plain.Level);
                Assert.False(plain.IsBreathing);

                // A breathing halo: the fade, then the breath, which stays
                // between its low and full.
                ring.IsLit = true;
                Assert.True(ring.IsMoving);
                Assert.False(ring.IsBreathing);
                Pump(dispatcher, () => ring.IsBreathing, "the breath");
                Assert.True(ring.IsMoving);
                Assert.InRange(ring.Level, Halo.BreathLow - 0.001, 1.0);

                // Off: both fade to nothing and stop.
                plain.IsLit = false;
                ring.IsLit = false;
                Assert.False(ring.IsBreathing);
                Pump(dispatcher, () => !plain.IsMoving && !ring.IsMoving, "the fade out");
                Assert.Equal(0.0, plain.Level);
                Assert.Equal(0.0, ring.Level);

                // Out of the window while it breathes: it holds at full.
                ring.IsLit = true;
                Pump(dispatcher, () => ring.IsBreathing, "the second breath");
                panel.Children.Remove(ring);
                Pump(dispatcher, () => !ring.IsLoaded && !ring.IsMoving, "the hold");
                Assert.False(ring.IsBreathing);
                Assert.Equal(1.0, ring.Level);
                // Lit while away, then back: there at once, and breathing,
                // with no fade up from nothing.
                double levelAtLoad = -1;
                bool breathingAtLoad = false;
                void Note(object sender, RoutedEventArgs e)
                {
                    levelAtLoad = ring.Level;
                    breathingAtLoad = ring.IsBreathing;
                }
                ring.Loaded += Note;
                panel.Children.Add(ring);
                Pump(dispatcher, () => levelAtLoad >= 0, "the load on the way back");
                ring.Loaded -= Note;
                Assert.Equal(1.0, levelAtLoad);
                Assert.True(breathingAtLoad);
                Assert.InRange(ring.Level, Halo.BreathLow - 0.001, 1.0);
                // No longer told to breathe: it settles at full and stops.
                ring.Breathes = false;
                Pump(dispatcher, () => !ring.IsMoving, "the settle");
                Assert.Equal(1.0, ring.Level);

                // With Windows' animations off: at once, and still.
                Halo.MotionEnabled = () => false;
                ring.Breathes = true;
                Assert.False(ring.IsMoving);
                ring.IsLit = false;
                Assert.False(ring.IsMoving);
                Assert.Equal(0.0, ring.Level);
                ring.IsLit = true;
                Assert.False(ring.IsMoving);
                Assert.False(ring.IsBreathing);
                Assert.Equal(1.0, ring.Level);
            }
            finally
            {
                Halo.MotionEnabled = motion;
                window.Close();
            }
        });

        /// <summary>The seam the motion setting is read through is Windows'
        /// own, and the halo's times are the design's: a fifth of a second
        /// to come on, 2.4 seconds for a breath's fall to 62 percent.</summary>
        [Fact]
        public void TheHalosTimesAreTheDesigns_AndItsMotionIsWindowsSetting()
        {
            Assert.Equal(TimeSpan.FromMilliseconds(200), Halo.FadeTime);
            Assert.Equal(TimeSpan.FromSeconds(2.4), Halo.BreathTime);
            Assert.Equal(0.62, Halo.BreathLow);
            string code = ExtrasPageTests.RepoText("PadForge.App", "Controls", "Halo.cs");
            At(code, "internal static Func<bool> MotionEnabled = () => SystemParameters.ClientAreaAnimation;");
            // Both the fade and the breath move the light's own opacity,
            // which Windows plays without asking for the picture again.
            Assert.Equal(3, Count(code, "_light.BeginAnimation(OpacityProperty, "));
            Assert.DoesNotContain("AffectsRender", code);
        }

        /// <summary>A sidebar pill's outline says its state when it is not
        /// the picked one: soft ember while forging, gold while waiting, the
        /// pill's own hairline while cold. A forging pill rests on a faint
        /// ember wash and stands in a light, which is stronger around the
        /// picked pill. The light is the first of the pill's children, so
        /// the row is drawn over it.</summary>
        [Fact]
        public void ASidebarPillsOutlineAndLightSayItsState()
        {
            Assert.Equal("EmberRingSoftBrush", MainWindow.PillOutlineKey(lit: true, cooling: false));
            Assert.Equal("EmberRingSoftBrush", MainWindow.PillOutlineKey(lit: true, cooling: true));
            Assert.Equal("WaitRingBrush", MainWindow.PillOutlineKey(lit: false, cooling: true));
            Assert.Equal("SteelPillLineBrush", MainWindow.PillOutlineKey(lit: false, cooling: false));
            Assert.Equal(19.0, MainWindow.PillCorner);

            string code = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            At(code, "card.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty,\r\n                    PillOutlineKey(lit, cooling));");
            At(code, "if (lit) card.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, \"EmberWashBrush\");");
            int light = At(code, "surface.Children.Insert(0, new PadForge.Controls.Halo");
            string halo = code.Substring(light, code.IndexOf("});", light, StringComparison.Ordinal) - light);
            At(halo, "IsHitTestVisible = false,");
            At(halo, "CornerRadius = PillCorner,");
            // The light reaches the pane's inset and no farther: a pill's
            // two ends are 12 from the pane's edges, and a longer light was
            // cut there (the owner, 2026-11-13). It is a little stronger
            // than the board's shadows of 18 at 0.28 and 14 at 0.10 are as
            // halos, since it is shorter. The picked pill has its hard ring.
            At(halo, "Radius = PillLightReach,");
            Assert.Equal(12.0, MainWindow.PillLightReach);
            At(halo, "Strength = picked ? 0.18 : 0.07,");
            At(halo, "Rim = picked ? 0.35 : 0,");
            At(halo, "IsLit = lit,");
            // The pill's own corner and height.
            At(code, "CornerRadius = new CornerRadius(PillCorner),\r\n                MinHeight = 38,\r\n                Padding = new Thickness(4, 0, 12, 0),");
        }

        /// <summary>The shell's steps and tints (#574): the window's ground
        /// is the darkest step, the sidebar one up, the page another. The
        /// title bar is warm while the engine runs. A forging pill has a
        /// soft ember outline and wash, and a waiting one a gold outline. A
        /// card's edge is lit at its top. Each has its dark value as the
        /// app's default and a value of its own on the light theme.</summary>
        [Fact]
        public void TheShellsStepsAndTints_AreInTheThemeForBothGrounds()
        {
            string theme = ExtrasPageTests.RepoText("PadForge.App", "Common", "EmberTheme.cs");
            string app = ExtrasPageTests.RepoText("PadForge.App", "App.xaml");
            At(theme, "SetBrush(\"SteelWindowBrush\", dark ? Color.FromRgb(0x07, 0x0A, 0x10) : Color.FromRgb(0xE9, 0xEB, 0xF0));");
            At(app, "<SolidColorBrush x:Key=\"SteelWindowBrush\" Color=\"#070A10\"/>");
            At(theme, "SetBrush(\"SteelPaneBrush\", dark ? Color.FromRgb(0x0A, 0x0E, 0x16) : Color.FromRgb(0xF3, 0xF4, 0xF7));");
            At(app, "<SolidColorBrush x:Key=\"SteelPaneBrush\" Color=\"#0A0E16\"/>");
            At(theme, "SetBrush(\"SteelPaneLineBrush\", dark ? Color.FromRgb(0x16, 0x1E, 0x2B) : Color.FromRgb(0xD6, 0xD8, 0xDE));");
            At(app, "<SolidColorBrush x:Key=\"SteelPaneLineBrush\" Color=\"#161E2B\"/>");
            At(theme, "SetBrush(\"EmberRingSoftBrush\", dark ? Color.FromArgb(0x73, 0xFF, 0x6B, 0x2C) : Color.FromArgb(0x99, 0xC2, 0x4A, 0x12));");
            At(app, "<SolidColorBrush x:Key=\"EmberRingSoftBrush\" Color=\"#73FF6B2C\"/>");
            At(theme, "SetBrush(\"EmberWashBrush\", dark ? Color.FromArgb(0x0D, 0xFF, 0x6B, 0x2C) : Color.FromArgb(0x14, 0xFF, 0x6B, 0x2C));");
            At(app, "<SolidColorBrush x:Key=\"EmberWashBrush\" Color=\"#0DFF6B2C\"/>");
            At(theme, "SetBrush(\"WaitRingBrush\", dark ? Color.FromArgb(0x66, 0xF2, 0xB8, 0x4B) : Color.FromArgb(0x99, 0xB0, 0x7C, 0x10));");
            At(app, "<SolidColorBrush x:Key=\"WaitRingBrush\" Color=\"#66F2B84B\"/>");

            // The title bar's warmth: one brush, fainter on a light ground,
            // with the app's default the dark one.
            At(theme, "SetTitleWarmth(dark ? (byte)0x66 : (byte)0x38);");
            At(theme, "Application.Current.Resources[\"TitleWarmthBrush\"] = warmth;");
            At(app, "<RadialGradientBrush x:Key=\"TitleWarmthBrush\" Center=\"0.8,-1.5\" GradientOrigin=\"0.8,-1.5\" RadiusX=\"0.6\" RadiusY=\"3.2\">");
            Assert.Contains("<GradientStop Color=\"#66FF6B2C\" Offset=\"0\"/>", app);
            Assert.Contains("Center = new Point(0.8, -1.5),", theme);
            Assert.Contains("RadiusX = 0.6,", theme);
            Assert.Contains("RadiusY = 3.2,", theme);

            // The page's ground, a step over the sidebar's.
            foreach (string key in new[] { "ApplicationBackgroundBrush", "SolidBackgroundFillColorBaseBrush", "NavigationViewContentBackground" })
                At(theme, "SetBrush(\"" + key + "\", Color.FromRgb(0x0B, 0x10, 0x18));");
            At(theme, "SetBrush(\"NavigationViewContentGridBorderBrush\", Color.FromRgb(0x16, 0x1E, 0x2B));");

            // A card's edge: lit over its top three pixels.
            Assert.Equal(Color.FromRgb(0x2C, 0x37, 0x4A), EmberTheme.CardEdgeTop);
            Assert.Equal(Color.FromRgb(0x1E, 0x27, 0x34), EmberTheme.CardEdge);
            At(theme, "MappingMode = BrushMappingMode.Absolute,\r\n                StartPoint = new Point(0, 0),\r\n                EndPoint = new Point(0, 3),");
            At(theme, "Application.Current.Resources[\"CardStrokeColorDefaultBrush\"] = edge;");
            Assert.Equal(1, Count(theme, "                SetCardEdge();"));

            // The window: the title bar stands on the window's ground with
            // the warmth over it, which shows only while the engine runs.
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml");
            At(window, "<Border x:Name=\"TitleWarmth\"");
            Assert.Contains("Background=\"{DynamicResource TitleWarmthBrush}\"", window);
        }

        /// <summary>A text of one value comes apart around the value, so
        /// the value can be drawn larger than its unit, in the order the
        /// language's own format puts them.</summary>
        [Fact]
        public void ATextOfOneValueComesApartAroundTheValue()
        {
            string thousand = 1000.4.ToString("F0", System.Globalization.CultureInfo.CurrentCulture);
            Assert.Equal((string.Empty, thousand, " Hz"), FormatRuns.SplitValue("{0:F0} Hz", 1000.4));
            Assert.Equal(("Hz ", thousand, string.Empty), FormatRuns.SplitValue("Hz {0:F0}", 1000.4));
            Assert.Equal(("about ", "7", " in all"), FormatRuns.SplitValue("about {0} in all", 7));
            Assert.Equal(("no place", string.Empty, string.Empty), FormatRuns.SplitValue("no place", 7));
            // The parts put back together are the text string.Format writes.
            foreach (string format in new[] { "{0:F0} Hz", "Hz {0:F0}", "{0}", "a {0:F2} b" })
            {
                var (before, value, after) = FormatRuns.SplitValue(format, 12.345);
                Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, format, 12.345), before + value + after);
            }
        }
    }
}
