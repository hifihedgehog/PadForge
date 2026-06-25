using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Common.Input;

namespace PadForge.Services
{
    /// <summary>
    /// Drives the OS mouse cursor from a Wii Remote's IR camera (issue #146): the
    /// "point at the screen" mode. When enabled, it samples the first online
    /// IR-capable Wii Remote's normalized pointer (the per-device
    /// <see cref="PadForge.Engine.WiiIrState"/> that SdlDeviceWrapper computes from
    /// the two sensor-bar dots) and moves the cursor to the matching primary-monitor
    /// pixel. This is the direct-cursor analogue of mapping "IR Pointer X/Y" to a
    /// stick; it reuses the same normalized pointer, so it inherits the same
    /// (hypothesis-under-test) sign conventions.
    ///
    /// Modeled on <see cref="CursorControlService"/>. A precise lightgun-grade
    /// mapping (Johnny-Lee 4-point homography) would replace the linear
    /// center-out mapping here; until that calibration UI exists, the linear map is
    /// accurate near center and softens toward the edges of the camera's field of
    /// view.
    /// </summary>
    public sealed class WiiIrCursorService : IDisposable
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int SampleIntervalMs = 8; // ~120 Hz, smooth without pegging a core

        private Timer _timer;
        private volatile bool _disposed;

        public WiiIrCursorService()
        {
            _timer = new Timer(_ => Tick(), null, 0, SampleIntervalMs);
        }

        private void Tick()
        {
            if (_disposed) return;
            try
            {
                var dev = FindActiveIrDevice();
                var st = dev?.InputState;
                if (st == null || !st.Ir.Detected) return;

                int w = GetSystemMetrics(SM_CXSCREEN);
                int h = GetSystemMetrics(SM_CYSCREEN);
                if (w <= 0 || h <= 0) return;

                // Normalized [-1..+1] -> primary-monitor pixels. X = +1 is screen
                // right; Y = +1 is screen up (see SdlDeviceWrapper.ReadIrPointer),
                // so Y inverts. Clamp to the screen so a dot near the FOV edge
                // pins to the border instead of running off.
                float nx = Math.Clamp(st.Ir.X, -1f, 1f);
                float ny = Math.Clamp(st.Ir.Y, -1f, 1f);
                int px = (int)((nx + 1f) * 0.5f * (w - 1));
                int py = (int)((1f - ny) * 0.5f * (h - 1));
                SetCursorPos(px, py);
            }
            catch { /* a transient read/SetCursorPos failure is not worth tearing down the timer */ }
        }

        private static PadForge.Engine.Data.UserDevice FindActiveIrDevice()
        {
            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return null;
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                return devices.FirstOrDefault(d =>
                    d != null && d.HasIrCamera && d.WiiIrAsCursor && d.IsOnline && d.InputState != null);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
