using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using InputManager = PadForge.Common.Input.InputManager;

namespace PadForge.Tests
{
    [CollectionDefinition("ScreenOverlayStatics", DisableParallelization = true)]
    public class ScreenOverlayStaticsCollection { }

    /// <summary>
    /// The Screen Overlay (#560): what a keyboard screen shows, in a window on the
    /// PC's own display. These share the macro queue, the light bar's colors and
    /// the settings statics, and read the z-order, so they run alone.
    /// </summary>
    [Collection("ScreenOverlayStatics")]
    public class ScreenOverlayTests
    {
        // ── The harness ──

        private static void OnSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception e) { failure = e; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the window run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>Lets the dispatcher run for a time.</summary>
        private static void Pump(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static BitmapSource Bitmap(int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixel)
        {
            var bgra = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    var (b, g, r, a) = pixel(x, y);
                    int i = (y * width + x) * 4;
                    bgra[i] = b;
                    bgra[i + 1] = g;
                    bgra[i + 2] = r;
                    bgra[i + 3] = a;
                }
            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        }

        private static byte[] Png(BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        private static (byte B, byte G, byte R, byte A) Opaque(int x, int y) => (0, 0, 255, 255);

        /// <summary>A picture file, deleted when the test ends.</summary>
        private static void WithPicture(int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixel, Action<string> test)
        {
            string path = Path.Combine(Path.GetTempPath(), "PadForge.Tests.ScreenOverlay." + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                File.WriteAllBytes(path, Png(Bitmap(width, height, pixel)));
                test(path);
            }
            finally { File.Delete(path); }
        }

        /// <summary>One pixel of a bitmap as non-premultiplied BGRA.</summary>
        private static (byte B, byte G, byte R, byte A) PixelOf(BitmapSource bitmap, int x, int y)
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return (pixel[0], pixel[1], pixel[2], pixel[3]);
        }

        private static BitmapSource Rendered(ScreenOverlayContentView view, int width, int height)
        {
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            view.UpdateLayout();
            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(view);
            return target;
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        }

        private static string RepoText(params string[] parts) => File.ReadAllText(RepoFile(parts));

        /// <summary>A window's position lands on a device pixel, so it reads back
        /// within a pixel of what was set.</summary>
        private static void AssertNear(double expected, double actual)
            => Assert.True(Math.Abs(expected - actual) <= 1, expected + " against " + actual);

        private static int CountOf(string text, string part)
            => Regex.Matches(text, Regex.Escape(part)).Count;

        // ── The kind ──

        [Fact]
        public void TheKind_IsAppended_AndNoEngineDrawsIt()
        {
            Assert.Equal(4, (int)KeyboardScreenKind.Overlay);
            Assert.Equal(KeyboardScreenKind.Overlay, Enum.GetValues<KeyboardScreenKind>()[^1]);
            Assert.DoesNotContain(KeyboardScreenKind.Overlay, KeyboardScreenFormats.Kinds);
            Assert.Equal((800, 800), KeyboardScreenFormats.SizeOf(KeyboardScreenKind.Overlay));
            Assert.Equal(KeyboardScreenInfo.OverlayRow, KeyboardScreenInfo.Overlay("Screen Overlay").RowGuid);
            Assert.Equal(KeyboardScreenKind.Overlay, KeyboardScreenInfo.Overlay("Screen Overlay").Kind);
        }

        /// <summary>The picker lists the kinds in their own order, since its index
        /// is the kind.</summary>
        [Fact]
        public void TheKindPicker_ListsEveryKindInOrder()
        {
            string pad = RepoText("PadForge.App", "Views", "PadPage.xaml");
            int at = pad.IndexOf("ScreenKindIndex, Mode=TwoWay", StringComparison.Ordinal);
            string box = pad.Substring(at, pad.IndexOf("</ComboBox>", at, StringComparison.Ordinal) - at);
            var names = Regex.Matches(box, "KeyboardScreens_Kind_(\\w+),").Select(m => m.Groups[1].Value).ToArray();
            Assert.Equal(new[] { "Monochrome", "Color", "Arx", "Strike7", "Overlay" }, names);
            Assert.Equal(Enum.GetValues<KeyboardScreenKind>().Length, names.Length);
            var action = new MacroAction { ScreenKindIndex = 4 };
            Assert.Equal(KeyboardScreenKind.Overlay, action.ScreenKind);
        }

        // ── The macro ──

        [Fact]
        public void TheToggle_IsAppended_AndSitsInBothSwitches()
        {
            Assert.Equal(66, (int)MacroActionType.ToggleScreenOverlay);
            Assert.Equal(MacroActionType.ToggleScreenOverlay, Enum.GetValues<MacroActionType>()[^1]);
            string source = RepoText("PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs");
            Assert.Equal(2, CountOf(source, "case MacroActionType.ToggleScreenOverlay:"));
            Assert.Equal(2, CountOf(source, "ToggleScreenOverlayRequested = true;"));
            Assert.Equal(Strings.Instance.MacroAction_ToggleScreenOverlay,
                new MacroAction { Type = MacroActionType.ToggleScreenOverlay }.DisplayText);
            Assert.Contains("ToggleScreenOverlay, S.Macro_ToggleScreenOverlay", RepoText("PadForge.App", "ViewModels", "MacroTypeCatalog.cs"));
        }

        [Fact]
        public void TheToggle_RaisesItsRequest_AndTheServiceClearsIt()
        {
            string service = RepoText("PadForge.App", "Services", "InputService.cs");
            int at = service.IndexOf("if (_inputManager.ToggleScreenOverlayRequested)", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = service.Substring(at, 200);
            Assert.Contains("_inputManager.ToggleScreenOverlayRequested = false;", body);
            Assert.Contains("ToggleScreenOverlay();", body);
        }

        // ── The queue ──

        [Fact]
        public void TheOverlaySwitch_OpensTheQueue_WithTheKeyboardScreensSwitchOff()
        {
            KeyboardScreenService.DropRequests();
            bool hardware = KeyboardScreensRuntime.Enabled;
            KeyboardScreensRuntime.Enabled = false;
            var vm = new DashboardViewModel();
            try
            {
                KeyboardScreenService.RequestStopwatch(KeyboardScreenStopwatchAction.Reset);
                Assert.Equal(0, KeyboardScreenService.PendingRequests);

                vm.EnableScreenOverlay = true;
                Assert.True(KeyboardScreensRuntime.OverlayEnabled);
                KeyboardScreenService.RequestStopwatch(KeyboardScreenStopwatchAction.Reset);
                Assert.Equal(1, KeyboardScreenService.PendingRequests);

                vm.ResetSettingCommand.Execute(nameof(DashboardViewModel.EnableScreenOverlay));
                Assert.False(vm.EnableScreenOverlay);
                Assert.False(KeyboardScreensRuntime.OverlayEnabled);
            }
            finally
            {
                vm.EnableScreenOverlay = false;
                KeyboardScreensRuntime.OverlayEnabled = false;
                KeyboardScreensRuntime.Enabled = hardware;
                KeyboardScreenService.DropRequests();
            }
        }

        /// <summary>A PC with no such keyboard never opens one: the overlay runs
        /// with no host, so no device is opened, no vendor library loads and no
        /// G19 is bound to WinUSB.</summary>
        [Fact]
        public void TheOverlay_RunsWithNoHost()
            => OnSta(() =>
            {
                var service = new KeyboardScreenService(new SettingsViewModel(), null);
                var shown = new List<ScreenOverlayContent>();
                service.OverlaySink = shown.Add;
                service.Tick(hardware: false, overlay: true);
                Assert.False(service.Running);
                Assert.True(service.Active);
                Assert.Empty(service.Screens);
                Assert.Single(shown);

                service.CloseHost();
                Assert.True(service.Active);

                service.Reset();
                Assert.False(service.Active);
            });

        [Fact]
        public void TheOverlay_TakesMacrosFromAnySlot_AndAShowHandsBackToTheMode()
            => OnSta(() =>
            {
                KeyboardScreenService.DropRequests();
                KeyboardScreensRuntime.OverlayEnabled = true;
                try
                {
                    string frame = Convert.ToBase64String(Png(Bitmap(4, 2, Opaque)));
                    var service = new KeyboardScreenService(new SettingsViewModel(), null);
                    var shown = new List<ScreenOverlayContent>();
                    service.OverlaySink = shown.Add;

                    // Slot 7 has nothing assigned, and the overlay has no row.
                    KeyboardScreenService.RequestMode(7, KeyboardScreenKind.Overlay, KeyboardScreenMode.ProfileNumber);
                    service.Tick(hardware: false, overlay: true);
                    Assert.Equal(KeyboardScreenMode.ProfileNumber, service.Get(KeyboardScreenInfo.OverlayRow).Mode);
                    Assert.NotNull(shown[^1].Text);

                    Thread.Sleep(300);
                    KeyboardScreenService.RequestShow(3, KeyboardScreenKind.Overlay, frame, KeyboardScreenService.MinFrameMs, 1);
                    service.Tick(hardware: false, overlay: true);
                    Assert.NotNull(shown[^1].Picture);
                    Assert.Equal(4, shown[^1].Picture.PixelWidth);

                    Thread.Sleep(KeyboardScreenService.MinFrameMs + 50);
                    service.Tick(hardware: false, overlay: true);
                    Assert.Null(shown[^1].Picture);
                    Assert.NotNull(shown[^1].Text);

                    // A hardware kind's macro does not reach the overlay.
                    Thread.Sleep(300);
                    KeyboardScreenService.RequestMode(0, KeyboardScreenKind.Color, KeyboardScreenMode.Clock);
                    KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Color, frame, KeyboardScreenService.MinFrameMs, 1);
                    service.Tick(hardware: false, overlay: true);
                    Assert.Equal(KeyboardScreenMode.ProfileNumber, service.Get(KeyboardScreenInfo.OverlayRow).Mode);
                    Assert.Null(shown[^1].Picture);
                }
                finally
                {
                    KeyboardScreensRuntime.OverlayEnabled = false;
                    KeyboardScreenService.DropRequests();
                }
            });

        /// <summary>The window is down: nothing is drawn, and a show does not wait
        /// to play when it comes back. A mode macro still lands, since it is a
        /// setting.</summary>
        [Fact]
        public void WhileTheWindowIsDown_AShowIsDropped()
            => OnSta(() =>
            {
                KeyboardScreenService.DropRequests();
                KeyboardScreensRuntime.OverlayEnabled = true;
                try
                {
                    string frame = Convert.ToBase64String(Png(Bitmap(4, 2, Opaque)));
                    var service = new KeyboardScreenService(new SettingsViewModel(), null);
                    var shown = new List<ScreenOverlayContent>();
                    service.OverlaySink = shown.Add;
                    KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Overlay, frame, 60000, 1);
                    KeyboardScreenService.RequestMode(0, KeyboardScreenKind.Overlay, KeyboardScreenMode.Clock);
                    service.Tick(hardware: false, overlay: false);
                    Assert.Empty(shown);
                    Assert.Equal(KeyboardScreenMode.Clock, service.Get(KeyboardScreenInfo.OverlayRow).Mode);

                    Thread.Sleep(300);
                    service.Tick(hardware: false, overlay: true);
                    Assert.NotNull(Assert.Single(shown).Text);
                }
                finally
                {
                    KeyboardScreensRuntime.OverlayEnabled = false;
                    KeyboardScreenService.DropRequests();
                }
            });

