using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace PadForge.Common.Input.ScreenColor
{
    /// <summary>
    /// Whether the display under a Windows.Graphics.Capture pool still is what
    /// the pool was built for (#559). That API sends no word of a mode change,
    /// where Desktop Duplication's capture ends with ACCESS_LOST, so an HDR
    /// switch would leave an 8-bit pool clipping or a float pool on its old
    /// format. Sunshine asks the DXGI factory's IsCurrent on every frame of its
    /// capture, this one included (display_base.cpp:337-341), and reads the SDR
    /// white level again once a second (display_base.cpp:955-964, display.h:324).
    /// This asks both once a second, with the HDR state itself beside them.
    /// Used from the capture's one thread.
    /// </summary>
    internal sealed class ScreenModeWatch
    {
        private readonly bool _builtHdr;
        private readonly Func<bool> _current, _hdr;
        private readonly Func<float> _white;
        private readonly Func<long> _clock;
        private long _nextAt;

        /// <summary>The white level the pool's frames convert through now.</summary>
        public float SdrWhite { get; private set; }

        public ScreenModeWatch(bool builtHdr, float builtWhite, Func<bool> current, Func<bool> hdr, Func<float> white, Func<long> clock)
        {
            _builtHdr = builtHdr;
            SdrWhite = builtWhite;
            _current = current;
            _hdr = hdr;
            _white = white;
            _clock = clock;
            _nextAt = clock() + ScreenCaptureWorker.ReturnCheckMs;
        }

        /// <summary>True when the pool has to be built again: the factory saw a
        /// change, or the display's HDR state is not the one the pool was built
        /// for. A white level that moved on an HDR display is taken at once, since
        /// the pool's format still fits.</summary>
        public bool Changed()
        {
            long now = _clock();
            if (now < _nextAt) return false;
            _nextAt = now + ScreenCaptureWorker.ReturnCheckMs;
            if (!_current() || _hdr() != _builtHdr) return true;
            if (_builtHdr) SdrWhite = _white();
            return false;
        }
    }

    /// <summary>
    /// One display through Windows.Graphics.Capture (#559), for a PC where
    /// Desktop Duplication answers DXGI_ERROR_UNSUPPORTED or that four other
    /// programs are already duplicating. A laptop with two GPUs is the usual
    /// case: AlienFX Tools documents it (Doc/alienfx-gui.md), and Sunshine keeps
    /// this path for it (src/platform/windows/display_wgc.cpp). The API needs
    /// Windows 10 1903. The yellow capture border stays off wherever Windows has
    /// IsBorderRequired, and on older builds it shows while this runs.
    /// </summary>
    internal sealed unsafe class WindowsCaptureGrabber : IScreenGrabber
    {
        private const string SessionClass = "Windows.Graphics.Capture.GraphicsCaptureSession";

        /// <summary>DXGI_MODE_ROTATION_IDENTITY: this API hands a rotated display
        /// over already turned, as Sunshine's display_wgc.cpp notes where it turns
        /// its own rotation handling off for these frames.</summary>
        private const int IdentityRotation = 1;

        /// <summary>The white level an 8-bit frame is read with. 8-bit color has no
        /// white level to convert through, so it goes unused.</summary>
        private const float SdrWhite8Bit = 80f;

        private static Guid s_iidInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        private static Guid s_iidItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        private static Guid s_iidAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

        private nint _device, _context, _factory, _output;
        private readonly ScreenDownscaler _downscaler;
        private readonly ScreenModeWatch _watch;
        private readonly IDirect3DDevice _captureDevice;
        private readonly Direct3D11CaptureFramePool _pool;
        private readonly GraphicsCaptureSession _session;
        private readonly Windows.Graphics.SizeInt32 _size;
        private readonly long _frequency = Stopwatch.Frequency;
        private volatile bool _closed;

        public string Kind => "Windows.Graphics.Capture";
        public long LastWorkMs { get; private set; }
        public string Output { get; }

        /// <summary>The newest of the frames <paramref name="next"/> hands out,
        /// each older one disposed, or null when it has none. The pool queues its
        /// frames ("Each frame ... is checked out when calling TryGetNextFrame",
        /// Microsoft, Screen capture), so one call hands over the oldest, a
        /// grab period behind. Sunshine keeps only the newest the same way
        /// (src/platform/windows/display_wgc.cpp, on_frame_arrived).</summary>
        internal static T Newest<T>(Func<T> next) where T : class, IDisposable
        {
            T newest = null;
            for (T frame = next(); frame != null; frame = next())
            {
                newest?.Dispose();
                newest = frame;
            }
            return newest;
        }

        /// <summary>The frame pool's format and the white level its frames
        /// convert through. On an HDR display 8-bit color comes out clipped
        /// (Microsoft, Screen capture: "To avoid pixel overclipping ... consider
        /// using DXGI_FORMAT_R16G16B16A16_FLOAT"), so the pool asks for 16-bit
        /// float there, as Sunshine does for scRGB
        /// (src/platform/windows/frame_contract.cpp, select_wgc_capture_format),
        /// and its frames convert through the display's SDR white level, as
        /// Desktop Duplication's do.</summary>
        internal static (DirectXPixelFormat Format, float SdrWhite) PoolFormat(bool hdr, Func<float> sdrWhite)
            => hdr ? (DirectXPixelFormat.R16G16B16A16Float, sdrWhite()) : (DirectXPixelFormat.B8G8R8A8UIntNormalized, SdrWhite8Bit);

        public WindowsCaptureGrabber(string display)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) || !GraphicsCaptureSession.IsSupported())
                throw new ScreenGrabberUnavailable("Windows.Graphics.Capture is not on this Windows.");

            ScreenDxgi.FindOutput(display, out nint adapter, out nint output, out var info, out _factory);
            Output = info.Name;
            _output = output;
            output = 0;
            nint dxgiDevice = 0, interop = 0;
            try
            {
                ScreenDxgi.CreateDevice(adapter, out _device, out _context);
                int hr = Marshal.QueryInterface(_device, in ScreenDxgi.IID_IDXGIDevice, out dxgiDevice);
                if (hr != 0) throw new ScreenGrabberUnavailable($"IDXGIDevice 0x{hr:X8}");
                hr = ScreenDxgi.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out nint inspectable);
                if (hr != 0) throw new ScreenGrabberUnavailable($"CreateDirect3D11DeviceFromDXGIDevice 0x{hr:X8}");
                _captureDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                Marshal.Release(inspectable);

                var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
                hr = Marshal.QueryInterface(factory.ThisPtr, in s_iidInterop, out interop);
                if (hr != 0) throw new ScreenGrabberUnavailable($"IGraphicsCaptureItemInterop 0x{hr:X8}");
                nint itemPtr;
                fixed (Guid* iid = &s_iidItem)
                    hr = ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)ScreenDxgi.Slot(interop, 4))(interop, info.Monitor, iid, &itemPtr);
                if (hr != 0) throw new ScreenGrabberWaiting($"CreateForMonitor on {info.Name} 0x{hr:X8}");
                var item = GraphicsCaptureItem.FromAbi(itemPtr);
                Marshal.Release(itemPtr);
                item.Closed += (_, _) => _closed = true;

                _size = item.Size;
                bool hdr = ScreenDxgi.IsHdr(_output);
                var (format, sdrWhite) = PoolFormat(hdr, () => ScreenDxgi.SdrWhiteNits(info.Name));
                string name = info.Name;
                _watch = new ScreenModeWatch(hdr, sdrWhite, () => ScreenDxgi.IsCurrent(_factory), () => ScreenDxgi.IsHdr(_output),
                    () => ScreenDxgi.SdrWhiteNits(name), () => Environment.TickCount64);
                _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_captureDevice, format, 2, _size);
                _session = _pool.CreateCaptureSession(item);
                TurnOffBorderAndPointer();
                _downscaler = new ScreenDownscaler(_device, _context);
                _session.StartCapture();
            }
            catch (ScreenGrabberWaiting)
            {
                Dispose();
                throw;
            }
            catch (ScreenGrabberUnavailable)
            {
                Dispose();
                throw;
            }
            catch (Exception e)
            {
                Dispose();
                throw new ScreenGrabberUnavailable(e.Message);
            }
            finally
            {
                ScreenDxgi.Release(ref interop);
                ScreenDxgi.Release(ref dxgiDevice);
                ScreenDxgi.Release(ref output);
                ScreenDxgi.Release(ref adapter);
            }
        }

        private void TurnOffBorderAndPointer()
        {
            try
            {
                if (ApiInformation.IsPropertyPresent(SessionClass, "IsCursorCaptureEnabled")
                    && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                    _session.IsCursorCaptureEnabled = false;
                if (ApiInformation.IsPropertyPresent(SessionClass, "IsBorderRequired")
                    && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
                {
                    GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().GetAwaiter().GetResult();
                    _session.IsBorderRequired = false;
                }
            }
            catch (Exception e)
            {
                PadForge.Engine.SdlDiagLog.WriteLine($"SCREEN capture border or pointer setting refused: {e.Message}");
            }
        }

        public ScreenGrabResult Grab(int timeoutMs, out ScreenFrame frame)
        {
            frame = null;
            if (_closed) return ScreenGrabResult.Lost;
            // An HDR switch or another display change: the pool's format no longer
            // fits, so the capture is built again (ScreenModeWatch).
            if (_watch.Changed()) return ScreenGrabResult.Lost;
            using var captured = Newest(_pool.TryGetNextFrame);
            if (captured == null)
            {
                Thread.Sleep(Math.Min(timeoutMs, 16));
                return ScreenGrabResult.NoChange;
            }
            // The display changed size, and the pool's frames were made for the
            // old one.
            var content = captured.ContentSize;
            if (content.Width != _size.Width || content.Height != _size.Height) return ScreenGrabResult.Lost;
            using var surface = captured.Surface;
            if (Marshal.QueryInterface(((IWinRTObject)surface).NativeObject.ThisPtr, in s_iidAccess, out nint access) != 0)
                return ScreenGrabResult.Lost;
            try
            {
                nint texture;
                int hr;
                fixed (Guid* iid = &ScreenDxgi.IID_ID3D11Texture2D)
                    hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)ScreenDxgi.Slot(access, 3))(access, iid, &texture);
                if (hr != 0) return ScreenGrabResult.Lost;
                try
                {
                    long start = Stopwatch.GetTimestamp();
                    frame = _downscaler.Read(texture, IdentityRotation, _watch.SdrWhite);
                    LastWorkMs = (Stopwatch.GetTimestamp() - start) * 1000 / _frequency;
                    return frame != null ? ScreenGrabResult.Frame : ScreenGrabResult.Lost;
                }
                finally
                {
                    Marshal.Release(texture);
                }
            }
            finally
            {
                Marshal.Release(access);
            }
        }

        public void Dispose()
        {
            _session?.Dispose();
            _pool?.Dispose();
            _downscaler?.Dispose();
            _captureDevice?.Dispose();
            ScreenDxgi.Release(ref _context);
            ScreenDxgi.Release(ref _device);
            ScreenDxgi.Release(ref _output);
            ScreenDxgi.Release(ref _factory);
        }
    }
}
