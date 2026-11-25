using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.OnScreenKeyboard;
using PadForge.Engine.Menus;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The windows shown over a game that a person calls up, the menu
    /// overlay and the On-Screen Keyboard, show on the monitor of the window
    /// in front, as the flyout family does (#600 item 42), and stand at their
    /// menu's Screen Position on it, in that monitor's pixels.
    /// </summary>
    [Collection("FlyoutPlacement")]
    public class FrontMonitorTests
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        private static void Pump(Dispatcher dispatcher, int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background, (_, _) => frame.Continue = false, dispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        /// <summary>The scale of the real monitor nearest a point, which is
        /// the scale Windows gives a window there when it is off every
        /// monitor.</summary>
        private static double ScaleNear(int x, int y)
        {
            var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, 2);
            return GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        }

        /// <summary>A monitor this PC does not have, far off every real one,
        /// at the scale of the real monitor nearest it.</summary>
        private static FlyoutPlacement.WorkArea MadeUpMonitor(int left, int top, int width, int height)
            => new(left, top, left + width, top + height, ScaleNear(left + width / 2, top + height / 2));

        private static (FlyoutPlacement.WorkArea First, FlyoutPlacement.WorkArea Second) MadeUpMonitors()
            => (MadeUpMonitor(-30000, -20000, 2560, 1392), MadeUpMonitor(30000, 20000, 1920, 1040));

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

        private const int WM_DPICHANGED = 0x02E0;

        /// <summary>The scale of the made-up monitor a window moves to: twice
        /// the PC's own. A window's size in pixels then doubles exactly and
        /// its size in DIPs does not change, so WPF has no change of size to
        /// report, and only the handling of the change of scale itself can
        /// put the window at its place.</summary>
        private static double OtherScale() => 2 * FlyoutPlacement.Of(IntPtr.Zero).Scale;

        /// <summary>What Windows sends a window that reaches a monitor of
        /// another scale: WM_DPICHANGED with the new scale and the rectangle
        /// Windows suggests, the window's size at that scale, here 97 and 53
        /// pixels off where the window is. Returns the rectangle as the
        /// window's handlers left it, once WPF has taken the change.</summary>
        private static RECT ChangeScale(System.Windows.Window window, double scale)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            Assert.True(GetWindowRect(hwnd, out var rect));
            double before = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
            int width = (int)Math.Round((rect.Right - rect.Left) * scale / before), height = (int)Math.Round((rect.Bottom - rect.Top) * scale / before);
            var suggested = new RECT { Left = rect.Left + 97, Top = rect.Top + 53, Right = rect.Left + 97 + width, Bottom = rect.Top + 53 + height };
            int dpi = (int)Math.Round(scale * 96);
            SendMessage(hwnd, WM_DPICHANGED, (IntPtr)(dpi | (dpi << 16)), ref suggested);
            // The control: WPF took the new scale.
            Assert.Equal(scale, System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX, 3);
            return suggested;
        }

        /// <summary>The window's corner, from its own rectangle, is where a
        /// placement puts a window of its size, within a pixel.</summary>
        private static void AssertAt(System.Windows.Window window, Func<int, int, (int X, int Y)> place)
        {
            Assert.True(GetWindowRect(new WindowInteropHelper(window).Handle, out var rect));
            var (x, y) = place(rect.Right - rect.Left, rect.Bottom - rect.Top);
            Assert.True(Math.Abs(rect.Left - x) <= 1 && Math.Abs(rect.Top - y) <= 1,
                $"The window ({rect.Left}, {rect.Top}, {rect.Right}, {rect.Bottom}) belongs at ({x}, {y}).");
        }

        private static (int Left, int Top) TopLeft(System.Windows.Window window)
        {
            Assert.True(GetWindowRect(new WindowInteropHelper(window).Handle, out var rect));
            return (rect.Left, rect.Top);
        }

        private static void AssertInside(System.Windows.Window window, FlyoutPlacement.WorkArea area)
        {
            Assert.True(GetWindowRect(new WindowInteropHelper(window).Handle, out var rect));
            Assert.True(rect.Left >= area.Left && rect.Right <= area.Right && rect.Top >= area.Top && rect.Bottom <= area.Bottom,
                $"The window ({rect.Left}, {rect.Top}, {rect.Right}, {rect.Bottom}) is not on the work area {area}.");
        }

        // ── The menu overlay ──

        /// <summary>A menu called up during a game shows on the game's
        /// monitor. The monitor is read once, while the overlay is still
        /// hidden, and the overlay stays on it while the menu is up, however
        /// the menu changes. Called up again, it reads the monitor
        /// again.</summary>
        [Fact]
        public void TheMenuOverlay_StandsOnTheMonitorOfTheWindowInFront_AtItsScreenPosition()
        {
            var savedFront = FlyoutPlacement.ForegroundSeam;
            var savedMonitor = FlyoutPlacement.MonitorSeam;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var (first, second) = MadeUpMonitors();
                var front = first;
                var overlay = new MenuOverlayWindow();
                // Whether the overlay was up at each reading of the monitor.
                var read = new List<bool>();
                FlyoutPlacement.ForegroundSeam = () => (IntPtr)0x5150;
                FlyoutPlacement.MonitorSeam = window =>
                {
                    read.Add(overlay.IsVisible);
                    return front;
                };
                void Update(MenuDefinitionEntry menu)
                {
                    overlay.UpdateFromSnapshot(new InputManager.MenuOverlayState { Menu = menu, HoveredIndex = -1 });
                    // Nothing shows on the PC's own desktop, wherever the
                    // window lands.
                    overlay.Opacity = 0;
                    Pump(dispatcher, 50);
                }
                try
                {
                    var ring = new MenuDefinitionEntry { Kind = MenuKind.Radial, CellCount = 4, PosXPercent = 25, PosYPercent = 75 };
                    Update(ring);
                    Assert.Equal(new[] { false }, read);
                    Assert.Equal(FlyoutPlacement.At(first, overlay.MenuCanvas.Width, overlay.MenuCanvas.Height, 25, 75), TopLeft(overlay));
                    AssertInside(overlay, first);

                    // Another menu while one is up redraws on the same
                    // monitor, though the window in front went to another.
                    front = second;
                    var grid = new MenuDefinitionEntry { Kind = MenuKind.Grid, CellCount = 6, PosXPercent = 25, PosYPercent = 75 };
                    Update(grid);
                    Assert.Single(read);
                    Assert.Equal(FlyoutPlacement.At(first, overlay.MenuCanvas.Width, overlay.MenuCanvas.Height, 25, 75), TopLeft(overlay));
                    AssertInside(overlay, first);

                    // Gone and called up again: the monitor in front now.
                    overlay.UpdateFromSnapshot(null);
                    Assert.False(overlay.IsVisible);
                    Update(grid);
                    Assert.Equal(new[] { false, false }, read);
                    Assert.Equal(FlyoutPlacement.At(second, overlay.MenuCanvas.Width, overlay.MenuCanvas.Height, 25, 75), TopLeft(overlay));
                    AssertInside(overlay, second);
                }
                finally
                {
                    FlyoutPlacement.ForegroundSeam = savedFront;
                    FlyoutPlacement.MonitorSeam = savedMonitor;
                    overlay.Close();
                }
            }, TimeSpan.FromMinutes(2), "The menu overlay test timed out.");
        }

        // ── The On-Screen Keyboard ──

        /// <summary>The keyboard's margin inside the work area, in DIPs
        /// (OnScreenKeyboardWindow.EdgeMargin).</summary>
        private const double KeyboardMargin = 12;

        /// <summary>An open keyboard's snapshot, built by the engine with a
        /// US layout and a record of what it would send.</summary>
        private static OskView OpenKeyboard()
        {
            var engine = new OskEngine(new OnScreenKeyboardEngineTests.Record());
            engine.SetLayout(new OnScreenKeyboardEngineTests.UsLayout());
            engine.ApplySettings(new OskSettings { Suggestions = false });
            engine.Open(new OskControlState());
            return OskView.Build(engine, OskFamily.Xbox, Environment.TickCount64, slot: 0, menuId: 1);
        }

        /// <summary>The keyboard types into the window in front, so it shows
        /// on that window's monitor, read once while it is still hidden. A
        /// drag to another monitor keeps the keyboard there, and the
        /// position the drag hands back is a Screen Position on the monitor
        /// it was dropped on. Opened again, it reads the monitor in front
        /// again.</summary>
        [Fact]
        public void TheKeyboard_StandsOnTheMonitorOfTheWindowInFront_AndStaysOnTheOneItIsDraggedTo()
        {
            var savedFront = FlyoutPlacement.ForegroundSeam;
            var savedMonitor = FlyoutPlacement.MonitorSeam;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var (first, second) = MadeUpMonitors();
                var dropped = (X: double.NaN, Y: double.NaN);
                var keyboard = new OnScreenKeyboardWindow(_ => { }, (x, y) => dropped = (x, y), _ => { });
                var read = new List<bool>();
                FlyoutPlacement.ForegroundSeam = () => (IntPtr)0x5150;
                FlyoutPlacement.MonitorSeam = window =>
                {
                    // The window in front stays on the first monitor.
                    if (window == (IntPtr)0x5150)
                    {
                        read.Add(keyboard.IsVisible);
                        return first;
                    }
                    // Any other window is on the monitor its center is on.
                    Assert.True(GetWindowRect(window, out var rect));
                    return rect.Left + (rect.Right - rect.Left) / 2 >= second.Left ? second : first;
                };
                var view = OpenKeyboard();
                var menu = new MenuDefinitionEntry { Kind = MenuKind.Keyboard, PosXPercent = 25, PosYPercent = 75 };
                // Two ticks of the UI timer: a keyboard that a placement took
                // to a monitor of another scale is placed again on the
                // second.
                void Update()
                {
                    for (int tick = 0; tick < 2; tick++)
                    {
                        keyboard.UpdateFromView(view, menu, (descriptor, device) => descriptor);
                        // Nothing shows on the PC's own desktop, wherever the
                        // window lands.
                        keyboard.Opacity = 0;
                        Pump(dispatcher, 30);
                    }
                }
                try
                {
                    Update();
                    Assert.Equal(new[] { false }, read);
                    Assert.Equal(FlyoutPlacement.At(first, keyboard.ActualWidth, keyboard.ActualHeight, 25, 75, KeyboardMargin), TopLeft(keyboard));
                    AssertInside(keyboard, first);

                    // Dragged to the second monitor, its center at 60 and 40
                    // there, and let go.
                    FlyoutPlacement.PlaceAt(keyboard, second, keyboard.ActualWidth, keyboard.ActualHeight, 60, 40);
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    typeof(OnScreenKeyboardWindow).GetField("_dragging", flags).SetValue(keyboard, true);
                    typeof(OnScreenKeyboardWindow).GetMethod("DragEnd", flags).Invoke(keyboard, new object[] { null, null });
                    Assert.Equal((60.0, 40.0), dropped);

                    // The menu keeps that Screen Position, and the next tick
                    // leaves the keyboard on the monitor it was dropped on.
                    menu.PosXPercent = 60;
                    menu.PosYPercent = 40;
                    Update();
                    Assert.Single(read);
                    Assert.Equal(FlyoutPlacement.At(second, keyboard.ActualWidth, keyboard.ActualHeight, 60, 40, KeyboardMargin), TopLeft(keyboard));
                    AssertInside(keyboard, second);

                    // Closed and opened again: the monitor in front now.
                    keyboard.HideKeyboard();
                    Assert.False(keyboard.IsVisible);
                    Update();
                    Assert.Equal(new[] { false, false }, read);
                    Assert.Equal(FlyoutPlacement.At(first, keyboard.ActualWidth, keyboard.ActualHeight, 60, 40, KeyboardMargin), TopLeft(keyboard));
                    AssertInside(keyboard, first);
                }
                finally
                {
                    FlyoutPlacement.ForegroundSeam = savedFront;
                    FlyoutPlacement.MonitorSeam = savedMonitor;
                    keyboard.Close();
                }
            }, TimeSpan.FromMinutes(2), "The keyboard test timed out.");
        }

        /// <summary>A keyboard larger than the work area of the monitor in
        /// front shrinks to fit it, the work area counted in DIPs at that
        /// monitor's own scale, and keeps its margin inside it.</summary>
        [Fact]
        public void AKeyboardLargerThanTheMonitorInFront_ShrinksToFitIt()
        {
            var savedFront = FlyoutPlacement.ForegroundSeam;
            var savedMonitor = FlyoutPlacement.MonitorSeam;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var small = MadeUpMonitor(-30000, -20000, 600, 400);
                double scale = small.Scale;
                var keyboard = new OnScreenKeyboardWindow(_ => { }, (x, y) => { }, _ => { });
                FlyoutPlacement.ForegroundSeam = () => (IntPtr)0x5150;
                FlyoutPlacement.MonitorSeam = _ => small;
                try
                {
                    var menu = new MenuDefinitionEntry { Kind = MenuKind.Keyboard, PosXPercent = 50, PosYPercent = 50 };
                    var view = OpenKeyboard();
                    for (int tick = 0; tick < 2; tick++)
                    {
                        keyboard.UpdateFromView(view, menu, (descriptor, device) => descriptor);
                        keyboard.Opacity = 0;
                        Pump(dispatcher, 30);
                    }
                    // It was shrunk: at its menu's size it does not fit.
                    Assert.True(keyboard.Scale.ScaleX < 1, $"Scale {keyboard.Scale.ScaleX}");
                    Assert.True(keyboard.ActualWidth <= 600 / scale - 2 * KeyboardMargin + 0.5, $"{keyboard.ActualWidth} DIPs wide");
                    Assert.True(keyboard.ActualHeight <= 400 / scale - 2 * KeyboardMargin + 0.5, $"{keyboard.ActualHeight} DIPs high");
                    Assert.Equal(FlyoutPlacement.At(small, keyboard.ActualWidth, keyboard.ActualHeight, 50, 50, KeyboardMargin), TopLeft(keyboard));
                    AssertInside(keyboard, small);
                }
                finally
                {
                    FlyoutPlacement.ForegroundSeam = savedFront;
                    FlyoutPlacement.MonitorSeam = savedMonitor;
                    keyboard.Close();
                }
            }, TimeSpan.FromMinutes(2), "The keyboard fit test timed out.");
        }

        // ── A change of scale ──
        //
        // Windows tells a window that reaches a monitor of another scale with
        // WM_DPICHANGED and a rectangle it suggests, and WPF moves the window
        // to that rectangle. Each test shows a window on a made-up monitor in
        // front at another scale than the PC's own, then sends that message
        // with the made-up monitor's scale, as Windows would. The window must
        // stand at its place the moment WPF has moved it, and after WPF has
        // laid it out at the new scale, whatever WPF does with its size.

        /// <summary>A flyout's size, held for a test at whole pixels at the
        /// PC's own scale and at twice it, so a change of scale between the
        /// two leaves its size in DIPs as it was. WPF then reports no change
        /// of size, and the flyout's own placement on a change of size cannot
        /// be what puts it back.</summary>
        private static void HoldSize(System.Windows.Window window, System.Windows.Threading.Dispatcher dispatcher)
        {
            window.SizeToContent = System.Windows.SizeToContent.Manual;
            window.Width = 300;
            window.Height = 80;
            Pump(dispatcher, 50);
        }

        /// <summary>The profile flyout, at the bottom center: through a
        /// change of scale that also changes its size, since its text
        /// measures otherwise at the new scale, and through one that leaves
        /// its size alone.</summary>
        [Fact]
        public void TheProfileFlyout_StandsAtItsPlace_ThroughAChangeOfScale()
        {
            var savedFront = FlyoutPlacement.ForegroundSeam;
            var savedMonitor = FlyoutPlacement.MonitorSeam;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                double other = OtherScale(), own = FlyoutPlacement.Of(IntPtr.Zero).Scale;
                var area = new FlyoutPlacement.WorkArea(-30000, -20000, -30000 + 2560, -20000 + 1392, other);
                FlyoutPlacement.ForegroundSeam = () => (IntPtr)0x5150;
                FlyoutPlacement.MonitorSeam = _ => area;
                var flyout = new ProfileSwitchOverlay();
                try
                {
                    flyout.ShowProfileName("Racing");
                    Pump(dispatcher, 50);
                    ChangeScale(flyout, other);
                    AssertAt(flyout, (w, h) => FlyoutPlacement.BottomCenterInPixels(area, w, h));
                    Pump(dispatcher, 100);
                    AssertAt(flyout, (w, h) => FlyoutPlacement.BottomCenterInPixels(area, w, h));

                    HoldSize(flyout, dispatcher);
                    ChangeScale(flyout, own);
                    AssertAt(flyout, (w, h) => FlyoutPlacement.BottomCenterInPixels(area, w, h));
                    Pump(dispatcher, 100);
                    AssertAt(flyout, (w, h) => FlyoutPlacement.BottomCenterInPixels(area, w, h));
                }
                finally
                {
                    FlyoutPlacement.ForegroundSeam = savedFront;
                    FlyoutPlacement.MonitorSeam = savedMonitor;
                    flyout.StopTimers();
                    flyout.Close();
                }
            }, TimeSpan.FromMinutes(2), "The profile flyout scale test timed out.");
        }

        /// <summary>The shift-layer flyout, at the bottom center, the same
        /// two ways.</summary>
        [Fact]
        public void TheShiftLayerFlyout_StandsAtItsPlace_ThroughAChangeOfScale()
        {
            var savedFront = FlyoutPlacement.ForegroundSeam;
            var savedMonitor = FlyoutPlacement.MonitorSeam;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                double other = OtherScale(), own = FlyoutPlacement.Of(IntPtr.Zero).Scale;
                var area = new FlyoutPlacement.WorkArea(-30000, -20000, -30000 + 2560, -20000 + 1392, other);
                FlyoutPlacement.ForegroundSeam = () => (IntPtr)0x5150;
                FlyoutPlacement.MonitorSeam = _ => area;
                var layer = new ShiftLayerFlyout();
                try
                {
                    layer.ShowLayer("Aim", "#FF6B2C", null);
                    Pump(dispatcher, 50);
                    ChangeScale(layer, other);
                    AssertAt(layer, (w, h) => FlyoutPlacement.BottomCenterInPixels(area, w, h));
                    Pump(dispatcher, 100);
                    AssertAt(layer, (w, h) => FlyoutPlacement.BottomCenterInPixels(area, w, h));

                    HoldSize(layer, dispatcher);
                    ChangeScale(layer, own);
                    AssertAt(layer, (w, h) => FlyoutPlacement.BottomCenterInPixels(area, w, h));
                    Pump(dispatcher, 100);
                    AssertAt(layer, (w, h) => FlyoutPlacement.BottomCenterInPixels(area, w, h));
                }
                finally
                {
                    FlyoutPlacement.ForegroundSeam = savedFront;
                    FlyoutPlacement.MonitorSeam = savedMonitor;
                    layer.Close();
                }
            }, TimeSpan.FromMinutes(2), "The shift-layer flyout scale test timed out.");
        }

        /// <summary>The menu overlay, at its Screen Position.</summary>
        [Fact]
        public void TheMenuOverlay_StandsAtItsPlace_ThroughAChangeOfScale()
        {
            var savedFront = FlyoutPlacement.ForegroundSeam;
            var savedMonitor = FlyoutPlacement.MonitorSeam;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                double other = OtherScale();
                var area = new FlyoutPlacement.WorkArea(-30000, -20000, -30000 + 2560, -20000 + 1392, other);
                FlyoutPlacement.ForegroundSeam = () => (IntPtr)0x5150;
                FlyoutPlacement.MonitorSeam = _ => area;
                var overlay = new MenuOverlayWindow();
                try
                {
                    var ring = new MenuDefinitionEntry { Kind = MenuKind.Radial, CellCount = 4, PosXPercent = 25, PosYPercent = 75 };
                    overlay.UpdateFromSnapshot(new InputManager.MenuOverlayState { Menu = ring, HoveredIndex = -1 });
                    overlay.Opacity = 0;
                    Pump(dispatcher, 50);
                    ChangeScale(overlay, other);
                    AssertAt(overlay, (w, h) => FlyoutPlacement.AtInPixels(area, w, h, 25, 75));
                    Pump(dispatcher, 100);
                    AssertAt(overlay, (w, h) => FlyoutPlacement.AtInPixels(area, w, h, 25, 75));
                }
                finally
                {
                    FlyoutPlacement.ForegroundSeam = savedFront;
                    FlyoutPlacement.MonitorSeam = savedMonitor;
                    overlay.Close();
                }
            }, TimeSpan.FromMinutes(2), "The menu overlay scale test timed out.");
        }

        /// <summary>The keyboard, at its Screen Position with its margin at
        /// the new scale. While the user drags it, Windows' rectangle stands:
        /// it keeps the keyboard under the pointer.</summary>
        [Fact]
        public void TheKeyboard_StandsAtItsPlace_ThroughAChangeOfScale_ButNotWhileDragged()
        {
            var savedFront = FlyoutPlacement.ForegroundSeam;
            var savedMonitor = FlyoutPlacement.MonitorSeam;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                double other = OtherScale(), own = FlyoutPlacement.Of(IntPtr.Zero).Scale;
                var area = new FlyoutPlacement.WorkArea(-30000, -20000, -30000 + 2560, -20000 + 1392, other);
                FlyoutPlacement.ForegroundSeam = () => (IntPtr)0x5150;
                FlyoutPlacement.MonitorSeam = _ => area;
                var keyboard = new OnScreenKeyboardWindow(_ => { }, (x, y) => { }, _ => { });
                var view = OpenKeyboard();
                var menu = new MenuDefinitionEntry { Kind = MenuKind.Keyboard, PosXPercent = 25, PosYPercent = 75 };
                void Tick()
                {
                    keyboard.UpdateFromView(view, menu, (descriptor, device) => descriptor);
                    keyboard.Opacity = 0;
                    Pump(dispatcher, 30);
                }
                int margin = (int)Math.Round(KeyboardMargin * other, MidpointRounding.AwayFromZero);
                try
                {
                    Tick();
                    ChangeScale(keyboard, other);
                    AssertAt(keyboard, (w, h) => FlyoutPlacement.AtInPixels(area, w, h, 25, 75, margin));
                    Tick();
                    AssertAt(keyboard, (w, h) => FlyoutPlacement.AtInPixels(area, w, h, 25, 75, margin));

                    // In the middle of a drag, Windows' rectangle stands.
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    typeof(OnScreenKeyboardWindow).GetField("_dragging", flags).SetValue(keyboard, true);
                    var suggested = ChangeScale(keyboard, own);
                    Assert.Equal((suggested.Left, suggested.Top), TopLeft(keyboard));
                }
                finally
                {
                    FlyoutPlacement.ForegroundSeam = savedFront;
                    FlyoutPlacement.MonitorSeam = savedMonitor;
                    keyboard.Close();
                }
            }, TimeSpan.FromMinutes(2), "The keyboard scale test timed out.");
        }

        // ── The Touchpad Overlay and the Screen Overlays ──

        private static object GetPrivate(object target, string field)
            => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        private static void SetPrivate(object target, string field, object value)
            => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        private static void Invoke(object target, string method, params object[] args)
            => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);

        /// <summary>Two monitors in front made up inside the PC's own one, so a
        /// window centered on either stays on a screen and nothing moves it
        /// back, each off the real monitor's center, and at a scale other than
        /// the real one, as a monitor in front can be: a window that moves
        /// there is not yet the size it will have, and must be centered at the
        /// size it has.</summary>
        private static (FlyoutPlacement.WorkArea First, FlyoutPlacement.WorkArea Second) FrontsOnThisScreen()
        {
            var real = FlyoutPlacement.Of(IntPtr.Zero);
            double other = real.Scale == 1.0 ? 1.5 : 1.0;
            return (new FlyoutPlacement.WorkArea(real.Left + 16, real.Top + 16, real.Left + 16 + real.Width * 3 / 5, real.Top + 16 + real.Height * 3 / 5, other),
                    new FlyoutPlacement.WorkArea(real.Right - 16 - real.Width * 3 / 5, real.Bottom - 16 - real.Height * 3 / 5, real.Right - 16, real.Bottom - 16, other));
        }

        private static void AssertCentered(System.Windows.Window window, FlyoutPlacement.WorkArea area, int step = 0)
        {
            Assert.True(GetWindowRect(new WindowInteropHelper(window).Handle, out var rect));
            int x = area.Left + (area.Width - (rect.Right - rect.Left)) / 2 + step, y = area.Top + (area.Height - (rect.Bottom - rect.Top)) / 2 + step;
            Assert.True(Math.Abs(rect.Left - x) <= 1 && Math.Abs(rect.Top - y) <= 1,
                $"The window ({rect.Left}, {rect.Top}, {rect.Right}, {rect.Bottom}) is not centered on {area}: its corner belongs at ({x}, {y}).");
        }

        /// <summary>A Touchpad Overlay the user never placed shows centered on
        /// the monitor of the window in front, read while it is hidden, at the
        /// size it has there. One the user placed keeps its place, and nothing
        /// reads the monitor in front for it. Reset Position while it is
        /// hidden clears its place, so its next showing is centered on the
        /// monitor in front then.</summary>
        [Fact]
        public void TheTouchpadOverlay_NeverPlaced_ShowsOnTheMonitorOfTheWindowInFront()
        {
            var savedFront = FlyoutPlacement.ForegroundSeam;
            var savedMonitor = FlyoutPlacement.MonitorSeam;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var (first, second) = FrontsOnThisScreen();
                var front = first;
                var vm = new MainViewModel();
                var input = new InputService(vm);
                var dash = vm.Dashboard;
                // Made here so the test can keep it from showing on the desktop.
                var overlay = new TouchpadOverlay { Opacity = 0 };
                SetPrivate(input, "_touchpadOverlay", overlay);
                var read = new List<bool>();
                FlyoutPlacement.ForegroundSeam = () => (IntPtr)0x5150;
                // The window in front is on the monitor in front. The overlay
                // is on the first monitor, where it opened.
                FlyoutPlacement.MonitorSeam = window =>
                {
                    if (window != (IntPtr)0x5150) return first;
                    read.Add(overlay.IsVisible);
                    return front;
                };
                void Show()
                {
                    Invoke(input, "ShowTouchpadOverlay");
                    Pump(dispatcher, 80);
                }
                void Hide()
                {
                    Invoke(input, "HideTouchpadOverlay", false);
                    Pump(dispatcher, 80);
                }
                try
                {
                    Assert.Equal(-1, dash.TouchpadOverlayLeft);
                    Assert.Equal(-1, dash.TouchpadOverlayTop);
                    Show();
                    Assert.True(overlay.IsVisible);
                    Assert.Equal(new[] { false }, read);
                    AssertCentered(overlay, first);

                    // Reset Position while it is up centers it on the monitor it
                    // is on, wherever the window in front is now, and saves that.
                    front = second;
                    Invoke(input, "OnResetTouchpadOverlayPosition", null, EventArgs.Empty);
                    Pump(dispatcher, 80);
                    AssertCentered(overlay, first);
                    Assert.Single(read);
                    Assert.Equal((overlay.Left, overlay.Top), (dash.TouchpadOverlayLeft, dash.TouchpadOverlayTop));
                    front = first;

                    // Placed: it keeps its place.
                    Hide();
                    dash.TouchpadOverlayLeft = 120;
                    dash.TouchpadOverlayTop = 90;
                    Show();
                    Assert.Single(read);
                    Assert.Equal((120.0, 90.0), (overlay.Left, overlay.Top));

                    // Reset Position while it is hidden: its next showing is on
                    // the monitor in front then.
                    Hide();
                    Invoke(input, "OnResetTouchpadOverlayPosition", null, EventArgs.Empty);
                    Pump(dispatcher, 80);
                    Assert.Equal((-1.0, -1.0), (dash.TouchpadOverlayLeft, dash.TouchpadOverlayTop));
                    front = second;
                    Show();
                    Assert.Equal(new[] { false, false }, read);
                    AssertCentered(overlay, second);
                }
                finally
                {
                    FlyoutPlacement.ForegroundSeam = savedFront;
                    FlyoutPlacement.MonitorSeam = savedMonitor;
                    Invoke(input, "HideTouchpadOverlay", true);
                    Pump(dispatcher, 50);
                }
            }, TimeSpan.FromMinutes(2), "The touchpad overlay test timed out.");
        }

        /// <summary>The same for a Screen Overlay, stepped down and across by
        /// its number as before.</summary>
        [Fact]
        public void AScreenOverlay_NeverPlaced_ShowsOnTheMonitorOfTheWindowInFront()
        {
            var savedFront = FlyoutPlacement.ForegroundSeam;
            var savedMonitor = FlyoutPlacement.MonitorSeam;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var (first, second) = FrontsOnThisScreen();
                var front = first;
                var vm = new MainViewModel();
                var input = new InputService(vm);
                var item = Assert.Single(vm.Dashboard.ScreenOverlays);
                Assert.Equal(1, item.Number);
                // Made here so the test can keep it from showing on the desktop.
                var overlay = new ScreenOverlay { Opacity = 0 };
                ((Dictionary<int, ScreenOverlay>)GetPrivate(input, "_screenOverlays"))[item.Number] = overlay;
                var read = new List<bool>();
                FlyoutPlacement.ForegroundSeam = () => (IntPtr)0x5150;
                // The window in front is on the monitor in front. The overlay
                // is on the first monitor, where it opened.
                FlyoutPlacement.MonitorSeam = window =>
                {
                    if (window != (IntPtr)0x5150) return first;
                    read.Add(overlay.IsVisible);
                    return front;
                };
                void Sync(bool on)
                {
                    item.Enabled = on;
                    Invoke(input, "SyncScreenOverlays");
                    Pump(dispatcher, 80);
                }
                try
                {
                    Assert.False(OverlayPlacement.HasPlace(item.Left, item.Top));
                    Sync(true);
                    Assert.True(overlay.IsVisible);
                    Assert.Equal(new[] { false }, read);
                    AssertCentered(overlay, first);

                    // A second sync while it is up moves nothing.
                    Sync(true);
                    Assert.Single(read);
                    AssertCentered(overlay, first);

                    // Reset Position while it is up centers it on the monitor it
                    // is on, wherever the window in front is now, and saves that.
                    front = second;
                    Invoke(input, "OnResetScreenOverlayPosition", null, item);
                    Pump(dispatcher, 80);
                    AssertCentered(overlay, first);
                    Assert.Single(read);
                    Assert.Equal((overlay.Left, overlay.Top), (item.Left, item.Top));
                    front = first;

                    // Placed: it keeps its place.
                    Sync(false);
                    item.Left = 200;
                    item.Top = 150;
                    Sync(true);
                    Assert.Single(read);
                    Assert.Equal((200.0, 150.0), (overlay.Left, overlay.Top));

                    // Reset Position while it is hidden.
                    Sync(false);
                    Invoke(input, "OnResetScreenOverlayPosition", null, item);
                    Pump(dispatcher, 80);
                    Assert.False(OverlayPlacement.HasPlace(item.Left, item.Top));
                    front = second;
                    Sync(true);
                    Assert.Equal(new[] { false, false }, read);
                    AssertCentered(overlay, second);
                }
                finally
                {
                    FlyoutPlacement.ForegroundSeam = savedFront;
                    FlyoutPlacement.MonitorSeam = savedMonitor;
                    Invoke(input, "CloseScreenOverlays");
                    Pump(dispatcher, 50);
                }
            }, TimeSpan.FromMinutes(2), "The screen overlay test timed out.");
        }

        // ── Dialogs ──

        /// <summary>A work area in the DIPs of a window on that monitor.</summary>
        private static System.Windows.Rect InDips(FlyoutPlacement.WorkArea area)
            => new(area.Left / area.Scale, area.Top / area.Scale, area.Width / area.Scale, area.Height / area.Scale);

        /// <summary>A tab change that resizes Get Profiles holds its size
        /// inside the work area of the monitor it is on and centers it there
        /// again, never on the primary monitor. The dialog has a handle, as a
        /// shown one does, and stays off the desktop.</summary>
        [Fact]
        public void GetProfiles_ATabChange_KeepsItInsideTheMonitorItIsOn()
        {
            var savedMonitor = FlyoutPlacement.MonitorSeam;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                double scale = FlyoutPlacement.Of(IntPtr.Zero).Scale;
                // A monitor right of every real one, at this PC's scale, room
                // for Community's 1100 by 780 DIPs, and a smaller one.
                var large = new FlyoutPlacement.WorkArea(30000, 20000, 30000 + (int)(1400 * scale), 20000 + (int)(900 * scale), scale);
                var small = new FlyoutPlacement.WorkArea(30000, 20000, 30000 + (int)(900 * scale), 20000 + (int)(700 * scale), scale);
                var own = large;
                var dialog = new GetProfilesDialog(new SettingsViewModel { EnableCommunityConfigLookup = false }, GetProfilesTab.Starters);
                FlyoutPlacement.MonitorSeam = _ => own;
                try
                {
                    new WindowInteropHelper(dialog).EnsureHandle();
                    void Tab(GetProfilesTab tab) => Invoke(dialog, "ShowTab", tab);

                    // Community is larger than Starters: the dialog takes its
                    // size and is centered on its own monitor, which it has no
                    // owner to center on.
                    Tab(GetProfilesTab.Community);
                    var dips = InDips(large);
                    Assert.Equal((1100.0, 780.0), (dialog.Width, dialog.Height));
                    Assert.InRange(dialog.Left, dips.Left + (dips.Width - 1100) / 2 - 1, dips.Left + (dips.Width - 1100) / 2 + 1);
                    Assert.InRange(dialog.Top, dips.Top + (dips.Height - 780) / 2 - 1, dips.Top + (dips.Height - 780) / 2 + 1);

                    // On a smaller monitor, Community is held to 95% of its work
                    // area and stays inside it.
                    Tab(GetProfilesTab.Starters);
                    own = small;
                    Tab(GetProfilesTab.Community);
                    dips = InDips(small);
                    // WPF gives a window a whole number of pixels: within one DIP.
                    Assert.InRange(dialog.Width, dips.Width * 0.95 - 1, dips.Width * 0.95 + 1);
                    Assert.InRange(dialog.Height, dips.Height * 0.95 - 1, dips.Height * 0.95 + 1);
                    Assert.True(dialog.Left >= dips.Left && dialog.Left + dialog.Width <= dips.Right
                        && dialog.Top >= dips.Top && dialog.Top + dialog.Height <= dips.Bottom,
                        $"({dialog.Left}, {dialog.Top}, {dialog.Width}, {dialog.Height}) is not inside {dips}.");
                }
                finally
                {
                    FlyoutPlacement.MonitorSeam = savedMonitor;
                    dialog.Close();
                }
            }, TimeSpan.FromMinutes(2), "The Get Profiles test timed out.");
        }

        /// <summary>A dialog's height is held to the work area of the monitor
        /// it opens on, less 64 DIPs, so a long list scrolls inside it: the
        /// monitor of its owner, which it opens over, or its own with no
        /// owner. Neither is the primary monitor here.</summary>
        [Fact]
        public void ADialogsHeight_IsHeldToTheMonitorItOpensOn()
        {
            var savedMonitor = FlyoutPlacement.MonitorSeam;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                double scale = FlyoutPlacement.Of(IntPtr.Zero).Scale;
                var ownersMonitor = new FlyoutPlacement.WorkArea(30000, 20000, 30000 + (int)(1200 * scale), 20000 + (int)(500 * scale), scale);
                var ownMonitor = new FlyoutPlacement.WorkArea(-30000, -20000, -30000 + (int)(1200 * scale), -20000 + (int)(400 * scale), scale);
                // Its own AllowsTransparency, so no style of the app's changes
                // it once it shows.
                var owner = new System.Windows.Window
                {
                    AllowsTransparency = false,
                    WindowStyle = System.Windows.WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -32000, Top = -32000, Width = 120, Height = 80,
                };
                IntPtr ownerHandle = IntPtr.Zero;
                FlyoutPlacement.MonitorSeam = window => window != IntPtr.Zero && window == ownerHandle ? ownersMonitor : ownMonitor;
                // Shown as the app shows a dialog, off the desktop: alone at a
                // place of its own, the other over its owner.
                var alone = new DialogWindow
                {
                    WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowActivated = false,
                };
                var owned = new DialogWindow { ShowActivated = false };
                try
                {
                    owner.Show();
                    ownerHandle = new WindowInteropHelper(owner).Handle;

                    // No owner: the monitor it is on.
                    alone.Show();
                    Assert.Equal(InDips(ownMonitor).Height - 64, alone.MaxHeight, 6);

                    // An owner: the monitor of the window it opens over.
                    owned.Owner = owner;
                    owned.Show();
                    Assert.Equal(InDips(ownersMonitor).Height - 64, owned.MaxHeight, 6);
                }
                finally
                {
                    FlyoutPlacement.MonitorSeam = savedMonitor;
                    alone.Close();
                    owned.Close();
                    owner.Close();
                }
            }, TimeSpan.FromMinutes(2), "The dialog height test timed out.");
        }

        // ── A Screen Position on a work area ──

        [Fact]
        public void AScreenPosition_PutsTheWindowsCenterAtItsPercents_InThatMonitorsPixels()
        {
            // A monitor right of the primary, at 150%: a window of 300 by 200
            // DIPs is 450 by 300 pixels there.
            var area = new FlyoutPlacement.WorkArea(2560, 0, 2560 + 3840, 2088, 1.5);
            Assert.Equal((2560 + 1920 - 225, 1044 - 150), FlyoutPlacement.At(area, 300, 200, 50, 50));
            Assert.Equal((2560 + 960 - 225, 1566 - 150), FlyoutPlacement.At(area, 300, 200, 25, 75));

            // The corners keep the whole window on the work area, and a
            // margin in DIPs is that many DIPs at the monitor's scale.
            Assert.Equal((2560, 0), FlyoutPlacement.At(area, 300, 200, 0, 0));
            Assert.Equal((2560 + 3840 - 450, 2088 - 300), FlyoutPlacement.At(area, 300, 200, 100, 100));
            Assert.Equal((2560 + 18, 18), FlyoutPlacement.At(area, 300, 200, 0, 0, margin: 12));
            Assert.Equal((2560 + 3840 - 18 - 450, 2088 - 18 - 300), FlyoutPlacement.At(area, 300, 200, 100, 100, margin: 12));

            // A percent past either end counts as that end.
            Assert.Equal(FlyoutPlacement.At(area, 300, 200, 0, 100), FlyoutPlacement.At(area, 300, 200, -10, 150));

            // A monitor left of the primary and above it, at 100%: a part of
            // a pixel rounds away from zero, on both sides of it.
            var left = new FlyoutPlacement.WorkArea(-1920, -300, 0, 740, 1.0);
            Assert.Equal((-1111, 185), FlyoutPlacement.At(left, 300.4, 70.2, 50, 50));

            // A window larger than the work area keeps its left and top on it.
            var small = new FlyoutPlacement.WorkArea(0, 0, 800, 600, 1.0);
            Assert.Equal((0, 0), FlyoutPlacement.At(small, 1000, 700, 50, 50));
            Assert.Equal((12, 12), FlyoutPlacement.At(small, 1000, 700, 100, 100, margin: 12));
        }

        [Fact]
        public void APointsScreenPosition_IsItsPercentOfTheWorkArea()
        {
            var area = new FlyoutPlacement.WorkArea(2560, 0, 2560 + 3840, 2088, 1.5);
            // The center of a window At put at 25 and 75 reads 25 and 75.
            var (x, y) = FlyoutPlacement.At(area, 300, 200, 25, 75);
            Assert.Equal((25, 75), FlyoutPlacement.PercentOf(area, x + 225, y + 150));

            // A point off the work area reads as its nearest edge.
            Assert.Equal((0, 100), FlyoutPlacement.PercentOf(area, 0, 5000));
            Assert.Equal((100, 0), FlyoutPlacement.PercentOf(area, 9000, -40));

            // A work area at negative coordinates.
            var left = new FlyoutPlacement.WorkArea(-1920, -300, 0, 740, 1.0);
            Assert.Equal((50, 50), FlyoutPlacement.PercentOf(left, -960, 220));
            Assert.Equal((0, 0), FlyoutPlacement.PercentOf(left, -1920, -300));
        }

        [Fact]
        public void TheMonitorSeam_AnswersForTheWindowInFront_AndForAnyWindow()
        {
            var savedFront = FlyoutPlacement.ForegroundSeam;
            var savedMonitor = FlyoutPlacement.MonitorSeam;
            try
            {
                var front = new FlyoutPlacement.WorkArea(-30000, -20000, -30000 + 2560, -20000 + 1392, 1.5);
                var other = new FlyoutPlacement.WorkArea(30000, 20000, 30000 + 1920, 20000 + 1040, 1.0);
                FlyoutPlacement.ForegroundSeam = () => (IntPtr)0x5150;
                FlyoutPlacement.MonitorSeam = window => window == (IntPtr)0x5150 ? front : other;
                Assert.Equal(front, FlyoutPlacement.Front());
                Assert.Equal(other, FlyoutPlacement.Of((IntPtr)0x2112));

                // Without the seam, Windows answers: a real monitor.
                FlyoutPlacement.MonitorSeam = null;
                FlyoutPlacement.ForegroundSeam = () => IntPtr.Zero;
                var primary = FlyoutPlacement.Of(IntPtr.Zero);
                Assert.Equal(primary, FlyoutPlacement.Front());
                Assert.True(primary.Width > 0 && primary.Height > 0 && primary.Scale >= 1.0);
            }
            finally
            {
                FlyoutPlacement.ForegroundSeam = savedFront;
                FlyoutPlacement.MonitorSeam = savedMonitor;
            }
        }
    }
}