        // ── What a mode shows ──

        [Fact]
        public void EveryMode_HasItsContent()
            => OnSta(() =>
            {
                LightbarColors.Clear();
                var service = new KeyboardScreenService(new SettingsViewModel(), null);
                long now = Environment.TickCount64;
                string name = DreamcastScreenService.ActiveProfileName();
                var cases = new Dictionary<KeyboardScreenMode, Action<ScreenOverlayContent>>
                {
                    [KeyboardScreenMode.Off] = c => Assert.True(c.IsEmpty),
                    [KeyboardScreenMode.ProfileName] = c => Assert.Equal(name, c.Text),
                    [KeyboardScreenMode.ProfileNumber] = c => Assert.Equal(DreamcastScreenService.ActiveProfileNumber().ToString(), c.Text),
                    [KeyboardScreenMode.ProfilePicture] = c => Assert.Equal(name, c.Text),
                    [KeyboardScreenMode.Clock] = c => Assert.Matches("^\\d\\d:\\d\\d$", c.Text),
                    [KeyboardScreenMode.PlayTime] = c => Assert.False(string.IsNullOrEmpty(c.Text)),
                    [KeyboardScreenMode.Picture] = c => Assert.True(c.IsEmpty),
                    [KeyboardScreenMode.Stopwatch] = c => Assert.Equal(KeyboardScreenService.FormatStopwatch(KeyboardScreenService.StopwatchTime(now)), c.Text),
                    [KeyboardScreenMode.Lightbar] = c => Assert.True(c.IsEmpty),
                };
                var modes = Enum.GetValues<KeyboardScreenMode>();
                Assert.Equal(modes.Length, cases.Count);
                foreach (var mode in modes)
                    cases[mode](service.OverlayContent(new KeyboardScreenData { Mode = mode }, now));
                Assert.True(service.OverlayContent(null, now).IsEmpty, "the default mode is the light bar, with no color on record");
            });

        [Fact]
        public void TheLightbarColor_FillsTheOverlay_WhileItsPadIsOnItsSlot()
            => OnSta(() =>
            {
                LightbarColors.Clear();
                try
                {
                    LightbarColors.Record(2, Guid.NewGuid(), 10, 20, 30);
                    Assert.Equal(Color.FromRgb(10, 20, 30), KeyboardScreenService.LightbarContent((_, _) => true).Fill);
                    Assert.True(KeyboardScreenService.LightbarContent((_, _) => false).IsEmpty);
                }
                finally { LightbarColors.Clear(); }
            });

