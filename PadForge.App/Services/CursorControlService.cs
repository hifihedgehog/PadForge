using System;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Engine.Common.Mapping;

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

        /// <summary>The active service instance so the macro evaluator (a separate
        /// object) can reach the cursor-write operations (#108 recenter, and later
        /// #109 pin / #110 clamp). Set on construction, cleared on dispose. Null
        /// while the engine is not running.</summary>
        internal static CursorControlService Active { get; private set; }

        public CursorControlService()
        {
            Active = this;
            SourceCoercion.MouseCursorProvider = () => (_normX, _normY);
            _timer = new Timer(_ => Tick(), null, 0, SampleIntervalMs);
        }

        private void Tick()
        {
            if (_disposed) return;
            if (!GetCursorPos(out POINT p)) return;
            if (!TryGetPrimaryRect(out RECT r)) return;

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
