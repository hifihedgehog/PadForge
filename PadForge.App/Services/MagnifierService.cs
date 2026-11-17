using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using PadForge.Common.Crosshairs;
using PadForge.Controls;
using PadForge.Views;

namespace PadForge.Services
{
    /// <summary>
    /// The magnifier (#585) in its three modes, following the display's
    /// center, the pointer, or the first crosshair that is on.
    ///
    /// <para>Lens is Microsoft's windowed magnifier (Windows-classic-samples,
    /// Samples/Magnification/cpp/Windowed/MagnifierSample.cpp): a magnifier
    /// control in a topmost layered host that the mouse goes through, zoomed
    /// by a scale matrix, its source set and its picture repainted once a
    /// frame, as the sample's timer does every 16 ms. Here the frame is WPF's
    /// (CompositionTarget.Rendering), which runs only while a lens or a mode
    /// that follows the pointer shows. A window region makes the circle, and
    /// a border window covers its stepped edge.</para>
    ///
    /// <para>Full Screen is Windows' full-screen magnifier
    /// (MagSetFullscreenTransform, as FullscreenMagnifierSample.cpp's SetZoom
    /// calls it), set back to 1 when it hides and when the engine stops.
    /// Spotlight is a shade over the display with a square cut out and a
    /// window in the square that draws the circle's smoothed edge.</para>
    ///
    /// <para>Every call is on the UI thread, which owns the windows.</para>
    /// </summary>
    internal sealed class MagnifierService
    {
        private readonly Action<string> _log;
        private readonly EventHandler _onRendering;

        public MagnifierService(Action<string> log)
        {
            _log = log;
            _onRendering = (_, _) => Follow();
        }

        private MagnifierData _look;
        private bool _initialized;
        private bool _framing;

        private Int32Rect _display;

        /// <summary>The point the magnifier centers on while it follows a
        /// display's center or a crosshair.</summary>
        private (int X, int Y) _aim;

        // Lens
        private IntPtr _host, _view;
        private Int32Rect _lens;
        private int _lensWidth, _lensHeight;
        private bool _lensRound;
        private double _zoom;
        private PixelOverlay _frame;
        private LensFrameView _frameView;
        private (int Width, int Height, int Thickness, bool Round, Color Color) _frameLook;

        // Full Screen
        private bool _fullScreen;
        private (int X, int Y) _offset;
        private double _fullScreenZoom;

        // Spotlight
        private IntPtr _shade;
        private PixelOverlay _hole;
        private SpotlightHoleView _holeView;
        private Int32Rect _shadeOn, _holeAt, _holeWindow;
        private Point _holeOrigin;
        private int _holeSize;
        private double _holeDim;

        /// <summary>Why the magnifier is not showing though it was asked to,
        /// in the user's words. Null when it shows or was not asked
        /// to.</summary>
        public string Failure { get; private set; }

        /// <summary>The magnifier is up.</summary>
        public bool IsShowing => _look != null;

        /// <summary>The crosshairs' windows, which the lens leaves out of what
        /// it magnifies. Set by InputService.</summary>
        public Func<IntPtr[]> CrosshairWindows { get; set; }

        /// <summary>The first crosshair that is on, as its display's name and
        /// its offset, or null with none on. Set by InputService.</summary>
        public Func<(string Display, double OffsetX, double OffsetY)?> FirstCrosshair { get; set; }

        /// <summary>The display and the point the magnifier centers on while it
        /// does not follow the pointer: a crosshair's point, or the display's
        /// center.</summary>
        private void Aim()
        {
            if (_look.Follow == MagnifierFollow.Crosshair && FirstCrosshair?.Invoke() is { } crosshair)
            {
                _display = DisplayBounds.Of(crosshair.Display);
                _aim = CrosshairPlace.Center(_display, crosshair.OffsetX, crosshair.OffsetY);
            }
            else
            {
                _display = DisplayBounds.Of(_look.Display);
                _aim = CrosshairPlace.Center(_display, 0, 0);
            }
        }