        [Fact]
        public void APicture_KeepsItsOwnSizeAndItsTransparency()
            => OnSta(() => WithPicture(30, 10, (x, _) => x < 15 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255, (byte)255), path =>
            {
                var service = new KeyboardScreenService(new SettingsViewModel(), null);
                var content = service.OverlayContent(new KeyboardScreenData { Mode = KeyboardScreenMode.Picture, Picture = path }, 1000);
                Assert.Equal(30, content.Picture.PixelWidth);
                Assert.Equal(10, content.Picture.PixelHeight);
                Assert.Equal(0, PixelOf(content.Picture, 2, 5).A);
                Assert.Equal(255, PixelOf(content.Picture, 25, 5).A);
                Assert.Equal(255, PixelOf(content.Picture, 25, 5).R);
            }));

        [Fact]
        public void TheDialogsPreview_ComesFromTheChoicesNotSavedYet()
            => OnSta(() => WithPicture(8, 8, Opaque, path =>
            {
                var service = new KeyboardScreenService(new SettingsViewModel(), null);
                Assert.Equal(8, service.OverlayPreview(new KeyboardScreenData { Mode = KeyboardScreenMode.Picture, Picture = path }, null, "A Game").Picture.PixelWidth);
                Assert.True(service.OverlayPreview(new KeyboardScreenData { Mode = KeyboardScreenMode.Picture }, null, "A Game").IsEmpty);
                Assert.Equal(8, service.OverlayPreview(new KeyboardScreenData { Mode = KeyboardScreenMode.ProfilePicture }, path, "A Game").Picture.PixelWidth);
                Assert.Equal("A Game", service.OverlayPreview(new KeyboardScreenData { Mode = KeyboardScreenMode.ProfilePicture }, null, "A Game").Text);
                Assert.Matches("^\\d\\d:\\d\\d$", service.OverlayPreview(new KeyboardScreenData { Mode = KeyboardScreenMode.Clock }, null, "A Game").Text);
            }));

        // ── A picture at its own size ──

        /// <summary>BoundedBitmap scales a small picture up to its box. A 320 by 240
        /// picture came out 2048 by 1536, so Picture mode showed a screen's window
        /// on the enlarged corner, with room to pan where the picture had none.
        /// RenderWindow's "at its own size" and RenderFitted's "never scaled up"
        /// held only for the picture they were handed.</summary>
        [Fact]
        public void APictureFile_LoadsAtItsOwnSize_UpToTheBound()
            => OnSta(() =>
            {
                WithPicture(320, 240, Opaque, path =>
                {
                    var picture = KeyboardScreenRenderer.LoadPicture(path);
                    Assert.Equal(320, picture.PixelWidth);
                    Assert.Equal(240, picture.PixelHeight);
                    Assert.Equal((0, 0), KeyboardScreenRenderer.PanLimits(picture, KeyboardScreenKind.Color));
                });
                WithPicture(3000, 1000, Opaque, path =>
                {
                    var picture = KeyboardScreenRenderer.LoadPicture(path);
                    Assert.Equal(KeyboardScreenRenderer.MaxPictureSide, picture.PixelWidth);
                    Assert.Equal(682, picture.PixelHeight);
                });
                WithPicture(1000, 3000, Opaque, path =>
                {
                    var picture = KeyboardScreenRenderer.LoadPicture(path);
                    Assert.Equal(682, picture.PixelWidth);
                    Assert.Equal(KeyboardScreenRenderer.MaxPictureSide, picture.PixelHeight);
                });
            });

        [Fact]
        public void ASmallPicture_ShowsCenteredAtItsOwnSize_OnAKeyboardScreen()
            => OnSta(() => WithPicture(10, 10, Opaque, path =>
            {
                var kind = KeyboardScreenKind.Color;
                var (width, height) = KeyboardScreenFormats.SizeOf(kind);
                byte[] frame = KeyboardScreenRenderer.RenderWindow(KeyboardScreenRenderer.LoadPicture(path), kind, 0, 0);
                byte Red(int x, int y) => frame[(y * width + x) * 4 + 2];
                Assert.Equal(255, Red(width / 2, height / 2));
                Assert.Equal(0, Red(0, 0));
                Assert.Equal(0, Red(width / 2 - 8, height / 2));

                var imported = Convert.FromBase64String(KeyboardScreenService.ImportFrame(path, kind));
                var decoded = BoundedBitmap.FromBytes(imported, width, height);
                Assert.Equal(width, decoded.PixelWidth);
                Assert.Equal(255, PixelOf(decoded, width / 2, height / 2).R);
                Assert.Equal(0, PixelOf(decoded, width / 2 - 8, height / 2).R);
            }));

        [Fact]
        public void BoundedBitmap_ScalesUpUnlessToldNot()
            => OnSta(() =>
            {
                byte[] small = Png(Bitmap(10, 20, Opaque));
                Assert.Equal(80, BoundedBitmap.FromBytes(small, 80, 80).PixelHeight);
                var own = BoundedBitmap.FromBytes(small, 80, 80, scaleUp: false);
                Assert.Equal(10, own.PixelWidth);
                Assert.Equal(20, own.PixelHeight);
                Assert.True(own.IsFrozen);
                var bound = BoundedBitmap.FromBytes(Png(Bitmap(200, 100, Opaque)), 80, 80, scaleUp: false);
                Assert.Equal(80, bound.PixelWidth);
                Assert.Equal(40, bound.PixelHeight);
            });

        // ── Frames ──

        [Fact]
        public void AnImportedFrame_KeepsItsProportionsAndTransparency()
            => OnSta(() =>
            {
                WithPicture(40, 20, (x, _) => x < 20 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)0, (byte)255, (byte)0, (byte)255), path =>
                {
                    string part = KeyboardScreenService.ImportFrame(path, KeyboardScreenKind.Overlay);
                    var frame = KeyboardScreenService.FramePicture(KeyboardScreenKind.Overlay, part);
                    Assert.Equal(40, frame.PixelWidth);
                    Assert.Equal(20, frame.PixelHeight);
                    Assert.Equal(0, PixelOf(frame, 5, 10).A);
                    Assert.Equal(255, PixelOf(frame, 30, 10).G);
                });
                // Formats a PNG encoder may not take as they are: a JPEG and a
                // palette picture both land as a frame.
                string jpeg = Path.Combine(Path.GetTempPath(), "PadForge.Tests.ScreenOverlay." + Guid.NewGuid().ToString("N") + ".jpg");
                try
                {
                    var encoder = new JpegBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(Bitmap(16, 8, Opaque), PixelFormats.Bgr24, null, 0)));
                    using (var stream = File.Create(jpeg)) encoder.Save(stream);
                    var frame = KeyboardScreenService.FramePicture(KeyboardScreenKind.Overlay,
                        KeyboardScreenService.ImportFrame(jpeg, KeyboardScreenKind.Overlay));
                    Assert.Equal(16, frame.PixelWidth);
                    Assert.Equal(255, PixelOf(frame, 8, 4).A);
                }
                finally { File.Delete(jpeg); }
                WithPicture(1600, 400, Opaque, path =>
                {
                    var frame = KeyboardScreenService.FramePicture(KeyboardScreenKind.Overlay,
                        KeyboardScreenService.ImportFrame(path, KeyboardScreenKind.Overlay));
                    Assert.Equal(KeyboardScreenFormats.OverlayFrameSide, frame.PixelWidth);
                    Assert.Equal(200, frame.PixelHeight);
                });
                string notAPicture = Path.Combine(Path.GetTempPath(), "PadForge.Tests.ScreenOverlay." + Guid.NewGuid().ToString("N") + ".png");
                try
                {
                    File.WriteAllBytes(notAPicture, new byte[] { 1, 2, 3, 4 });
                    Assert.Null(KeyboardScreenService.ImportFrame(notAPicture, KeyboardScreenKind.Overlay));
                    Assert.Null(KeyboardScreenService.ImportFrame(notAPicture, KeyboardScreenKind.Color));
                }
                finally { File.Delete(notAPicture); }
            });

        [Fact]
        public void TheFrames_DecodeUpToEight_AndTheEditorListsThemUnchanged()
            => OnSta(() =>
            {
                string frame = Convert.ToBase64String(Png(Bitmap(6, 3, Opaque)));
                string nine = string.Join(",", Enumerable.Repeat(frame, 9));
                Assert.Equal(KeyboardScreenService.MaxFrames, KeyboardScreenService.OverlayFrames(nine).Count);
                Assert.Empty(KeyboardScreenService.OverlayFrames(null));
                Assert.Single(KeyboardScreenService.OverlayFrames("not-a-picture," + frame));

                var action = new MacroAction { ScreenKind = KeyboardScreenKind.Overlay, ScreenFrames = frame };
                var image = Assert.IsAssignableFrom<BitmapSource>(Assert.Single(action.ScreenFrameImages));
                Assert.Equal(6, image.PixelWidth);
                Assert.Equal(3, image.PixelHeight);

                var color = new MacroAction { ScreenKind = KeyboardScreenKind.Color, ScreenFrames = frame };
                var (width, _) = KeyboardScreenFormats.SizeOf(KeyboardScreenKind.Color);
                Assert.Equal(width, Assert.IsAssignableFrom<BitmapSource>(Assert.Single(color.ScreenFrameImages)).PixelWidth);
            });

        // ── Fitting to the boundaries ──

        [Theory]
        // A picture smaller than the bounds scales up until it touches them.
        [InlineData(10, 10, 200, 100, 50, 0, 100, 100)]
        [InlineData(2, 4, 300, 300, 75, 0, 150, 300)]
        // Wider and taller than the bounds' shape.
        [InlineData(100, 10, 200, 100, 0, 40, 200, 20)]
        [InlineData(1000, 2000, 100, 100, 25, 0, 50, 100)]
        // The same shape fills them.
        [InlineData(400, 300, 80, 60, 0, 0, 80, 60)]
        public void APicture_TouchesTheBounds_WithItsProportionsKept(int pw, int ph, double bw, double bh, double x, double y, double w, double h)
        {
            var at = ScreenOverlayContentView.FitPicture(pw, ph, new Size(bw, bh));
            Assert.Equal(x, at.X, 6);
            Assert.Equal(y, at.Y, 6);
            Assert.Equal(w, at.Width, 6);
            Assert.Equal(h, at.Height, 6);
            Assert.Equal((double)pw / ph, at.Width / at.Height, 6);
        }

        /// <summary>WPF's own Stretch.Uniform lands on the same size.</summary>
        [Theory]
        [InlineData(10, 10, 200, 100)]
        [InlineData(2, 4, 300, 300)]
        [InlineData(100, 10, 200, 100)]
        [InlineData(1000, 2000, 100, 100)]
        [InlineData(400, 300, 80, 60)]
        public void TheFit_MatchesWpfsOwnUniformStretch(int pw, int ph, double bw, double bh)
            => OnSta(() =>
            {
                var image = new System.Windows.Controls.Image
                {
                    Source = Bitmap(pw, ph, Opaque),
                    Stretch = Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                };
                image.Measure(new Size(bw, bh));
                var at = ScreenOverlayContentView.FitPicture(pw, ph, new Size(bw, bh));
                Assert.Equal(image.DesiredSize.Width, at.Width, 6);
                Assert.Equal(image.DesiredSize.Height, at.Height, 6);
            });

        [Fact]
        public void APictureDrawn_FillsTheBoundsAndNothingBeyondIt()
            => OnSta(() =>
            {
                var picture = Bitmap(10, 10, Opaque);
                picture.Freeze();
                var view = new ScreenOverlayContentView();
                view.Show(new ScreenOverlayContent(Picture: picture));
                var drawn = Rendered(view, 200, 100);
                Assert.Equal(255, PixelOf(drawn, 100, 50).R);
                Assert.Equal(255, PixelOf(drawn, 55, 2).A);
                Assert.Equal(255, PixelOf(drawn, 145, 97).A);
                Assert.Equal(0, PixelOf(drawn, 10, 50).A);
                Assert.Equal(0, PixelOf(drawn, 190, 50).A);
            });

        [Fact]
        public void APicturesTransparency_ShowsThroughWhenDrawn()
            => OnSta(() =>
            {
                var picture = Bitmap(20, 20, (x, _) => x < 10 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255, (byte)255));
                picture.Freeze();
                var view = new ScreenOverlayContentView();
                view.Show(new ScreenOverlayContent(Picture: picture));
                var drawn = Rendered(view, 100, 100);
                Assert.Equal(0, PixelOf(drawn, 20, 50).A);
                Assert.Equal(255, PixelOf(drawn, 80, 50).A);
            });

        [Fact]
        public void AColor_FillsTheBounds_AndNothingDrawsNothing()
            => OnSta(() =>
            {
                var view = new ScreenOverlayContentView();
                view.Show(new ScreenOverlayContent(Fill: Color.FromRgb(1, 2, 3)));
                var drawn = Rendered(view, 50, 40);
                Assert.Equal(((byte)3, (byte)2, (byte)1, (byte)255), PixelOf(drawn, 0, 0));
                Assert.Equal(((byte)3, (byte)2, (byte)1, (byte)255), PixelOf(drawn, 49, 39));

                view.Show(null);
                Assert.True(view.Shown.IsEmpty);
                Assert.Equal(0, PixelOf(Rendered(view, 51, 41), 25, 20).A);
            });

        [Fact]
        public void Text_IsWhiteOnABlackFieldThatFillsTheBounds()
            => OnSta(() =>
            {
                var view = new ScreenOverlayContentView();
                view.Show(new ScreenOverlayContent("12:34"));
                var drawn = Rendered(view, 300, 150);
                Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), PixelOf(drawn, 0, 0));
                Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), PixelOf(drawn, 299, 149));
                bool white = false;
                for (int x = 0; x < 300 && !white; x++)
                    for (int y = 0; y < 150 && !white; y++)
                        white = PixelOf(drawn, x, y).R > 200;
                Assert.True(white);
            });

        [Fact]
        public void Text_GrowsWithItsBounds_AndWrapsToStayLarge()
            => OnSta(() =>
            {
                var small = ScreenOverlayContentView.FitText("12:34", new Size(100, 60), 1.0);
                var large = ScreenOverlayContentView.FitText("12:34", new Size(800, 480), 1.0);
                Assert.True(large.Text.Width > small.Text.Width * 7, large.Text.Width + " against " + small.Text.Width);
                Assert.True(large.Text.Width <= 800 - 2 * 40);

                // Words wrap, RenderText's rule: on one line these would be far smaller.
                const string words = "A Rather Long Profile Name";
                var wrapped = ScreenOverlayContentView.FitText(words, new Size(200, 200), 1.0);
                Assert.True(wrapped.Text.Height <= 200 - 2 * 10);
                Assert.True(wrapped.Text.MinWidth <= 200 - 2 * 10);
                var oneLine = ScreenOverlayContentView.FitText(words, new Size(2000, 40), 1.0);
                Assert.True(wrapped.Text.Baseline > oneLine.Text.Baseline * 0.8, wrapped.Text.Baseline + " against " + oneLine.Text.Baseline);

                // A word too long to fit a line shrinks until it fits.
                var word = ScreenOverlayContentView.FitText("Supercalifragilisticexpialidocious", new Size(120, 240), 1.0);
                Assert.True(word.Text.MinWidth <= 120 - 2 * 6);
            });

        [Fact]
        public void TheSameContent_IsNotDrawnAgain()
            => OnSta(() =>
            {
                var view = new ScreenOverlayContentView();
                var first = new ScreenOverlayContent("12:34");
                view.Show(first);
                view.Show(new ScreenOverlayContent("12:34"));
                Assert.Same(first, view.Shown);
                var next = new ScreenOverlayContent("12:35");
                view.Show(next);
                Assert.Same(next, view.Shown);
            });

        // ── The window ──

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint WM_MOUSEACTIVATE = 0x0021;
        private const uint GW_HWNDNEXT = 2;
        private const uint GW_HWNDPREV = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        private static IntPtr Handle(Window window) => new WindowInteropHelper(window).EnsureHandle();

        private static bool IsAbove(IntPtr upper, IntPtr lower)
        {
            for (var at = GetWindow(lower, GW_HWNDPREV); at != IntPtr.Zero; at = GetWindow(at, GW_HWNDPREV))
                if (at == upper) return true;
            return false;
        }

        [Fact]
        public void TheWindow_CarriesItsStyles_BeforeItFirstShows()
            => OnSta(() =>
            {
                var window = new ScreenOverlay();
                try
                {
                    Assert.False(window.ShowActivated);
                    Assert.False(window.Focusable);
                    Assert.False(window.ShowInTaskbar);
                    Assert.True(window.Topmost);
                    Assert.True(window.AllowsTransparency);
                    Assert.Equal(WindowStyle.None, window.WindowStyle);
                    Assert.Equal(80, window.MinWidth);
                    Assert.Equal(60, window.MinHeight);

                    int style = GetWindowLong(Handle(window), GWL_EXSTYLE);
                    Assert.NotEqual(0, style & WS_EX_NOACTIVATE);
                    Assert.NotEqual(0, style & WS_EX_TOOLWINDOW);
                    Assert.NotEqual(0, style & WS_EX_LAYERED);
                    Assert.NotEqual(0, style & WS_EX_TRANSPARENT);
                    Assert.Equal((IntPtr)3, SendMessage(Handle(window), WM_MOUSEACTIVATE, IntPtr.Zero, IntPtr.Zero));
                }
                finally { window.Close(); }
            });

        [Fact]
        public void Arranging_TakesTheMouse_AndShowsTheOutlineAndGrip()
            => OnSta(() =>
            {
                var window = new ScreenOverlay();
                try
                {
                    IntPtr hwnd = Handle(window);
                    Assert.False(window.Arranging);
                    Assert.Null(window.Surface.Background);
                    Assert.Equal(Visibility.Collapsed, window.ResizeGrip.Visibility);

                    window.SetArranging(true);
                    Assert.True(window.Arranging);
                    Assert.Equal(0, GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT);
                    Assert.Equal(1, ((SolidColorBrush)window.Surface.Background).Color.A);
                    Assert.Equal(Visibility.Visible, window.ResizeGrip.Visibility);
                    Assert.Equal(Visibility.Visible, window.EmptyHint.Visibility);

                    window.ShowContent(new ScreenOverlayContent("12:34"));
                    Assert.Equal(Visibility.Collapsed, window.EmptyHint.Visibility);
                    window.ShowContent(ScreenOverlayContent.Nothing);
                    Assert.Equal(Visibility.Visible, window.EmptyHint.Visibility);

                    window.SetArranging(false);
                    Assert.False(window.Arranging);
                    Assert.NotEqual(0, GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT);
                    Assert.Null(window.Surface.Background);
                    Assert.Null(window.Surface.BorderBrush);
                    Assert.Equal(Visibility.Collapsed, window.ResizeGrip.Visibility);
                    Assert.Equal(Visibility.Collapsed, window.EmptyHint.Visibility);
                }
                finally { window.Close(); }
            });

        private static void SetPrivate(object target, string field, object value)
            => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        private static object GetPrivate(object target, string field)
            => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        [Fact]
        public void ADragInProgress_FinishesInTheStateItBeganIn()
            => OnSta(() =>
            {
                var window = new ScreenOverlay();
                try
                {
                    IntPtr hwnd = Handle(window);
                    window.SetArranging(true);
                    SetPrivate(window, "_isMouseDragging", true);
                    window.SetArranging(false);
                    Assert.True(window.Arranging);
                    Assert.Equal(0, GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT);
                    SetPrivate(window, "_isMouseDragging", false);
                    SetPrivate(window, "_isResizing", true);
                    window.SetArranging(false);
                    Assert.True(window.Arranging);
                    SetPrivate(window, "_isResizing", false);
                    SetPrivate(window, "_dragTouchId", (int?)4);
                    window.SetArranging(false);
                    Assert.True(window.Arranging);
                    SetPrivate(window, "_dragTouchId", null);
                    window.SetArranging(false);
                    Assert.False(window.Arranging);
                }
                finally { window.Close(); }
            });

        /// <summary>Windows routes a click by the window at its point: while another
        /// program is in front it finds the window beneath the overlay, and while
        /// PadForge is in front it finds the overlay, the positive control in the
        /// same place on the same two windows. The overlay holds an opaque color, since
        /// a layered window passes clicks through a pixel with no alpha whatever its
        /// style. An empty overlay proved nothing here: with the style gone it still
        /// passed.</summary>
        [Fact]
        public void AClick_ReachesTheWindowBeneath_UnlessArranging()
            => OnSta(() =>
            {
                const double left = -3000, top = -3000, side = 300;
                var beneath = new Window
                {
                    Left = left, Top = top, Width = side, Height = side, WindowStyle = WindowStyle.None,
                    ShowActivated = false, ShowInTaskbar = false,
                };
                var overlay = new ScreenOverlay { Left = left, Top = top, Width = side, Height = side };
                try
                {
                    overlay.ShowContent(new ScreenOverlayContent(Fill: Colors.Red));
                    overlay.SetContentOpacity(1);
                    beneath.Show();
                    overlay.Show();
                    Pump(300);
                    var at = overlay.PointToScreen(new Point(side / 2, side / 2));
                    var point = new POINT { X = (int)at.X, Y = (int)at.Y };

                    overlay.SetArranging(true);
                    Pump(300);
                    Assert.Equal(Handle(overlay), WindowFromPoint(point));

                    overlay.SetArranging(false);
                    Pump(100);
                    Assert.Equal(Handle(beneath), WindowFromPoint(point));

                    overlay.SetArranging(true);
                    Pump(300);
                    Assert.Equal(Handle(overlay), WindowFromPoint(point));
                }
                finally
                {
                    overlay.Close();
                    beneath.Close();
                }
            });

        /// <summary>g-helper's check: back on top once another program's window has
        /// gone above it, and only while another program is in front. PadForge's own
        /// topmost windows stay above it. The test names the window that stands for
        /// a game, since other programs' topmost windows sit on this desktop too.</summary>
        [Fact]
        public void KeepOnTop_GoesBackAboveAWindowThatMadeItselfTopmost()
            => OnSta(() =>
            {
                var overlay = new ScreenOverlay { Left = -3000, Top = -3000 };
                var game = new Window
                {
                    Left = -3000, Top = -3000, Width = 100, Height = 100, WindowStyle = WindowStyle.None,
                    ShowActivated = false, ShowInTaskbar = false, Topmost = true,
                };
                try
                {
                    overlay.Show();
                    game.Show();
                    Pump(100);
                    Assert.True(IsAbove(Handle(game), Handle(overlay)), "the second topmost window goes above the first");

                    bool Ours(IntPtr hwnd) => hwnd != Handle(game);

                    overlay.SetArranging(true);
                    overlay.KeepOnTop(Ours);
                    Assert.True(IsAbove(Handle(game), Handle(overlay)), "arranging leaves everything where it is");

                    overlay.SetArranging(false);
                    overlay.KeepOnTop(_ => true);
                    Assert.True(IsAbove(Handle(game), Handle(overlay)), "PadForge's own topmost window stays above it");

                    game.Hide();
                    overlay.KeepOnTop(Ours);
                    Assert.True(IsAbove(Handle(game), Handle(overlay)), "a hidden window does not count");

                    game.Show();
                    overlay.KeepOnTop(Ours);
                    Assert.True(IsAbove(Handle(overlay), Handle(game)));
                    Assert.NotEqual(0, GetWindowLong(Handle(overlay), GWL_EXSTYLE) & WS_EX_TOPMOST);

                    Assert.True(ScreenOverlay.IsOurWindow(Handle(game)));
                    Assert.False(ScreenOverlay.IsOurWindow(GetDesktopWindow()));
                }
                finally
                {
                    game.Close();
                    overlay.Close();
                }
            });

        [Fact]
        public void Opacity_LandsOnTheContentAlone()
            => OnSta(() =>
            {
                var window = new ScreenOverlay();
                try
                {
                    window.SetContentOpacity(0.25);
                    Assert.Equal(0.25, window.ContentView.Opacity);
                    Assert.Equal(1.0, window.Opacity);
                    Assert.Equal(1.0, window.Surface.Opacity);
                    Assert.Equal(1.0, window.ResizeGrip.Opacity);
                    window.SetContentOpacity(-3);
                    Assert.Equal(0.0, window.ContentView.Opacity);
                    window.SetContentOpacity(9);
                    Assert.Equal(1.0, window.ContentView.Opacity);
                }
                finally { window.Close(); }
            });

        private static void LoseCapture(UIElement element)
            => element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });

        /// <summary>A capture taken away mid-drag never delivers the button's
        /// release, and the window would follow the pointer until the next click.</summary>
        [Fact]
        public void ALostCapture_EndsADragOrResize_OnBothOverlays()
            => OnSta(() =>
            {
                var screen = new ScreenOverlay();
                var touchpad = new TouchpadOverlay();
                try
                {
                    int screenSaves = 0, touchpadSaves = 0;
                    screen.PositionChanged += () => screenSaves++;
                    touchpad.PositionChanged += () => touchpadSaves++;

                    SetPrivate(screen, "_isMouseDragging", true);
                    LoseCapture(screen.Surface);
                    Assert.False((bool)GetPrivate(screen, "_isMouseDragging"));
                    Assert.Equal(1, screenSaves);
                    LoseCapture(screen.Surface);
                    Assert.Equal(1, screenSaves);

                    SetPrivate(screen, "_isResizing", true);
                    LoseCapture(screen.ResizeGrip);
                    Assert.False((bool)GetPrivate(screen, "_isResizing"));
                    Assert.Equal(2, screenSaves);

                    SetPrivate(touchpad, "_isMouseDragging", true);
                    LoseCapture(touchpad.Surface);
                    Assert.False((bool)GetPrivate(touchpad, "_isMouseDragging"));
                    Assert.Equal(1, touchpadSaves);

                    SetPrivate(touchpad, "_isResizing", true);
                    LoseCapture(touchpad.ResizeGrip);
                    Assert.False((bool)GetPrivate(touchpad, "_isResizing"));
                    Assert.Equal(2, touchpadSaves);
                }
                finally
                {
                    screen.Close();
                    touchpad.Close();
                }
            });

        /// <summary>Show activates a window by default, and the Touchpad Overlay's
        /// no-activate style only went on once it had loaded, so the first toggle
        /// during a game could take the game's focus.</summary>
        [Fact]
        public void TheTouchpadOverlay_ShowsWithoutActivating()
            => OnSta(() =>
            {
                var window = new TouchpadOverlay();
                try { Assert.False(window.ShowActivated); }
                finally { window.Close(); }
            });

        /// <summary>A monitor left of or above the primary has negative coordinates.
        /// The Touchpad Overlay read those as no saved place.</summary>
        [Fact]
        public void TheTouchpadOverlay_KeepsASavedPlaceWithNegativeCoordinates()
        {
            string service = RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("if (dash.TouchpadOverlayLeft != -1 || dash.TouchpadOverlayTop != -1)", service);
            Assert.DoesNotContain("dash.TouchpadOverlayLeft >= 0", service);
        }

        // ── Placement ──

        [Fact]
        public void Placement_CentersOnAMonitor_AndAWindowNoMonitorShowsComesBack()
            => OnSta(() =>
            {
                var window = new Window { Width = 200, Height = 100, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
                try
                {
                    OverlayPlacement.MoveToMonitor(window, 0);
                    double left = window.Left, top = window.Top;
                    OverlayPlacement.MoveToMonitor(window, 999);
                    AssertNear(left, window.Left);
                    AssertNear(top, window.Top);
                    OverlayPlacement.MoveToMonitor(window, -1);
                    AssertNear(left, window.Left);

                    Handle(window);
                    Assert.Equal(0, OverlayPlacement.GetCurrentMonitor(window));

                    // On a monitor, it stays where it is.
                    OverlayPlacement.EnsureOnScreen(window, 0);
                    AssertNear(left, window.Left);

                    window.Left = -30000;
                    window.Top = -30000;
                    OverlayPlacement.EnsureOnScreen(window, 0);
                    AssertNear(left, window.Left);
                    AssertNear(top, window.Top);

                    Assert.True(OverlayPlacement.HasPlace(-1920, -20));
                    Assert.True(OverlayPlacement.HasPlace(0, 0));
                    Assert.False(OverlayPlacement.HasPlace(OverlayPlacement.NoPlace, 0));
                    Assert.False(OverlayPlacement.HasPlace(0, OverlayPlacement.NoPlace));
                }
                finally { window.Close(); }
            });

        // ── The view model ──

        [Fact]
        public void TheViewModel_HoldsTheOverlaysValuesInRange()
        {
            var vm = new DashboardViewModel();
            Assert.False(vm.EnableScreenOverlay);
            Assert.Equal(0.8, vm.ScreenOverlayOpacity);
            Assert.Equal(80, vm.ScreenOverlayOpacityPercent);
            Assert.Equal(OverlayPlacement.NoPlace, vm.ScreenOverlayLeft);
            Assert.Equal(OverlayPlacement.NoPlace, vm.ScreenOverlayTop);
            Assert.Equal(320, vm.ScreenOverlayWidth);
            Assert.Equal(240, vm.ScreenOverlayHeight);

            vm.ScreenOverlayOpacityPercent = 35;
            Assert.Equal(0.35, vm.ScreenOverlayOpacity, 6);
            vm.ScreenOverlayOpacity = 4;
            Assert.Equal(1.0, vm.ScreenOverlayOpacity);
            Assert.Equal(100, vm.ScreenOverlayOpacityPercent);
            vm.ScreenOverlayOpacity = -1;
            Assert.Equal(0.0, vm.ScreenOverlayOpacity);
            vm.ResetScreenOverlayOpacityCommand.Execute(null);
            Assert.Equal(0.8, vm.ScreenOverlayOpacity);

            vm.ScreenOverlayWidth = 5;
            vm.ScreenOverlayHeight = 5;
            Assert.Equal(80, vm.ScreenOverlayWidth);
            Assert.Equal(60, vm.ScreenOverlayHeight);

            vm.ScreenOverlayLeft = -1920;
            Assert.Equal(-1920, vm.ScreenOverlayLeft);
            vm.ScreenOverlayLeft = OverlayPlacement.NoPlace;
            Assert.Equal(OverlayPlacement.NoPlace, vm.ScreenOverlayLeft);

            Assert.Equal(Strings.Instance.Common_Stopped, vm.ScreenOverlayStatus);
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            vm.IsScreenOverlayRunning = true;
            Assert.Equal(Strings.Instance.Common_Running, vm.ScreenOverlayStatus);
            Assert.Contains(nameof(DashboardViewModel.ScreenOverlayStatus), changed);

            int resets = 0, chooses = 0;
            vm.ResetScreenOverlayPositionRequested += (_, _) => resets++;
            vm.ChooseScreenOverlayContentRequested += (_, _) => chooses++;
            vm.ResetScreenOverlayPositionCommand.Execute(null);
            vm.ChooseScreenOverlayContentCommand.Execute(null);
            Assert.Equal(1, resets);
            Assert.Equal(1, chooses);
        }

        // ── Settings ──

        private static readonly (string Touchpad, string Screen)[] SettingNames =
        {
            ("EnableTouchpadOverlay", "EnableScreenOverlay"),
            ("TouchpadOverlayOpacity", "ScreenOverlayOpacity"),
            ("TouchpadOverlayMonitor", "ScreenOverlayMonitor"),
            ("TouchpadOverlayLeft", "ScreenOverlayLeft"),
            ("TouchpadOverlayTop", "ScreenOverlayTop"),
            ("TouchpadOverlayWidth", "ScreenOverlayWidth"),
            ("TouchpadOverlayHeight", "ScreenOverlayHeight"),
        };

        /// <summary>Every place a Touchpad Overlay value is read or written has its
        /// Screen Overlay value beside it.</summary>
        [Fact]
        public void EverySettingSite_HasItsSibling()
        {
            string settings = RepoText("PadForge.App", "Services", "SettingsService.cs");
            string window = RepoText("PadForge.App", "MainWindow.xaml.cs");
            string service = RepoText("PadForge.App", "Services", "InputService.cs");
            foreach (var (touchpad, screen) in SettingNames)
            {
                Assert.Equal(CountOf(settings, touchpad), CountOf(settings, screen));
                Assert.Equal(CountOf(window, touchpad), CountOf(window, screen));
                Assert.Equal(1, CountOf(service, screen + " = _mainVm.Dashboard." + screen + ","));
                Assert.Equal(1, CountOf(service, "profile." + screen + " = snapshot." + screen + ";"));
                Assert.Equal(1, CountOf(service, "_mainVm.Dashboard." + screen + " = profile." + screen));
            }
        }

        private static T RoundTrip<T>(T data)
        {
            var serializer = new XmlSerializer(typeof(T));
            using var writer = new StringWriter();
            serializer.Serialize(writer, data);
            return (T)serializer.Deserialize(new StringReader(writer.ToString()));
        }

        [Fact]
        public void TheSettings_RoundTrip_AndNoSavedPlaceStaysNoSavedPlace()
        {
            var app = RoundTrip(new AppSettingsData());
            Assert.False(app.EnableScreenOverlay);
            Assert.Equal(0.8, app.ScreenOverlayOpacity);
            Assert.Equal(OverlayPlacement.NoPlace, app.ScreenOverlayLeft);
            Assert.Equal(OverlayPlacement.NoPlace, app.ScreenOverlayTop);
            Assert.Equal(320, app.ScreenOverlayWidth);
            Assert.Equal(240, app.ScreenOverlayHeight);

            var placed = RoundTrip(new AppSettingsData
            {
                EnableScreenOverlay = true, ScreenOverlayOpacity = 0.4, ScreenOverlayMonitor = 2,
                ScreenOverlayLeft = -1500.5, ScreenOverlayTop = -20, ScreenOverlayWidth = 640, ScreenOverlayHeight = 480,
            });
            Assert.True(placed.EnableScreenOverlay);
            Assert.Equal(0.4, placed.ScreenOverlayOpacity);
            Assert.Equal(2, placed.ScreenOverlayMonitor);
            Assert.Equal(-1500.5, placed.ScreenOverlayLeft);
            Assert.Equal(-20, placed.ScreenOverlayTop);
            Assert.Equal(640, placed.ScreenOverlayWidth);
            Assert.Equal(480, placed.ScreenOverlayHeight);

            var profile = RoundTrip(new ProfileData { Id = "p", EnableScreenOverlay = true, ScreenOverlayLeft = -3, ScreenOverlayHeight = 90 });
            Assert.True(profile.EnableScreenOverlay);
            Assert.Equal(-3, profile.ScreenOverlayLeft);
            Assert.Equal(OverlayPlacement.NoPlace, profile.ScreenOverlayTop);
            Assert.Equal(90, profile.ScreenOverlayHeight);

            // A file saved before these fields existed.
            var old = (ProfileData)new XmlSerializer(typeof(ProfileData)).Deserialize(new StringReader("<ProfileData Id=\"old\"><Name>Old</Name></ProfileData>"));
            Assert.False(old.EnableScreenOverlay);
            Assert.Equal(0.8, old.ScreenOverlayOpacity);
            Assert.Equal(OverlayPlacement.NoPlace, old.ScreenOverlayLeft);
            Assert.Equal(320, old.ScreenOverlayWidth);
        }

        private sealed class SavedStatics : IDisposable
        {
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly List<ProfileData> _profiles = SettingsManager.Profiles;
            private readonly string _active = SettingsManager.ActiveProfileId;
            private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
            private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
            private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();
            private readonly List<int> _xbox = SettingsManager.XboxSlotOrder;
            private readonly List<int> _playStation = SettingsManager.PlayStationSlotOrder;
            private readonly List<int> _extended = SettingsManager.ExtendedSlotOrder;
            private readonly List<int> _kbm = SettingsManager.KeyboardMouseSlotOrder;
            private readonly List<int> _midi = SettingsManager.MidiSlotOrder;

            public SavedStatics()
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.Profiles = new List<ProfileData>();
                SettingsManager.ActiveProfileId = null;
                SettingsManager.PendingDefaultSnapshot = null;
                Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
                Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
                SettingsManager.SlotCreated[0] = true;
                SettingsManager.SlotEnabled[0] = true;
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                SettingsManager.XboxSlotOrder = new List<int> { 0 };
                SettingsManager.PlayStationSlotOrder = new List<int>();
                SettingsManager.ExtendedSlotOrder = new List<int>();
                SettingsManager.KeyboardMouseSlotOrder = new List<int>();
                SettingsManager.MidiSlotOrder = new List<int>();
            }

            public void Dispose()
            {
                SettingsManager.UserDevices = _devices;
                SettingsManager.UserSettings = _settings;
                SettingsManager.Profiles = _profiles;
                SettingsManager.ActiveProfileId = _active;
                SettingsManager.PendingDefaultSnapshot = null;
                SettingsManager.SlotMappingSets = _sets;
                Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
                Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
                SettingsManager.XboxSlotOrder = _xbox;
                SettingsManager.PlayStationSlotOrder = _playStation;
                SettingsManager.ExtendedSlotOrder = _extended;
                SettingsManager.KeyboardMouseSlotOrder = _kbm;
                SettingsManager.MidiSlotOrder = _midi;
                KeyboardScreensRuntime.OverlayEnabled = false;
            }
        }

        private static void SetOverlayValues(DashboardViewModel dash)
        {
            dash.EnableScreenOverlay = true;
            dash.ScreenOverlayOpacity = 0.4;
            dash.ScreenOverlayMonitor = 1;
            dash.ScreenOverlayLeft = -1500;
            dash.ScreenOverlayTop = 30;
            dash.ScreenOverlayWidth = 640;
            dash.ScreenOverlayHeight = 480;
        }

        private static void AssertOverlayValues(DashboardViewModel dash)
        {
            Assert.True(dash.EnableScreenOverlay);
            Assert.Equal(0.4, dash.ScreenOverlayOpacity);
            Assert.Equal(1, dash.ScreenOverlayMonitor);
            Assert.Equal(-1500, dash.ScreenOverlayLeft);
            Assert.Equal(30, dash.ScreenOverlayTop);
            Assert.Equal(640, dash.ScreenOverlayWidth);
            Assert.Equal(480, dash.ScreenOverlayHeight);
        }

        [Fact]
        public void TheGlobalSettings_SurviveTheActualSaveAndLoad()
            => OnSta(() =>
            {
                using var statics = new SavedStatics();
                var vm = new MainViewModel();
                var settings = new SettingsService(vm);
                SetOverlayValues(vm.Dashboard);
                var built = (AppSettingsData)typeof(SettingsService)
                    .GetMethod("BuildAppSettings", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(settings, null);
                var loaded = RoundTrip(built);
                var fresh = new MainViewModel();
                typeof(SettingsService).GetMethod("LoadAppSettings", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(new SettingsService(fresh), new object[] { loaded });
                AssertOverlayValues(fresh.Dashboard);
            });

        [Fact]
        public void AProfile_CarriesTheOverlay_ThroughBothServices()
            => OnSta(() =>
            {
                using var statics = new SavedStatics();
                var vm = new MainViewModel();
                var settings = new SettingsService(vm);
                var input = new InputService(vm) { SettingsService = settings };
                SetOverlayValues(vm.Dashboard);

                var snapshot = input.SnapshotCurrentProfile();
                Assert.True(snapshot.EnableScreenOverlay);
                Assert.Equal(-1500, snapshot.ScreenOverlayLeft);
                Assert.Equal(480, snapshot.ScreenOverlayHeight);

                var fresh = new MainViewModel();
                var freshInput = new InputService(fresh) { SettingsService = new SettingsService(fresh) };
                freshInput.ApplyProfile(snapshot);
                AssertOverlayValues(fresh.Dashboard);
                Assert.True(KeyboardScreensRuntime.OverlayEnabled);

                // A profile from before the overlay has no place and turns it off.
                freshInput.ApplyProfile(new ProfileData { Id = "old", Name = "Old" });
                Assert.False(fresh.Dashboard.EnableScreenOverlay);
                Assert.Equal(OverlayPlacement.NoPlace, fresh.Dashboard.ScreenOverlayLeft);
                Assert.Equal(320, fresh.Dashboard.ScreenOverlayWidth);
                Assert.Equal(240, fresh.Dashboard.ScreenOverlayHeight);

                // The active profile keeps what the Dashboard holds.
                var active = new ProfileData { Id = "p1", Name = "Game" };
                SettingsManager.Profiles.Add(active);
                SettingsManager.ActiveProfileId = "p1";
                settings.UpdateActiveProfileSnapshot();
                Assert.True(active.EnableScreenOverlay);
                Assert.Equal(0.4, active.ScreenOverlayOpacity);
                Assert.Equal(-1500, active.ScreenOverlayLeft);
                Assert.Equal(640, active.ScreenOverlayWidth);

                // And the running profile's save does too.
                SetOverlayValues(fresh.Dashboard);
                SettingsManager.Profiles.Clear();
                var running = new ProfileData { Id = "p2", Name = "Other" };
                SettingsManager.Profiles.Add(running);
                SettingsManager.ActiveProfileId = "p2";
                freshInput.SaveActiveProfileState();
                Assert.True(running.EnableScreenOverlay);
                Assert.Equal(-1500, running.ScreenOverlayLeft);
                Assert.Equal(480, running.ScreenOverlayHeight);
            });

        // ── The lifecycle on the real service ──

        private static T Invoke<T>(object target, string method, params object[] args)
            => (T)target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);

        [Fact]
        public void TheService_ShowsPlacesTicksAndClosesTheWindow()
            => OnSta(() =>
            {
                using var statics = new SavedStatics();
                var vm = new MainViewModel();
                var input = new InputService(vm) { SettingsService = new SettingsService(vm) };
                var dash = vm.Dashboard;
                dash.ScreenOverlayOpacity = 0.5;
                dash.ScreenOverlayWidth = 200;
                dash.ScreenOverlayHeight = 120;

                Invoke<object>(input, "ShowScreenOverlay");
                Pump(50);
                var window = (ScreenOverlay)GetPrivate(input, "_screenOverlay");
                try
                {
                    Assert.True(window.IsVisible);
                    Assert.True(dash.IsScreenOverlayRunning);
                    Assert.Equal(0.5, window.ContentView.Opacity);
                    Assert.Equal(200, window.Width);
                    Assert.Equal(120, window.Height);
                    // No saved place: centered, on a monitor.
                    Assert.Equal(0, OverlayPlacement.GetCurrentMonitor(window));

                    // The tick feeds the window with the Keyboard Screens switch off, and no host opens.
                    KeyboardScreensRuntime.OverlayEnabled = true;
                    KeyboardScreenService.RequestMode(5, KeyboardScreenKind.Overlay, KeyboardScreenMode.Clock);
                    Invoke<object>(input, "TickKeyboardScreens");
                    Assert.False(input.KeyboardScreens.Running);
                    Assert.Matches("^\\d\\d:\\d\\d$", window.ContentView.Shown.Text);

                    // Another program is in front of the test host, so clicks pass through.
                    Assert.False(window.Arranging);

                    // A drag lands in the settings, and the window stays where the drag put it.
                    double left = window.Left + 7, top = window.Top + 5;
                    window.Left = left;
                    window.Top = top;
                    window.Width = 300;
                    window.Height = 150;
                    Invoke<object>(input, "OnScreenOverlayPositionChanged");
                    Pump(50);
                    AssertNear(left, dash.ScreenOverlayLeft);
                    AssertNear(top, dash.ScreenOverlayTop);
                    Assert.Equal(300, dash.ScreenOverlayWidth);
                    Assert.Equal(150, dash.ScreenOverlayHeight);
                    AssertNear(left, window.Left);
                    AssertNear(top, window.Top);

                    // A saved place with negative coordinates is a place.
                    dash.ScreenOverlayLeft = -640;
                    dash.ScreenOverlayTop = -20;
                    Invoke<object>(input, "PlaceScreenOverlay");
                    AssertNear(-640, window.Left);
                    AssertNear(-20, window.Top);

                    // A profile switch moves a window that is up.
                    dash.ScreenOverlayLeft = left + 1;
                    dash.ScreenOverlayTop = top + 1;
                    Invoke<object>(input, "ScreenOverlayPlaceChanged");
                    Pump(50);
                    AssertNear(left + 1, window.Left);
                    AssertNear(top + 1, window.Top);

                    // Reset Position recenters a window that is up, and saves the place.
                    object sender = null;
                    Invoke<object>(input, "OnResetScreenOverlayPosition", sender, EventArgs.Empty);
                    Pump(50);
                    Assert.Equal(window.Left, dash.ScreenOverlayLeft);
                    Assert.True(Math.Abs(window.Left - (left + 1)) > 3);

                    Invoke<object>(input, "HideScreenOverlay");
                    Pump(50);
                    Assert.False(window.IsVisible);
                    Assert.False(dash.IsScreenOverlayRunning);

                    // Hidden, Reset Position clears the place for the next show.
                    Invoke<object>(input, "OnResetScreenOverlayPosition", sender, EventArgs.Empty);
                    Pump(50);
                    Assert.Equal(OverlayPlacement.NoPlace, dash.ScreenOverlayLeft);
                    Assert.Equal(OverlayPlacement.NoPlace, dash.ScreenOverlayTop);

                    // With both off, a tick ends what the overlay held.
                    Assert.True(input.KeyboardScreens.Active);
                    Invoke<object>(input, "TickKeyboardScreens");
                    Assert.False(input.KeyboardScreens.Active);

                    // The macro's toggle flips the Dashboard's switch.
                    input.ToggleScreenOverlay();
                    Pump(50);
                    Assert.True(dash.EnableScreenOverlay);
                    input.ToggleScreenOverlay();
                    Pump(50);
                    Assert.False(dash.EnableScreenOverlay);

                    Invoke<object>(input, "ShowScreenOverlay");
                    Pump(50);
                    Assert.True(window.IsVisible);
                    Assert.NotNull(input.KeyboardScreens.OverlaySink);
                }
                finally
                {
                    Invoke<object>(input, "CloseScreenOverlay");
                }
                Assert.Null(GetPrivate(input, "_screenOverlay"));
                Assert.False(dash.IsScreenOverlayRunning);
                Assert.Null(input.KeyboardScreens.OverlaySink);
                KeyboardScreenService.DropRequests();
            });

        /// <summary>The window's own file keeps its grip in place in every language.</summary>
        [Fact]
        public void TheWindow_StaysLeftToRight_InEveryLanguage()
            => Assert.Contains("PadForge.Common.UiLanguage.KeepLeftToRight(this);",
                RepoText("PadForge.App", "Views", "ScreenOverlay.xaml.cs"));
    }
}
