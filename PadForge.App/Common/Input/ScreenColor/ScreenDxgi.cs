using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input.ScreenColor
{
    /// <summary>A grabber that cannot run now, and may be able to soon: a UAC
    /// prompt or the lock screen holds the desktop.</summary>
    internal sealed class ScreenGrabberWaiting : Exception
    {
        public ScreenGrabberWaiting(string message) : base(message) { }
    }

    /// <summary>A grabber that cannot run on this PC in this setup, so the
    /// next kind should try.</summary>
    internal sealed class ScreenGrabberUnavailable : Exception
    {
        public ScreenGrabberUnavailable(string message) : base(message) { }
    }

    /// <summary>
    /// The DXGI and Direct3D 11 calls screen capture needs (#559), as raw COM
    /// slots through function pointers. A slot number is the method's place in
    /// the C vtable struct of the Windows SDK 10.0.26100 headers (d3d11.h,
    /// dxgi.h, dxgi1_2.h, dxgi1_5.h and dxgi1_6.h).
    /// </summary>
    internal static unsafe class ScreenDxgi
    {
        public const int ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        public const int ERROR_UNSUPPORTED = unchecked((int)0x887A0004);
        public const int ERROR_NOT_CURRENTLY_AVAILABLE = unchecked((int)0x887A0022);
        public const int E_NOTIMPL = unchecked((int)0x80004001);

        public const uint FORMAT_R16G16B16A16_FLOAT = 10;
        public const uint FORMAT_B8G8R8A8_UNORM = 87;

        /// <summary>DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020, what an output
        /// in HDR reports.</summary>
        public const int COLOR_SPACE_HDR = 12;

        public static Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        public static Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
        public static Guid IID_IDXGIOutput5 = new("80A07424-AB52-42EB-833C-0C42FD282D98");
        public static Guid IID_IDXGIOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");
        public static Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        public static Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(Guid* riid, out nint factory);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
            int* featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint context);

        [DllImport("d3d11.dll")]
        public static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

        public static nint Slot(nint com, int index) => (*(nint**)com)[index];

        public static void Release(ref nint com)
        {
            if (com != 0)
            {
                Marshal.Release(com);
                com = 0;
            }
        }

        /// <summary>What an output said about itself.</summary>
        public struct OutputInfo
        {
            public string Name;
            public int Rotation;
            public nint Monitor;
        }

        /// <summary>
        /// The adapter and output of a display, by its GDI name (\\.\DISPLAY1),
        /// with the empty name and a name no longer attached both meaning the
        /// primary display. The primary is the one at the desktop's origin. A
        /// device has to come from the adapter its output is connected to
        /// (Microsoft, IDXGIOutput5::DuplicateOutput1), which is why this walks
        /// every adapter the way Prismatik's DDuplGrabber does.
        /// </summary>
        public static void FindOutput(string display, out nint adapter, out nint output, out OutputInfo info)
        {
            FindOutput(display, out adapter, out output, out info, out nint factory);
            Release(ref factory);
        }

        /// <summary>The same, with the factory the walk ran on handed to the
        /// caller, who releases it. Its <see cref="IsCurrent"/> turns false when
        /// the displays change, an HDR switch among them.</summary>
        public static void FindOutput(string display, out nint adapter, out nint output, out OutputInfo info, out nint factoryOut)
        {
            int hr;
            nint factory;
            factoryOut = 0;
            fixed (Guid* iid = &IID_IDXGIFactory1)
                hr = CreateDXGIFactory1(iid, out factory);
            if (hr != 0) throw new ScreenGrabberUnavailable($"CreateDXGIFactory1 0x{hr:X8}");
            nint primaryAdapter = 0, primaryOutput = 0;
            var primaryInfo = default(OutputInfo);
            adapter = 0;
            output = 0;
            info = default;
            byte* desc = stackalloc byte[96];
            try
            {
                var enumAdapters = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, 12);
                for (uint a = 0; ; a++)
                {
                    nint candidate;
                    if (enumAdapters(factory, a, &candidate) != 0) break;
                    var enumOutputs = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(candidate, 7);
                    bool keepAdapter = false;
                    for (uint o = 0; ; o++)
                    {
                        nint one;
                        if (enumOutputs(candidate, o, &one) != 0) break;
                        ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(one, 7))(one, desc);
                        string name = Marshal.PtrToStringUni((nint)desc) ?? string.Empty;
                        bool attached = *(int*)(desc + 80) != 0;
                        bool primary = attached && *(int*)(desc + 64) == 0 && *(int*)(desc + 68) == 0;
                        var found = new OutputInfo { Name = name, Rotation = *(int*)(desc + 84), Monitor = *(nint*)(desc + 88) };
                        if (attached && !string.IsNullOrEmpty(display) && string.Equals(name, display, StringComparison.OrdinalIgnoreCase))
                        {
                            // The primary may be on this adapter too, and the
                            // one reference to it moves to the caller.
                            if (primaryAdapter == candidate) primaryAdapter = 0;
                            adapter = candidate;
                            output = one;
                            info = found;
                            factoryOut = factory;
                            factory = 0;
                            return;
                        }
                        if (primary && primaryOutput == 0)
                        {
                            primaryAdapter = candidate;
                            primaryOutput = one;
                            primaryInfo = found;
                            keepAdapter = true;
                            continue;
                        }
                        Marshal.Release(one);
                    }
                    if (!keepAdapter) Marshal.Release(candidate);
                }
                if (primaryOutput == 0) throw new ScreenGrabberUnavailable("No display is attached to the desktop.");
                adapter = primaryAdapter;
                output = primaryOutput;
                info = primaryInfo;
                primaryAdapter = 0;
                primaryOutput = 0;
                factoryOut = factory;
                factory = 0;
            }
            finally
            {
                Release(ref primaryOutput);
                Release(ref primaryAdapter);
                Release(ref factory);
            }
        }

        /// <summary>IDXGIFactory1::IsCurrent (slot 13): false once the adapters
        /// or outputs a factory saw have changed. Sunshine asks it on every frame
        /// of every capture, its Windows.Graphics.Capture one included, because
        /// "This will return false if the HDR state changes"
        /// (src/platform/windows/display_base.cpp:337-341).</summary>
        public static bool IsCurrent(nint factory)
            => factory != 0 && ((delegate* unmanaged[Stdcall]<nint, int>)Slot(factory, 13))(factory) != 0;

        /// <summary>A device on one adapter, from feature levels the runtime
        /// picks.</summary>
        public static void CreateDevice(nint adapter, out nint device, out nint context)
        {
            const int DriverTypeUnknown = 0;
            const uint SdkVersion = 7;
            int hr = D3D11CreateDevice(adapter, DriverTypeUnknown, 0, 0, null, 0, SdkVersion, out device, out _, out context);
            if (hr != 0) throw new ScreenGrabberUnavailable($"D3D11CreateDevice 0x{hr:X8}");
        }

        /// <summary>Whether an output is in HDR now.</summary>
        public static bool IsHdr(nint output)
        {
            if (Marshal.QueryInterface(output, in IID_IDXGIOutput6, out nint output6) != 0) return false;
            try
            {
                byte* desc = stackalloc byte[160];
                int hr = ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(output6, 27))(output6, desc);
                return hr == 0 && *(int*)(desc + 100) == COLOR_SPACE_HDR;
            }
            finally
            {
                Marshal.Release(output6);
            }
        }

        /// <summary>The white level Windows draws a display's SDR content at in
        /// HDR, in nits, or the 200 most desktops sit near when it does not
        /// say. Microsoft's DISPLAYCONFIG_SDR_WHITE_LEVEL holds it in
        /// thousandths of 80 nits.</summary>
        public static float SdrWhiteNits(string display)
        {
            const float Fallback = 200f;
            const uint OnlyActivePaths = 2;
            const int PathSize = 72, ModeSize = 64;
            const uint GetSourceName = 1, GetSdrWhiteLevel = 11;
            if (GetDisplayConfigBufferSizes(OnlyActivePaths, out uint paths, out uint modes) != 0 || paths == 0) return Fallback;
            byte[] pathBytes = new byte[paths * PathSize];
            byte[] modeBytes = new byte[modes * ModeSize];
            fixed (byte* pathData = pathBytes)
            fixed (byte* modeData = modeBytes)
            {
                if (QueryDisplayConfig(OnlyActivePaths, ref paths, pathData, ref modes, modeData, 0) != 0) return Fallback;
                byte* name = stackalloc byte[84];
                byte* white = stackalloc byte[24];
                for (uint i = 0; i < paths; i++)
                {
                    byte* path = pathData + i * PathSize;
                    Header(name, GetSourceName, 84, path, sourceId: *(uint*)(path + 8));
                    if (DisplayConfigGetDeviceInfo(name) != 0) continue;
                    string gdi = Marshal.PtrToStringUni((nint)(name + 20)) ?? string.Empty;
                    if (!string.IsNullOrEmpty(display) && !string.Equals(gdi, display, StringComparison.OrdinalIgnoreCase)) continue;
                    Header(white, GetSdrWhiteLevel, 24, path + 20, sourceId: *(uint*)(path + 28));
                    if (DisplayConfigGetDeviceInfo(white) != 0) continue;
                    uint level = *(uint*)(white + 20);
                    if (level > 0) return level * 80f / 1000f;
                }
            }
            return Fallback;
        }

        private static void Header(byte* header, uint type, uint size, byte* adapterId, uint sourceId)
        {
            *(uint*)header = type;
            *(uint*)(header + 4) = size;
            *(long*)(header + 8) = *(long*)adapterId;
            *(uint*)(header + 16) = sourceId;
        }

        /// <summary>Whether DXGI's own walk, on a new factory so it sees the
        /// displays as they are now, finds <paramref name="display"/> attached:
        /// the question <see cref="FindOutput"/> answers when a capture is built,
        /// without falling back to the primary.</summary>
        public static bool Resolves(string display)
        {
            if (string.IsNullOrEmpty(display)) return false;
            nint adapter = 0, output = 0;
            try
            {
                FindOutput(display, out adapter, out output, out var info);
                return string.Equals(info.Name, display, StringComparison.OrdinalIgnoreCase);
            }
            catch (ScreenGrabberUnavailable)
            {
                return false;
            }
            finally
            {
                Release(ref output);
                Release(ref adapter);
            }
        }

        /// <summary>Whether a display by its GDI name (\\.\DISPLAY2) is attached
        /// to the desktop now: DISPLAY_DEVICEW's StateFlags at byte 324, with
        /// DISPLAY_DEVICE_ATTACHED_TO_DESKTOP (wingdi.h).</summary>
        public static bool IsAttached(string display)
        {
            const int Size = 840, NameAt = 4, FlagsAt = 324;
            const uint AttachedToDesktop = 0x1;
            if (string.IsNullOrEmpty(display)) return false;
            byte* device = stackalloc byte[Size];
            for (uint i = 0; ; i++)
            {
                new Span<byte>(device, Size).Clear();
                *(uint*)device = Size;
                if (!EnumDisplayDevicesW(null, i, device, 0)) return false;
                string name = Marshal.PtrToStringUni((nint)(device + NameAt)) ?? string.Empty;
                if (string.Equals(name, display, StringComparison.OrdinalIgnoreCase))
                    return (*(uint*)(device + FlagsAt) & AttachedToDesktop) != 0;
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevicesW(string device, uint index, byte* displayDevice, uint flags);

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint paths, byte* pathArray, ref uint modes, byte* modeArray, nint topology);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(byte* packet);
    }

    /// <summary>
    /// A desktop texture down to a few dozen cells on the GPU, then read back
    /// (#559): a mip chain as ScreenCapture.NET builds one
    /// (DX11ScreenCapture.PerformCaptureZoneUpdate), so no full frame reaches
    /// the CPU. Runs on the one thread that owns its device.
    /// </summary>
    internal sealed unsafe class ScreenDownscaler : IDisposable
    {
        /// <summary>The most columns a picture has.</summary>
        internal const int MaxCols = 64;

        private readonly nint _device;
        private readonly nint _context;
        private nint _scale, _view, _staging;
        private uint _width, _height, _format, _mip, _cols, _rows;

        public ScreenDownscaler(nint device, nint context)
        {
            _device = device;
            _context = context;
        }

        /// <summary>The picture in a desktop texture, or null when the device
        /// refused a step, in which case the capture is rebuilt.</summary>
        public ScreenFrame Read(nint texture, int dxgiRotation, float sdrWhiteNits)
        {
            uint* desc = stackalloc uint[11];
            ((delegate* unmanaged[Stdcall]<nint, uint*, void>)ScreenDxgi.Slot(texture, 10))(texture, desc);
            if (desc[0] != _width || desc[1] != _height || desc[4] != _format)
            {
                DisposeTextures();
                if (!Build(desc[0], desc[1], desc[4])) return null;
            }

            var copy = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, void*, void>)ScreenDxgi.Slot(_context, 46);
            copy(_context, _scale, 0, 0, 0, 0, texture, 0, null);
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)ScreenDxgi.Slot(_context, 54))(_context, _view);
            copy(_context, _staging, 0, 0, 0, 0, _scale, _mip, null);

            const uint MapRead = 1;
            byte* mapped = stackalloc byte[16];
            int hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, void*, int>)ScreenDxgi.Slot(_context, 14))(_context, _staging, 0, MapRead, 0, mapped);
            if (hr != 0) return null;
            var cells = new int[_cols * _rows];
            byte* data = *(byte**)mapped;
            uint pitch = *(uint*)(mapped + 8);
            if (_format == ScreenDxgi.FORMAT_R16G16B16A16_FLOAT)
            {
                for (uint y = 0; y < _rows; y++)
                {
                    var row = (Half*)(data + y * pitch);
                    for (uint x = 0; x < _cols; x++)
                        cells[y * _cols + x] = ScreenColorMath.ScRgbToRgb((float)row[x * 4], (float)row[x * 4 + 1], (float)row[x * 4 + 2], sdrWhiteNits);
                }
            }
            else
            {
                for (uint y = 0; y < _rows; y++)
                {
                    byte* row = data + y * pitch;
                    for (uint x = 0; x < _cols; x++)
                        cells[y * _cols + x] = (row[x * 4 + 2] << 16) | (row[x * 4 + 1] << 8) | row[x * 4];
                }
            }
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)ScreenDxgi.Slot(_context, 15))(_context, _staging, 0);
            return ScreenColorMath.Oriented((int)_cols, (int)_rows, cells, dxgiRotation);
        }

        private bool Build(uint width, uint height, uint format)
        {
            uint mip = 0;
            while ((width >> (int)mip) > MaxCols) mip++;
            uint cols = Math.Max(1, width >> (int)mip);
            uint rows = Math.Max(1, height >> (int)mip);

            const uint BindRenderTargetShaderResource = 0x28, MiscGenerateMips = 1, UsageStaging = 3, CpuRead = 0x20000;
            var createTexture = (delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)ScreenDxgi.Slot(_device, 5);
            uint* desc = stackalloc uint[11];
            desc[0] = width; desc[1] = height; desc[2] = mip + 1; desc[3] = 1; desc[4] = format;
            desc[5] = 1; desc[6] = 0; desc[7] = 0; desc[8] = BindRenderTargetShaderResource; desc[9] = 0; desc[10] = MiscGenerateMips;
            nint scale, staging, view;
            if (createTexture(_device, desc, 0, &scale) != 0) return false;
            _scale = scale;
            if (((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)ScreenDxgi.Slot(_device, 7))(_device, _scale, 0, &view) != 0) return false;
            _view = view;
            desc[0] = cols; desc[1] = rows; desc[2] = 1; desc[7] = UsageStaging; desc[8] = 0; desc[9] = CpuRead; desc[10] = 0;
            if (createTexture(_device, desc, 0, &staging) != 0) return false;
            _staging = staging;
            _width = width;
            _height = height;
            _format = format;
            _mip = mip;
            _cols = cols;
            _rows = rows;
            return true;
        }

        private void DisposeTextures()
        {
            ScreenDxgi.Release(ref _staging);
            ScreenDxgi.Release(ref _view);
            ScreenDxgi.Release(ref _scale);
            _width = _height = _format = 0;
        }

        public void Dispose() => DisposeTextures();
    }

    /// <summary>
    /// One display through Desktop Duplication (#559), Microsoft's API for
    /// reading the desktop image. On an HDR display it asks for the 16-bit float
    /// format with DuplicateOutput1, and a frame in that format converts through
    /// the display's SDR white level, the fields Sunshine reads
    /// (src/platform/windows/display_base.cpp). A process that is not per-monitor
    /// DPI aware gets DXGI_ERROR_UNSUPPORTED from DuplicateOutput1, measured, so
    /// the older DuplicateOutput follows it.
    /// </summary>
    internal sealed unsafe class DesktopDuplicationGrabber : IScreenGrabber
    {
        private nint _device, _context, _duplication;
        private readonly ScreenDownscaler _downscaler;
        private readonly int _rotation;
        private readonly float _sdrWhite;
        private readonly long _frequency = Stopwatch.Frequency;

        public string Kind => "Desktop Duplication";
        public long LastWorkMs { get; private set; }
        public string Output { get; }

        public DesktopDuplicationGrabber(string display)
        {
            ScreenDxgi.FindOutput(display, out nint adapter, out nint output, out var info);
            Output = info.Name;
            try
            {
                ScreenDxgi.CreateDevice(adapter, out _device, out _context);
                bool hdr = ScreenDxgi.IsHdr(output);
                _sdrWhite = hdr ? ScreenDxgi.SdrWhiteNits(info.Name) : 80f;
                int hr = Duplicate(output, hdr, out _duplication);
                if (_duplication == 0)
                {
                    string why = $"DuplicateOutput on {info.Name} 0x{hr:X8}";
                    // Prismatik's DDuplGrabber::_reallocate sorts these the same way:
                    // a PC where this cannot run, against a desktop held for now.
                    if (hr is ScreenDxgi.ERROR_UNSUPPORTED or ScreenDxgi.ERROR_NOT_CURRENTLY_AVAILABLE or ScreenDxgi.E_NOTIMPL)
                        throw new ScreenGrabberUnavailable(why);
                    throw new ScreenGrabberWaiting(why);
                }
                byte* desc = stackalloc byte[36];
                ((delegate* unmanaged[Stdcall]<nint, byte*, void>)ScreenDxgi.Slot(_duplication, 7))(_duplication, desc);
                _rotation = *(int*)(desc + 28);
                _downscaler = new ScreenDownscaler(_device, _context);
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                ScreenDxgi.Release(ref output);
                ScreenDxgi.Release(ref adapter);
            }
        }

        private int Duplicate(nint output, bool hdr, out nint duplication)
        {
            duplication = 0;
            int hr = ScreenDxgi.ERROR_UNSUPPORTED;
            if (Marshal.QueryInterface(output, in ScreenDxgi.IID_IDXGIOutput5, out nint output5) == 0)
            {
                uint* formats = stackalloc uint[2];
                uint count = 0;
                if (hdr) formats[count++] = ScreenDxgi.FORMAT_R16G16B16A16_FLOAT;
                formats[count++] = ScreenDxgi.FORMAT_B8G8R8A8_UNORM;
                nint made;
                hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint*, nint*, int>)ScreenDxgi.Slot(output5, 26))(output5, _device, 0, count, formats, &made);
                Marshal.Release(output5);
                if (hr == 0)
                {
                    duplication = made;
                    return 0;
                }
                if (hr != ScreenDxgi.ERROR_UNSUPPORTED) return hr;
            }
            if (Marshal.QueryInterface(output, in ScreenDxgi.IID_IDXGIOutput1, out nint output1) == 0)
            {
                nint made;
                hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)ScreenDxgi.Slot(output1, 22))(output1, _device, &made);
                Marshal.Release(output1);
                if (hr == 0) duplication = made;
            }
            return hr;
        }

        public ScreenGrabResult Grab(int timeoutMs, out ScreenFrame frame)
        {
            frame = null;
            byte* info = stackalloc byte[48];
            nint resource = 0;
            int hr = ((delegate* unmanaged[Stdcall]<nint, uint, byte*, nint*, int>)ScreenDxgi.Slot(_duplication, 8))(_duplication, (uint)timeoutMs, info, &resource);
            if (hr == ScreenDxgi.ERROR_WAIT_TIMEOUT) return ScreenGrabResult.NoChange;
            if (hr != 0) return ScreenGrabResult.Lost;
            try
            {
                // A frame with no present time only moved the pointer
                // (ScreenCapture.NET, DX11ScreenCapture.CaptureScreen).
                if (resource == 0 || *(long*)info == 0) return ScreenGrabResult.NoChange;
                if (Marshal.QueryInterface(resource, in ScreenDxgi.IID_ID3D11Texture2D, out nint texture) != 0)
                    return ScreenGrabResult.Lost;
                try
                {
                    long start = Stopwatch.GetTimestamp();
                    frame = _downscaler.Read(texture, _rotation, _sdrWhite);
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
                ScreenDxgi.Release(ref resource);
                ((delegate* unmanaged[Stdcall]<nint, int>)ScreenDxgi.Slot(_duplication, 14))(_duplication);
            }
        }

        public void Dispose()
        {
            _downscaler?.Dispose();
            ScreenDxgi.Release(ref _duplication);
            ScreenDxgi.Release(ref _context);
            ScreenDxgi.Release(ref _device);
        }
    }
}
