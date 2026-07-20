using System;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Engine.Common.Mapping;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// Samples the desktop cursor position at 200 Hz and publishes it, normalized
    /// to the [-1..+1] stick range per screen axis, into
    /// <see cref="SourceCoercion.MouseCursorProvider"/> for the "Mouse Position X" /
    /// "Mouse Position Y" sources (issue #107).
    ///
    /// <para>Primary monitor only. Screen center reads (0, 0); the published value
    /// is unclamped (the per-source sensitivity + clamp happens in
    /// <c>SourceCoercion.ReadTunedMouseCursor</c>), so a cursor at the edge or on a
    /// secondary monitor reads past ±1 and pins at the boundary after clamping.
    /// Normalized by width/10 on both axes: sensitivity 1.0 reaches full deflection
    /// at 10% of screen width from center.</para>
    ///
    /// <para>Both <c>GetCursorPos</c> and <c>GetMonitorInfo</c> return physical
    /// pixels under the PerMonitorV2 awareness declared in app.manifest, so the math
    /// is straight pixel arithmetic and stays correct on a scaled primary monitor
    /// (no DPI conversion needed). Re-resolves the primary monitor rect each tick so
    /// a resolution change is picked up without a window-message hook.</para>
    ///
    /// <para>This service owns the single 200 Hz cursor timeline. The cursor-write
    /// macro actions (#108 recenter / #109 pin / #110 region) will share this thread
    /// so reads and writes cannot race.</para>
    /// </summary>
    public sealed class CursorControlService : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private const uint MONITOR_DEFAULTTOPRIMARY = 1;
        private const int SampleIntervalMs = 5; // 200 Hz

        // Published sample, lock-free. Volatile single floats; a reader that catches
        // a torn (x, y) pair sees at worst one stale axis for one 5 ms tick.
        private volatile float _normX;
        private volatile float _normY;

        private Timer _timer;
        private volatile bool _disposed;

        // Sticky pin state (#109) and region-clamp state (#110). The enable bools
        // are volatile so the 200 Hz timer thread sees a toggle promptly; the
        // config fields are published before the bool is set true (volatile-bool
        // release on write, acquire on read), so a tick that observes the flag also
        // observes a consistent config. They are not changed again while engaged,
        // so the timer reads stable values.
        private volatile bool _isPinned;
        private CursorPinMode _pinMode;
        private int _pinX, _pinY;

        private volatile bool _isClamped;
        private CursorClampMode _clampMode;
        private int _clampInsetX, _clampInsetY;

        /// <summary>The active service instance so the macro evaluator (a separate
        /// object) can reach the cursor-write operations (#108 recenter, and later
        /// #109 pin / #110 clamp). Set on construction, cleared on dispose. Null
        /// while the engine is not running.</summary>
        internal static CursorControlService Active { get; private set; }

        /// <summary>TickCount64 of the last MouseCursorProvider read.
        /// 0 until something actually maps a Mouse Position source, so a
        /// fresh engine start idles immediately.</summary>
        private long _lastProviderReadMs;

        /// <summary>Ticks with no provider read after which the sampler
        /// idles. The first read after an idle stretch sees at most one
        /// stale sample (the next 5 ms tick refreshes), which matches
        /// the documented torn-pair tolerance.</summary>
        private const long ProviderIdleMs = 2000;

        public CursorControlService()
        {
            Active = this;
            SourceCoercion.MouseCursorProvider = () =>
            {
                System.Threading.Volatile.Write(ref _lastProviderReadMs, Environment.TickCount64);
                return (_normX, _normY);
            };
            _timer = new Timer(_ => Tick(), null, 0, SampleIntervalMs);
        }

        private void Tick()
        {
            if (_disposed) return;
            // Demand gate: the 200 Hz monitor+cursor syscalls ran forever
            // even when no mapping consumed Mouse Position. Pin/clamp
            // enforcement must keep running while engaged regardless of
            // reads, so the gate only closes when neither is active.
            if (!_isPinned && !_isClamped
                && Environment.TickCount64 - System.Threading.Volatile.Read(ref _lastProviderReadMs) > ProviderIdleMs)
                return;
            if (!TryGetPrimaryRect(out RECT r)) return;

            // Enforce the cursor-write contracts before sampling so the published
            // sample reflects the post-write position (#109 pin, #110 clamp). One
            // thread owns both the read and the writes, so they cannot race.
            EnforcePin(r);
            EnforceClamp(r);

            if (!GetCursorPos(out POINT p)) return;

            float w = r.Right - r.Left;
            if (w <= 0f) return;

            float centerX = (r.Left + r.Right) / 2f;
            float centerY = (r.Top + r.Bottom) / 2f;
            float div = w / 10f; // width/10 for both axes (recipe convention)

            _normX = (p.X - centerX) / div;
            _normY = (p.Y - centerY) / div;
        }

        /// <summary>Resolves the primary monitor's physical-pixel rect. The primary
        /// lives at virtual-desktop origin; DEFAULTTOPRIMARY keeps us there even if
        /// (0,0) drifts. Re-queried per call so a resolution change is picked up.</summary>
        private static bool TryGetPrimaryRect(out RECT rect)
        {
            rect = default;
            var hMon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            if (hMon == IntPtr.Zero) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMon, ref mi)) return false;
            rect = mi.rcMonitor;
            return true;
        }

        /// <summary>Recenters the desktop cursor on the primary monitor (issue #108).
        /// <paramref name="centerX"/> / <paramref name="centerY"/> select which axes
        /// snap to center; an unselected axis keeps its current coordinate. Fired
        /// once per macro press by the MouseRecenter action. The next 200 Hz tick
        /// (≤5 ms later) re-samples, so the mapped "Mouse Position" source reports 0
        /// on the recentered axes.</summary>
        public void RecenterCursor(bool centerX, bool centerY)
        {
            if (_disposed) return;
            if (!TryGetPrimaryRect(out RECT r)) return;
            int cx = (r.Left + r.Right) / 2;
            int cy = (r.Top + r.Bottom) / 2;
            if (!GetCursorPos(out POINT p)) { p.X = cx; p.Y = cy; }
            SetCursorPos(centerX ? cx : p.X, centerY ? cy : p.Y);
        }

        /// <summary>Warps the desktop cursor to an absolute primary-monitor pixel
        /// (issue #9). Fired once per macro press by the MoveMouseToScreenPosition
        /// action. The coordinate is already clamped on-screen by the action's
        /// MouseX / MouseY setters, so this is a straight SetCursorPos.</summary>
        public void MoveCursorTo(int x, int y)
        {
            if (_disposed) return;
            SetCursorPos(x, y);
        }

        /// <summary>Toggles the sticky cursor pin (issue #109). First call engages
        /// the pin at (<paramref name="x"/>, <paramref name="y"/>) for the selected
        /// axes; the next 200 Hz tick starts writing the cursor there before
        /// sampling. A second call releases it. Config is published before the
        /// enable flag so the timer never reads a half-set target.</summary>
        public void TogglePin(CursorPinMode mode, int x, int y)
        {
            if (_disposed) return;
            if (_isPinned) { _isPinned = false; return; }
            _pinMode = mode;
            _pinX = x;
            _pinY = y;
            _isPinned = true;
        }

        /// <summary>Toggles the cursor region clamp (issue #110). First call engages
        /// the clamp with the given per-edge insets for the selected axes; each tick
        /// then keeps the cursor inside the inset rectangle, writing only when a
        /// clamped axis is outside. A second call releases it.</summary>
        public void ToggleClamp(CursorClampMode mode, int insetX, int insetY)
        {
            if (_disposed) return;
            if (_isClamped) { _isClamped = false; return; }
            _clampMode = mode;
            _clampInsetX = insetX;
            _clampInsetY = insetY;
            _isClamped = true;
        }

        /// <summary>Writes the cursor to the pin target on the pinned axes (#109).
        /// Runs before the sample so the published position is the pinned coord.</summary>
        private void EnforcePin(RECT r)
        {
            if (!_isPinned) return;
            if (!GetCursorPos(out POINT p)) return;
            int tx = _pinMode != CursorPinMode.YOnly ? _pinX : p.X;
            int ty = _pinMode != CursorPinMode.XOnly ? _pinY : p.Y;
            if (tx != p.X || ty != p.Y) SetCursorPos(tx, ty);
        }

        /// <summary>Keeps the cursor inside the inset rectangle on the clamped axes
        /// (#110). Writes only when a clamped axis is outside, matching the
        /// only-when-different optimization from the reference implementation.</summary>
        private void EnforceClamp(RECT r)
        {
            if (!_isClamped) return;
            if (!GetCursorPos(out POINT p)) return;
            int left = r.Left + _clampInsetX;
            int right = r.Right - _clampInsetX;
            int top = r.Top + _clampInsetY;
            int bottom = r.Bottom - _clampInsetY;
            int nx = p.X, ny = p.Y;
            if (_clampMode != CursorClampMode.YOnly)
            {
                if (nx < left) nx = left; else if (nx > right) nx = right;
            }
            if (_clampMode != CursorClampMode.XOnly)
            {
                if (ny < top) ny = top; else if (ny > bottom) ny = bottom;
            }
            if (nx != p.X || ny != p.Y) SetCursorPos(nx, ny);
        }

        /// <summary>Primary-monitor center in physical pixels. Used to seed a pin
        /// action's default target (issue #109).</summary>
        public static bool TryGetPrimaryCenter(out int x, out int y)
        {
            x = 0; y = 0;
            if (!TryGetPrimaryRect(out RECT r)) return false;
            x = (r.Left + r.Right) / 2;
            y = (r.Top + r.Bottom) / 2;
            return true;
        }

        /// <summary>Current desktop cursor position in physical pixels (issue #9).
        /// Used by the MoveMouseToScreenPosition editor's "Pick on screen" capture.
        /// Static so the macro editor can read it without a running engine.</summary>
        public static bool TryGetCursorPosition(out int x, out int y)
        {
            x = 0; y = 0;
            if (!GetCursorPos(out POINT p)) return false;
            x = p.X; y = p.Y;
            return true;
        }

        /// <summary>Primary-monitor width/height in physical pixels. Used to clamp
        /// pin coords (#109) and region insets (#110) to on-screen ranges.</summary>
        public static bool TryGetPrimarySize(out int width, out int height)
        {
            width = 0; height = 0;
            if (!TryGetPrimaryRect(out RECT r)) return false;
            width = r.Right - r.Left;
            height = r.Bottom - r.Top;
            return true;
        }

        public void Dispose()
        {
            _disposed = true;
            if (Active == this) Active = null;
            // Only unhook our own provider so a re-create can rewire cleanly.
            SourceCoercion.MouseCursorProvider = null;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