        /// <summary>Shows the magnifier as <paramref name="look"/> sets it, or
        /// brings the one up into line with it. A change of mode starts over.
        /// When Windows refuses, nothing stays up and <see cref="Failure"/>
        /// says so.</summary>
        public void Show(MagnifierData look)
        {
            if (_look != null && _look.Mode != look.Mode) Hide();
            Failure = null;
            if (!_initialized)
            {
                if (!MagnifierNative.MagInitialize())
                {
                    Fail("MagInitialize");
                    return;
                }
                _initialized = true;
            }
            bool first = _look == null;
            _look = look;
            Aim();
            switch (look.Mode)
            {
                case MagnifierMode.Lens:
                    if (!ArrangeLens()) return;
                    break;
                case MagnifierMode.Spotlight:
                    ArrangeSpotlight();
                    break;
            }
            Follow();
            if (_look == null) return;
            if (first)
            {
                if (_host != IntPtr.Zero) MagnifierNative.Show(_host);
                if (_shade != IntPtr.Zero) MagnifierNative.Show(_shade);
                _frame?.Raise();
                _hole?.Raise();
            }
            Frames(look.Mode == MagnifierMode.Lens || look.Follow == MagnifierFollow.Pointer);
        }

        /// <summary>Takes the magnifier down: its windows close and a
        /// full-screen zoom goes back to 1.</summary>
        public void Hide()
        {
            Frames(false);
            if (_fullScreen)
            {
                if (!MagnifierNative.SetFullScreen(1f, 0, 0))
                    _log?.Invoke("Magnifier: full-screen reset failed, error " + Marshal.GetLastWin32Error());
                _fullScreen = false;
            }
            MagnifierNative.Destroy(_host);
            _host = _view = IntPtr.Zero;
            MagnifierNative.Destroy(_shade);
            _shade = IntPtr.Zero;
            _frame?.Close();
            _frame = null;
            _frameView = null;
            _frameLook = default;
            _hole?.Close();
            _hole = null;
            _holeView = null;
            _look = null;
            _lens = _shadeOn = _holeAt = _holeWindow = default;
            _holeSize = 0;
        }

        /// <summary>The engine stopped or PadForge is closing: down, and the
        /// Magnification API's objects let go (MagUninitialize, as both
        /// samples end).</summary>
        public void Close()
        {
            Hide();
            Failure = null;
            if (!_initialized) return;
            MagnifierNative.MagUninitialize();
            _initialized = false;
        }

        /// <summary>Once a second while it is up: back above a game that went
        /// on top, as the Screen Overlay is, and in line with a display that
        /// changed. The crosshairs go on top after this.</summary>
        public void Tend()
        {
            if (_look == null) return;
            var display = _display;
            var aim = _aim;
            Aim();
            if (display != _display || aim != _aim)
            {
                Follow();
                if (_look == null) return;
            }
            if (_host != IntPtr.Zero) ScreenOverlay.KeepAboveOthers(_host, ScreenOverlay.IsOurWindow);
            if (_shade != IntPtr.Zero) ScreenOverlay.KeepAboveOthers(_shade, ScreenOverlay.IsOurWindow);
            _frame?.KeepOnTop();
            _hole?.KeepOnTop();
        }

        /// <summary>The crosshairs' windows changed: the lens leaves out the
        /// new set.</summary>
        public void RefreshLeftOut()
        {
            if (_view == IntPtr.Zero) return;
            var crosshairs = CrosshairWindows?.Invoke() ?? Array.Empty<IntPtr>();
            var all = new IntPtr[crosshairs.Length + (_frame != null ? 1 : 0)];
            crosshairs.CopyTo(all, 0);
            if (_frame != null) all[^1] = _frame.Handle;
            MagnifierNative.Exclude(_view, all);
        }

