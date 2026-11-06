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
using PadForge.Engine;
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
            Assert.Equal(KeyboardScreenInfo.OverlayRow(2), KeyboardScreenInfo.Overlay(2).RowGuid);
            Assert.NotEqual(KeyboardScreenInfo.OverlayRow(1), KeyboardScreenInfo.OverlayRow(2));
            Assert.Equal(KeyboardScreenKind.Overlay, KeyboardScreenInfo.Overlay(2).Kind);
            Assert.Equal(2, KeyboardScreenInfo.Overlay(2).OverlayNumber);
            Assert.Equal(0, KeyboardScreenInfo.Arx().OverlayNumber);
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

        /// <summary>An overlay is a window and no device. Nothing makes a row of it on
        /// the Devices page, and a macro reaches it from any slot, with nothing fed.</summary>
        [Fact]
        public void AnOverlay_IsNoDevice()
        {
            Assert.DoesNotContain("Overlay", RepoText("PadForge.App", "Common", "Input", "KeyboardScreenDevice.cs"));
            string rows = RepoText("PadForge.App", "Common", "Input", "InputManager.KeyboardScreens.cs");
            Assert.Contains("KeyboardScreensRuntime.Screens", rows);
            Assert.DoesNotContain("KeyboardScreenInfo.Overlay(", rows);
            Assert.DoesNotContain("OverlayRow", rows);
            string service = RepoText("PadForge.App", "Services", "KeyboardScreenService.cs");
            int at = service.IndexOf("private List<OverlayScreen> OverlaysFor", StringComparison.Ordinal);
            Assert.True(at > 0);
            Assert.DoesNotContain("SlotsFed", service.Substring(at, 300));
        }

        /// <summary>A language switch makes a new list of overlays, which clears a
        /// list box's selection. The macro keeps the overlay it names, and the box goes
        /// back to it.</summary>
        [Fact]
        public void ALanguageSwitch_KeepsTheOverlayAMacroNames()
            => OnSta(() =>
            {
                var action = new MacroAction { Type = MacroActionType.ToggleScreenOverlay, ScreenOverlayNumber = 3 };
                var box = new System.Windows.Controls.ComboBox
                {
                    DataContext = action,
                    ItemsSource = action.ScreenOverlayChoices,
                    DisplayMemberPath = nameof(ScreenOverlayChoice.Display),
                    SelectedValuePath = nameof(ScreenOverlayChoice.Value),
                };
                box.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty,
                    new System.Windows.Data.Binding(nameof(MacroAction.ScreenOverlayNumber)) { Mode = System.Windows.Data.BindingMode.TwoWay });
                Assert.Equal(3, box.SelectedIndex);
                var raised = new List<string>();
                action.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

                // The new language's lines differ, which clears the selection.
                box.ItemsSource = action.ScreenOverlayChoices.Select(line => line with { Display = "x" + line.Display }).ToList();
                Assert.Null(box.SelectedItem);
                Assert.Equal(3, action.ScreenOverlayNumber);

                typeof(MacroAction).GetMethod("OnCultureChanged", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(action, null);

                Assert.True(raised.IndexOf(nameof(MacroAction.ScreenOverlayChoices)) >= 0);
                Assert.True(raised.IndexOf(nameof(MacroAction.ScreenOverlayNumber)) > raised.IndexOf(nameof(MacroAction.ScreenOverlayChoices)));
                Assert.Equal(3, action.ScreenOverlayNumber);
                Assert.Equal(3, box.SelectedIndex);
                Assert.Equal("x" + KeyboardScreenService.OverlayName(3), ((ScreenOverlayChoice)box.SelectedItem).Display);
            });

        /// <summary>The picker for which overlay shows on the actions that name one,
        /// and its list runs from every overlay to the last number.</summary>
        [Fact]
        public void AnAction_CanNameItsOverlay()
        {
            var show = new MacroAction { Type = MacroActionType.ShowKeyboardScreen, ScreenKind = KeyboardScreenKind.Color };
            Assert.False(show.IsScreenOverlayTargetType);
            show.ScreenKind = KeyboardScreenKind.Overlay;
            Assert.True(show.IsScreenOverlayTargetType);
            Assert.True(new MacroAction { Type = MacroActionType.ToggleScreenOverlay }.IsScreenOverlayTargetType);
            Assert.True(new MacroAction { Type = MacroActionType.PanKeyboardScreen }.IsScreenOverlayTargetType);
            Assert.True(new MacroAction { Type = MacroActionType.KeyboardScreenMode, ScreenKind = KeyboardScreenKind.Overlay }.IsScreenOverlayTargetType);
            Assert.False(new MacroAction { Type = MacroActionType.Stopwatch, ScreenKind = KeyboardScreenKind.Overlay }.IsScreenOverlayTargetType);

            Assert.Equal(KeyboardScreenInfo.MaxOverlays + 1, show.ScreenOverlayChoices.Count);
            Assert.Equal(new ScreenOverlayChoice(Strings.Instance.Macro_KeyboardScreen_EveryOverlay, 0), show.ScreenOverlayChoices[0]);
            Assert.Equal(new ScreenOverlayChoice(KeyboardScreenService.OverlayName(3), 3), show.ScreenOverlayChoices[3]);
            show.ScreenOverlayNumber = 99;
            Assert.Equal(KeyboardScreenInfo.MaxOverlays, show.ScreenOverlayNumber);
            show.ScreenOverlayNumber = -4;
            Assert.Equal(0, show.ScreenOverlayNumber);

            show.ScreenOverlayNumber = 2;
            Assert.Contains(KeyboardScreenService.OverlayName(2), show.DisplayText);
            show.ScreenOverlayNumber = 0;
            Assert.Contains(Strings.Instance.KeyboardScreens_Kind_Overlay, show.DisplayText);

            var pan = new MacroAction { Type = MacroActionType.PanKeyboardScreen };
            Assert.DoesNotContain(KeyboardScreenService.OverlayName(2), pan.DisplayText);
            string everyOverlay = pan.DisplayText;
            pan.ScreenOverlayNumber = 2;
            Assert.Contains(KeyboardScreenService.OverlayName(2), pan.DisplayText);
            Assert.Contains(everyOverlay, pan.DisplayText);

            // Through the settings file's record, with the repeat that holds.
            var item = new MacroItem { Name = "icon" };
            item.Actions.Add(new MacroAction { Type = MacroActionType.ShowKeyboardScreen, ScreenKind = KeyboardScreenKind.Overlay, ScreenOverlayNumber = 5, ScreenRepeat = 0 });
            var back = SettingsService.LoadMacroFromData(RoundTrip(SettingsService.BuildMacroDataForMacro(item, 0)), VirtualControllerType.Xbox, null).Actions[0];
            Assert.Equal(5, back.ScreenOverlayNumber);
            Assert.Equal(0, back.ScreenRepeat);
            Assert.Equal(KeyboardScreenKind.Overlay, back.ScreenKind);
        }

        // ── The macro ──

        [Fact]
        public void TheToggle_IsAppended_AndSitsInBothSwitches()
        {
            Assert.Equal(66, (int)MacroActionType.ToggleScreenOverlay);
            Assert.Equal(MacroActionType.ToggleScreenOverlay, Enum.GetValues<MacroActionType>()[^1]);
            string source = RepoText("PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs");
            Assert.Equal(2, CountOf(source, "case MacroActionType.ToggleScreenOverlay:"));
            Assert.Equal(2, CountOf(source, "RequestScreenOverlayToggle(action.ScreenOverlayNumber);"));
            Assert.Equal(Strings.Instance.MacroAction_ToggleScreenOverlay,
                new MacroAction { Type = MacroActionType.ToggleScreenOverlay }.DisplayText);
            Assert.Equal(string.Format(Strings.Instance.MacroAction_ToggleScreenOverlay_Format, 3),
                new MacroAction { Type = MacroActionType.ToggleScreenOverlay, ScreenOverlayNumber = 3 }.DisplayText);
            Assert.Contains("ToggleScreenOverlay, S.Macro_ToggleScreenOverlay", RepoText("PadForge.App", "ViewModels", "MacroTypeCatalog.cs"));
        }

        [Fact]
        public void TheToggle_RaisesItsRequest_AndTheServiceClearsIt()
        {
            string service = RepoText("PadForge.App", "Services", "InputService.cs");
            int at = service.IndexOf("if (Volatile.Read(ref _inputManager.ToggleScreenOverlayMask) != 0)", StringComparison.Ordinal);
            Assert.True(at > 0);
            Assert.Contains("ToggleScreenOverlays(Interlocked.Exchange(ref _inputManager.ToggleScreenOverlayMask, 0));", service.Substring(at, 200));
        }

        // ── The queue ──

        private static readonly Size AnyWindow = new(320, 240);

        private static List<ScreenOverlayContent> Attach(KeyboardScreenService service, int number, Size? bounds = null)
        {
            var shown = new List<ScreenOverlayContent>();
            var size = bounds ?? AnyWindow;
            service.AttachOverlay(number, shown.Add, () => size);
            return shown;
        }

        /// <summary>Runs a body with the macro queue empty, and leaves it empty.</summary>
        private static void WithQueue(Action body)
        {
            KeyboardScreenService.DropRequests();
            try { body(); }
            finally { KeyboardScreenService.DropRequests(); }
        }

        /// <summary>A macro for a keyboard's screen waits on the Keyboard Screens
        /// switch. One for an overlay needs no switch and no window up.</summary>
        [Fact]
        public void AnOverlaysMacro_NeedsNoSwitch()
        {
            KeyboardScreenService.DropRequests();
            bool hardware = KeyboardScreensRuntime.Enabled;
            KeyboardScreensRuntime.Enabled = false;
            try
            {
                KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Color, 0, "", 1000, 1);
                KeyboardScreenService.RequestMode(0, KeyboardScreenKind.Arx, 0, KeyboardScreenMode.Clock);
                Assert.Equal(0, KeyboardScreenService.PendingRequests);

                KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Overlay, 1, "", 1000, 1);
                KeyboardScreenService.RequestMode(0, KeyboardScreenKind.Overlay, 1, KeyboardScreenMode.Clock);
                KeyboardScreenService.RequestStopwatch(KeyboardScreenStopwatchAction.Reset);
                KeyboardScreenService.RequestPan(0, 1, KeyboardScreenPanDirection.Right, 8);
                Assert.Equal(4, KeyboardScreenService.PendingRequests);

                KeyboardScreensRuntime.Enabled = true;
                KeyboardScreenService.DropRequests();
                KeyboardScreenService.RequestMode(0, KeyboardScreenKind.Arx, 0, KeyboardScreenMode.Clock);
                Assert.Equal(1, KeyboardScreenService.PendingRequests);
            }
            finally
            {
                KeyboardScreensRuntime.Enabled = hardware;
                KeyboardScreenService.DropRequests();
            }
        }

        /// <summary>With every window down, a macro's mode is kept, and its show waits
        /// for the window: "set the picture, then bring up the panel" works in that
        /// order too. A timed show that ran out while the window was down is gone.</summary>
        [Fact]
        public void AMacro_LandsWithEveryWindowDown()
            => OnSta(() => WithQueue(() =>
            {
                string frame = Convert.ToBase64String(Png(Bitmap(4, 2, Opaque)));
                var service = new KeyboardScreenService(new SettingsViewModel(), null);

                KeyboardScreenService.RequestMode(0, KeyboardScreenKind.Overlay, 3, KeyboardScreenMode.Clock);
                KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Overlay, 1, frame, 60000, 0);
                service.TickWithNothingUp();
                Assert.Equal(0, KeyboardScreenService.PendingRequests);
                Assert.Equal(KeyboardScreenMode.Clock, service.Get(KeyboardScreenInfo.OverlayRow(3)).Mode);

                var shown = Attach(service, 1);
                service.Tick(hardware: false);
                Assert.Equal(4, shown[^1].Picture.PixelWidth);
                service.DetachOverlay(1);

                KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Overlay, 2, frame, KeyboardScreenService.MinFrameMs, 1);
                service.TickWithNothingUp();
                Thread.Sleep(KeyboardScreenService.MinFrameMs * 2);
                service.TickWithNothingUp();
                var two = Attach(service, 2);
                service.Tick(hardware: false);
                Assert.Null(two[^1].Picture);
            }));

        /// <summary>A picture held until the next macro is still there after the hotkey
        /// hides the panel and shows it again, through the idle pass that lets go of
        /// the screens. A mode macro with the window down ends it. The engine stopping
        /// ends it too.</summary>
        [Fact]
        public void AHeldShow_ComesBackWithItsWindow()
            => OnSta(() => WithQueue(() =>
            {
                string frame = Convert.ToBase64String(Png(Bitmap(4, 2, Opaque)));
                var service = new KeyboardScreenService(new SettingsViewModel(), null);
                var shown = Attach(service, 1);
                KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Overlay, 1, frame, 60000, 0);
                service.Tick(hardware: false);
                Assert.NotNull(shown[^1].Picture);

                // Nothing is drawn while the window is down.
                service.DetachOverlay(1);
                int drawn = shown.Count;
                Thread.Sleep(300);
                service.Tick(hardware: false);
                Assert.Equal(drawn, shown.Count);

                service.TickWithNothingUp();
                service.ReleaseWhileIdle();
                Assert.False(service.Active);
                shown = Attach(service, 1);
                service.Tick(hardware: false);
                Assert.NotNull(shown[^1].Picture);

                service.DetachOverlay(1);
                KeyboardScreenService.RequestMode(0, KeyboardScreenKind.Overlay, 1, KeyboardScreenMode.Clock);
                service.TickWithNothingUp();
                shown = Attach(service, 1);
                service.Tick(hardware: false);
                Assert.Null(shown[^1].Picture);
                Assert.NotNull(shown[^1].Text);

                Thread.Sleep(300);
                KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Overlay, 1, frame, 60000, 0);
                service.Tick(hardware: false);
                service.DetachOverlay(1);
                service.Reset();
                shown = Attach(service, 1);
                service.Tick(hardware: false);
                Assert.Null(shown[^1].Picture);
            }));

        /// <summary>A macro for every overlay reaches each one on the card, its window
        /// up or down, and none that is not on it.</summary>
        [Fact]
        public void EveryOverlay_MeansEveryOneOnTheCard()
            => OnSta(() => WithQueue(() =>
            {
                string frame = Convert.ToBase64String(Png(Bitmap(4, 2, Opaque)));
                var service = new KeyboardScreenService(new SettingsViewModel(), null) { OverlayNumbers = () => new[] { 1, 2 } };
                var one = Attach(service, 1);

                KeyboardScreenService.RequestMode(0, KeyboardScreenKind.Overlay, 0, KeyboardScreenMode.Clock);
                service.Tick(hardware: false);
                Assert.Equal(KeyboardScreenMode.Clock, service.Get(KeyboardScreenInfo.OverlayRow(1)).Mode);
                Assert.Equal(KeyboardScreenMode.Clock, service.Get(KeyboardScreenInfo.OverlayRow(2)).Mode);
                Assert.Null(service.Get(KeyboardScreenInfo.OverlayRow(3)));

                Thread.Sleep(300);
                KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Overlay, 0, frame, 60000, 0);
                service.Tick(hardware: false);
                var two = Attach(service, 2);
                service.Tick(hardware: false);
                Assert.NotNull(one[^1].Picture);
                Assert.NotNull(two[^1].Picture);
            }));

        /// <summary>One picture held needs no timing, so the tick goes back to four
        /// passes a second. Two pictures held take turns, so they keep it at the frame
        /// rate.</summary>
        [Fact]
        public void AHeldPicture_DoesNotKeepTheFastTick()
            => OnSta(() => WithQueue(() =>
            {
                Assert.True(new FrameShow<string>(new List<string> { "a" }, 1000, 0, 0).IsStill);
                Assert.False(new FrameShow<string>(new List<string> { "a", "b" }, 1000, 0, 0).IsStill);
                Assert.False(new FrameShow<string>(new List<string> { "a" }, 1000, 1, 0).IsStill);

                string frame = Convert.ToBase64String(Png(Bitmap(4, 2, Opaque)));
                var service = new KeyboardScreenService(new SettingsViewModel(), null);
                var shown = Attach(service, 1);
                KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Overlay, 1, frame, 60000, 0);
                service.Tick(hardware: false);
                int passes = shown.Count;
                service.Tick(hardware: false);
                Assert.Equal(passes, shown.Count);

                Thread.Sleep(300);
                KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Overlay, 1, frame + "," + frame, 60000, 0);
                service.Tick(hardware: false);
                passes = shown.Count;
                service.Tick(hardware: false);
                Assert.True(shown.Count > passes);
            }));

        /// <summary>A PC with no such keyboard never opens one: the overlay runs
        /// with no host, so no device is opened, no vendor library loads and no
        /// G19 is bound to WinUSB.</summary>
        [Fact]
        public void TheOverlay_RunsWithNoHost()
            => OnSta(() =>
            {
                var service = new KeyboardScreenService(new SettingsViewModel(), null);
                Assert.False(service.Active);
                var shown = Attach(service, 1);
                service.Tick(hardware: false);
                Assert.False(service.Running);
                Assert.True(service.Active);
                Assert.Empty(service.Screens);
                Assert.Single(shown);

                service.CloseHost();
                Assert.True(service.Active);

                // Down, it stays active until a reset ends what it held.
                service.DetachOverlay(1);
                Assert.True(service.Active);
                service.Reset();
                Assert.False(service.Active);
            });

        [Fact]
        public void TheOverlay_TakesMacrosFromAnySlot_AndAShowHandsBackToTheMode()
            => OnSta(() => WithQueue(() =>
            {
                string frame = Convert.ToBase64String(Png(Bitmap(4, 2, Opaque)));
                var service = new KeyboardScreenService(new SettingsViewModel(), null);
                var shown = Attach(service, 1);

                // Slot 7 has nothing assigned, and the overlay feeds no slot.
                KeyboardScreenService.RequestMode(7, KeyboardScreenKind.Overlay, 1, KeyboardScreenMode.ProfileNumber);
                service.Tick(hardware: false);
                Assert.Equal(KeyboardScreenMode.ProfileNumber, service.Get(KeyboardScreenInfo.OverlayRow(1)).Mode);
                Assert.NotNull(shown[^1].Text);

                Thread.Sleep(300);
                KeyboardScreenService.RequestShow(3, KeyboardScreenKind.Overlay, 1, frame, KeyboardScreenService.MinFrameMs, 1);
                service.Tick(hardware: false);
                Assert.NotNull(shown[^1].Picture);
                Assert.Equal(4, shown[^1].Picture.PixelWidth);

                Thread.Sleep(KeyboardScreenService.MinFrameMs + 50);
                service.Tick(hardware: false);
                Assert.Null(shown[^1].Picture);
                Assert.NotNull(shown[^1].Text);

                // A hardware kind's macro does not reach the overlay.
                Thread.Sleep(300);
                KeyboardScreenService.RequestMode(0, KeyboardScreenKind.Color, 0, KeyboardScreenMode.Clock);
                KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Color, 0, frame, KeyboardScreenService.MinFrameMs, 1);
                service.Tick(hardware: false);
                Assert.Equal(KeyboardScreenMode.ProfileNumber, service.Get(KeyboardScreenInfo.OverlayRow(1)).Mode);
                Assert.Null(shown[^1].Picture);
            }));

        /// <summary>The window is down: nothing is drawn, and a show keeps its clock,
        /// so a window that comes up part way through shows what it has reached. A mode
        /// macro that names it lands, since it is a setting, and leaves a timed show
        /// playing.</summary>
        [Fact]
        public void WhileTheWindowIsDown_AShowKeepsItsClock()
            => OnSta(() => WithQueue(() =>
            {
                string frame = Convert.ToBase64String(Png(Bitmap(4, 2, Opaque)));
                var service = new KeyboardScreenService(new SettingsViewModel(), null);
                KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Overlay, 1, frame, 60000, 1);
                KeyboardScreenService.RequestMode(0, KeyboardScreenKind.Overlay, 1, KeyboardScreenMode.Clock);
                service.Tick(hardware: false);
                Assert.Equal(KeyboardScreenMode.Clock, service.Get(KeyboardScreenInfo.OverlayRow(1)).Mode);

                var shown = Attach(service, 1);
                service.Tick(hardware: false);
                Assert.NotNull(Assert.Single(shown).Picture);
            }));

        /// <summary>A macro's request does not wait out the quarter second between
        /// passes, so a press changes the picture on the next tick.</summary>
        [Fact]
        public void AMacro_DoesNotWaitForTheNextPass()
            => OnSta(() => WithQueue(() =>
            {
                string frame = Convert.ToBase64String(Png(Bitmap(4, 2, Opaque)));
                var service = new KeyboardScreenService(new SettingsViewModel(), null);
                var shown = Attach(service, 1);
                service.Tick(hardware: false);
                int passes = shown.Count;
                service.Tick(hardware: false);
                Assert.Equal(passes, shown.Count);

                KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Overlay, 1, frame, 60000, 1);
                service.Tick(hardware: false);
                Assert.NotNull(shown[^1].Picture);
            }));

        /// <summary>Several overlays show different things at once, a macro can name
        /// one, and at 0 it reaches them all.</summary>
        [Fact]
        public void AMacro_NamesItsOverlay_OrReachesThemAll()
            => OnSta(() => WithQueue(() =>
            {
                string frame = Convert.ToBase64String(Png(Bitmap(4, 2, Opaque)));
                var service = new KeyboardScreenService(new SettingsViewModel(), null);
                service.Update(KeyboardScreenInfo.OverlayRow(1), d => d.Mode = KeyboardScreenMode.Clock);
                service.Update(KeyboardScreenInfo.OverlayRow(2), d => d.Mode = KeyboardScreenMode.Stopwatch);
                var one = Attach(service, 1);
                var two = Attach(service, 2);
                service.Tick(hardware: false);
                Assert.Matches("^\\d\\d:\\d\\d$", one[^1].Text);
                Assert.Equal(KeyboardScreenService.FormatStopwatch(KeyboardScreenService.StopwatchTime(Environment.TickCount64)), two[^1].Text);

                Thread.Sleep(300);
                KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Overlay, 2, frame, 60000, 1);
                service.Tick(hardware: false);
                Assert.NotNull(one[^1].Text);
                Assert.NotNull(two[^1].Picture);

                KeyboardScreenService.RequestShow(5, KeyboardScreenKind.Overlay, 0, frame, 60000, 1);
                service.Tick(hardware: false);
                Assert.NotNull(one[^1].Picture);
                Assert.NotNull(two[^1].Picture);
            }));

        /// <summary>A repeat count of 0 stays until the next macro, for a weapon icon
        /// that stays until the next press. Another show replaces it, a mode macro ends
        /// it, and a mode macro leaves a timed show playing.</summary>
        [Fact]
        public void AHeldShow_StaysUntilTheNextMacro()
            => OnSta(() => WithQueue(() =>
            {
                string first = Convert.ToBase64String(Png(Bitmap(4, 2, Opaque)));
                string second = Convert.ToBase64String(Png(Bitmap(6, 2, Opaque)));
                var service = new KeyboardScreenService(new SettingsViewModel(), null);
                var shown = Attach(service, 1);

                KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Overlay, 1, first, KeyboardScreenService.MinFrameMs, 0);
                service.Tick(hardware: false);
                Assert.Equal(4, shown[^1].Picture.PixelWidth);

                Thread.Sleep(KeyboardScreenService.MinFrameMs * 3);
                service.Tick(hardware: false);
                Assert.Equal(4, shown[^1].Picture.PixelWidth);

                KeyboardScreenService.RequestShow(1, KeyboardScreenKind.Overlay, 1, second, KeyboardScreenService.MinFrameMs, 0);
                service.Tick(hardware: false);
                Assert.Equal(6, shown[^1].Picture.PixelWidth);

                KeyboardScreenService.RequestMode(2, KeyboardScreenKind.Overlay, 1, KeyboardScreenMode.Clock);
                service.Tick(hardware: false);
                Assert.Null(shown[^1].Picture);
                Assert.NotNull(shown[^1].Text);

                Thread.Sleep(300);
                KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Overlay, 1, first, 60000, 1);
                service.Tick(hardware: false);
                KeyboardScreenService.RequestMode(0, KeyboardScreenKind.Overlay, 1, KeyboardScreenMode.Stopwatch);
                service.Tick(hardware: false);
                Assert.NotNull(shown[^1].Picture);
                Assert.Equal(KeyboardScreenMode.Stopwatch, service.Get(KeyboardScreenInfo.OverlayRow(1)).Mode);
            }));

        [Fact]
        public void AShow_HoldsAtARepeatOfZero_AndEndsOtherwise()
        {
            var frames = new List<byte[]> { new byte[1], new byte[2] };
            var held = new KeyboardScreenShow(frames, 100, 0, 0);
            Assert.True(held.Held);
            Assert.Same(frames[0], held.Frame(0));
            Assert.Same(frames[1], held.Frame(100));
            Assert.Same(frames[0], held.Frame(100 * 1000));

            var timed = new KeyboardScreenShow(frames, 100, 1, 0);
            Assert.False(timed.Held);
            Assert.Same(frames[1], timed.Frame(199));
            Assert.Null(timed.Frame(200));

            var action = new MacroAction { Type = MacroActionType.ShowKeyboardScreen, ScreenRepeat = 0 };
            Assert.Equal(0, action.ScreenRepeat);
            Assert.Equal(string.Format(Strings.Instance.MacroAction_ShowKeyboardScreen_Hold_Format,
                Strings.Instance.KeyboardScreens_Kind_Monochrome, 0, 1000, 0), action.DisplayText);
            action.ScreenRepeat = -5;
            Assert.Equal(0, action.ScreenRepeat);
            action.ScreenRepeat = 5000;
            Assert.Equal(1000, action.ScreenRepeat);
        }

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
        public void TheLightbarColor_FillsTheOverlay_FromTheSlotItsDialogChose()
            => OnSta(() =>
            {
                LightbarColors.Clear();
                try
                {
                    LightbarColors.Record(2, Guid.NewGuid(), 10, 20, 30);
                    LightbarColors.Record(5, Guid.NewGuid(), 40, 50, 60);
                    Assert.Equal(Color.FromRgb(10, 20, 30), KeyboardScreenService.LightbarContent((_, _) => true, null).Fill);
                    Assert.Equal(Color.FromRgb(40, 50, 60), KeyboardScreenService.LightbarContent((_, _) => true, s => s == 5).Fill);
                    Assert.Equal(KeyboardScreenService.Blank.NoLightbar, KeyboardScreenService.LightbarContent((_, _) => true, s => s == 9).Blank);
                    Assert.Equal(KeyboardScreenService.Blank.NoLightbar, KeyboardScreenService.LightbarContent((_, _) => false, null).Blank);
                }
                finally { LightbarColors.Clear(); }
            });

        /// <summary>The real path, from the saved slot through the pad's slot check.</summary>
        [Fact]
        public void TheLightbarMode_ReadsTheSavedSlot()
            => OnSta(() =>
            {
                using var statics = new SavedStatics();
                LightbarColors.Clear();
                try
                {
                    Guid first = Guid.NewGuid(), second = Guid.NewGuid();
                    foreach (var (device, slot) in new[] { (first, 1), (second, 4) })
                    {
                        SettingsManager.UserDevices.Items.Add(new UserDevice { InstanceGuid = device, ProductGuid = device, IsOnline = true });
                        SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = device, ProductGuid = device, MapTo = slot });
                    }
                    LightbarColors.Record(1, first, 1, 2, 3);
                    LightbarColors.Record(4, second, 4, 5, 6);
                    var service = new KeyboardScreenService(new SettingsViewModel(), null);
                    long now = Environment.TickCount64;
                    Assert.Equal(Color.FromRgb(1, 2, 3), service.OverlayContent(new KeyboardScreenData { Mode = KeyboardScreenMode.Lightbar }, now).Fill);
                    Assert.Equal(Color.FromRgb(4, 5, 6), service.OverlayContent(new KeyboardScreenData { Mode = KeyboardScreenMode.Lightbar, LightbarSlot = 4 }, now).Fill);
                    Assert.Equal(KeyboardScreenService.Blank.NoLightbar, service.OverlayContent(new KeyboardScreenData { Mode = KeyboardScreenMode.Lightbar, LightbarSlot = 7 }, now).Blank);
                }
                finally { LightbarColors.Clear(); }
            });

        [Fact]
        public void AMode_SaysWhyItShowsNothing()
            => OnSta(() =>
            {
                LightbarColors.Clear();
                var service = new KeyboardScreenService(new SettingsViewModel(), null);
                long now = Environment.TickCount64;
                Assert.Equal(KeyboardScreenService.Blank.Off, service.OverlayContent(new KeyboardScreenData { Mode = KeyboardScreenMode.Off }, now).Blank);
                Assert.Equal(KeyboardScreenService.Blank.NoPicture, service.OverlayContent(new KeyboardScreenData { Mode = KeyboardScreenMode.Picture }, now).Blank);
                Assert.Equal(KeyboardScreenService.Blank.NoLightbar, service.OverlayContent(new KeyboardScreenData { Mode = KeyboardScreenMode.Lightbar }, now).Blank);
                Assert.Equal(KeyboardScreenService.Blank.None, service.OverlayContent(new KeyboardScreenData { Mode = KeyboardScreenMode.Clock }, now).Blank);
                Assert.Equal(KeyboardScreenService.Blank.NoPicture, service.OverlayPreview(new KeyboardScreenData { Mode = KeyboardScreenMode.Picture }, null, "A Game").Blank);
            });

        [Fact]
        public void TheSavedChoices_RoundTripAndNormalize()
        {
            var data = new KeyboardScreenData { Device = Guid.NewGuid().ToString("D"), Mode = KeyboardScreenMode.Picture, Zoom = 350, LightbarSlot = 6, PanX = 9 };
            var serializer = new XmlSerializer(typeof(KeyboardScreenData));
            using var writer = new StringWriter();
            serializer.Serialize(writer, data);
            var back = (KeyboardScreenData)serializer.Deserialize(new StringReader(writer.ToString()));
            Assert.Equal(350, back.Zoom);
            Assert.Equal(6, back.LightbarSlot);

            var old = (KeyboardScreenData)serializer.Deserialize(new StringReader("<KeyboardScreenData Device=\"x\"><Mode>Clock</Mode></KeyboardScreenData>"));
            Assert.Equal(KeyboardScreenData.DefaultZoom, old.Zoom);
            Assert.Equal(KeyboardScreenData.LowestSlot, old.LightbarSlot);

            Assert.True(new KeyboardScreenData().IsDefault);
            Assert.False(new KeyboardScreenData { Zoom = 200 }.IsDefault);
            Assert.False(new KeyboardScreenData { LightbarSlot = 0 }.IsDefault);

            var normal = KeyboardScreenData.Normalize(new[]
            {
                new KeyboardScreenData { Device = Guid.NewGuid().ToString("D"), Mode = KeyboardScreenMode.Clock, Zoom = 5, LightbarSlot = 999 },
                new KeyboardScreenData { Device = Guid.NewGuid().ToString("D"), Mode = KeyboardScreenMode.Clock, Zoom = 99999, LightbarSlot = -7 },
            });
            Assert.Equal(KeyboardScreenData.DefaultZoom, normal[0].Zoom);
            Assert.Equal(KeyboardScreenData.LowestSlot, normal[0].LightbarSlot);
            Assert.Equal(KeyboardScreenData.MaxZoom, normal[1].Zoom);
            Assert.Equal(KeyboardScreenData.LowestSlot, normal[1].LightbarSlot);
        }

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

        [Theory]
        // Twice the size that fits, in a window of the same shape: half shows in
        // each direction, and the pan moves it, held to the picture's far edge.
        [InlineData(400, 400, 100, 100, 200, 0, 0, 0, 0, 200, 200)]
        [InlineData(400, 400, 100, 100, 200, 100, 50, -50, -25, 200, 200)]
        [InlineData(400, 400, 100, 100, 200, 9999, 9999, -100, -100, 200, 200)]
        // A wide picture overflows across and still fits down, so it stays centered
        // down, RenderWindow's rule for a keyboard screen.
        [InlineData(400, 100, 100, 100, 200, 100, 50, -50, 25, 200, 50)]
        public void AZoomedPicture_RunsPastTheBounds_AndThePanMovesIt(int pw, int ph, double bw, double bh, int zoom, int panX, int panY, double x, double y, double w, double h)
        {
            var at = ScreenOverlayContentView.FitPicture(pw, ph, new Size(bw, bh), zoom, panX, panY);
            Assert.Equal(x, at.X, 6);
            Assert.Equal(y, at.Y, 6);
            Assert.Equal(w, at.Width, 6);
            Assert.Equal(h, at.Height, 6);
            Assert.Equal((double)pw / ph, at.Width / at.Height, 6);
        }

        [Fact]
        public void ThePanStops_AtThePicturesEdges()
        {
            var bounds = new Size(100, 100);
            Assert.Equal((0, 0), ScreenOverlayContentView.PanLimits(400, 300, 100, bounds));
            Assert.Equal((200, 100), ScreenOverlayContentView.PanLimits(400, 300, 200, bounds));
            Assert.Equal((300, 200), ScreenOverlayContentView.PanLimits(400, 300, 400, bounds));
            // Below 100 percent reads as 100, and a window with no size has no limits.
            Assert.Equal((0, 0), ScreenOverlayContentView.PanLimits(400, 300, 10, bounds));
            Assert.Equal((0, 0), ScreenOverlayContentView.PanLimits(400, 300, 200, new Size(0, 0)));
            Assert.Equal(0, ScreenOverlayContentView.PictureScale(0, 300, 200, bounds));
        }

        /// <summary>The map's two halves, one at a time, with the rest cut off at the
        /// window's edge.</summary>
        [Fact]
        public void AZoomedPictureDrawn_ShowsWhereThePanIs()
            => OnSta(() =>
            {
                var picture = Bitmap(200, 100, (x, _) => x < 100 ? ((byte)0, (byte)0, (byte)255, (byte)255) : ((byte)255, (byte)0, (byte)0, (byte)255));
                picture.Freeze();
                var view = new ScreenOverlayContentView();
                view.Show(new ScreenOverlayContent(Picture: picture, Zoom: 200));
                var left = Rendered(view, 100, 100);
                Assert.Equal(255, PixelOf(left, 50, 50).R);
                Assert.Equal(0, PixelOf(left, 50, 50).B);
                Assert.Equal(255, PixelOf(left, 99, 99).A);

                view.Show(new ScreenOverlayContent(Picture: picture, Zoom: 200, PanX: 100));
                var right = Rendered(view, 101, 100);
                Assert.Equal(255, PixelOf(right, 50, 50).B);
                Assert.Equal(0, PixelOf(right, 50, 50).R);
            });

        /// <summary>The Pan macro moves a zoomed picture from any slot, stopping at its
        /// edges, and leaves a picture that shows whole alone.</summary>
        [Fact]
        public void ThePanMacro_MovesAZoomedPicture()
            => OnSta(() => WithQueue(() => WithPicture(400, 300, Opaque, path =>
            {
                var service = new KeyboardScreenService(new SettingsViewModel(), null);
                var row = KeyboardScreenInfo.OverlayRow(1);
                service.Update(row, d =>
                {
                    d.Mode = KeyboardScreenMode.Picture;
                    d.Picture = path;
                });
                var shown = Attach(service, 1, new Size(100, 100));

                KeyboardScreenService.RequestPan(6, 0, KeyboardScreenPanDirection.Right, 64);
                service.Tick(hardware: false);
                Assert.Equal(0, service.Get(row).PanX);

                service.Update(row, d => d.Zoom = 200);
                KeyboardScreenService.RequestPan(6, 0, KeyboardScreenPanDirection.Right, 64);
                KeyboardScreenService.RequestPan(6, 0, KeyboardScreenPanDirection.Down, 5000);
                Thread.Sleep(300);
                service.Tick(hardware: false);
                Assert.Equal(64, service.Get(row).PanX);
                Assert.Equal(100, service.Get(row).PanY);
                Assert.Equal(200, shown[^1].Zoom);
                Assert.Equal(64, shown[^1].PanX);
                Assert.Equal(100, shown[^1].PanY);
            })));

        /// <summary>The pan macro names one overlay, or moves every one at 0. A window
        /// that is down stays where it was, since a pan's limits come from its size.</summary>
        [Fact]
        public void ThePanMacro_NamesItsOverlay()
            => OnSta(() => WithPicture(400, 300, Opaque, path => WithQueue(() =>
            {
                var service = new KeyboardScreenService(new SettingsViewModel(), null);
                var one = KeyboardScreenInfo.OverlayRow(1);
                var two = KeyboardScreenInfo.OverlayRow(2);
                foreach (var row in new[] { one, two })
                    service.Update(row, d =>
                    {
                        d.Mode = KeyboardScreenMode.Picture;
                        d.Picture = path;
                        d.Zoom = 200;
                    });
                Attach(service, 1, new Size(100, 100));
                Attach(service, 2, new Size(100, 100));

                KeyboardScreenService.RequestPan(0, 2, KeyboardScreenPanDirection.Right, 8);
                Thread.Sleep(300);
                service.Tick(hardware: false);
                Assert.Equal(0, service.Get(one).PanX);
                Assert.Equal(8, service.Get(two).PanX);

                KeyboardScreenService.RequestPan(0, 0, KeyboardScreenPanDirection.Right, 8);
                Thread.Sleep(300);
                service.Tick(hardware: false);
                Assert.Equal(8, service.Get(one).PanX);
                Assert.Equal(16, service.Get(two).PanX);

                service.DetachOverlay(2);
                KeyboardScreenService.RequestPan(0, 2, KeyboardScreenPanDirection.Right, 8);
                Thread.Sleep(300);
                service.Tick(hardware: false);
                Assert.Equal(16, service.Get(two).PanX);
            })));

        /// <summary>The left button drags the picture with the pointer, the
        /// S.T.R.I.K.E. 7's finger, held to the same edges.</summary>
        [Fact]
        public void ADrag_MovesAZoomedPicture()
            => OnSta(() => WithPicture(400, 300, Opaque, path =>
            {
                var service = new KeyboardScreenService(new SettingsViewModel(), null);
                var row = KeyboardScreenInfo.OverlayRow(1);
                service.Update(row, d =>
                {
                    d.Mode = KeyboardScreenMode.Picture;
                    d.Picture = path;
                    d.Zoom = 200;
                    d.PanX = 10;
                });
                Attach(service, 1, new Size(100, 100));

                // At this zoom a window unit is two of the picture's pixels.
                service.DragOverlayPan(1, default, true);
                service.DragOverlayPan(1, new Vector(-20, 0), false);
                Assert.Equal(50, service.Get(row).PanX);
                service.DragOverlayPan(1, new Vector(-30, -10), false);
                Assert.Equal(70, service.Get(row).PanX);
                Assert.Equal(20, service.Get(row).PanY);
                service.DragOverlayPan(1, new Vector(-9000, 9000), false);
                Assert.Equal(200, service.Get(row).PanX);
                Assert.Equal(0, service.Get(row).PanY);

                // An overlay whose window is down is left as it was.
                service.DetachOverlay(1);
                service.DragOverlayPan(1, new Vector(5, 5), false);
                Assert.Equal(200, service.Get(row).PanX);
            }));

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
                    SetPrivate(window, "_isPanning", true);
                    Assert.True(window.InGesture);
                    window.SetArranging(false);
                    Assert.True(window.Arranging);
                    SetPrivate(window, "_isPanning", false);
                    Assert.False(window.InGesture);
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

                    // A pan saves no place, and it ends with its capture too.
                    SetPrivate(screen, "_isPanning", true);
                    LoseCapture(screen.Surface);
                    Assert.False((bool)GetPrivate(screen, "_isPanning"));
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

        /// <summary>The hint says why it shows nothing, while PadForge is in front.</summary>
        [Fact]
        public void TheEmptyHint_SaysWhy()
            => OnSta(() =>
            {
                var window = new ScreenOverlay();
                try
                {
                    window.SetArranging(true);
                    window.ShowContent(new ScreenOverlayContent(Blank: KeyboardScreenService.Blank.NoPicture));
                    Assert.Equal(Strings.Instance.ScreenOverlay_Blank_NoPicture, window.EmptyHint.Text);
                    Assert.Equal(Visibility.Visible, window.EmptyHint.Visibility);
                    window.ShowContent(new ScreenOverlayContent(Blank: KeyboardScreenService.Blank.NoLightbar));
                    Assert.Equal(Strings.Instance.ScreenOverlay_Blank_NoLightbar, window.EmptyHint.Text);
                    window.ShowContent(ScreenOverlayContent.Nothing);
                    Assert.Equal(Strings.Instance.ScreenOverlay_Empty, window.EmptyHint.Text);
                    window.ShowContent(new ScreenOverlayContent("12:34"));
                    Assert.Equal(Visibility.Collapsed, window.EmptyHint.Visibility);
                }
                finally { window.Close(); }
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
        public void TheCard_StartsWithOneOverlay_WithItsValuesInRange()
        {
            var vm = new DashboardViewModel();
            var one = Assert.Single(vm.ScreenOverlays);
            Assert.Equal(1, one.Number);
            Assert.False(one.OneOfSeveral);
            Assert.False(one.Enabled);
            Assert.Equal(0.8, one.Opacity);
            Assert.Equal(80, one.OpacityPercent);
            Assert.Equal(OverlayPlacement.NoPlace, one.Left);
            Assert.Equal(OverlayPlacement.NoPlace, one.Top);
            Assert.Equal(320, one.Width);
            Assert.Equal(240, one.Height);

            one.OpacityPercent = 35;
            Assert.Equal(0.35, one.Opacity, 6);
            one.Opacity = 4;
            Assert.Equal(1.0, one.Opacity);
            Assert.Equal(100, one.OpacityPercent);
            one.Opacity = -1;
            Assert.Equal(0.0, one.Opacity);
            one.ResetOpacityCommand.Execute(null);
            Assert.Equal(0.8, one.Opacity);

            one.Width = 5;
            one.Height = 5;
            Assert.Equal(80, one.Width);
            Assert.Equal(60, one.Height);

            one.Left = -1920;
            Assert.Equal(-1920, one.Left);
            one.Left = OverlayPlacement.NoPlace;
            Assert.Equal(OverlayPlacement.NoPlace, one.Left);

            var running = new List<string>();
            one.PropertyChanged += (_, e) => running.Add(e.PropertyName);
            Assert.Equal(Strings.Instance.Common_Stopped, one.Status);
            one.IsRunning = true;
            Assert.Equal(Strings.Instance.Common_Running, one.Status);
            Assert.Contains(nameof(ScreenOverlayItem.Status), running);

            one.Blank = KeyboardScreenService.Blank.NoPicture;
            Assert.Equal(string.Format(Strings.Instance.KeyboardScreens_Status_NoPicture_Format, Strings.Instance.Common_Running), one.Status);
            one.Blank = KeyboardScreenService.Blank.NoLightbar;
            Assert.Equal(string.Format(Strings.Instance.KeyboardScreens_Status_NoLightbar_Format, Strings.Instance.Common_Running), one.Status);
            one.Blank = KeyboardScreenService.Blank.Off;
            Assert.Equal(Strings.Instance.Common_Running, one.Status);

            ScreenOverlayItem reset = null, chosen = null;
            vm.ResetScreenOverlayPositionRequested += (_, item) => reset = item;
            vm.ChooseScreenOverlayContentRequested += (_, item) => chosen = item;
            one.ResetPositionCommand.Execute(null);
            one.ChooseCommand.Execute(null);
            Assert.Same(one, reset);
            Assert.Same(one, chosen);
        }

        /// <summary>A number stays with its overlay, so a macro that names overlay 3
        /// keeps meaning the same window after overlay 2 goes. There is always one.</summary>
        [Fact]
        public void TheCard_AddsAndRemovesOverlays_AndNumbersStay()
        {
            var vm = new DashboardViewModel();
            int[] Numbers() => vm.ScreenOverlays.Select(o => o.Number).ToArray();

            vm.AddScreenOverlayCommand.Execute(null);
            vm.AddScreenOverlayCommand.Execute(null);
            Assert.Equal(new[] { 1, 2, 3 }, Numbers());
            Assert.All(vm.ScreenOverlays, o => Assert.True(o.OneOfSeveral));

            vm.ScreenOverlays[1].RemoveCommand.Execute(null);
            Assert.Equal(new[] { 1, 3 }, Numbers());

            // The lowest free number, in its place in the list.
            vm.AddScreenOverlay();
            Assert.Equal(new[] { 1, 2, 3 }, Numbers());

            for (int i = 0; i < KeyboardScreenInfo.MaxOverlays + 5; i++) vm.AddScreenOverlay();
            Assert.Equal(KeyboardScreenInfo.MaxOverlays, vm.ScreenOverlays.Count);
            Assert.False(vm.CanAddScreenOverlay);

            while (vm.ScreenOverlays.Count > 1) vm.RemoveScreenOverlay(vm.ScreenOverlays[0]);
            Assert.Equal(new[] { KeyboardScreenInfo.MaxOverlays }, Numbers());
            Assert.False(vm.ScreenOverlays[0].OneOfSeveral);
            Assert.True(vm.CanAddScreenOverlay);
            vm.RemoveScreenOverlay(vm.ScreenOverlays[0]);
            Assert.Single(vm.ScreenOverlays);
        }

        /// <summary>A saved value's change marks the settings dirty and brings the
        /// windows into line. Whether the window is running is neither.</summary>
        [Fact]
        public void TheCard_ReportsSavedChanges_AndOnlyThose()
        {
            var vm = new DashboardViewModel();
            var one = vm.ScreenOverlays[0];
            int changes = 0;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DashboardViewModel.ScreenOverlays)) changes++; };

            one.Opacity = 0.5;
            Assert.Equal(1, changes);
            one.Left = 10;
            one.Top = 20;
            one.Width = 200;
            one.Height = 100;
            one.Monitor = 1;
            Assert.Equal(6, changes);
            one.Left = 10;
            Assert.Equal(6, changes);

            one.IsRunning = true;
            one.Blank = KeyboardScreenService.Blank.NoPicture;
            Assert.Equal(6, changes);

            vm.AddScreenOverlay();
            Assert.Equal(7, changes);
            vm.RemoveScreenOverlay(vm.ScreenOverlays[1]);
            Assert.Equal(8, changes);
        }

        // ── Settings ──

        /// <summary>Every place the settings are read or written carries the list.</summary>
        [Fact]
        public void EverySettingSite_CarriesTheList()
        {
            string settings = RepoText("PadForge.App", "Services", "SettingsService.cs");
            string window = RepoText("PadForge.App", "MainWindow.xaml.cs");
            string service = RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Equal(2, CountOf(settings, "public ScreenOverlayData[] ScreenOverlays { get; set; }"));
            Assert.Equal(1, CountOf(settings, "_mainVm.Dashboard.LoadScreenOverlays(appSettings.ScreenOverlays);"));
            Assert.Equal(1, CountOf(settings, "_mainVm.Dashboard.LoadScreenOverlays(active.ScreenOverlays);"));
            Assert.Equal(2, CountOf(settings, "ScreenOverlays = _mainVm.Dashboard.SnapshotScreenOverlays()"));
            Assert.Equal(1, CountOf(window, "nameof(DashboardViewModel.ScreenOverlays)"));
            Assert.Equal(1, CountOf(service, "ScreenOverlays = _mainVm.Dashboard.SnapshotScreenOverlays(),"));
            Assert.Equal(1, CountOf(service, "_mainVm.Dashboard.LoadScreenOverlays(profile.ScreenOverlays);"));
            Assert.Equal(1, CountOf(service, "profile.ScreenOverlays = snapshot.ScreenOverlays;"));
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
            Assert.Null(RoundTrip(new AppSettingsData()).ScreenOverlays);

            var plain = new ScreenOverlayData();
            Assert.Equal(1, plain.Number);
            Assert.False(plain.Enabled);
            Assert.Equal(0.8, plain.Opacity);
            Assert.Equal(OverlayPlacement.NoPlace, plain.Left);
            Assert.Equal(OverlayPlacement.NoPlace, plain.Top);
            Assert.Equal(320, plain.Width);
            Assert.Equal(240, plain.Height);

            var app = RoundTrip(new AppSettingsData
            {
                ScreenOverlays = new[]
                {
                    new ScreenOverlayData { Number = 2, Enabled = true, Opacity = 0.4, Monitor = 2, Left = -1500.5, Top = -20, Width = 640, Height = 480 },
                    new ScreenOverlayData { Number = 5 },
                },
            });
            Assert.Equal(2, app.ScreenOverlays.Length);
            var placed = app.ScreenOverlays[0];
            Assert.Equal(2, placed.Number);
            Assert.True(placed.Enabled);
            Assert.Equal(0.4, placed.Opacity);
            Assert.Equal(2, placed.Monitor);
            Assert.Equal(-1500.5, placed.Left);
            Assert.Equal(-20, placed.Top);
            Assert.Equal(640, placed.Width);
            Assert.Equal(480, placed.Height);
            Assert.Equal(5, app.ScreenOverlays[1].Number);
            Assert.Equal(OverlayPlacement.NoPlace, app.ScreenOverlays[1].Left);

            var profile = RoundTrip(new ProfileData { Id = "p", ScreenOverlays = new[] { new ScreenOverlayData { Number = 3, Height = 90 } } });
            Assert.Equal(3, profile.ScreenOverlays[0].Number);
            Assert.Equal(90, profile.ScreenOverlays[0].Height);

            // A file saved before these existed.
            var old = (ProfileData)new XmlSerializer(typeof(ProfileData)).Deserialize(new StringReader("<ProfileData Id=\"old\"><Name>Old</Name></ProfileData>"));
            Assert.Null(old.ScreenOverlays);
        }

        /// <summary>What a file holds comes through the setters' bounds. Nothing in it
        /// leaves the card with no overlay, a number out of range or a number twice.</summary>
        [Fact]
        public void TheCard_LoadsAList_AndDropsWhatCannotBe()
        {
            var vm = new DashboardViewModel();
            vm.LoadScreenOverlays(null);
            var one = Assert.Single(vm.ScreenOverlays);
            Assert.Equal(1, one.Number);
            Assert.False(one.Enabled);

            vm.LoadScreenOverlays(Array.Empty<ScreenOverlayData>());
            Assert.Single(vm.ScreenOverlays);

            vm.LoadScreenOverlays(new[]
            {
                new ScreenOverlayData { Number = 4, Opacity = 9, Width = 0, Height = -3, Left = -640, Top = 12, Monitor = 2 },
                null,
                new ScreenOverlayData { Number = 0 },
                new ScreenOverlayData { Number = KeyboardScreenInfo.MaxOverlays + 1 },
                new ScreenOverlayData { Number = 4, Left = 999 },
                new ScreenOverlayData { Number = 2, Width = 5, Height = 5 },
            });
            Assert.Equal(new[] { 2, 4 }, vm.ScreenOverlays.Select(o => o.Number));
            var four = vm.FindScreenOverlay(4);
            Assert.Equal(1.0, four.Opacity);
            Assert.Equal(320, four.Width);
            Assert.Equal(240, four.Height);
            Assert.Equal(-640, four.Left);
            Assert.Equal(12, four.Top);
            Assert.Equal(2, four.Monitor);
            Assert.Equal(80, vm.FindScreenOverlay(2).Width);
            Assert.Equal(60, vm.FindScreenOverlay(2).Height);
            Assert.All(vm.ScreenOverlays, o => Assert.True(o.OneOfSeveral));
            Assert.Null(vm.FindScreenOverlay(1));

            var back = vm.SnapshotScreenOverlays();
            Assert.Equal(new[] { 2, 4 }, back.Select(o => o.Number));
            Assert.Equal(-640, back[1].Left);
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
            }
        }

        private static void SetOverlayValues(DashboardViewModel dash)
        {
            dash.LoadScreenOverlays(new[]
            {
                new ScreenOverlayData { Number = 1, Enabled = true, Opacity = 0.4, Monitor = 1, Left = -1500, Top = 30, Width = 640, Height = 480 },
                new ScreenOverlayData { Number = 3, Opacity = 0.6, Left = 70, Top = 80, Width = 100, Height = 90 },
            });
        }

        private static void AssertOverlayValues(DashboardViewModel dash)
        {
            Assert.Equal(new[] { 1, 3 }, dash.ScreenOverlays.Select(o => o.Number));
            var one = dash.FindScreenOverlay(1);
            Assert.True(one.Enabled);
            Assert.Equal(0.4, one.Opacity);
            Assert.Equal(1, one.Monitor);
            Assert.Equal(-1500, one.Left);
            Assert.Equal(30, one.Top);
            Assert.Equal(640, one.Width);
            Assert.Equal(480, one.Height);
            var three = dash.FindScreenOverlay(3);
            Assert.False(three.Enabled);
            Assert.Equal(0.6, three.Opacity);
            Assert.Equal(70, three.Left);
            Assert.Equal(90, three.Height);
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
        public void AProfile_CarriesTheOverlays_ThroughBothServices()
            => OnSta(() =>
            {
                using var statics = new SavedStatics();
                var vm = new MainViewModel();
                var settings = new SettingsService(vm);
                var input = new InputService(vm) { SettingsService = settings };
                SetOverlayValues(vm.Dashboard);

                var snapshot = input.SnapshotCurrentProfile();
                Assert.Equal(new[] { 1, 3 }, snapshot.ScreenOverlays.Select(o => o.Number));

                var fresh = new MainViewModel();
                var freshInput = new InputService(fresh) { SettingsService = new SettingsService(fresh) };
                freshInput.ApplyProfile(snapshot);
                AssertOverlayValues(fresh.Dashboard);

                // A profile from before the overlay has one, off, with no place.
                freshInput.ApplyProfile(new ProfileData { Id = "old", Name = "Old" });
                var one = Assert.Single(fresh.Dashboard.ScreenOverlays);
                Assert.False(one.Enabled);
                Assert.Equal(OverlayPlacement.NoPlace, one.Left);
                Assert.Equal(320, one.Width);

                // The active profile keeps what the Dashboard holds.
                var active = new ProfileData { Id = "p1", Name = "Game" };
                SettingsManager.Profiles.Add(active);
                SettingsManager.ActiveProfileId = "p1";
                settings.UpdateActiveProfileSnapshot();
                Assert.Equal(new[] { 1, 3 }, active.ScreenOverlays.Select(o => o.Number));
                Assert.Equal(-1500, active.ScreenOverlays[0].Left);

                // And the running profile's save does too.
                SetOverlayValues(fresh.Dashboard);
                SettingsManager.Profiles.Clear();
                var running = new ProfileData { Id = "p2", Name = "Other" };
                SettingsManager.Profiles.Add(running);
                SettingsManager.ActiveProfileId = "p2";
                freshInput.SaveActiveProfileState();
                Assert.Equal(new[] { 1, 3 }, running.ScreenOverlays.Select(o => o.Number));
                Assert.Equal(480, running.ScreenOverlays[0].Height);
            });

        // ── The lifecycle on the real service ──

        private static T Invoke<T>(object target, string method, params object[] args)
            => (T)target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static).Invoke(target, args);

        [Fact]
        public void TheService_BringsTheWindowsIntoLineWithTheList()
            => OnSta(() =>
            {
                using var statics = new SavedStatics();
                var vm = new MainViewModel();
                var input = new InputService(vm) { SettingsService = new SettingsService(vm) };
                var dash = vm.Dashboard;
                var windows = (Dictionary<int, ScreenOverlay>)GetPrivate(input, "_screenOverlays");
                var one = dash.ScreenOverlays[0];
                one.Opacity = 0.5;
                one.Width = 200;
                one.Height = 120;
                try
                {
                    // Off: no window.
                    Invoke<object>(input, "SyncScreenOverlays");
                    Assert.Empty(windows);

                    one.Enabled = true;
                    Invoke<object>(input, "SyncScreenOverlays");
                    var window = windows[1];
                    Assert.True(window.IsVisible);
                    Assert.True(one.IsRunning);
                    Assert.Equal(0.5, window.ContentView.Opacity);
                    Assert.Equal(200, window.Width);
                    Assert.Equal(120, window.Height);
                    // No saved place: centered, on a monitor.
                    Assert.Equal(0, OverlayPlacement.GetCurrentMonitor(window));

                    // Running it again changes nothing.
                    double left = window.Left;
                    Invoke<object>(input, "SyncScreenOverlays");
                    Assert.Same(window, windows[1]);
                    Assert.Equal(left, window.Left);

                    // The tick feeds it with the Keyboard Screens switch off, and no host opens.
                    KeyboardScreenService.RequestMode(5, KeyboardScreenKind.Overlay, 1, KeyboardScreenMode.Clock);
                    Invoke<object>(input, "TickKeyboardScreens");
                    Assert.False(input.KeyboardScreens.Running);
                    Assert.Matches("^\\d\\d:\\d\\d$", window.ContentView.Shown.Text);

                    // Another program is in front of the test host, so clicks pass through.
                    Assert.False(window.Arranging);

                    // The content's reason reaches the card's status line.
                    input.KeyboardScreens.Update(KeyboardScreenInfo.OverlayRow(1), d => d.Mode = KeyboardScreenMode.Picture);
                    Invoke<object>(input, "TickKeyboardScreens");
                    Assert.Equal(KeyboardScreenService.Blank.NoPicture, one.Blank);
                    Assert.Contains(Strings.Instance.Common_Running, one.Status);
                    Assert.NotEqual(Strings.Instance.Common_Running, one.Status);

                    // A drag lands in the settings, and the window stays where the drag put it.
                    double movedLeft = window.Left + 7, movedTop = window.Top + 5;
                    window.Left = movedLeft;
                    window.Top = movedTop;
                    window.Width = 300;
                    window.Height = 150;
                    Invoke<object>(input, "OnScreenOverlayPositionChanged", 1);
                    Invoke<object>(input, "SyncScreenOverlays");
                    AssertNear(movedLeft, one.Left);
                    AssertNear(movedTop, one.Top);
                    Assert.Equal(300, one.Width);
                    Assert.Equal(150, one.Height);
                    AssertNear(movedLeft, window.Left);
                    AssertNear(movedTop, window.Top);

                    // A saved place with negative coordinates is a place.
                    one.Left = -640;
                    one.Top = -20;
                    typeof(InputService).GetMethod("PlaceScreenOverlay", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { window, one });
                    AssertNear(-640, window.Left);
                    AssertNear(-20, window.Top);

                    // Moved on the list, as a profile switch does, it follows.
                    one.Left = movedLeft + 1;
                    one.Top = movedTop + 1;
                    Invoke<object>(input, "SyncScreenOverlays");
                    AssertNear(movedLeft + 1, window.Left);
                    AssertNear(movedTop + 1, window.Top);

                    // A place in the middle of a drag is the drag's until it ends.
                    SetPrivate(window, "_isMouseDragging", true);
                    double dragging = window.Left;
                    one.Left = dragging + 40;
                    Invoke<object>(input, "SyncScreenOverlays");
                    Assert.Equal(dragging, window.Left);
                    SetPrivate(window, "_isMouseDragging", false);
                    Invoke<object>(input, "SyncScreenOverlays");
                    AssertNear(dragging + 40, window.Left);

                    // A second overlay is a second window, with its own place and opacity.
                    dash.AddScreenOverlay();
                    var two = dash.FindScreenOverlay(2);
                    two.Enabled = true;
                    two.Opacity = 0.3;
                    Invoke<object>(input, "SyncScreenOverlays");
                    Assert.True(windows[2].IsVisible);
                    Assert.NotSame(window, windows[2]);
                    Assert.Equal(0.3, windows[2].ContentView.Opacity);
                    Assert.Equal(0.5, window.ContentView.Opacity);

                    // Reset Position recenters a window that is up, and saves the place.
                    object sender = null;
                    double before = window.Left;
                    Invoke<object>(input, "OnResetScreenOverlayPosition", sender, one);
                    Pump(50);
                    Assert.Equal(window.Left, one.Left);
                    Assert.True(Math.Abs(window.Left - before) > 3);

                    // Toggles: a bit per number, and bit 0 for all of them.
                    input.ToggleScreenOverlays(1 << 2);
                    Assert.False(two.Enabled);
                    Assert.True(one.Enabled);
                    Invoke<object>(input, "SyncScreenOverlays");
                    Assert.False(windows[2].IsVisible);
                    Assert.False(two.IsRunning);
                    input.ToggleScreenOverlays(1);
                    Assert.False(one.Enabled);
                    Assert.False(two.Enabled);
                    input.ToggleScreenOverlays(1);
                    Assert.True(one.Enabled);
                    Assert.True(two.Enabled);
                    input.ToggleScreenOverlays(1 << 2);
                    input.ToggleScreenOverlays(1 << 7);
                    Assert.False(two.Enabled);

                    // A toggle opens the window at once, so a macro that opens an
                    // overlay and shows a picture in one press has the picture up in
                    // the same tick.
                    Assert.False(windows[2].IsVisible);
                    KeyboardScreenService.RequestShow(0, KeyboardScreenKind.Overlay, 2,
                        Convert.ToBase64String(Png(Bitmap(4, 2, Opaque))), 60000, 0);
                    input.ToggleScreenOverlays(1 << 2);
                    Assert.True(windows[2].IsVisible);
                    Invoke<object>(input, "TickKeyboardScreens");
                    Assert.NotNull(windows[2].ContentView.Shown.Picture);
                    input.ToggleScreenOverlays(1 << 2);
                    Assert.False(windows[2].IsVisible);

                    // A new overlay with no place opens clear of the one before it.
                    dash.AddScreenOverlay();
                    var three = dash.FindScreenOverlay(3);
                    three.Enabled = true;
                    three.Width = one.Width;
                    double oneAtReset = window.Left;
                    Invoke<object>(input, "SyncScreenOverlays");
                    AssertNear(oneAtReset + 64, windows[3].Left);
                    three.Enabled = false;
                    Invoke<object>(input, "SyncScreenOverlays");
                    dash.RemoveScreenOverlay(three);
                    Invoke<object>(input, "SyncScreenOverlays");

                    // Hidden, Reset Position clears the place for the next show.
                    Invoke<object>(input, "OnResetScreenOverlayPosition", sender, two);
                    Pump(50);
                    Assert.Equal(OverlayPlacement.NoPlace, two.Left);
                    Assert.Equal(OverlayPlacement.NoPlace, two.Top);

                    // Removed, its window closes.
                    dash.RemoveScreenOverlay(two);
                    Invoke<object>(input, "SyncScreenOverlays");
                    Assert.False(windows.ContainsKey(2));

                    // The Keyboard Screens switch going off leaves a window that is up
                    // with what it holds.
                    Invoke<object>(input, "LetGoOfScreens");
                    Assert.True(input.KeyboardScreens.Active);

                    // With all of them off, a tick ends what they held.
                    one.Enabled = false;
                    Invoke<object>(input, "SyncScreenOverlays");
                    Assert.False(window.IsVisible);
                    Assert.False(one.IsRunning);
                    Assert.True(input.KeyboardScreens.Active);
                    Invoke<object>(input, "TickKeyboardScreens");
                    Assert.False(input.KeyboardScreens.Active);

                    // Back on, the same window comes up.
                    one.Enabled = true;
                    Invoke<object>(input, "SyncScreenOverlays");
                    Assert.Same(window, windows[1]);
                    Assert.True(window.IsVisible);
                }
                finally
                {
                    Invoke<object>(input, "CloseScreenOverlays");
                }
                Assert.Empty(windows);
                Assert.All(dash.ScreenOverlays, o => Assert.False(o.IsRunning));

                // A sync queued as the engine stopped opens nothing.
                Invoke<object>(input, "SyncScreenOverlays");
                Assert.Empty(windows);
                KeyboardScreenService.DropRequests();
            });

        /// <summary>The window's own file keeps its grip in place in every language.</summary>
        [Fact]
        public void TheWindow_StaysLeftToRight_InEveryLanguage()
            => Assert.Contains("PadForge.Common.UiLanguage.KeepLeftToRight(this);",
                RepoText("PadForge.App", "Views", "ScreenOverlay.xaml.cs"));
    }
}
