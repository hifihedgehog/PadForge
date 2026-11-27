using System;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Common.Input;
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
    /// <para>Measured on the monitor the pointer is on (#600). That monitor's
    /// center reads (0, 0). The published value is unclamped (the per-source
    /// sensitivity + clamp happens in <c>SourceCoercion.ReadTunedMouseCursor</c>),
    /// so a cursor at the edge reads past ±1 and pins at the boundary after
    /// clamping. Normalized by width/10 on both axes: sensitivity 1.0 reaches full
    /// deflection at 10% of that monitor's width from its center. The same sample
    /// in pixels goes to <see cref="SourceCoercion.MouseCursorPointProvider"/>.</para>
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

        [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPosNative(int X, int Y);

        /// <summary>The one way this service moves the cursor. The test host
        /// moves none (InputHookManager.DesktopInputOffLimits) and is
        /// answered a refused move. A move not proven the person's own leaves
        /// the cursor where it is while the window in front runs with rights
        /// (ProgramInputGate).</summary>
        private static bool SetCursorPos(int X, int Y, OutputCause cause)
            => !PadForge.Engine.Common.InputHookManager.DesktopInputOffLimits && !PadForge.Common.Input.ProgramInputGate.HoldsKeys(cause) && SetCursorPosNative(X, Y);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private const uint MONITOR_DEFAULTTOPRIMARY = 1;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int SampleIntervalMs = 5; // 200 Hz

        // Published sample, lock-free. Volatile single floats; a reader that catches
        // a torn (x, y) pair sees at worst one stale axis for one 5 ms tick.
        private volatile float _normX;
        private volatile float _normY;

        // The same sample in pixels (#600): the pointer, its monitor's
        // center and a tenth of that monitor's width, for Mouse Position's
        // anchor. Same torn-pair tolerance.
        private volatile float _pointX, _pointY, _monitorCenterX, _monitorCenterY, _monitorUnit;

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
        // What turned the pin and the clamp on, which every write they make
        // carries (ProgramInputGate).
        private OutputCause _pinCause, _clampCause;
        private int _pinX, _pinY;

        private volatile bool _isClamped;
        private CursorClampMode _clampMode;
        private int _clampInsetX, _clampInsetY;
        private int _clampOffsetX, _clampOffsetY;

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

        /// <summary>The two providers this instance installed, so its dispose
        /// takes only its own.</summary>
        private readonly Func<(float normX, float normY)> _cursorProvider;
        private readonly Func<(double x, double y, double centerX, double centerY, double unit)?> _pointProvider;

        public CursorControlService()
        {
            Active = this;
            _cursorProvider = () =>
            {
                System.Threading.Volatile.Write(ref _lastProviderReadMs, Environment.TickCount64);
                WakeSampler();
                return (_normX, _normY);
            };
            _pointProvider = () =>
            {
                System.Threading.Volatile.Write(ref _lastProviderReadMs, Environment.TickCount64);
                WakeSampler();
                float unit = _monitorUnit;
                return unit > 0 ? (_pointX, _pointY, _monitorCenterX, _monitorCenterY, unit) : null;
            };
            SourceCoercion.MouseCursorProvider = _cursorProvider;
            SourceCoercion.MouseCursorPointProvider = _pointProvider;
            _timer = new Timer(_ => Tick(), null, 0, SampleIntervalMs);
        }

        /// <summary>True while the sampler timer is stopped instead of
        /// running at the 5 ms sample period. dotnet-counters showed the
        /// 200 Hz timer WAKES themselves (~200 threadpool work items/s) were
        /// the residual cost after the syscall demand-gate landed, and a
        /// 250 ms idle period still woke the pool four times a second with
        /// nobody reading. Any provider read or a pin/clamp engage restarts
        /// the 5 ms period immediately, and those are the only demands.</summary>
        private volatile bool _samplerIdle;

        /// <summary>Ticks so far and whether the sampler idles, for the
        /// test of its idle wait.</summary>
        private int _tickCount;
        internal int TickCount => System.Threading.Volatile.Read(ref _tickCount);
        internal bool SamplerIdle => _samplerIdle;

        /// <summary>Whether something consumes Mouse Position or a pin or
        /// clamp is engaged.</summary>
        private bool SamplerWanted()
            => _isPinned || _isClamped
                || Environment.TickCount64 - System.Threading.Volatile.Read(ref _lastProviderReadMs) <= ProviderIdleMs;

        private void WakeSampler()
        {
            if (!_samplerIdle || _disposed) return;
            _samplerIdle = false;
            try { _timer?.Change(0, SampleIntervalMs); } catch (ObjectDisposedException) { }
        }

        private void Tick()
        {
            if (_disposed) return;
            System.Threading.Interlocked.Increment(ref _tickCount);
            // Demand gate: the monitor+cursor syscalls (and the 200 Hz wake
            // itself) run only while something consumes Mouse Position or a
            // pin/clamp is engaged.
            if (!SamplerWanted())
            {
                if (!_samplerIdle)
                {
                    _samplerIdle = true;
                    try { _timer?.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
                    // A pin, clamp or read that landed while this tick
                    // decided saw the sampler awake and woke nothing, so the
                    // demand is read again after the stop.
                    System.Threading.Interlocked.MemoryBarrier();
                    if (SamplerWanted())
                    {
                        _samplerIdle = false;
                        try { _timer?.Change(0, SampleIntervalMs); } catch (ObjectDisposedException) { }
                    }
                }
                return;
            }
            if (_samplerIdle) WakeSampler();
            if (!TryGetPrimaryRect(out RECT r)) return;

            // Enforce the cursor-write contracts before sampling so the published
            // sample reflects the post-write position (#109 pin, #110 clamp). One
            // thread owns both the read and the writes, so they cannot race.
            EnforcePin(r);
            EnforceClamp(r);

            if (!GetCursorPos(out POINT p)) return;

            // Measured on the monitor the pointer is on (#600), the nearest
            // one as Windows picks it, so a pointer on a second monitor
            // reads from that monitor's center. The primary's rect stays
            // the pin's and the clamp's, which act on it.
            var mon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi)) r = mi.rcMonitor;

            var m = Measure(p.X, p.Y, r.Left, r.Top, r.Right, r.Bottom);
            if (m.unit <= 0f) return;
            _normX = m.normX;
            _normY = m.normY;
            _pointX = p.X;
            _pointY = p.Y;
            _monitorCenterX = m.centerX;
            _monitorCenterY = m.centerY;
            _monitorUnit = m.unit;
        }

        /// <summary>Where a pointer is on a monitor, as Mouse Position reads
        /// it: from the monitor's center over a tenth of its width on both
        /// axes (the recipe's convention), and that center and tenth in
        /// pixels. A monitor with no width gives a unit of 0.</summary>
        internal static (float normX, float normY, float centerX, float centerY, float unit) Measure(
            int x, int y, int left, int top, int right, int bottom)
        {
            float w = right - left;
            if (w <= 0f) return (0f, 0f, 0f, 0f, 0f);
            float centerX = (left + right) / 2f;
            float centerY = (top + bottom) / 2f;
            float div = w / 10f;
            return ((x - centerX) / div, (y - centerY) / div, centerX, centerY, div);
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
        internal void RecenterCursor(bool centerX, bool centerY, OutputCause cause)
        {
            if (_disposed) return;
            if (!TryGetPrimaryRect(out RECT r)) return;
            int cx = (r.Left + r.Right) / 2;
            int cy = (r.Top + r.Bottom) / 2;
            if (!GetCursorPos(out POINT p)) { p.X = cx; p.Y = cy; }
            SetCursorPos(centerX ? cx : p.X, centerY ? cy : p.Y, cause);
        }

        /// <summary>Warps the desktop cursor to an absolute primary-monitor pixel
        /// (issue #9). Fired once per macro press by the MoveMouseToScreenPosition
        /// action. The coordinate is already clamped on-screen by the action's
        /// MouseX / MouseY setters, so this is a straight SetCursorPos.</summary>
        internal void MoveCursorTo(int x, int y, OutputCause cause)
        {
            if (_disposed) return;
            SetCursorPos(x, y, cause);
        }

        /// <summary>Toggles the sticky cursor pin (issue #109). First call engages
        /// the pin at (<paramref name="x"/>, <paramref name="y"/>) for the selected
        /// axes; the next 200 Hz tick starts writing the cursor there before
        /// sampling. A second call releases it. Config is published before the
        /// enable flag so the timer never reads a half-set target.</summary>
        internal void TogglePin(CursorPinMode mode, int x, int y, OutputCause cause)
        {
            if (_disposed) return;
            if (_isPinned) { _isPinned = false; return; }
            _pinCause = cause;
            _pinMode = mode;
            _pinX = x;
            _pinY = y;
            _isPinned = true;
            // Restore the 5 ms period at once so the first pin write does
            // not wait out an idle interval. The barrier pairs with the one
            // in Tick: either Tick sees the pin or this call sees the
            // sampler idle.
            System.Threading.Interlocked.MemoryBarrier();
            WakeSampler();
        }

        /// <summary>Toggles the cursor region clamp (issue #110). First call engages
        /// the clamp with the given per-edge insets for the selected axes; each tick
        /// then keeps the cursor inside the inset rectangle, writing only when a
        /// clamped axis is outside. A second call releases it.</summary>
        internal void ToggleClamp(CursorClampMode mode, int insetX, int insetY, int offsetX, int offsetY, OutputCause cause)
            => SetClamp(!_isClamped, mode, insetX, insetY, offsetX, offsetY, cause);

        /// <summary>Sets the clamp to an explicit state. The toggle above flips,
        /// which is right for a one-button clamp and wrong for an engage and
        /// release PAIR: either leg can be delayed or canceled, and a pair built
        /// from two flips inverts the moment they land out of order or one is
        /// dropped, leaving the cursor trapped with nothing holding the trigger.
        /// An explicit state is idempotent, so a repeated engage or an orphaned
        /// release still lands where the trigger says.</summary>
        internal void SetClamp(bool engaged, CursorClampMode mode, int insetX, int insetY, int offsetX, int offsetY, OutputCause cause)
        {
            if (_disposed) return;
            if (!engaged) { _isClamped = false; return; }
            _clampCause = cause;
            _clampMode = mode;
            _clampInsetX = insetX;
            _clampInsetY = insetY;
            _clampOffsetX = offsetX;
            _clampOffsetY = offsetY;
            _isClamped = true;
            System.Threading.Interlocked.MemoryBarrier();
            WakeSampler();
        }

        /// <summary>Writes the cursor to the pin target on the pinned axes (#109).
        /// Runs before the sample so the published position is the pinned coord.</summary>
        private void EnforcePin(RECT r)
        {
            if (!_isPinned) return;
            if (!GetCursorPos(out POINT p)) return;
            int tx = _pinMode != CursorPinMode.YOnly ? _pinX : p.X;
            int ty = _pinMode != CursorPinMode.XOnly ? _pinY : p.Y;
            if (tx != p.X || ty != p.Y) SetCursorPos(tx, ty, _pinCause);
        }

        /// <summary>Keeps the cursor inside the inset rectangle on the clamped axes
        /// (#110). Writes only when a clamped axis is outside, matching the
        /// only-when-different optimization from the reference implementation.</summary>
        private void EnforceClamp(RECT r)
        {
            if (!_isClamped) return;
            if (!GetCursorPos(out POINT p)) return;
            var (nx, ny) = ClampPoint(p.X, p.Y, r.Left, r.Top, r.Right, r.Bottom,
                _clampMode, _clampInsetX, _clampInsetY, _clampOffsetX, _clampOffsetY);
            if (nx != p.X || ny != p.Y) SetCursorPos(nx, ny, _clampCause);
        }

        /// <summary>The point held inside the region: the screen inset on each
        /// edge, then moved by the offset.</summary>
        internal static (int X, int Y) ClampPoint(int x, int y, int screenLeft, int screenTop, int screenRight, int screenBottom,
            CursorClampMode mode, int insetX, int insetY, int offsetX, int offsetY)
        {
            int left = screenLeft + insetX + offsetX;
            int right = screenRight - insetX + offsetX;
            int top = screenTop + insetY + offsetY;
            int bottom = screenBottom - insetY + offsetY;
            if (mode != CursorClampMode.YOnly)
            {
                if (x < left) x = left; else if (x > right) x = right;
            }
            if (mode != CursorClampMode.XOnly)
            {
                if (y < top) y = top; else if (y > bottom) y = bottom;
            }
            return (x, y);
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
            // Only our own providers: a newer instance's stay, as Active does.
            if (ReferenceEquals(SourceCoercion.MouseCursorProvider, _cursorProvider)) SourceCoercion.MouseCursorProvider = null;
            if (ReferenceEquals(SourceCoercion.MouseCursorPointProvider, _pointProvider)) SourceCoercion.MouseCursorPointProvider = null;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
