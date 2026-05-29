using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Services
{
    /// <summary>
    /// Headless helper that polls the Windows Input Pane COM API and reports
    /// when the cloaked touch keyboard is visible.
    /// </summary>
    internal sealed class WindowsTouchKeyboardFocusMonitor : IDisposable
    {
        private const int S_OK = 0;

        private readonly TimeSpan _pollInterval;
        private Thread _thread;
        private CancellationTokenSource _cts;
        private bool _disposed;

        public WindowsTouchKeyboardFocusMonitor(TimeSpan? pollInterval = null)
        {
            _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(150);
        }

        public event Action<bool> VisibilityChanged;

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WindowsTouchKeyboardFocusMonitor));
            if (_thread != null) return;

            _cts = new CancellationTokenSource();
            _thread = new Thread(() => PollLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "WindowsTouchKeyboardFocusMonitor",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public void Stop()
        {
            var cts = _cts;
            var thread = _thread;
            _cts = null;
            _thread = null;

            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }

            if (thread != null && thread.IsAlive)
            {
                try { thread.Join(1000); } catch { }
            }

            cts?.Dispose();
        }

        private void PollLoop(CancellationToken token)
        {
            IFrameworkInputPane inputPane = null;
            bool? previous = null;
            try
            {
                inputPane = (IFrameworkInputPane)(object)new FrameworkInputPane();

                while (!token.IsCancellationRequested)
                {
                    bool visible = IsTouchKeyboardVisible(inputPane);
                    if (previous != visible)
                    {
                        previous = visible;
                        try { VisibilityChanged?.Invoke(visible); } catch { }
                    }

                    try
                    {
                        if (token.WaitHandle.WaitOne(_pollInterval)) break;
                    }
                    catch
                    {
                        break;
                    }
                }
            }
            catch
            {
                if (previous != false)
                {
                    try { VisibilityChanged?.Invoke(false); } catch { }
                }
            }
            finally
            {
                if (inputPane != null && Marshal.IsComObject(inputPane))
                    Marshal.ReleaseComObject(inputPane);
            }
        }

        private static bool IsTouchKeyboardVisible(IFrameworkInputPane inputPane)
        {
            if (inputPane == null) return false;

            try
            {
                int hr = inputPane.Location(out var rect);
                return hr == S_OK && !rect.IsEmpty;
            }
            catch
            {
                return false;
            }
        }

        [ComImport]
        [Guid("D5120AA3-46BA-44C5-822D-CA8092C1FC72")]
        private sealed class FrameworkInputPane
        {
        }

        [ComImport]
        [Guid("5752238B-24F0-495A-82F1-2FD593056796")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFrameworkInputPane
        {
            [PreserveSig]
            int Advise(
                [MarshalAs(UnmanagedType.IUnknown)] object pWindow,
                [MarshalAs(UnmanagedType.IUnknown)] object pHandler,
                out uint pdwCookie);

            [PreserveSig]
            int AdviseWithHWND(
                nint hwnd,
                [MarshalAs(UnmanagedType.IUnknown)] object pHandler,
                out uint pdwCookie);

            [PreserveSig]
            int Unadvise(uint dwCookie);

            [PreserveSig]
            int Location(out Rect prcInputPaneScreenLocation);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public bool IsEmpty => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