        private void Fail(string what)
        {
            _log?.Invoke("Magnifier: " + what + " failed, error " + Marshal.GetLastWin32Error());
            Hide();
            Failure = PadForge.Resources.Strings.Strings.Instance.Extras_Magnifier_Failed;
        }

        private void Frames(bool on)
        {
            if (on == _framing) return;
            _framing = on;
            if (on) CompositionTarget.Rendering += _onRendering;
            else CompositionTarget.Rendering -= _onRendering;
        }

        // ─────────────────────────────────────────────
        //  Lens
        // ─────────────────────────────────────────────

        private bool ArrangeLens()
        {
            int width = (int)Math.Round(_look.LensWidth), height = (int)Math.Round(_look.LensHeight);
            bool round = _look.LensShape == LensShape.Circle;
            if (_host == IntPtr.Zero)
            {
                _lens = MagnifierMath.Lens(_display, _aim.X, _aim.Y, width, height);
                (_host, _view) = MagnifierNative.CreateLens(_lens, round, _look.Zoom);
                if (_host == IntPtr.Zero)
                {
                    Fail("CreateLens");
                    return false;
                }
                _zoom = _look.Zoom;
            }
            else
            {
                if (width != _lensWidth || height != _lensHeight || round != _lensRound)
                {
                    _lens = MagnifierMath.Lens(_display, _lens.X + _lens.Width / 2, _lens.Y + _lens.Height / 2, width, height);
                    MagnifierNative.ResizeLens(_host, _view, _lens, round);
                }
                if (_look.Zoom != _zoom && MagnifierNative.SetZoom(_view, _look.Zoom)) _zoom = _look.Zoom;
            }
            _lensWidth = width;
            _lensHeight = height;
            _lensRound = round;
            ArrangeFrame();
            RefreshLeftOut();
            return true;
        }

        private void ArrangeFrame()
        {
            if (!_look.Border)
            {
                _frame?.Close();
                _frame = null;
                _frameView = null;
                _frameLook = default;
                return;
            }
            int thickness = (int)Math.Round(_look.BorderThickness);
            (int Width, int Height, int Thickness, bool Round, Color Color) look =
                (_lens.Width, _lens.Height, thickness, _lensRound, Color.FromRgb(_look.BorderR, _look.BorderG, _look.BorderB));
            if (_frame == null)
            {
                _frameView = new LensFrameView();
                _frame = new PixelOverlay(_frameView, PadForge.Resources.Strings.Strings.Instance.Extras_Magnifier_Title);
                _frameLook = default;
            }
            if (look != _frameLook)
            {
                _frameView.Show(look.Width, look.Height, look.Thickness, look.Round, look.Color);
                _frameLook = look;
            }
            _frame.PlaceAt(FrameAround(_lens, thickness));
        }

        private static Int32Rect FrameAround(Int32Rect lens, int thickness)
            => new(lens.X - thickness, lens.Y - thickness, lens.Width + thickness * 2, lens.Height + thickness * 2);

        // ─────────────────────────────────────────────
        //  Spotlight
        // ─────────────────────────────────────────────

        private void ArrangeSpotlight()
        {
            byte alpha = SpotlightHoleView.ShadeAlpha(_look.SpotlightDim);
            if (_shade == IntPtr.Zero)
            {
                _shade = MagnifierNative.CreateShade(_display, alpha);
                if (_shade == IntPtr.Zero)
                {
                    Fail("CreateShade");
                    return;
                }
                _holeView = new SpotlightHoleView();
                _hole = new PixelOverlay(_holeView, PadForge.Resources.Strings.Strings.Instance.Extras_Magnifier_Title);
                _shadeOn = _display;
            }
            else MagnifierNative.SetAlpha(_shade, alpha);
            // A new size or shade draws the hole again on the next frame.
            _holeAt = default;
        }

