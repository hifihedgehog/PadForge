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

        /// <summary>The frame pool asks for 8-bit color, which has no white level
        /// to convert through.</summary>
        private const float UnusedSdrWhite = 80f;

        private static Guid s_iidInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        private static Guid s_iidItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        private static Guid s_iidAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

        private nint _device, _context;
        private readonly ScreenDownscaler _downscaler;
        private readonly IDirect3DDevice _captureDevice;
        private readonly Direct3D11CaptureFramePool _pool;
        private readonly GraphicsCaptureSession _session;
        private readonly Windows.Graphics.SizeInt32 _size;
        private readonly long _frequency = Stopwatch.Frequency;
        private volatile bool _closed;

        public string Kind => "Windows.Graphics.Capture";
        public long LastWorkMs { get; private set; }

        public WindowsCaptureGrabber(string display)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) || !GraphicsCaptureSession.IsSupported())
                throw new ScreenGrabberUnavailable("Windows.Graphics.Capture is not on this Windows.");

            ScreenDxgi.FindOutput(display, out nint adapter, out nint output, out var info);
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
                _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
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
            using var captured = _pool.TryGetNextFrame();
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
                    frame = _downscaler.Read(texture, IdentityRotation, UnusedSdrWhite);
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
        }
    }
}
