using System;
using System.Diagnostics;
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
using PadForge.Models3D;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The stills a Home card shows are kept for the whole process
    /// (StagePictures), and the tests that make them, forget them or put a
    /// drawer of their own in their place run one at a time. Run beside each
    /// other, one test forgot the still another had just made, and that test
    /// found no marks for it.</summary>
    [CollectionDefinition("StagePictureStatics", DisableParallelization = true)]
    public class StagePictureStaticsCollection { }

    /// <summary>A Home card's controller moves (owner, 2026-11-16: "I
    /// expected the controller to actually, you know, show its preview, not
    /// some static image"): the model its still was drawn from, frozen and
    /// shared, shown through the still's own piece of the drawing so it stands
    /// where the still stands, with its sticks and triggers turned and what is
    /// held lit as the controller's page turns and lights them.</summary>
    [Collection("StagePictureStatics")]
    public class HomeLiveModelTests
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

        /// <summary>The pixels that differ between two drawings.</summary>
        private static System.Collections.Generic.IEnumerable<(int X, int Y)> Changed(byte[] before, byte[] after)
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

        [Fact]
        public void TheModelStandsWhereTheStillStands_AndAStickMovesOnlyAroundItsCap() => RunSta(() =>
        {
            Assert.True(LiveStage.DrawsIn3D, "this computer draws 3D in software, where a card keeps its still");
            StagePictures.ForgetStills();
            try
            {
                StagePictures.MakeStill("XboxSeries", "Carbon");
                Assert.True(StagePictures.TryGetStill("XboxSeries", "Carbon", out var color, out _));
                var live = StagePictures.LiveModelFor("XboxSeries", "Carbon");
                Assert.NotNull(live);
                Assert.True(live.Parts.IsFrozen);
                Assert.True(live.Cut.Width > 0 && live.Cut.Height > 0);

                // A card's own copy costs the interface's thread milliseconds.
                live.NewInstance();
                var clock = Stopwatch.StartNew();
                for (int i = 0; i < 10; i++) live.NewInstance();
                clock.Stop();
                Assert.True(clock.Elapsed.TotalMilliseconds / 10 < 20, "a card's copy took " + clock.Elapsed.TotalMilliseconds / 10 + " ms");

                byte[] still = Draw(new Image { Source = color, Stretch = Stretch.Uniform });
                var stage = new LiveStage { Model = live, Picture = color };
                byte[] rest = Draw(stage);

                // At rest the model covers what the still covers, to a pixel.
                var stillBox = StagePictures.Drawn(still, W, H);
                var restBox = StagePictures.Drawn(rest, W, H);
                Assert.False(restBox.IsEmpty, "the stage drew nothing");
                Assert.True(Math.Abs(stillBox.X - restBox.X) <= 1 && Math.Abs(stillBox.Y - restBox.Y) <= 1
                    && Math.Abs(stillBox.Width - restBox.Width) <= 2 && Math.Abs(stillBox.Height - restBox.Height) <= 2,
                    "the still is drawn at " + stillBox + " and the model at " + restBox);

                // The left stick hard right: pixels change around the stick,
                // in the left half of the controller, and on no other
                // control. A mark covers a control's top, and the tilt also
                // swings the stick's skirt below its cap, so the stick's own
                // mark is no bound for what it moves.
                stage.Pose = new StagePose(short.MaxValue, 0, 0, 0, 0, 0);
                byte[] moved = Draw(stage);
                var marks = StagePictures.StillMarksFor("XboxSeries", "Carbon", "xbox-series-xs");
                Rect box = HeldGlow.PictureBox(new Size(W, H), color.Width, color.Height);
                Rect Px(Rect m) => new(box.X + m.X * box.Width, box.Y + m.Y * box.Height, m.Width * box.Width, m.Height * box.Height);
                var stick = Px(marks.First(m => m.Role == StillMarks.LeftStick).Box);
                var others = marks.Where(m => m.Role is not (StillMarks.LeftStick or "LeftThumbButton"))
                    .Select(m => (m.Role, Box: Px(m.Box))).ToList();
                int near = 0;
                foreach (var (x, y) in Changed(rest, moved))
                {
                    Assert.True(x < box.X + box.Width / 2, $"the left stick moved a pixel at {x},{y} in the right half");
                    foreach (var (role, other) in others)
                        Assert.False(other.Contains(x + 0.5, y + 0.5), $"the left stick moved a pixel at {x},{y} on {role}");
                    if (stick.Contains(x + 0.5, y + 0.5)) near++;
                }
                Assert.True(near > 20, near + " pixels changed on the left stick's cap");

                // A trigger turns too.
                stage.Pose = new StagePose(0, 0, 0, 0, 1, 0);
                byte[] pulled = Draw(stage);
                var trigger = Px(marks.First(m => m.Role == StillMarks.LeftTrigger).Box);
                trigger.Inflate(trigger.Width * 0.25, trigger.Height * 0.25);
                int onTrigger = 0;
                foreach (var (x, y) in Changed(rest, pulled))
                {
                    Assert.True(x < box.X + box.Width / 2, $"the left trigger moved a pixel at {x},{y} in the right half");
                    if (trigger.Contains(x + 0.5, y + 0.5)) onTrigger++;
                }
                Assert.True(onTrigger > 10, onTrigger + " pixels changed on the left trigger");

                // Back at rest, the picture is the rest picture again.
                stage.Pose = StagePose.Rest;
                Assert.Equal(rest, Draw(stage));
            }
            finally
            {
                StagePictures.ForgetStills();
            }
        });

        /// <summary>The bit a pose holds a button's role in.</summary>
        private static ulong Bit(string role)
        {
            int at = ControllerModelView.ButtonRoles.ToList().IndexOf(role);
            Assert.True(at >= 0, role + " is no button the page lights");
            return 1UL << at;
        }

        /// <summary>A held button glows on its own control and nowhere else,
        /// the Guide button's emblem and the Share button among them, and a
        /// finger on a stick's top glows that stick: the card lights what the
        /// controller's page lights, through the page's own glow
        /// (ModelGlow).</summary>
        [Fact]
        public void AHeldButtonGlowsOnItsOwnControl_AndATouchedStickGlows() => RunSta(() =>
        {
            Assert.True(ControllerModelView.ButtonRoles.Count <= 64, "a pose holds one bit for each button");
            string view = ExtrasPageTests.RepoText("PadForge.App", "Views", "ControllerModelView.xaml.cs");
            foreach (var call in new[] { "ModelGlow.SetGroupHighlight(", "ModelGlow.GradientHighlight(",
                "ModelGlow.StickGlowsFrom", "ModelGlow.TriggerGlowsFrom" })
                Assert.Contains(call, view);

            StagePictures.ForgetStills();
            try
            {
                StagePictures.MakeStill("XboxSeries", "Carbon");
                Assert.True(StagePictures.TryGetStill("XboxSeries", "Carbon", out var color, out _));
                var live = StagePictures.LiveModelFor("XboxSeries", "Carbon");
                var stage = new LiveStage { Model = live, Picture = color };
                byte[] rest = Draw(stage);
                var marks = StagePictures.StillMarksFor("XboxSeries", "Carbon", "xbox-series-xs");
                Rect box = HeldGlow.PictureBox(new Size(W, H), color.Width, color.Height);
                Rect Px(Rect m) => new(box.X + m.X * box.Width, box.Y + m.Y * box.Height, m.Width * box.Width, m.Height * box.Height);

                foreach (var role in new[] { "ButtonA", "ButtonGuide", "ButtonShare", "ButtonStart" })
                {
                    stage.Pose = new StagePose(0, 0, 0, 0, 0, 0, Bit(role));
                    byte[] lit = Draw(stage);
                    var own = Px(marks.First(m => m.Role == role).Box);
                    var others = marks.Where(m => m.Role != role).Select(m => (m.Role, Box: Px(m.Box))).ToList();
                    int on = 0;
                    foreach (var (x, y) in Changed(rest, lit))
                    {
                        if (own.Contains(x + 0.5, y + 0.5)) { on++; continue; }
                        foreach (var (other, at) in others)
                            Assert.False(at.Contains(x + 0.5, y + 0.5), $"holding {role} lit a pixel at {x},{y} on {other}");
                    }
                    Assert.True(on > 10, $"holding {role} lit {on} pixels on its control");
                }

                stage.Pose = new StagePose(0, 0, 0, 0, 0, 0, 0, LeftTouch: true);
                byte[] touched = Draw(stage);
                var stick = Px(marks.First(m => m.Role == StillMarks.LeftStick).Box);
                int onStick = 0;
                foreach (var (x, y) in Changed(rest, touched))
                {
                    Assert.True(x < box.X + box.Width / 2, $"a finger on the left stick lit a pixel at {x},{y} in the right half");
                    if (stick.Contains(x + 0.5, y + 0.5)) onStick++;
                }
                Assert.True(onStick > 10, onStick + " pixels lit on the touched stick's cap");

                stage.Pose = StagePose.Rest;
                Assert.Equal(rest, Draw(stage));
            }
            finally
            {
                StagePictures.ForgetStills();
            }
        });

        /// <summary>A card that shows its model moving reads what is held
        /// into the model's pose, in steps, and leaves out a button its
        /// preset lacks as the controller's page does (the Share button on an
        /// Xbox One preset). Its ember marks are for a picture that does not
        /// move, so it leaves their levels at rest.</summary>
        [Fact]
        public void ACardReadsItsModelsPose_LessTheControlsItsPresetLacks() => RunSta(() =>
        {
            StagePictures.ForgetStills();
            try
            {
                StagePictures.MakeStill("XboxSeries", "Carbon");
                Assert.True(StagePictures.TryGetStill("XboxSeries", "Carbon", out var color, out var gray));
                var live = StagePictures.LiveModelFor("XboxSeries", "Carbon");
                Assert.Equal(Bit("ButtonShare"), live.ButtonsFor("xbox-series-xs") & Bit("ButtonShare"));
                Assert.Equal(0UL, live.ButtonsFor("xbox-one-s") & Bit("ButtonShare"));
                Assert.Equal(Bit("ButtonA"), live.ButtonsFor("xbox-one-s") & Bit("ButtonA"));

                var pad = new PadViewModel(0)
                {
                    ButtonA = true, ButtonShare = true, RawThumbLX = 1000, RawThumbRY = -1000,
                    LeftTrigger = 0.5, LeftStickTouch = true,
                };
                var has = new SlotSummary(0);
                has.SetPicture("xbox-series-xs", VirtualControllerType.Xbox, "", true, color, gray, false,
                    StagePictures.StillMarksFor("XboxSeries", "Carbon", "xbox-series-xs"), live);
                Assert.True(has.ShowsLiveModel);
                has.ReadHeld(pad);
                Assert.Equal(new StagePose(768, 0, 0, -768, 0.5, 0, Bit("ButtonA") | Bit("ButtonShare"), LeftTouch: true),
                    has.StagePose);
                Assert.All(has.HeldLevels, level => Assert.Equal((byte)0, level));

                var lacks = new SlotSummary(1);
                lacks.SetPicture("xbox-one-s", VirtualControllerType.Xbox, "", true, color, gray, false,
                    StagePictures.StillMarksFor("XboxSeries", "Carbon", "xbox-one-s"), live);
                lacks.ReadHeld(pad);
                Assert.Equal(Bit("ButtonA"), lacks.StagePose.Held);

                // A step too small to see changes nothing.
                var before = has.StagePose;
                pad.RawThumbLX = 1020;
                has.ReadHeld(pad);
                Assert.Equal(before, has.StagePose);
            }
            finally
            {
                StagePictures.ForgetStills();
            }
        });
    }
}