        private void FollowSpotlight(Int32Rect display, int x, int y)
        {
            if (display != _shadeOn)
            {
                MagnifierNative.Place(_shade, display);
                _shadeOn = display;
                _holeAt = default;
            }
            int size = Math.Max(2, (int)Math.Round(_look.SpotlightSize) / 2 * 2);
            var hole = MagnifierMath.Hole(x, y, size);
            if (hole == _holeAt) return;
            MagnifierNative.CutHole(_shade, display, hole);
            _holeAt = hole;
            int left = Math.Max(hole.X, display.X), top = Math.Max(hole.Y, display.Y);
            int right = Math.Min(hole.X + hole.Width, display.X + display.Width), bottom = Math.Min(hole.Y + hole.Height, display.Y + display.Height);
            if (right <= left || bottom <= top)
            {
                if (_hole.IsVisible) _hole.Hide();
                _holeWindow = default;
                return;
            }
            var window = new Int32Rect(left, top, right - left, bottom - top);
            var origin = new Point(x - left, y - top);
            if (window.Width != _holeWindow.Width || window.Height != _holeWindow.Height || origin != _holeOrigin
                || size != _holeSize || _look.SpotlightDim != _holeDim || !_hole.IsVisible)
            {
                _holeView.Show(size, origin, window.Width, window.Height, _look.SpotlightDim);
                _hole.PlaceAt(window);
                _holeSize = size;
                _holeDim = _look.SpotlightDim;
                _holeOrigin = origin;
            }
            else _hole.MoveTo(window.X, window.Y);
            _holeWindow = window;
        }

        // ─────────────────────────────────────────────
        //  Each frame
        // ─────────────────────────────────────────────

        /// <summary>One frame: the point the magnifier centers on, and every
        /// window and the zoom where that point puts them. Makes no garbage,
        /// since it runs once a frame.</summary>
        private void Follow()
        {
            var look = _look;
            if (look == null) return;
            int x, y;
            Int32Rect display;
            if (look.Follow == MagnifierFollow.Pointer && MagnifierNative.GetCursorPos(out var pointer))
            {
                x = pointer.X;
                y = pointer.Y;
                display = DisplayBounds.At(x, y);
            }
            else
            {
                display = _display;
                (x, y) = _aim;
            }
            if (display.Width <= 0 || display.Height <= 0) return;
            switch (look.Mode)
            {
                case MagnifierMode.Lens:
                {
                    var lens = MagnifierMath.Lens(display, x, y, _lensWidth, _lensHeight);
                    if (lens != _lens)
                    {
                        if (lens.Width != _lens.Width || lens.Height != _lens.Height)
                        {
                            MagnifierNative.ResizeLens(_host, _view, lens, _lensRound);
                            _lens = lens;
                            ArrangeFrame();
                        }
                        else
                        {
                            MagnifierNative.MoveTo(_host, lens.X, lens.Y);
                            int thickness = _frameLook.Thickness;
                            _frame?.MoveTo(lens.X - thickness, lens.Y - thickness);
                            _lens = lens;
                        }
                    }
                    MagnifierNative.ShowSource(_view, MagnifierMath.Source(display, x, y, _lens.Width, _lens.Height, _zoom));
                    break;
                }
                case MagnifierMode.FullScreen:
                {
                    var offset = MagnifierMath.FullScreenOffset(display, x, y, look.Zoom);
                    if (_fullScreen && offset == _offset && look.Zoom == _fullScreenZoom) break;
                    if (!MagnifierNative.SetFullScreen((float)look.Zoom, offset.X, offset.Y))
                    {
                        Fail("MagSetFullscreenTransform");
                        return;
                    }
                    _fullScreen = true;
                    _offset = offset;
                    _fullScreenZoom = look.Zoom;
                    break;
                }
                case MagnifierMode.Spotlight:
                    FollowSpotlight(display, x, y);
                    break;
            }
        }
    }
}
