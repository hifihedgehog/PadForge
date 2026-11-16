using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PadForge.Common;
using PadForge.Controls;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Models2D;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A Home card's controller moves in 2D too, for a page that
    /// shows the 2D view: the card's picture is the 2D view at rest, made of
    /// the view's own layers, and while the controller forges the card draws
    /// those layers as the view moves them.</summary>
    public class HomeLiveArtTests
    {
        private static void RunSta(Action body)
        {
            ExceptionDispatchInfo failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            failure?.Throw();
        }

        /// <summary>A stage's picture box at twice its size on screen.</summary>
        private const int W = StagePictures.BoxWidth, H = StagePictures.BoxHeight;

        private static byte[] Draw(UIElement element)
        {
            var host = new Grid { Width = W, Height = H };
            host.Children.Add(element);
            host.Measure(new Size(W, H));
            host.Arrange(new Rect(0, 0, W, H));
            host.UpdateLayout();
            var bitmap = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            host.Children.Clear();
            var pixels = new byte[W * H * 4];
            bitmap.CopyPixels(pixels, W * 4, 0);
            return pixels;
        }

        private static IEnumerable<(int X, int Y)> Changed(byte[] before, byte[] after)
        {
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    int d = Math.Abs(after[i] - before[i]) + Math.Abs(after[i + 1] - before[i + 1])
                        + Math.Abs(after[i + 2] - before[i + 2]) + Math.Abs(after[i + 3] - before[i + 3]);
                    if (d > 24) yield return (x, y);
                }
        }

        private static ulong Bit(string role) => 1UL << ControllerModelView.ButtonRoles.ToList().IndexOf(role);

        private static readonly string[] Folders = { "DS3", "DS4", "DualSense", "DUALSENSEEDGE", "XBOXONE", "XBOXSERIES",
            "SWITCHPRO", "SWITCH2PRO", "STEAMDECK", "STEAMCONTROLLER", "STEAMCONTROLLER2", "XBOX360" };

        /// <summary>The picture is the 2D view at rest: the triggers' art
        /// behind the body and the sticks' art over their wells, where the
        /// bare body has neither. Every art PadForge ships is made into
        /// layers, with its sticks and its buttons, and the stage at rest
        /// stands where the picture stands.</summary>
        [Fact]
        public void ThePictureIsThe2DViewAtRest_AndTheStageStandsOnIt() => RunSta(() =>
        {
            foreach (string folder in Folders)
            {
                string first = StagePictures.FirstPath(folder);
                var made = StagePictures.LiveArtFor(first);
                Assert.True(made != null, folder + " made no layers");
                Assert.Same(made.Still, StagePictures.Color(first));
                Assert.Contains(made.Layers, l => l.Kind == LiveArt.Kind.Stick);
                Assert.True((made.Buttons & Bit("ButtonA")) != 0, folder + " shows no A button held");
                Assert.True(made.StickTravel > 0, folder);
            }

            string path = StagePictures.PathFor("xbox-series-xs", VirtualControllerType.Xbox, _ => null);
            var art = StagePictures.LiveArtFor(path);
            Assert.Same(art, StagePictures.LiveArtFor(path));
            foreach (var kind in new[] { LiveArt.Kind.Stick, LiveArt.Kind.Trigger, LiveArt.Kind.StickClick })
                Assert.Equal(2, art.Layers.Count(l => l.Kind == kind));
            Assert.Contains(art.Layers, l => l.Kind == LiveArt.Kind.Stick && l.Left);
            Assert.Contains(art.Layers, l => l.Kind == LiveArt.Kind.Trigger && !l.Left);

            // The triggers' art stands above the body, so the picture starts
            // higher than the bare body does.
            BitmapSource body = new FormatConvertedBitmap(EmbeddedBitmaps.Load(path), PixelFormats.Pbgra32, null, 0);
            var bodyPixels = new byte[body.PixelWidth * 4 * body.PixelHeight];
            body.CopyPixels(bodyPixels, body.PixelWidth * 4, 0);
            var bare = StagePictures.Drawn(bodyPixels, body.PixelWidth, body.PixelHeight);
            Assert.True(art.Cut.Y < bare.Y, "the picture starts at " + art.Cut.Y + " and the bare body at " + bare.Y);

            byte[] still = Draw(new Image { Source = art.Still, Stretch = Stretch.Uniform });
            byte[] rest = Draw(new LiveArtStage { Art = art, Picture = art.Still });
            var stillBox = StagePictures.Drawn(still, W, H);
            var restBox = StagePictures.Drawn(rest, W, H);
            Assert.False(restBox.IsEmpty, "the stage drew nothing");
            Assert.True(Math.Abs(stillBox.X - restBox.X) <= 2 && Math.Abs(stillBox.Y - restBox.Y) <= 2
                && Math.Abs(stillBox.Width - restBox.Width) <= 2 && Math.Abs(stillBox.Height - restBox.Height) <= 2,
                "the picture is drawn at " + stillBox + " and the stage at " + restBox);
        });

        /// <summary>A tilted stick shifts its art around its own place, a
        /// pulled trigger fills its own, a held button shows its own pressed
        /// art and a finger on a stick's top shows the click art faintly,
        /// as the controller page's 2D view does. Back at rest the stage is
        /// as it was.</summary>
        [Fact]
        public void AStickShifts_ATriggerFills_AndAHeldButtonShows_EachOnItsOwnControl() => RunSta(() =>
        {
            string path = StagePictures.PathFor("xbox-series-xs", VirtualControllerType.Xbox, _ => null);
            var art = StagePictures.LiveArtFor(path);
            var stage = new LiveArtStage { Art = art, Picture = art.Still };
            byte[] rest = Draw(stage);
            var marks = StagePictures.ArtMarks(path);
            Rect box = HeldGlow.PictureBox(new Size(W, H), art.Still.Width, art.Still.Height);
            Rect Px(Rect m, double grow)
            {
                var r = new Rect(box.X + m.X * box.Width, box.Y + m.Y * box.Height, m.Width * box.Width, m.Height * box.Height);
                r.Inflate(grow, grow);
                return r;
            }
            double travel = art.StickTravel * box.Width / art.Width;

            void OnlyOn(StagePose pose, string role, double grow, string what)
            {
                stage.Pose = pose;
                byte[] moved = Draw(stage);
                var own = Px(marks.First(m => m.Role == role).Box, grow);
                int on = 0;
                foreach (var (x, y) in Changed(rest, moved))
                {
                    Assert.True(own.Contains(x + 0.5, y + 0.5), $"{what} changed a pixel at {x},{y}, off {role} at {own}");
                    on++;
                }
                Assert.True(on > 10, $"{what} changed {on} pixels");
            }
            OnlyOn(new StagePose(short.MaxValue, 0, 0, 0, 0, 0), StillMarks.LeftStick, travel + 2, "the left stick hard right");
            OnlyOn(new StagePose(0, 0, 0, short.MinValue, 0, 0), StillMarks.RightStick, travel + 2, "the right stick hard down");
            OnlyOn(new StagePose(0, 0, 0, 0, 1, 0), StillMarks.LeftTrigger, 2, "a full left pull");
            OnlyOn(new StagePose(0, 0, 0, 0, 0, 0, Bit("ButtonA")), "ButtonA", 2, "A held");
            OnlyOn(new StagePose(0, 0, 0, 0, 0, 0, Bit("ButtonShare")), "ButtonShare", 2, "Share held");
            OnlyOn(new StagePose(0, 0, 0, 0, 0, 0, Bit("LeftThumbButton")), "LeftThumbButton", 2, "the left stick clicked");
            OnlyOn(new StagePose(0, 0, 0, 0, 0, 0, 0, LeftTouch: true), "LeftThumbButton", 2, "a finger on the left stick");

            // A trigger fills from the bottom: half a pull fills the lower
            // half of its art.
            stage.Pose = new StagePose(0, 0, 0, 0, 0, 0.5);
            var right = Px(marks.First(m => m.Role == StillMarks.RightTrigger).Box, 0);
            int half = 0;
            foreach (var (x, y) in Changed(rest, Draw(stage)))
            {
                Assert.True(right.Contains(x + 0.5, y + 0.5), $"half a right pull changed a pixel at {x},{y}, off the trigger at {right}");
                Assert.True(y + 1 >= right.Y + right.Height / 2, $"half a right pull changed a pixel at {x},{y}, in the upper half of {right}");
                half++;
            }
            Assert.True(half > 5, $"half a right pull changed {half} pixels");

            // A touch shows the click art at the 2D view's touch strength
            // (ControllerModel2DView.StickTouchOpacity), where a click shows
            // it whole.
            long Total(byte[] after)
            {
                long sum = 0;
                for (int i = 0; i < after.Length; i++) sum += Math.Abs(after[i] - rest[i]);
                return sum;
            }
            stage.Pose = new StagePose(0, 0, 0, 0, 0, 0, Bit("LeftThumbButton"));
            long clicked = Total(Draw(stage));
            stage.Pose = new StagePose(0, 0, 0, 0, 0, 0, 0, LeftTouch: true);
            long touched = Total(Draw(stage));
            double strength = touched / (double)clicked;
            Assert.True(Math.Abs(strength - ControllerModel2DView.StickTouchOpacity) < 0.1,
                $"a touch shows the click art at {strength:0.00} of a click");

            stage.Pose = StagePose.Rest;
            Assert.Equal(rest, Draw(stage));
        });

        /// <summary>The 2D view shows held the buttons the 3D view lights: its
        /// own list (UpdateButtons) names each role of
        /// ControllerModelView.ButtonRoles once, so a card drawn in 2D reads
        /// the bits the 2D view would show.</summary>
        [Fact]
        public void The2DViewShowsTheButtonsThe3DViewLights()
        {
            string view = ExtrasPageTests.RepoText("PadForge.App", "Views", "ControllerModel2DView.xaml.cs");
            int at = view.IndexOf("private void UpdateButtons()", StringComparison.Ordinal);
            Assert.True(at >= 0, "UpdateButtons is gone");
            string body = view.Substring(at, view.IndexOf("\n        }", at, StringComparison.Ordinal) - at);
            var named = System.Text.RegularExpressions.Regex.Matches(body, @"Set(?:Overlay|StickCap)Visible\(""(\w+)""")
                .Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(ControllerModelView.ButtonRoles.OrderBy(r => r, StringComparer.Ordinal), named.OrderBy(r => r, StringComparer.Ordinal));
        }

        /// <summary>A card given the 2D art shows it moving while the
        /// controller forges and reads what is held into its pose: the
        /// buttons the art shows, a finger on either stick's top, in steps.
        /// Its ember marks are for a picture that does not move.</summary>
        [Fact]
        public void ACardWithTheArtReadsItsPose() => RunSta(() =>
        {
            string path = StagePictures.PathFor("xbox-series-xs", VirtualControllerType.Xbox, _ => null);
            var art = StagePictures.LiveArtFor(path);
            var card = new SlotSummary(0);
            var raised = new List<string>();
            card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            card.SetPicture("xbox-series-xs", VirtualControllerType.Xbox, "", false,
                StagePictures.Color(path), StagePictures.Gray(path), false, StagePictures.ArtMarks(path), null, art);
            Assert.Same(art, card.LiveArt);
            Assert.True(card.ShowsLiveArt);
            Assert.False(card.ShowsLiveModel);
            Assert.True(card.PictureMoves);
            foreach (var name in new[] { nameof(SlotSummary.LiveArt), nameof(SlotSummary.ShowsLiveArt), nameof(SlotSummary.PictureMoves) })
                Assert.Contains(name, raised);

            var pad = new PadViewModel(0) { ButtonA = true, LeftThumbButton = true, RawThumbRX = -2000, RightTrigger = 0.25, RightStickTouch = true };
            card.ReadHeld(pad);
            Assert.Equal(new StagePose(0, 0, -1792, 0, 0, 0.25, (Bit("ButtonA") | Bit("LeftThumbButton")) & art.Buttons, RightTouch: true),
                card.StagePose);
            Assert.Equal(Bit("ButtonA") | Bit("LeftThumbButton"), card.StagePose.Held);
            Assert.All(card.HeldLevels, level => Assert.Equal((byte)0, level));

            // The art taken away: the card's picture no longer moves.
            raised.Clear();
            card.SetPicture(string.Empty, VirtualControllerType.Midi, string.Empty, false, null, null);
            Assert.False(card.PictureMoves);
            Assert.Contains(nameof(SlotSummary.PictureMoves), raised);
            Assert.Equal(StagePose.Rest, card.StagePose);
        });
    }
}
