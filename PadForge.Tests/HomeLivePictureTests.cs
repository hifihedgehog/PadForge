using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using PadForge.Common;
using PadForge.Controls;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// What the approved "keep the frame" canvas added to Home and Profiles,
    /// as arithmetic and as view-model rules. A Home card's picture is
    /// live: a held button lights on it, a trigger and a stick as far as
    /// they are held. The cards are detailed, compact, or whichever fits
    /// the window. A compact card names one device. A profile's card shows
    /// its controllers' pictures in one row, as many as fit.
    /// </summary>
    public class HomeLivePictureTests
    {
        private static void Sta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(20000), "the run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        // ── the live picture ───────────────────────────────────────────

        /// <summary>A picture lights the buttons the controller's page
        /// lights, by the same names, and the two triggers and the two
        /// sticks. A button is held or not. A trigger is as far lit as it
        /// is pulled, and a stick as far as it stands from its center.</summary>
        [Fact]
        public void APictureLightsWhatTheControllersPageLights() => Sta(() =>
        {
            foreach (string role in new[] { "ButtonA", "DPadUp", "LeftShoulder", "LeftThumbButton", "Paddle1", "ButtonGuide",
                         StillMarks.LeftTrigger, StillMarks.RightTrigger, StillMarks.LeftStick, StillMarks.RightStick })
                Assert.True(StillMarks.Reads(role), role);
            foreach (string role in new[] { "LeftThumbRing", "Touchpad", "", null, "buttona" })
                Assert.False(StillMarks.Reads(role), role ?? "null");
            Assert.Equal(("LeftTrigger", "RightTrigger", "LeftStick", "RightStick"),
                (StillMarks.LeftTrigger, StillMarks.RightTrigger, StillMarks.LeftStick, StillMarks.RightStick));

            var pad = new PadViewModel(0);
            Assert.All(new[] { "ButtonA", StillMarks.LeftTrigger, StillMarks.LeftStick, StillMarks.RightStick }, role => Assert.Equal(0, StillMarks.Level(pad, role)));
            pad.ButtonA = true;
            pad.LeftTrigger = 0.4;
            pad.RightTrigger = 1;
            pad.RawThumbLX = short.MaxValue;
            pad.RawThumbRX = 16384;
            pad.RawThumbRY = -16384;
            Assert.Equal(1, StillMarks.Level(pad, "ButtonA"));
            Assert.Equal(0, StillMarks.Level(pad, "ButtonB"));
            Assert.Equal(0.4, StillMarks.Level(pad, StillMarks.LeftTrigger));
            Assert.Equal(1, StillMarks.Level(pad, StillMarks.RightTrigger));
            Assert.Equal(1, StillMarks.Level(pad, StillMarks.LeftStick));
            Assert.Equal(Math.Sqrt(2) * 16384 / short.MaxValue, StillMarks.Level(pad, StillMarks.RightStick), 9);
            // No controller, and a role nothing reads: at rest.
            Assert.Equal(0, StillMarks.Level(null, "ButtonA"));
            Assert.Equal(0, StillMarks.Level(pad, "Touchpad"));

            // A stick in a corner is past the circle, and reads as fully out.
            Assert.Equal(1, StillMarks.Reach(short.MaxValue, short.MaxValue));
            Assert.Equal(1, StillMarks.Reach(short.MinValue, 0));
            Assert.Equal(0, StillMarks.Reach(0, 0));

            // A level in steps: a card is drawn again only when one changes.
            Assert.Equal(15, StillMarks.Steps);
            Assert.Equal(new byte[] { 0, 0, 1, 8, 15, 15, 0, 0 },
                new[] { 0, 0.03, 0.04, 0.5, 1, 7.5, -1, double.NaN }.Select(StillMarks.Step).ToArray());
        });

        /// <summary>A mark is told in the pixels of the picture a control
        /// was drawn in, and kept as parts of the piece that was cut out
        /// of it, so it stays on its control at any size.</summary>
        [Fact]
        public void AMarkIsKeptInPartsOfThePictureThatWasCutOut()
        {
            var marks = new[] { new StillMark("ButtonA", new Rect(300, 150, 40, 20)), new StillMark(StillMarks.LeftStick, new Rect(100, 100, 100, 50)) };
            var placed = StillMarks.Place(marks, new Int32Rect(100, 50, 400, 200));
            Assert.Equal(new[] { "ButtonA", StillMarks.LeftStick }, placed.Select(m => m.Role));
            Assert.Equal(new Rect(0.5, 0.5, 0.1, 0.1), placed[0].Box);
            Assert.Equal(new Rect(0, 0.25, 0.25, 0.25), placed[1].Box);
            // Nothing to place, or nothing cut out: no marks.
            Assert.Same(StillMarks.None, StillMarks.Place(null, new Int32Rect(0, 0, 10, 10)));
            Assert.Same(StillMarks.None, StillMarks.Place(Array.Empty<StillMark>(), new Int32Rect(0, 0, 10, 10)));
            Assert.Same(StillMarks.None, StillMarks.Place(marks, Int32Rect.Empty));
            Assert.Same(StillMarks.None, StillMarks.Place(marks, new Int32Rect(0, 0, 10, 0)));
            Assert.Empty(StillMarks.None);
        }

        /// <summary>The 2D art's marks come from the art's own table of
        /// controls: its buttons, its stick clicks and its triggers by their
        /// names, and each stick's ring as the stick. They are scaled to
        /// the picture's pixels.</summary>
        [Fact]
        public void The2DArtsMarksAreItsOwnControls() => Sta(() =>
        {
            Assert.Equal("XBOX360", StagePictures.FolderOf("2DModels/XBOX360/base.png"));
            Assert.Equal("DualSense", StagePictures.FolderOf("2dmodels/DualSense/DualSense.png"));
            Assert.Equal(string.Empty, StagePictures.FolderOf("2DModels/base.png"));
            Assert.Equal(string.Empty, StagePictures.FolderOf("Models/XBOX360/base.png"));
            Assert.Equal(string.Empty, StagePictures.FolderOf(null));
            // The art's own path names its folder.
            foreach (string folder in new[] { "DS3", "DS4", "DualSense", "DUALSENSEEDGE", "XBOXONE", "XBOXSERIES", "SWITCHPRO", "SWITCH2PRO",
                         "STEAMDECK", "STEAMCONTROLLER", "STEAMCONTROLLER2", "XBOX360" })
                Assert.Equal(folder, StagePictures.FolderOf(StagePictures.BasePath(folder)), ignoreCase: true);

            var (_, width, height, overlays) = StagePictures.LayoutOf("XBOX360");
            Assert.True(width > 0 && height > 0);
            var marks = StagePictures.LayoutMarks("XBOX360", width, height);
            Assert.NotEmpty(marks);
            var roles = marks.Select(m => m.Role).ToList();
            foreach (string role in new[] { "ButtonA", "ButtonB", "ButtonX", "ButtonY", "LeftShoulder", "RightShoulder", "DPadUp",
                         StillMarks.LeftTrigger, StillMarks.RightTrigger, StillMarks.LeftStick, StillMarks.RightStick })
                Assert.Contains(role, roles);
            Assert.All(roles, role => Assert.True(StillMarks.Reads(role), role));
            Assert.DoesNotContain("LeftThumbRing", roles);
            // At the art's own size a mark is its control's place.
            var a = overlays.First(o => o.TargetName == "ButtonA");
            Assert.Equal(new Rect(a.X, a.Y, a.Width, a.Height), marks.First(m => m.Role == "ButtonA").Box);
            // At half the size it is half as far in and half as large.
            var half = StagePictures.LayoutMarks("XBOX360", width / 2, height / 2);
            Assert.Equal(marks.Count, half.Count);
            double sx = (width / 2) / (double)width, sy = (height / 2) / (double)height;
            Assert.Equal(new Rect(a.X * sx, a.Y * sy, a.Width * sx, a.Height * sy), half.First(m => m.Role == "ButtonA").Box);
            // Every folder's art has controls to light.
            foreach (string folder in new[] { "DS3", "DS4", "DualSense", "DUALSENSEEDGE", "XBOXONE", "XBOXSERIES", "SWITCHPRO", "SWITCH2PRO",
                         "STEAMDECK", "STEAMCONTROLLER", "STEAMCONTROLLER2" })
                Assert.Contains(StagePictures.LayoutMarks(folder, 1000, 700), m => m.Role == "ButtonA");
        });

        /// <summary>The glow lies over the picture's own box in the card's
        /// stage: the box an image fit whole and centered takes. A glow is
        /// its control's box and a spill around it.</summary>
        [Fact]
        public void AGlowStandsOnItsControlAtAnySize() => Sta(() =>
        {
            // A wide picture in a tall room: full width, centered up and down.
            Assert.Equal(new Rect(0, 50, 200, 100), HeldGlow.PictureBox(new Size(200, 200), 400, 200));
            // A tall picture in a wide room: full height, centered across.
            Assert.Equal(new Rect(150, 0, 100, 200), HeldGlow.PictureBox(new Size(400, 200), 100, 200));
            // A picture smaller than the room is made larger to fit it.
            Assert.Equal(new Rect(0, 0, 300, 150), HeldGlow.PictureBox(new Size(300, 150), 30, 15));
            foreach (var (room, w, h) in new[] { (new Size(0, 100), 10.0, 10.0), (new Size(100, 100), 0.0, 10.0), (new Size(100, 100), 10.0, double.NaN) })
                Assert.True(HeldGlow.PictureBox(room, w, h).IsEmpty);

            var picture = new Rect(10, 20, 200, 100);
            double spill = 200 * HeldGlow.Spill;
            Assert.Equal(0.044, HeldGlow.Spill);
            Assert.Equal(new Rect(10 + 100 - spill, 20 + 50 - spill, 20 + 2 * spill, 10 + 2 * spill),
                HeldGlow.GlowBox(picture, new Rect(0.5, 0.5, 0.1, 0.1)));
            // A control with no size is the spill alone.
            var point = HeldGlow.GlowBox(picture, new Rect(0, 0, 0, 0));
            Assert.Equal((2 * spill, 2 * spill), (point.Width, point.Height));

            // It takes no click and no focus, and is drawn again when what it shows changes.
            var glow = new HeldGlow();
            Assert.False(glow.IsHitTestVisible);
            Assert.False(glow.Focusable);
            foreach (var property in new[] { HeldGlow.MarksProperty, HeldGlow.LevelsProperty, HeldGlow.PictureProperty })
                Assert.True(((FrameworkPropertyMetadata)property.GetMetadata(typeof(HeldGlow))).AffectsRender, property.Name);
        });

        /// <summary>A card holds how far each control of its picture is
        /// held, in the marks' order and in steps. It tells the card only
        /// when a step changed, with a new list, and a new picture starts
        /// at rest.</summary>
        [Fact]
        public void ACardTellsItsPictureOnlyWhenAStepChanges() => Sta(() =>
        {
            var card = new SlotSummary(0);
            Assert.Empty(card.PictureMarks);
            Assert.Empty(card.HeldLevels);
            var pad = new PadViewModel(0);
            // With no picture there is nothing to read.
            pad.ButtonA = true;
            card.ReadHeld(pad);
            Assert.Empty(card.HeldLevels);
            pad.ButtonA = false;

            var said = new List<string>();
            card.PropertyChanged += (_, e) => said.Add(e.PropertyName);
            var picture = Swatch();
            var marks = new[]
            {
                new StillMark("ButtonA", new Rect(0.6, 0.4, 0.05, 0.08)),
                new StillMark(StillMarks.LeftTrigger, new Rect(0.1, 0, 0.1, 0.1)),
                new StillMark(StillMarks.LeftStick, new Rect(0.2, 0.3, 0.1, 0.15)),
            };
            card.SetPicture("p", VirtualControllerType.Xbox, "", false, picture, picture, false, marks);
            Assert.Same(marks, card.PictureMarks);
            Assert.Equal(new byte[] { 0, 0, 0 }, card.HeldLevels);
            // The marks are told before the picture they are for.
            Assert.True(said.IndexOf(nameof(SlotSummary.PictureMarks)) < said.IndexOf(nameof(SlotSummary.Picture)));
            Assert.True(said.IndexOf(nameof(SlotSummary.HeldLevels)) < said.IndexOf(nameof(SlotSummary.Picture)));

            said.Clear();
            card.ReadHeld(pad);
            Assert.Empty(said);
            var rest = card.HeldLevels;

            pad.ButtonA = true;
            pad.LeftTrigger = 0.5;
            pad.RawThumbLY = short.MaxValue;
            card.ReadHeld(pad);
            Assert.Equal(new byte[] { 15, 8, 15 }, card.HeldLevels);
            Assert.NotSame(rest, card.HeldLevels);
            Assert.Equal(new byte[] { 0, 0, 0 }, rest);
            Assert.Equal(new[] { nameof(SlotSummary.HeldLevels) }, said);

            // A move inside one step says nothing.
            said.Clear();
            var held = card.HeldLevels;
            pad.LeftTrigger = 0.51;
            card.ReadHeld(pad);
            Assert.Empty(said);
            Assert.Same(held, card.HeldLevels);
            pad.LeftTrigger = 0.6;
            card.ReadHeld(pad);
            Assert.Equal(new byte[] { 15, 9, 15 }, card.HeldLevels);
            Assert.Single(said);

            // No controller: everything is let go.
            card.ReadHeld(null);
            Assert.Equal(new byte[] { 0, 0, 0 }, card.HeldLevels);

            // The same marks again keep the levels, and other marks start at rest.
            pad.ButtonA = true;
            card.ReadHeld(pad);
            var before = card.HeldLevels;
            card.SetPicture("p", VirtualControllerType.Xbox, "", false, picture, picture, false, marks);
            Assert.Same(before, card.HeldLevels);
            var fewer = new[] { new StillMark("ButtonB", new Rect(0.7, 0.3, 0.05, 0.08)) };
            card.SetPicture("p", VirtualControllerType.Xbox, "", false, picture, picture, false, fewer);
            Assert.Equal(new byte[] { 0 }, card.HeldLevels);
            // No picture: no marks, whatever is handed over with it.
            card.SetPicture("p", VirtualControllerType.Xbox, "", false, null, null, true, marks);
            Assert.Same(StillMarks.None, card.PictureMarks);
            Assert.Empty(card.HeldLevels);
            card.ReadHeld(pad);
            Assert.Empty(card.HeldLevels);
        });

        private static System.Windows.Media.ImageSource Swatch()
        {
            var pixels = new byte[4 * 4 * 4];
            var picture = System.Windows.Media.Imaging.BitmapSource.Create(4, 4, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, pixels, 16);
            picture.Freeze();
            return picture;
        }

        // ── the cards' size ────────────────────────────────────────────

        /// <summary>The cards are detailed, compact, or Auto: detailed
        /// while they fit the window and compact once they would run past
        /// it. A settings file that holds no choice, or one this build
        /// does not know, is Auto.</summary>
        [Fact]
        public void TheCardsAreDetailedCompactOrWhicheverFits()
        {
            Assert.Equal(new[] { 0, 1, 2 }, new[] { (int)HomeCardSize.Auto, (int)HomeCardSize.Detailed, (int)HomeCardSize.Compact });
            Assert.Equal(new[] { HomeCardSize.Auto, HomeCardSize.Detailed, HomeCardSize.Compact, HomeCardSize.Auto, HomeCardSize.Auto },
                new[] { 0, 1, 2, 3, -1 }.Select(HomeCardSizes.FromStored));

            // The cards start 120 down the page and are 500 tall.
            Assert.False(HomeCardSizes.RunPast(120, 500, 700));
            Assert.False(HomeCardSizes.RunPast(120, 500, 620));
            Assert.False(HomeCardSizes.RunPast(120, 500, 619.6));
            Assert.True(HomeCardSizes.RunPast(120, 500, 619));

            Assert.True(HomeCardSizes.IsCompact(HomeCardSize.Compact, false));
            Assert.True(HomeCardSizes.IsCompact(HomeCardSize.Compact, true));
            Assert.False(HomeCardSizes.IsCompact(HomeCardSize.Detailed, false));
            Assert.False(HomeCardSizes.IsCompact(HomeCardSize.Detailed, true));
            Assert.False(HomeCardSizes.IsCompact(HomeCardSize.Auto, false));
            Assert.True(HomeCardSizes.IsCompact(HomeCardSize.Auto, true));

            var home = new DashboardViewModel();
            Assert.Equal(HomeCardSize.Auto, home.HomeCards);
            Assert.False(home.IsCardsCompact);
            var said = new List<string>();
            home.PropertyChanged += (_, e) => said.Add(e.PropertyName);
            home.DetailedCardsRunPast = true;
            Assert.True(home.IsCardsCompact);
            Assert.Equal(new[] { nameof(DashboardViewModel.DetailedCardsRunPast), nameof(DashboardViewModel.IsCardsCompact) }, said);
            said.Clear();
            home.HomeCards = HomeCardSize.Detailed;
            Assert.False(home.IsCardsCompact);
            Assert.Equal(new[] { nameof(DashboardViewModel.HomeCards), nameof(DashboardViewModel.IsCardsCompact) }, said);
            home.DetailedCardsRunPast = false;
            home.HomeCards = HomeCardSize.Compact;
            Assert.True(home.IsCardsCompact);
            // The same value again says nothing.
            said.Clear();
            home.HomeCards = HomeCardSize.Compact;
            home.DetailedCardsRunPast = false;
            Assert.Empty(said);
        }

        /// <summary>A compact card names one device: the first that is
        /// connected, or the first when none is. After its name it says
        /// how many more there are, or that it is not connected.</summary>
        [Fact]
        public void ACompactCardNamesOneDevice()
        {
            static PadViewModel.MappedDeviceInfo Device(string name, bool online)
                => new PadViewModel.MappedDeviceInfo { Name = name, InstanceGuid = Guid.NewGuid(), IsOnline = online };
            var s = Strings.Instance;
            Assert.Equal("+{0}", s.Common_MoreCount_Format);

            Assert.Equal((null, string.Empty), SlotSummary.FirstDeviceOf(null));
            Assert.Equal((null, string.Empty), SlotSummary.FirstDeviceOf(Array.Empty<PadViewModel.MappedDeviceInfo>()));

            var pad = Device("Pad", true);
            Assert.Equal((pad, string.Empty), SlotSummary.FirstDeviceOf(new[] { pad }));
            var keys = Device("Keys", true);
            var wheel = Device("Wheel", false);
            Assert.Equal((pad, "+2"), SlotSummary.FirstDeviceOf(new[] { pad, keys, wheel }));
            // The first that is connected, wherever it stands, and the count is of all the others.
            Assert.Equal((keys, "+1"), SlotSummary.FirstDeviceOf(new[] { wheel, keys }));
            Assert.Equal((pad, "+2"), SlotSummary.FirstDeviceOf(new[] { wheel, pad, keys }));
            // None connected: the first, and that it is not connected.
            var gone = Device("Gone", false);
            Assert.Equal((wheel, s.Home_Device_NotConnected), SlotSummary.FirstDeviceOf(new[] { wheel, gone }));
            Assert.Equal((gone, s.Home_Device_NotConnected), SlotSummary.FirstDeviceOf(new[] { gone }));
        }

        // ── a profile's pictures ───────────────────────────────────────

        /// <summary>A profile's card shows its controllers' pictures in one
        /// row at one size: the full size while they all fit, smaller down
        /// to the least size, and past that as many as fit, sharing the
        /// room. The card writes the count of the rest.</summary>
        [Fact]
        public void AProfilesPicturesStandInOneRow_AsManyAsFit() => Sta(() =>
        {
            const double Full = 186, Least = 96, Gap = 10;
            (int, double) Plan(double room, int count) => StillRowPanel.Plan(room, count, Full, Least, Gap);
            // All at the full size.
            Assert.Equal((1, 186.0), Plan(400, 1));
            Assert.Equal((2, 186.0), Plan(382, 2));
            // All, narrower: the room less the gaps, shared.
            Assert.Equal((2, 185.0), Plan(380, 2));
            Assert.Equal((3, 120.0), Plan(380, 3));
            Assert.Equal((3, 96.0), Plan(308, 3));
            // Past the least size: as many as fit at it, sharing the room.
            Assert.Equal((2, 148.5), Plan(307, 3));
            Assert.Equal((3, 100.0), Plan(320, 8));
            Assert.Equal((1, 100.0), Plan(100, 4));
            // Too narrow for one, and nothing to show.
            Assert.Equal((0, 0.0), Plan(95, 4));
            Assert.Equal((0, 0.0), Plan(-20, 2));
            Assert.Equal((0, 0.0), Plan(400, 0));
            // A room with no limit shows all at the full size.
            Assert.Equal((5, 186.0), Plan(double.PositiveInfinity, 5));
            Assert.Equal((5, 186.0), Plan(double.NaN, 5));

            Assert.Equal(126.0 / 186.0, StillRowPanel.Shape);
            var row = new StillRowPanel();
            Assert.Equal((186.0, 96.0, 10.0, 0.0), (row.Full, row.Least, row.Gap, row.Reserve));

            // Laid out: the row leaves its reserve of the room it is given,
            // collapses the pictures it leaves out, tells its pictures
            // their height and is as wide as what it shows.
            row.Reserve = 170;
            var pictures = Enumerable.Range(0, 5).Select(_ => new System.Windows.Controls.Border { Height = 60 }).ToList();
            foreach (var picture in pictures) row.Children.Add(picture);
            row.Measure(new Size(170 + 320, 400));
            Assert.Equal(new Size(3 * 100 + 2 * 10, 60), row.DesiredSize);
            Assert.Equal(new[] { Visibility.Visible, Visibility.Visible, Visibility.Visible, Visibility.Collapsed, Visibility.Collapsed },
                pictures.Select(p => p.Visibility));
            Assert.Equal(Math.Round(100 * StillRowPanel.Shape), StillRowPanel.GetStillHeight(row));
            // A picture inherits the height from the row.
            Assert.Equal(StillRowPanel.GetStillHeight(row), StillRowPanel.GetStillHeight(pictures[0]));
            row.Arrange(new Rect(0, 0, 320, 60));
            Assert.Equal(new[] { 0.0, 110.0, 220.0 }, pictures.Take(3).Select(p => p.TranslatePoint(new Point(0, 0), row).X));
            Assert.All(pictures.Take(3), p => Assert.Equal(100, p.RenderSize.Width));
            // More room: every picture is back, at the full size.
            row.Measure(new Size(170 + 1200, 400));
            Assert.All(pictures, p => Assert.Equal(Visibility.Visible, p.Visibility));
            Assert.Equal(5 * 186 + 4 * 10, row.DesiredSize.Width);
            Assert.Equal(126, StillRowPanel.GetStillHeight(row));
        });
    }
}
