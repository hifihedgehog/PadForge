using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Controls;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Models2D;
using PadForge.Models3D;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The parts a controller's Home card is made of (#574, the world-class
    /// pass): the battery drawn as a meter, the light of a device that is
    /// sending input, the controller's picture on the card's stage, the
    /// flag that tells a part whether what it stands in is live, the
    /// halo's hard rim, the theme's tints for them and the code that wires
    /// them.
    /// </summary>
    public class HomeCardPartsTests
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
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the run timed out");
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

        // ── the battery meter ──────────────────────────────────────────

        /// <summary>The bar is as long as the charge, never so short that a
        /// low battery shows none, and there is none for an empty or
        /// unknown one.</summary>
        [Fact]
        public void TheMetersBar_IsAsLongAsTheCharge()
        {
            Assert.Equal(0, BatteryMeter.BarLength(0));
            Assert.Equal(0, BatteryMeter.BarLength(-1));
            Assert.Equal(BatteryMeter.BarLeast, BatteryMeter.BarLength(1));
            Assert.Equal(BatteryMeter.BarLeast, BatteryMeter.BarLength(10));
            Assert.Equal(BatteryMeter.BarRoom, BatteryMeter.BarLength(100));
            Assert.Equal(BatteryMeter.BarRoom, BatteryMeter.BarLength(140));
            Assert.Equal(BatteryMeter.BarRoom / 2, BatteryMeter.BarLength(50), 12);
            double last = 0;
            for (int percent = 1; percent <= 100; percent++)
            {
                double now = BatteryMeter.BarLength(percent);
                Assert.True(now >= last, "the bar shrank at " + percent);
                last = now;
            }
            // The bar's room is the outline's inside less the same gap at each end.
            Assert.Equal(26, BatteryMeter.UnitWidth);
            Assert.Equal(12, BatteryMeter.UnitHeight);
            Assert.True(BatteryMeter.BarRoom < 21.5 - 1.5);
        }

        private static byte[] Pixels(FrameworkElement element, int width, int height, double scale)
        {
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
            int w = (int)Math.Round(width * scale), h = (int)Math.Round(height * scale);
            var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var pixels = new byte[w * h * 4];
            bitmap.CopyPixels(pixels, w * 4, 0);
            return pixels;
        }

        /// <summary>The meter is 26 by 12 unless a height is set, keeps
        /// those proportions, and takes no room with no battery. It draws
        /// its outline and cap in one color and its bar in another, the bar
        /// from the outline's start, and a bolt in the ground's color while
        /// it charges.</summary>
        [Fact]
        public void TheMeter_DrawsAnOutlineACapAndABar_AndABoltWhileItCharges() => RunSta(() =>
        {
            var none = new BatteryMeter();
            none.Measure(new Size(200, 200));
            Assert.Equal(new Size(0, 0), none.DesiredSize);
            var plain = new BatteryMeter { Percent = 50 };
            plain.Measure(new Size(200, 200));
            Assert.Equal(new Size(26, 12), plain.DesiredSize);
            var tall = new BatteryMeter { Percent = 50, Height = 24 };
            tall.Measure(new Size(200, 200));
            Assert.Equal(new Size(52, 24), tall.DesiredSize);

            // Drawn at ten pixels a unit, so a unit's place is easy to read.
            const int S = 10, W = 26 * S;
            var meter = new BatteryMeter
            {
                Percent = 50, Stroke = Brushes.Red, Fill = Brushes.Lime, Ground = Brushes.Blue,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            var half = Pixels(meter, 26, 12, S);
            (byte B, byte G, byte R, byte A) P(byte[] px, double x, double y)
            {
                int i = ((int)(y * S) * W + (int)(x * S)) * 4;
                return (px[i], px[i + 1], px[i + 2], px[i + 3]);
            }
            // The outline's top edge, in the stroke's red.
            Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), P(half, 11, 0.75));
            // The cap, past the body's end.
            Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), P(half, 24.5, 6));
            // The bar is half the room: green at its start and middle, clear past its end.
            Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)255), P(half, 4, 6));
            Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)255), P(half, 2.75 + BatteryMeter.BarRoom / 2 - 1, 6));
            Assert.Equal((byte)0, P(half, 2.75 + BatteryMeter.BarRoom / 2 + 1, 6).A);
            Assert.Equal((byte)0, P(half, 19, 6).A);
            // No bolt while it does not charge.
            Assert.NotEqual((byte)255, P(half, 11.6, 6).B);

            meter.Percent = 100;
            var full = Pixels(meter, 26, 12, S);
            Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)255), P(full, 19, 6));
            Assert.Equal((byte)0, P(full, 2.75 + BatteryMeter.BarRoom + 0.6, 6).A);

            meter.Percent = 0;
            var empty = Pixels(meter, 26, 12, S);
            Assert.Equal((byte)0, P(empty, 4, 6).A);
            Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), P(empty, 11, 0.75));

            // Charging: the bolt, in the ground's blue, over the bar's middle.
            meter.Percent = 100;
            meter.Charging = true;
            var charging = Pixels(meter, 26, 12, S);
            Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), P(charging, 11.6, 6));
            Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)255), P(charging, 5, 6));

            // No battery: nothing at all.
            meter.Percent = -1;
            Assert.All(Pixels(meter, 26, 12, S), b => Assert.Equal((byte)0, b));

            // It is a picture, so it is no part of what a screen reader is told.
            Assert.Null(System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(meter));
        });

        /// <summary>The meter's colors are the theme's: the quiet text color
        /// for its outline and the text's own for its bar, cold blue while
        /// it charges and gold while it is low. It reads left to right in
        /// every language.</summary>
        [Fact]
        public void TheMetersStyle_IsGoldWhileLow_AndColdWhileItCharges()
        {
            string styles = ExtrasPageTests.RepoText("PadForge.App", "Resources", "ControllerIcons.xaml");
            int at = At(styles, "<Style TargetType=\"{x:Type controls:BatteryMeter}\">");
            string style = styles.Substring(at, styles.IndexOf("</Style>", at, StringComparison.Ordinal) - at);
            At(style, "<Setter Property=\"FlowDirection\" Value=\"LeftToRight\"/>");
            At(style, "<Setter Property=\"Stroke\" Value=\"{DynamicResource TextFillColorTertiaryBrush}\"/>");
            At(style, "<Setter Property=\"Fill\" Value=\"{DynamicResource TextFillColorPrimaryBrush}\"/>");
            int charging = At(style, "<Trigger Property=\"Charging\" Value=\"True\">");
            int low = At(style, "<Trigger Property=\"Low\" Value=\"True\">");
            Assert.True(charging < low);
            Assert.Equal(2, Count(style, "Value=\"{DynamicResource ColdBrush}\"/>"));
            Assert.Equal(2, Count(style, "Value=\"{DynamicResource WaitBrush}\"/>"));
        }

        // ── the light of a device that is sending input ────────────────

        private static CustomInputState Rest()
        {
            var state = new CustomInputState();
            foreach (int stick in new[] { 0, 1, 3, 4 }) state.Axis[stick] = 32767;
            return state;
        }

        /// <summary>A device is sending while a button or a direction is
        /// held, and for a moment after, so a short tap can be seen. The
        /// first look at a device only notes where its axes rest.</summary>
        [Fact]
        public void ADeviceIsSending_WhileSomethingIsHeld_AndForAMomentAfter()
        {
            var watch = new InputActivity();
            var pad = Guid.NewGuid();
            var state = Rest();
            Assert.False(watch.Look(pad, state, gamepad: true, nowMs: 1000));
            Assert.False(watch.Look(pad, state, true, 1033));

            state.Buttons[3] = true;
            Assert.True(watch.Look(pad, state, true, 1066));
            state.Buttons[3] = false;
            Assert.True(watch.Look(pad, state, true, 1100));
            Assert.True(watch.Look(pad, state, true, 1066 + InputActivity.HoldMs));
            Assert.False(watch.Look(pad, state, true, 1066 + InputActivity.HoldMs + 1));

            state.Povs[0] = 9000;
            Assert.True(watch.Look(pad, state, true, 3000));
            state.Povs[0] = -1;
            Assert.False(watch.Look(pad, state, true, 3000 + InputActivity.HoldMs + 1));

            // A trigger held and a stick held far over: sending for as long as they are.
            state.Axis[2] = 30000;
            Assert.True(watch.Look(pad, state, true, 5000));
            Assert.True(watch.Look(pad, state, true, 9000));
            state.Axis[2] = 0;
            state.Axis[0] = 65535;
            Assert.True(watch.Look(pad, state, true, 9400));
            Assert.True(watch.Look(pad, state, true, 12000));
            state.Axis[0] = 32767;
            // The stick's way back is a movement too.
            Assert.True(watch.Look(pad, state, true, 12033));
            Assert.False(watch.Look(pad, state, true, 12033 + InputActivity.HoldMs + 1));

            // No state: not sending, and the device is forgotten.
            Assert.False(watch.Look(pad, null, true, 13000));
        }

        /// <summary>An axis that moves is input. A value that only shakes
        /// inside the idle test's slop is not, and a slow sweep counts each
        /// time it has gone that far. A device that is no gamepad is judged
        /// by movement and what is held alone, since where its axes rest is
        /// not known: a pedal at the top of its range is at rest.</summary>
        [Fact]
        public void AnAxisThatMovesIsInput_AndOneThatOnlyShakesIsNot()
        {
            var watch = new InputActivity();
            var wheel = Guid.NewGuid();
            var state = new CustomInputState();
            state.Axis[1] = 65535;   // a pedal at rest
            Assert.False(watch.Look(wheel, state, gamepad: false, nowMs: 0));
            Assert.False(watch.Look(wheel, state, false, 5000));

            // Shaking inside the slop, around where it rests.
            long now = 6000;
            foreach (int shake in new[] { 300, -400, 900, -1000, IdleInputDetector.DeltaSlop, 0 })
            {
                state.Axis[0] = shake;
                Assert.False(watch.Look(wheel, state, false, now += 33), "a shake of " + shake + " counted");
            }
            // One step past the slop.
            state.Axis[0] = IdleInputDetector.DeltaSlop + 1;
            Assert.True(watch.Look(wheel, state, false, now += 33));
            Assert.False(watch.Look(wheel, state, false, now += InputActivity.HoldMs + 1));

            // A slow sweep: 400 a look, counted once it has gone past the slop.
            int seen = 0;
            for (int i = 0; i < 12; i++)
            {
                state.Axis[0] += 400;
                // Far enough apart that no look is inside the hold of the last.
                if (watch.Look(wheel, state, false, now += InputActivity.HoldMs + 1)) seen++;
            }
            Assert.InRange(seen, 3, 5);

            // A slider and the mouse's own counts are input as well.
            Assert.False(watch.Look(wheel, state, false, now += InputActivity.HoldMs + 1));
            state.Sliders[0] = 5000;
            Assert.True(watch.Look(wheel, state, false, now += InputActivity.HoldMs + 1));
            Assert.False(watch.Look(wheel, state, false, now += InputActivity.HoldMs + 1));
            state.MouseRawDX = 3;
            Assert.True(watch.Look(wheel, state, false, now += InputActivity.HoldMs + 1));
            Assert.False(watch.Look(wheel, state, false, now += InputActivity.HoldMs + 1));

            // A device that was forgotten starts over: its first look only notes.
            var other = Guid.NewGuid();
            watch.Look(other, Rest(), true, now);
            watch.KeepOnly(new[] { other });
            state.Axis[0] = 0;
            Assert.False(watch.Look(wheel, state, false, now += InputActivity.HoldMs + 1));
            watch.Forget(other);
            var held = Rest();
            held.Buttons[0] = true;
            Assert.True(watch.Look(other, held, true, now));
        }

        /// <summary>The service asks about each mapped device on the
        /// dashboard's lane, only for one that is connected and only while
        /// the engine reads devices.</summary>
        [Fact]
        public void TheService_AsksAboutEachConnectedMappedDevice_WhileTheEngineReads()
        {
            string svc = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
            At(svc, "private readonly InputActivity _inputActivity = new();");
            At(svc, "bool engineReads = dash.EngineStateKey != \"Stopped\";");
            At(svc, "var mapped = OnlineMatch(devArr, dev.InstanceGuid);");
            At(svc, "dev.IsOnline = mapped != null;");
            At(svc, "dev.IsSending = mapped != null && engineReads\r\n                        && _inputActivity.Look(dev.InstanceGuid, mapped.InputState,\r\n                            mapped.CapType == InputDeviceType.Gamepad, nowMs);");
            // The one that is connected is the one the count is made of.
            int any = At(svc, "private static bool AnyOnlineMatch(UserDevice[] devices, Guid guid)");
            int match = At(svc, "private static UserDevice OnlineMatch(UserDevice[] devices, Guid guid)");
            string anyBody = svc.Substring(any, match - any);
            string matchBody = svc.Substring(match, svc.IndexOf("\n        }", match, StringComparison.Ordinal) - match);
            At(anyBody, "if (d != null && d.InstanceGuid == guid && d.IsOnline) return true;");
            At(matchBody, "if (d != null && d.InstanceGuid == guid && d.IsOnline) return d;");
            // The watch is the idle test's own slop, read from the engine's library.
            Assert.Equal(1024, IdleInputDetector.DeltaSlop);
            Assert.Equal(350, InputActivity.HoldMs);
        }

        // ── the picture on the stage ───────────────────────────────────

        /// <summary>Which art a controller's card shows: its preset's, in
        /// the finish chosen on its page. An Xbox and a PlayStation
        /// controller always have a body. A Nintendo and an Extended one
        /// have theirs only where PadForge ships that controller's own art,
        /// and the other three types have none.</summary>
        [Fact]
        public void ACardsPicture_IsItsPresetsArt_InTheFinishChosenOnItsPage()
        {
            string None(string family) => null;
            Assert.Equal("2DModels/XBOXSERIES/" + Controller2DColorways.For("XBOXSERIES").Set[0].BaseFile,
                StagePictures.PathFor("xbox-series-xs-bluetooth", VirtualControllerType.Xbox, None));
            Assert.Equal("2DModels/XBOXSERIES/" + Controller2DColorways.For("XBOXSERIES").Set[0].BaseFile,
                StagePictures.PathFor("xbox-series-xs-bluetooth", VirtualControllerType.Xbox, null));
            // The finish the page keeps for the art's family.
            var (family, set) = Controller2DColorways.For("XBOXSERIES");
            Assert.True(set.Length > 1, "the Series art has one finish");
            var last = set[^1];
            string asked = null;
            Assert.Equal("2DModels/XBOXSERIES/" + last.BaseFile,
                StagePictures.PathFor("xbox-series-xs", VirtualControllerType.Xbox, f => { asked = f; return last.Id; }));
            Assert.Equal(family, asked);
            // A finish the art does not have: the first.
            Assert.Equal("2DModels/XBOXSERIES/" + set[0].BaseFile,
                StagePictures.PathFor("xbox-series-xs", VirtualControllerType.Xbox, _ => "no-such-finish"));
            // The same answer is the same string, so the card's lane makes none.
            Assert.Same(StagePictures.PathFor("xbox-series-xs", VirtualControllerType.Xbox, None),
                StagePictures.PathFor("xbox-series-xs", VirtualControllerType.Xbox, None));

            // An unknown preset: the family's oldest body, as the page draws it.
            Assert.StartsWith("2DModels/XBOX360/", StagePictures.PathFor("a-new-xbox", VirtualControllerType.Xbox, None));
            Assert.StartsWith("2DModels/XBOX360/", StagePictures.PathFor(null, VirtualControllerType.Xbox, None));
            Assert.StartsWith("2DModels/DS4/", StagePictures.PathFor(string.Empty, VirtualControllerType.PlayStation, None));
            Assert.StartsWith("2DModels/DualSense/", StagePictures.PathFor("dualsense-composite", VirtualControllerType.PlayStation, None));
            Assert.StartsWith("2DModels/DUALSENSEEDGE/", StagePictures.PathFor("dualsense-edge", VirtualControllerType.PlayStation, None));

            // Art of its own, or none.
            Assert.Equal(SwitchProLayout.BasePath, StagePictures.PathFor("switch-pro", VirtualControllerType.Nintendo, None));
            Assert.Equal(Switch2ProLayout.BasePath, StagePictures.PathFor("switch2-pro", VirtualControllerType.Nintendo, None));
            Assert.Equal(string.Empty, StagePictures.PathFor("joycon-pair", VirtualControllerType.Nintendo, None));
            Assert.Equal(SteamDeckLayout.BasePath, StagePictures.PathFor("steam-deck", VirtualControllerType.Extended, None));
            Assert.Equal(string.Empty, StagePictures.PathFor("a-flight-stick", VirtualControllerType.Extended, None));
            Assert.Equal(string.Empty, StagePictures.PathFor(string.Empty, VirtualControllerType.Extended, None));
            foreach (var type in new[] { VirtualControllerType.KeyboardMouse, VirtualControllerType.Midi, VirtualControllerType.Vr })
                Assert.Equal(string.Empty, StagePictures.PathFor("xbox-series-xs", type, None));
            // Every type is ruled on.
            Assert.Equal(7, Enum.GetValues(typeof(VirtualControllerType)).Length);
        }

        /// <summary>The base art of each folder is the one the 2D view's own
        /// table names, and every folder's art is in the program, in each
        /// of its finishes.</summary>
        [Fact]
        public void EveryFoldersBaseArt_IsThe2DViewsOwn_AndIsInTheProgram()
        {
            string file = ExtrasPageTests.RepoText("PadForge.App", "Views", "ControllerModel2DView.xaml.cs");
            int table = At(file, "switch (modelName)");
            string view = file.Substring(table, At(file, "string folder = modelName;") - table);
            var cases = Regex.Matches(view, "case \"(\\w+)\":\\s*(?:[^;]*;\\s*)*?basePath = (\\w+)\\.BasePath;");
            Assert.True(cases.Count >= 11, cases.Count + " folders in the view's table");
            var folders = new List<string>();
            foreach (Match one in cases)
            {
                string folder = one.Groups[1].Value;
                folders.Add(folder);
                var layout = typeof(Xbox360Layout).Assembly.GetType("PadForge.Models2D." + one.Groups[2].Value);
                Assert.NotNull(layout);
                string path = (string)layout.GetField("BasePath").GetRawConstantValue();
                Assert.Equal(path, StagePictures.BasePath(folder));
            }
            Assert.Matches(new Regex("default:\\s*(?:[^;]*;\\s*)*?basePath = Xbox360Layout\\.BasePath;"), view);
            Assert.Equal(Xbox360Layout.BasePath, StagePictures.BasePath("XBOX360"));
            Assert.Equal(Xbox360Layout.BasePath, StagePictures.BasePath("anything else"));
            folders.Add("XBOX360");

            foreach (string folder in folders.Distinct())
            {
                var (_, set) = Controller2DColorways.For(folder);
                var paths = set == null
                    ? new[] { StagePictures.BasePath(folder) }
                    : set.Select(finish => "2DModels/" + folder + "/" + finish.BaseFile).ToArray();
                foreach (string path in paths)
                    Assert.True(PadForge.Views.EmbeddedBitmaps.Load(path) != null, path + " is not in the program");
            }
        }

        /// <summary>A picture is cut to what is drawn in it and made to fit
        /// the stage's box, and its gray copy keeps its shape: each pixel's
        /// colors become its brightness and how clear it is stays.</summary>
        [Fact]
        public void APicture_IsCutToWhatIsDrawn_FitToTheStage_AndHasAGrayCopy()
        {
            // 1000 by 800 of nothing with a 400 by 200 block of orange at (300, 300).
            const int W = 1000, H = 800;
            var data = new byte[W * H * 4];
            for (int y = 300; y < 500; y++)
                for (int x = 300; x < 700; x++)
                {
                    int i = (y * W + x) * 4;
                    data[i] = 0x2C;
                    data[i + 1] = 0x6B;
                    data[i + 2] = 0xFF;
                    data[i + 3] = 0xFF;
                }
            Assert.Equal(new Int32Rect(300, 300, 400, 200), StagePictures.Drawn(data, W, H));
            Assert.True(StagePictures.Drawn(new byte[W * H * 4], W, H).IsEmpty);
            // A pixel that is all but clear is not drawn.
            var faint = new byte[4 * 4 * 4];
            faint[(1 * 4 + 2) * 4 + 3] = 8;
            Assert.True(StagePictures.Drawn(faint, 4, 4).IsEmpty);
            faint[(1 * 4 + 2) * 4 + 3] = 9;
            Assert.Equal(new Int32Rect(2, 1, 1, 1), StagePictures.Drawn(faint, 4, 4));

            var large = BitmapSource.Create(W, H, 144, 144, PixelFormats.Pbgra32, null, data, W * 4);
            var fit = StagePictures.Fit(large);
            // 400 by 200 into 352 by 232: the width decides.
            Assert.Equal(StagePictures.BoxWidth, fit.PixelWidth);
            Assert.Equal(176, fit.PixelHeight);
            Assert.True(fit.IsFrozen);
            Assert.Equal(PixelFormats.Pbgra32, fit.Format);
            var middle = new byte[4];
            fit.CopyPixels(new Int32Rect(176, 88, 1, 1), middle, 4, 0);
            Assert.Equal(new byte[] { 0x2C, 0x6B, 0xFF, 0xFF }, middle);
            // One that already fits keeps its size, and nothing drawn is no picture.
            var small = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Pbgra32, null, faint, 16);
            var kept = StagePictures.Fit(small);
            Assert.Equal(1, kept.PixelWidth);
            Assert.Null(StagePictures.Fit(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Pbgra32, null, new byte[64], 16)));
            Assert.Null(StagePictures.Fit(null));

            // Gray: red, a half-clear blue, white and nothing.
            var four = new byte[]
            {
                0, 0, 255, 255,
                128, 0, 0, 128,
                255, 255, 255, 255,
                0, 0, 0, 0,
            };
            var gray = StagePictures.MakeGray(BitmapSource.Create(4, 1, 96, 96, PixelFormats.Pbgra32, null, four, 16));
            var read = new byte[16];
            gray.CopyPixels(read, 16, 0);
            Assert.Equal(new byte[] { 77, 77, 77, 255, 15, 15, 15, 128, 255, 255, 255, 255, 0, 0, 0, 0 }, read);
            Assert.True(gray.IsFrozen);
            Assert.Null(StagePictures.MakeGray(null));

            // The stage's own copies are made once.
            string path = Xbox360Layout.BasePath;
            var color = StagePictures.Color(path);
            Assert.NotNull(color);
            Assert.Same(color, StagePictures.Color(path));
            Assert.Same(StagePictures.Gray(path), StagePictures.Gray(path));
            var picture = (BitmapSource)color;
            // It fills the box one way and fits it the other, to the pixel a rounding may add.
            Assert.True(picture.PixelWidth <= StagePictures.BoxWidth + 1 && picture.PixelHeight <= StagePictures.BoxHeight + 1,
                picture.PixelWidth + " by " + picture.PixelHeight);
            Assert.True(Math.Abs(picture.PixelWidth - StagePictures.BoxWidth) <= 1 || Math.Abs(picture.PixelHeight - StagePictures.BoxHeight) <= 1,
                picture.PixelWidth + " by " + picture.PixelHeight);
            Assert.Equal(picture.PixelWidth, ((BitmapSource)StagePictures.Gray(path)).PixelWidth);
            Assert.Null(StagePictures.Color(string.Empty));
            Assert.Null(StagePictures.Gray(null));
            Assert.Null(StagePictures.Color("2DModels/no/such.png"));
        }

        /// <summary>A card keeps what its picture was made for, so the
        /// service asks for the art only when the preset, the type or the
        /// finishes changed, and the card says so when it has one.</summary>
        [Fact]
        public void ACard_KeepsWhatItsPictureWasMadeFor()
        {
            var slot = new SlotSummary(0);
            var changed = new List<string>();
            slot.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.False(slot.HasPicture);
            Assert.False(slot.PictureIsFor(null, VirtualControllerType.Xbox, string.Empty, false));

            var color = StagePictures.Color(Xbox360Layout.BasePath);
            var gray = StagePictures.Gray(Xbox360Layout.BasePath);
            slot.SetPicture("xbox-360-wired", VirtualControllerType.Xbox, "Xbox360=White", false, color, gray);
            Assert.Same(color, slot.Picture);
            Assert.Same(gray, slot.PictureGray);
            Assert.True(slot.HasPicture);
            Assert.Equal(new[] { nameof(SlotSummary.Picture), nameof(SlotSummary.PictureGray), nameof(SlotSummary.HasPicture) }, changed);
            Assert.True(slot.PictureIsFor("xbox-360-wired", VirtualControllerType.Xbox, "Xbox360=White", false));
            Assert.False(slot.PictureIsFor("xbox-360-wired", VirtualControllerType.Xbox, "Xbox360=Black", false));
            Assert.False(slot.PictureIsFor("xbox-one-s", VirtualControllerType.Xbox, "Xbox360=White", false));
            Assert.False(slot.PictureIsFor("xbox-360-wired", VirtualControllerType.Extended, "Xbox360=White", false));
            // The page's view changed: the picture is of the other kind.
            Assert.False(slot.PictureIsFor("xbox-360-wired", VirtualControllerType.Xbox, "Xbox360=White", true));

            // The same art for other facts: remembered, and nothing raised.
            changed.Clear();
            slot.SetPicture("xbox-360-wireless", VirtualControllerType.Xbox, "Xbox360=White", false, color, gray);
            Assert.Empty(changed);
            Assert.True(slot.PictureIsFor("xbox-360-wireless", VirtualControllerType.Xbox, "Xbox360=White", false));
            // A type with none: the card says it has no picture, and that is an answer too.
            slot.SetPicture(string.Empty, VirtualControllerType.Midi, string.Empty, false, null, null);
            Assert.False(slot.HasPicture);
            Assert.Contains(nameof(SlotSummary.HasPicture), changed);
            Assert.True(slot.PictureIsFor(string.Empty, VirtualControllerType.Midi, string.Empty, false));

            // A still that is being made: no picture yet, and the stage
            // says one is coming, so the type's symbol does not stand in.
            changed.Clear();
            slot.SetPicture("xbox-series-xs", VirtualControllerType.Xbox, string.Empty, true, null, null, coming: true);
            Assert.Null(slot.Picture);
            Assert.True(slot.HasPicture);
            Assert.Equal(new[] { nameof(SlotSummary.HasPicture) }, changed);
            // It came.
            changed.Clear();
            slot.SetPicture("xbox-series-xs", VirtualControllerType.Xbox, string.Empty, true, color, gray);
            Assert.True(slot.HasPicture);
            Assert.Equal(new[] { nameof(SlotSummary.Picture), nameof(SlotSummary.PictureGray) }, changed);
            // Forgotten, it is asked for again and answers as before.
            Assert.True(slot.PictureIsFor("xbox-series-xs", VirtualControllerType.Xbox, string.Empty, true));
            slot.ForgetPicture();
            Assert.False(slot.PictureIsFor("xbox-series-xs", VirtualControllerType.Xbox, string.Empty, true));
            Assert.Same(color, slot.Picture);

            string svc = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
            // One rule for a still or the 2D art, which the Devices page's
            // panel reads as well.
            At(svc, "private bool PicturesAreStills => !(_mainVm.Settings?.Use2DControllerView ?? false);");
            Assert.Equal(2, Count(svc, "bool still = PicturesAreStills;"));
            At(svc, "if (!slot.PictureIsFor(padVm.ProfileId, padVm.OutputType, padVm.Model3DAppearances, still))");
            At(svc, "string art = StagePictures.PathFor(profile, type, padVm.GetModelAppearance);");
            At(svc, "slot.SetPicture(profile, type, finishes, still, StagePictures.Color(art), StagePictures.Gray(art));");
        }

        // ── the still of the 3D model ──────────────────────────────────

        /// <summary>A still is drawn in the finish chosen on the
        /// controller's page when the model has it, in the model's first
        /// when it does not, and a model of one finish has none to choose.
        /// The lists are the model classes' own, by the one function the
        /// page's 3D view asks too.</summary>
        [Fact]
        public void AStillsFinish_IsTheChosenOneOrTheModelsFirst()
        {
            Assert.Equal("VelocityGreen", ControllerStill.FinishFor("XboxSeries", "VelocityGreen"));
            Assert.Equal(ControllerModelXboxSeries.AppearanceIds[0], ControllerStill.FinishFor("XboxSeries", null));
            Assert.Equal(ControllerModelXboxSeries.AppearanceIds[0], ControllerStill.FinishFor("XboxSeries", "no-such-finish"));
            Assert.Equal("DeepEarthVolcanic", ControllerStill.FinishFor("DualSense", "DeepEarthVolcanic"));
            Assert.Equal(ControllerModelDS4.AppearanceIds[0], ControllerStill.FinishFor("DS4", string.Empty));
            foreach (string family in new[] { "XBOX360", "DS3", "Switch2Pro", "SteamDeck", "SteamController", "SteamController2", "anything" })
                Assert.Null(ControllerStill.FinishFor(family, "Carbon"));

            Assert.Same(ControllerModelXboxSeries.AppearanceIds, ControllerModelBase.Appearances("XboxSeries").Ids);
            Assert.Same(ControllerModelXboxSeries.AppearanceNames, ControllerModelBase.Appearances("XboxSeries").Names);
            Assert.Same(ControllerModelDualSense.AppearanceIds, ControllerModelBase.Appearances("DualSense").Ids);
            Assert.Same(ControllerModelDS4.AppearanceIds, ControllerModelBase.Appearances("DS4").Ids);
            Assert.Same(ControllerModelDualSenseEdge.AppearanceIds, ControllerModelBase.Appearances("DualSenseEdge").Ids);
            Assert.Null(ControllerModelBase.Appearances("XBOX360").Ids);
            string view = ExtrasPageTests.RepoText("PadForge.App", "Views", "ControllerModelView.xaml.cs");
            At(view, "private static (string[] ids, string[] names) AppearanceRegistry(string family)\r\n            => ControllerModelBase.Appearances(family);");

            // Which model, by the picture's own rule.
            string None(string family) => null;
            Assert.Equal(("XboxSeries", ControllerModelXboxSeries.AppearanceIds[0]), StagePictures.StillFor("xbox-series-xs", VirtualControllerType.Xbox, None));
            string asked = null;
            Assert.Equal(("XboxSeries", "PulseRed"), StagePictures.StillFor("xbox-one-s", VirtualControllerType.Xbox, f => { asked = f; return "PulseRed"; }));
            Assert.Equal("XboxSeries", asked);
            Assert.Equal(("XBOX360", (string)null), StagePictures.StillFor("a-new-xbox", VirtualControllerType.Xbox, None));
            Assert.Equal(("DualSense", "White"), StagePictures.StillFor("dualsense-composite", VirtualControllerType.PlayStation, null));
            Assert.Equal(("Switch2Pro", (string)null), StagePictures.StillFor("switch-pro", VirtualControllerType.Nintendo, None));
            Assert.Equal(((string)null, (string)null), StagePictures.StillFor("joycon-pair", VirtualControllerType.Nintendo, None));
            Assert.Equal(("SteamDeck", (string)null), StagePictures.StillFor("steam-deck", VirtualControllerType.Extended, None));
            Assert.Equal(((string)null, (string)null), StagePictures.StillFor("a-flight-stick", VirtualControllerType.Extended, None));
            foreach (var type in new[] { VirtualControllerType.KeyboardMouse, VirtualControllerType.Midi, VirtualControllerType.Vr })
                Assert.Null(StagePictures.StillFor("xbox-series-xs", type, None).Family);
            // A body or none, for the 2D art and the still alike.
            Assert.True(StagePictures.HasBody(null, VirtualControllerType.Xbox));
            Assert.True(StagePictures.HasBody("switch2-pro", VirtualControllerType.Nintendo));
            Assert.False(StagePictures.HasBody("joycon-pair", VirtualControllerType.Nintendo));
            Assert.False(StagePictures.HasBody("xbox-series-xs", VirtualControllerType.Midi));
        }

        /// <summary>A model is drawn at rest on clear ground, from the
        /// page's camera and under the page's lights, to a frozen bitmap
        /// three times the size of a stage's picture.</summary>
        [Fact]
        public void AModelIsDrawnOnClearGround_ToAFrozenBitmap() => RunSta(() =>
        {
            var still = ControllerStill.Render("XBOX360", null);
            Assert.True(still.IsFrozen);
            Assert.Equal(ControllerStill.DrawWidth, still.PixelWidth);
            Assert.Equal(ControllerStill.DrawHeight, still.PixelHeight);
            Assert.Equal(3 * StagePictures.BoxWidth, ControllerStill.DrawWidth);
            Assert.Equal(3 * StagePictures.BoxHeight, ControllerStill.DrawHeight);
            var pixels = new byte[still.PixelWidth * still.PixelHeight * 4];
            still.CopyPixels(pixels, still.PixelWidth * 4, 0);
            byte Alpha(int x, int y) => pixels[(y * still.PixelWidth + x) * 4 + 3];
            // The controller is in the middle, and the corners are clear.
            Assert.True(Alpha(still.PixelWidth / 2, still.PixelHeight / 2) >= 250);
            foreach (var (x, y) in new[] { (2, 2), (still.PixelWidth - 3, 2), (2, still.PixelHeight - 3), (still.PixelWidth - 3, still.PixelHeight - 3) })
                Assert.Equal((byte)0, Alpha(x, y));
            // It is framed as the page frames it, so it is all in the picture.
            var drawn = StagePictures.Drawn(pixels, still.PixelWidth, still.PixelHeight);
            Assert.True(drawn.X > 0 && drawn.Y > 0 && drawn.X + drawn.Width < still.PixelWidth && drawn.Y + drawn.Height < still.PixelHeight,
                "the model runs out of the picture: " + drawn);
            Assert.True(drawn.Width > still.PixelWidth / 2, "the model is small in the picture: " + drawn);

            string code = ExtrasPageTests.RepoText("PadForge.App", "Models3D", "ControllerStill.cs");
            At(code, "var (distance, lift) = ControllerModelFraming.Fit(outline, DrawWidth, DrawHeight);");
            At(code, "viewport.Children.Add(new HelixToolkit.Wpf.SunLight());");
            At(code, "using var model = ControllerModelBase.Create(family, finish, extraControls: false);");
            // The page's own two other lights.
            string page = ExtrasPageTests.RepoText("PadForge.App", "Views", "ControllerModelView.xaml");
            At(page, "<helix:DirectionalHeadLight Brightness=\"0.35\" />");
            At(page, "<DirectionalLight Color=\"#5A321C\" Direction=\"0,-0.7,0.7\"/>");
            Assert.Equal(Color.FromRgb(89, 89, 89), ControllerStill.HeadLight);
            Assert.Equal((byte)Math.Round(0.35 * 255), ControllerStill.HeadLight.R);
            Assert.Equal(Color.FromRgb(0x5A, 0x32, 0x1C), ControllerStill.RimLight);
        });

        /// <summary>A still is made once for each model and finish, cut and
        /// fit as the 2D art is and with a gray copy. A model that cannot be
        /// drawn is made too, as nothing, so it is not tried again and the
        /// card keeps its 2D art.</summary>
        [Fact]
        public void AStillIsMadeOnce_AndAModelThatCannotBeDrawnIsNothing()
        {
            var draw = StagePictures.DrawStill;
            try
            {
                StagePictures.ForgetStills();
                int drawn = 0;
                var orange = new byte[600 * 400 * 4];
                for (int y = 100; y < 300; y++)
                    for (int x = 100; x < 500; x++)
                    {
                        int i = (y * 600 + x) * 4;
                        orange[i] = 0x2C;
                        orange[i + 1] = 0x6B;
                        orange[i + 2] = 0xFF;
                        orange[i + 3] = 0xFF;
                    }
                StagePictures.DrawStill = (family, finish) =>
                {
                    drawn++;
                    if (family == "Broken") throw new InvalidOperationException("no such model");
                    var made = BitmapSource.Create(600, 400, 96, 96, PixelFormats.Pbgra32, null, orange, 600 * 4);
                    made.Freeze();
                    return made;
                };

                Assert.False(StagePictures.TryGetStill("Test", "A", out var none, out _));
                Assert.Null(none);
                StagePictures.MakeStill("Test", "A");
                Assert.True(StagePictures.TryGetStill("Test", "A", out var color, out var gray));
                var picture = Assert.IsAssignableFrom<BitmapSource>(color);
                // 400 by 200 of orange, cut out and fit to the stage's box.
                Assert.Equal(StagePictures.BoxWidth, picture.PixelWidth);
                Assert.Equal(176, picture.PixelHeight);
                Assert.True(picture.IsFrozen);
                Assert.Equal(picture.PixelWidth, ((BitmapSource)gray).PixelWidth);
                Assert.NotSame(color, gray);
                // Once: a second ask draws nothing, and another finish is another still.
                StagePictures.MakeStill("Test", "A");
                Assert.Equal(1, drawn);
                Assert.False(StagePictures.TryGetStill("Test", "B", out _, out _));
                StagePictures.MakeStill("Test", null);
                Assert.Equal(2, drawn);
                Assert.True(StagePictures.TryGetStill("Test", null, out _, out _));
                Assert.True(StagePictures.TryGetStill("Test", string.Empty, out _, out _));

                StagePictures.MakeStill("Broken", null);
                Assert.True(StagePictures.TryGetStill("Broken", null, out var broken, out var brokenGray));
                Assert.Null(broken);
                Assert.Null(brokenGray);
                StagePictures.MakeStill("Broken", null);
                Assert.Equal(3, drawn);
            }
            finally
            {
                StagePictures.DrawStill = draw;
                StagePictures.ForgetStills();
            }
            Assert.Equal(nameof(ControllerStill.Render), draw.Method.Name);
        }

        /// <summary>The interface never waits for a still: it is made on a
        /// thread of its own, which can make one (a single-threaded
        /// apartment, as WPF asks), and the interface's thread is told
        /// when it is there.</summary>
        [Fact]
        public void AStillIsMadeOnAThreadOfItsOwn_AndTheInterfaceIsTold() => RunSta(() =>
        {
            var draw = StagePictures.DrawStill;
            try
            {
                StagePictures.ForgetStills();
                int home = Environment.CurrentManagedThreadId, drewOn = 0, toldOn = 0;
                var apartment = ApartmentState.Unknown;
                bool background = false;
                StagePictures.DrawStill = (family, finish) =>
                {
                    drewOn = Environment.CurrentManagedThreadId;
                    apartment = Thread.CurrentThread.GetApartmentState();
                    background = Thread.CurrentThread.IsBackground;
                    // What a real drawing does: WPF objects of this thread's own.
                    var visual = new DrawingVisual();
                    using (var context = visual.RenderOpen())
                        context.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 8, 8));
                    var made = new RenderTargetBitmap(8, 8, 96, 96, PixelFormats.Pbgra32);
                    made.Render(visual);
                    made.Freeze();
                    return made;
                };
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                bool told = false;
                StagePictures.MakeStillOnAWorker("Worker", "A", dispatcher, () =>
                {
                    told = true;
                    toldOn = Environment.CurrentManagedThreadId;
                });
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!told)
                {
                    Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "the interface was not told");
                    dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Thread.Sleep(10);
                }
                Assert.NotEqual(0, drewOn);
                Assert.NotEqual(home, drewOn);
                Assert.Equal(home, toldOn);
                Assert.Equal(ApartmentState.STA, apartment);
                Assert.True(background);
                // The still was there before the interface was told.
                Assert.True(StagePictures.TryGetStill("Worker", "A", out var color, out _));
                Assert.NotNull(color);
                // A frozen picture, so this thread may read it.
                Assert.True(((BitmapSource)color).PixelWidth > 0);
            }
            finally
            {
                StagePictures.DrawStill = draw;
                StagePictures.ForgetStills();
            }
        });

        /// <summary>The service's side: a card of a controller whose page
        /// shows the 3D view takes the still that is made, or waits with an
        /// empty stage for the one being made, and one whose model cannot
        /// be drawn takes the 2D art. One still is made at a time, the
        /// first when the interface is idle, and every card asks again
        /// when one is done.</summary>
        [Fact]
        public void TheServiceGivesACardTheStill_OrWaitsForIt_OrTheArt()
        {
            string svc = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
            int at = At(svc, "private void SetSlotPicture(SlotSummary slot, PadViewModel padVm, bool still)");
            string body = svc.Substring(at, svc.IndexOf("\n        }", at, StringComparison.Ordinal) - at);
            int ask = At(body, "var (family, finish) = StagePictures.StillFor(profile, type, padVm.GetModelAppearance);");
            int waiting = At(body, "slot.SetPicture(profile, type, finishes, still, null, null, coming: true);");
            int made = At(body, "slot.SetPicture(profile, type, finishes, still, color, gray);");
            int art = At(body, "slot.SetPicture(profile, type, finishes, still, StagePictures.Color(art), StagePictures.Gray(art));");
            Assert.True(ask < waiting && waiting < made && made < art);
            At(body, "if (!StagePictures.TryGetStill(family, finish, out var color, out var gray))");
            At(body, "WantStill(family, finish);");
            At(body, "if (color != null)");
            At(svc, "Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(MakeNextStill));");
            At(svc, "StagePictures.MakeStillOnAWorker(family, finish, Dispatcher.CurrentDispatcher, StillMade);");
            int done = At(svc, "private void StillMade()");
            string after = svc.Substring(done, svc.IndexOf("\n        }", done, StringComparison.Ordinal) - done);
            At(after, "_stillPosted = false;");
            At(after, "RefreshPictures();");
            At(after, "PostNextStill();");
            // The cards take the still at once: no timer refreshes them
            // while the engine is stopped.
            int again = At(svc, "internal void RefreshPictures()");
            string pictures = svc.Substring(again, svc.IndexOf("\n        }", again, StringComparison.Ordinal) - again);
            Assert.True(At(pictures, "foreach (var slot in _mainVm.Dashboard.SlotSummaries) slot.ForgetPicture();")
                < At(pictures, "RefreshSlotSummaryProperties();"));
            At(svc, "if (_stillPosted || _stillsWanted.Count == 0) return;");
            At(svc, "if (!_stillsWanted.Contains((family, finish))) _stillsWanted.Add((family, finish));");
        }

        /// <summary>A card's picture fades in when it arrives, and only
        /// then: nothing fades when one picture takes another's place, in
        /// no window, or with Windows' animations off. The fade lets go, so
        /// the picture's opacity is its style's after it.</summary>
        [Fact]
        public void APictureFadesInWhenItArrives() => RunSta(() =>
        {
            var motion = Appear.MotionEnabled;
            Appear.MotionEnabled = () => true;
            var picture = new Border { Width = 40, Height = 20, Opacity = 0.55 };
            // In no window: nothing.
            Appear.SetWhen(picture, "one");
            Assert.False(picture.HasAnimatedProperties);
            Appear.SetWhen(picture, null);
            var window = new Window
            {
                Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, Content = picture,
            };
            try
            {
                window.Show();
                var dispatcher = window.Dispatcher;
                void Pump(Func<bool> until, string what)
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (!until())
                    {
                        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), what + " did not happen");
                        dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                        Thread.Sleep(15);
                    }
                }
                Pump(() => picture.IsLoaded, "the load");
                Assert.False(picture.HasAnimatedProperties);

                // It arrives: a fade, which starts from clear at the next
                // frame and lets go at its end.
                Appear.SetWhen(picture, "one");
                Assert.True(picture.HasAnimatedProperties);
                Pump(() => !picture.HasAnimatedProperties, "the fade's end");
                Assert.Equal(0.55, picture.Opacity);
                // Another takes its place: no fade.
                Appear.SetWhen(picture, "two");
                Assert.False(picture.HasAnimatedProperties);
                // It leaves: no fade.
                Appear.SetWhen(picture, null);
                Assert.False(picture.HasAnimatedProperties);
                // With Windows' animations off: no fade.
                Appear.MotionEnabled = () => false;
                Appear.SetWhen(picture, "three");
                Assert.False(picture.HasAnimatedProperties);
                Assert.Equal(0.55, picture.Opacity);
            }
            finally
            {
                Appear.MotionEnabled = motion;
                window.Close();
            }
            Assert.Equal(TimeSpan.FromMilliseconds(200), Appear.FadeTime);
            string appear = ExtrasPageTests.RepoText("PadForge.App", "Controls", "Appear.cs");
            At(appear, "internal static Func<bool> MotionEnabled = () => SystemParameters.ClientAreaAnimation;");
            At(appear, "From = 0,");
            At(appear, "FillBehavior = FillBehavior.Stop,");

            // Home's stage: the picture's holder fades in with the picture,
            // over a soft light that is always there.
            string home = ExtrasPageTests.RepoText("PadForge.App", "Views", "HomePage.xaml");
            At(home, "<Grid Margin=\"10,8,10,8\" FlowDirection=\"LeftToRight\"\r\n                                                  controls:Appear.When=\"{Binding Picture}\">");
            At(home, "<RadialGradientBrush Center=\"0.5,0.4\" GradientOrigin=\"0.5,0.4\" RadiusX=\"0.58\" RadiusY=\"0.62\">");
            At(home, "<GradientStop Color=\"#2996AACD\" Offset=\"0\"/>");
        });

        // ── live or not ────────────────────────────────────────────────

        /// <summary>Whether a part stands in something live is true unless
        /// a parent says otherwise, and what a parent says reaches every
        /// part inside it. A stage icon with something set is ember only
        /// where that is true, and bright steel where it is not.</summary>
        [Fact]
        public void WhatIsNotLiveShowsNoEmber() => RunSta(() =>
        {
            var inner = new TextBlock();
            var card = new Border { Child = new StackPanel { Children = { inner } } };
            Assert.True(Heat.GetIsLive(inner));
            Heat.SetIsLive(card, false);
            Assert.False(Heat.GetIsLive(inner));
            Heat.SetIsLive(card, true);
            Assert.True(Heat.GetIsLive(inner));
            card.ClearValue(Heat.IsLiveProperty);
            Assert.True(Heat.GetIsLive(inner));
            Assert.True(((FrameworkPropertyMetadata)Heat.IsLiveProperty.GetMetadata(typeof(TextBlock))).Inherits);

            string styles = ExtrasPageTests.RepoText("PadForge.App", "Resources", "ControllerIcons.xaml");
            foreach (var (key, property) in new[] { ("StageGlyphText", "Foreground"), ("StageGlyphShape", "Fill") })
            {
                int at = At(styles, "<Style x:Key=\"" + key + "\"");
                string style = styles.Substring(at, styles.IndexOf("</Style>", at, StringComparison.Ordinal) - at);
                int hot = At(style, "<DataTrigger Binding=\"{Binding IsHot}\" Value=\"True\">");
                int cold = At(style, "<Condition Binding=\"{Binding (controls:Heat.IsLive), RelativeSource={RelativeSource Self}}\" Value=\"False\"/>");
                // The later trigger wins, so it must come after the ember one.
                Assert.True(hot < cold);
                At(style, "<Setter Property=\"" + property + "\" Value=\"{DynamicResource SubRowTextBrush}\"/>");
                At(style, "<Setter Property=\"" + property + "\" Value=\"{DynamicResource EmberBrush}\"/>");
            }
        });

        // ── the halo's rim ─────────────────────────────────────────────

        /// <summary>A halo's rim is a line one pixel wide that hugs its
        /// child's edge from outside, in the light's color, and shows with
        /// or without the light.</summary>
        [Fact]
        public void AHalosRim_IsOnePixelOutsideItsChildsEdge() => RunSta(() =>
        {
            const int W = 120, H = 80;
            var child = new Border { Width = 60, Height = 30 };
            var halo = new Halo
            {
                Child = child, CornerRadius = 0, Radius = 20, Strength = 0, Rim = 0.5, IsLit = true,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var ground = new Grid { Width = W, Height = H };
            ground.Children.Add(halo);
            byte[] Alphas()
            {
                var px = Pixels(ground, W, H, 1);
                var alphas = new byte[W * H];
                for (int i = 0; i < alphas.Length; i++) alphas[i] = px[i * 4 + 3];
                return alphas;
            }
            var rim = Alphas();
            // The child is x 30 to 90, y 25 to 55. The line is the row over it.
            Assert.InRange(rim[24 * W + 60], 122, 134);
            Assert.Equal((byte)0, rim[23 * W + 60]);
            Assert.Equal((byte)0, rim[25 * W + 60]);
            Assert.Equal((byte)0, rim[40 * W + 60]);
            Assert.InRange(rim[40 * W + 29], 122, 134);
            Assert.InRange(rim[40 * W + 90], 122, 134);
            Assert.InRange(rim[55 * W + 60], 122, 134);
            Assert.Equal((byte)0, rim[40 * W + 27]);

            // With the light: the line over the light's own edge value.
            halo.Strength = 0.2;
            var both = Alphas();
            Assert.True(both[24 * W + 60] > rim[24 * W + 60]);
            Assert.True(both[22 * W + 60] > 0);
            // No rim: the light alone.
            halo.Rim = 0;
            var light = Alphas();
            Assert.True(light[24 * W + 60] < rim[24 * W + 60]);
            // Unlit: nothing, rim or no rim.
            halo.Rim = 0.5;
            halo.IsLit = false;
            Assert.All(Alphas(), a => Assert.Equal((byte)0, a));
        });

        // ── the theme and the page's code ──────────────────────────────

        /// <summary>The four tints the cards brought, each with its dark
        /// value as the app's default and a value of its own on the light
        /// theme.</summary>
        [Fact]
        public void TheCardsTints_AreInTheThemeForBothGrounds()
        {
            string theme = ExtrasPageTests.RepoText("PadForge.App", "Common", "EmberTheme.cs");
            string app = ExtrasPageTests.RepoText("PadForge.App", "App.xaml");
            At(theme, "SetBrush(\"EmberRingBrush\", dark ? Color.FromArgb(0x8C, 0xFF, 0x6B, 0x2C) : Color.FromArgb(0xB3, 0xC2, 0x4A, 0x12));");
            At(app, "<SolidColorBrush x:Key=\"EmberRingBrush\" Color=\"#8CFF6B2C\"/>");
            At(theme, "SetBrush(\"EmberRingFaintBrush\", dark ? Color.FromArgb(0x4D, 0xFF, 0x6B, 0x2C) : Color.FromArgb(0x66, 0xC2, 0x4A, 0x12));");
            At(app, "<SolidColorBrush x:Key=\"EmberRingFaintBrush\" Color=\"#4DFF6B2C\"/>");
            At(theme, "SetBrush(\"WaitTintBrush\", dark ? Color.FromArgb(0x1A, 0xF2, 0xB8, 0x4B) : Color.FromArgb(0x24, 0xB0, 0x7C, 0x10));");
            At(app, "<SolidColorBrush x:Key=\"WaitTintBrush\" Color=\"#1AF2B84B\"/>");
            At(theme, "SetBrush(\"SteelMarkBrush\", dark ? Color.FromRgb(0x3A, 0x47, 0x5A) : Color.FromRgb(0xAE, 0xB6, 0xC4));");
            At(app, "<SolidColorBrush x:Key=\"SteelMarkBrush\" Color=\"#3A475A\"/>");

            // The state pill takes its three looks from the heat.
            string styles = ExtrasPageTests.RepoText("PadForge.App", "Resources", "ControllerIcons.xaml");
            int at = At(styles, "<Style x:Key=\"StatePill\" TargetType=\"Button\">");
            string pill = styles.Substring(at, styles.IndexOf("\n    </Style>", at, StringComparison.Ordinal) - at);
            At(pill, "<Setter Property=\"Height\" Value=\"32\"/>");
            // Round at either height: a border rounds a corner by half its height at most.
            At(pill, "BorderThickness=\"1\" CornerRadius=\"19\" Padding=\"{TemplateBinding Padding}\">");
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding Heat\\}\" Value=\"Forging\">\\s*"
                + "<Setter Property=\"BorderBrush\" Value=\"\\{DynamicResource EmberRingBrush\\}\"/>\\s*"
                + "<Setter Property=\"Background\" Value=\"\\{DynamicResource EmberTintBrush\\}\"/>\\s*"
                + "<Setter Property=\"Foreground\" Value=\"\\{DynamicResource EmberHotBrush\\}\"/>"), pill);
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding Heat\\}\" Value=\"Waiting\">\\s*"
                + "<Setter Property=\"BorderBrush\" Value=\"\\{DynamicResource WaitRingBrush\\}\"/>\\s*"
                + "<Setter Property=\"Background\" Value=\"\\{DynamicResource WaitTintBrush\\}\"/>\\s*"
                + "<Setter Property=\"Foreground\" Value=\"\\{DynamicResource WaitBrush\\}\"/>"), pill);
            // The heat's words are the view model's constants.
            Assert.Equal("Forging", SlotSummary.HeatForging);
            Assert.Equal("Waiting", SlotSummary.HeatWaiting);
            Assert.Equal("Cold", SlotSummary.HeatCold);

            // The X in a card's corner: no edge, in the secondary text color
            // as the board draws it, and brighter under the pointer.
            int closeAt = At(app, "<Style x:Key=\"CardCloseButton\" TargetType=\"uictrl:Button\" BasedOn=\"{StaticResource EmberIconButton}\">");
            string close = app.Substring(closeAt, app.IndexOf("</Style>", closeAt, StringComparison.Ordinal) - closeAt);
            At(close, "<Setter Property=\"BorderBrush\" Value=\"Transparent\"/>");
            At(close, "<Setter Property=\"Foreground\" Value=\"{DynamicResource TextFillColorSecondaryBrush}\"/>");
            Assert.Matches(new Regex("<Trigger Property=\"IsMouseOver\" Value=\"True\">\\s*"
                + "<Setter Property=\"Foreground\" Value=\"\\{DynamicResource TextFillColorPrimaryBrush\\}\"/>"), close);
        }

        /// <summary>A click on a card opens the controller once. The stage
        /// opens it for the keyboard and a screen reader, and the mouse's
        /// release is handled where the card's click is answered, before
        /// the stage hears it. Add Device on an empty card opens the
        /// Devices page, as the controller page's own does.</summary>
        [Fact]
        public void ACardOpensOnce_AndAddDeviceOpensTheDevicesPage()
        {
            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "HomePage.xaml.cs");
            int open = At(code, "private void OpenSlot_Click(object sender, RoutedEventArgs e)");
            string opener = code.Substring(open, code.IndexOf("\n        }", open, StringComparison.Ordinal) - open);
            At(opener, "if (sender is FrameworkElement stage && stage.Tag is int slotIndex)");
            At(opener, "SlotCardClicked?.Invoke(this, slotIndex);");
            int end = At(code, "private void OnDragEnd(object sender, MouseButtonEventArgs e)");
            string release = code.Substring(end, code.IndexOf("\n        }", end, StringComparison.Ordinal) - end);
            int click = At(release, "SlotCardClicked?.Invoke(this, padIndex);");
            Assert.True(release.IndexOf("e.Handled = true;", click, StringComparison.Ordinal) > click, "the click's release is not handled");
            Assert.Equal(2, Count(code, "SlotCardClicked?.Invoke(this, "));
            At(code, "public event EventHandler AddDeviceRequested;");
            At(code, "private void AddDevice_Click(object sender, RoutedEventArgs e)\r\n            => AddDeviceRequested?.Invoke(this, EventArgs.Empty);");
            // A button inside the card is no drag handle, and the stage is no button.
            At(code, "if (IsInsideButton(e.OriginalSource as DependencyObject, card)) return;");
            Assert.False(typeof(System.Windows.Controls.Primitives.ButtonBase).IsAssignableFrom(typeof(ClickBorder)));

            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            At(window, "HomePageView.AddDeviceRequested += (s, e) => SelectNavItemByTag(\"Devices\");");
            At(window, "PadPageView.AddDeviceRequested += (s, e) => SelectNavItemByTag(\"Devices\");");
        }
    }
}
