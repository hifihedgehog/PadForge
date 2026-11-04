using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using PadForge.Common.Input.Peripherals;

namespace PadForge.Services
{
    /// <summary>
    /// Production native layer of the iCUE worker
    /// (<see cref="IcueBackend"/>, #557): the iCUE SDK v4's client library,
    /// loaded from where it is found, with the exports RGB.NET's provider
    /// resolves (RGB.NET.Devices.Corsair Native/_CUESDK.cs:108-120).
    ///
    /// <para>Where the library comes from. Corsair ships it in the SDK's
    /// release zip, in the redist folder the SDK's documentation describes
    /// as the 32 and 64 bit .dll files (cue-sdk docs/index.html, "SDK
    /// Package"), for an application to put beside its own executable
    /// (RGB.NET.Devices.Corsair README.md, "Required SDK"), and Aurora ships
    /// it so (AuroraDeviceManager.csproj:141, loaded in
    /// NewCorsairRgbNetDevice.cs:39-42). PadForge ships version
    /// 4.0.84, signed by Corsair Memory, Inc., inside its executable, as it
    /// ships the Interhaptics engine (PadForge.App.csproj), and loads it by
    /// name where the runtime finds the libraries the executable carries. A
    /// copy in iCUE 5's own folder is the fallback (<see cref="LibraryPaths"/>).
    /// The route is offered where iCUE 4 or 5 is installed
    /// (<see cref="Installed"/>, <see cref="PeripheralPresence.CorsairIcueSdk"/>),
    /// the versions SDK v4 serves ("SDK features are supported in iCUE
    /// version 4.31 or higher", docs/index.html, "Overview"). The library is
    /// x64 only from SDK v4.0.84 on (the README's x86 note), so an ARM64
    /// process never loads it.</para>
    ///
    /// <para>Marshaling, from the SDK's header (cue-sdk
    /// src/include/iCUESDK.h): every export is cdecl and returns a
    /// CorsairError (RGB.NET's function pointers are unmanaged[Cdecl],
    /// _CUESDK.cs:169-181). A device id is a char array of
    /// CORSAIR_STRING_SIZE_M, passed as a pointer to its first byte (lines
    /// 17 and 336-384). CorsairDeviceInfo is the type, three char arrays of
    /// 128 and two ints, 396 bytes (212-220). CorsairLedPosition is an
    /// unsigned int and two doubles, 24 bytes with the doubles aligned to 8
    /// (222-227). CorsairLedColor is an unsigned int and four bytes, 8 bytes
    /// (299-306). The session handler gets a pointer to
    /// CorsairSessionStateChanged, whose first field is the state (206-210,
    /// 315). The handler's delegate lives as long as the process, as
    /// RGB.NET's does (_CUESDK.cs:50-51).</para>
    ///
    /// <para>Single caller: the worker's thread makes every call.</para>
    /// </summary>
    internal sealed class IcueSdkNative : IIcueNative
    {
        /// <summary>Set in the test host: no library is loaded, so no call
        /// reaches iCUE.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>The library's file name in the SDK's x64 redist folder
        /// (RGB.NET.Devices.Corsair README.md, CorsairDeviceProvider.cs:46).</summary>
        internal const string LibraryName = "iCUESDK.x64_2019.dll";

        /// <summary>Where iCUE 5 installs itself, the folder uninstall
        /// listings give for it (Advanced Uninstaller, Corsair iCUE5 Software
        /// 5.15.117: C:\Program Files\Corsair\Corsair iCUE5 Software). No
        /// source shows the library in it, so it is the fallback to
        /// PadForge's own copy.</summary>
        internal static IEnumerable<string> LibraryPaths()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
                yield return Path.Combine(programFiles, "Corsair", "Corsair iCUE5 Software", LibraryName);
        }

        /// <summary>Where iCUE 5 and iCUE 4 put iCUE.exe: Corsair iCUE5
        /// Software and CORSAIR iCUE 4 Software under Program Files
        /// (Advanced Uninstaller's entries for iCUE 5.15.117 and for iCUE 4),
        /// where Corsair's own clean-reinstall article puts iCUE 4 and 5
        /// (help.corsair.com, article 360025166712).</summary>
        internal static IEnumerable<string> ProgramPaths()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrEmpty(programFiles)) yield break;
            yield return Path.Combine(programFiles, "Corsair", "Corsair iCUE5 Software", "iCUE.exe");
            yield return Path.Combine(programFiles, "Corsair", "CORSAIR iCUE 4 Software", "iCUE.exe");
        }

        /// <summary>The value iCUE 5's installer writes under the machine's
        /// Run key, which Aurora takes as iCUE installed wherever it went
        /// (Aurora IcueInstallationUtils.cs:11-17, AutoStartUtils.cs:84-98).</summary>
        internal const string RunValueName = "Corsair iCUE5 Software";

        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>Whether iCUE 4 or 5 is installed, in an x64 process: its
        /// iCUE.exe in its folder (<see cref="ProgramPaths"/>), or iCUE 5's
        /// Run value (<see cref="RunValueName"/>).</summary>
        internal static bool Installed()
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return false;
            try
            {
                foreach (string path in ProgramPaths())
                    if (File.Exists(path)) return true;
                using var run = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RunKey);
                return run?.GetValue(RunValueName) != null;
            }
            catch { return false; }
        }

        /// <summary>The first of <see cref="LibraryPaths"/> that exists, in
        /// an x64 process, or null.</summary>
        internal static string FindLibrary()
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return null;
            try
            {
                foreach (string path in LibraryPaths())
                    if (File.Exists(path)) return path;
            }
            catch { }
            return null;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        private const uint LoadWithAlteredSearchPath = 0x00000008;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void StateHandler(IntPtr context, IntPtr eventData);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ConnectFn(StateHandler handler, IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NoArgFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetDevicesFn(ref int filter, int sizeMax, IntPtr devices, out int size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetLedPositionsFn(byte[] deviceId, int sizeMax, IntPtr positions, out int size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetLedColorsFn(byte[] deviceId, int size, [In] IcueLedColor[] colors);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int RequestControlFn(byte[] deviceId, int accessLevel);

        /// <summary>The struct sizes of the header's layout.</summary>
        internal const int DeviceInfoSize = 4 + 3 * IcueRoute.StringSizeM + 4 + 4;
        internal const int LedPositionSize = 24;

        private IntPtr _module;
        private ConnectFn _connect;
        private NoArgFn _disconnect;
        private GetDevicesFn _getDevices;
        private GetLedPositionsFn _getLedPositions;
        private SetLedColorsFn _setLedColors;
        private RequestControlFn _requestControl;

        /// <summary>The session handler and the state it keeps. The SDK's
        /// client is one per process and keeps the handler it was given
        /// until CorsairDisconnect, so both are the process's too, and the
        /// handler's delegate is never collected, as RGB.NET keeps its
        /// handler in a static field (_CUESDK.cs:50-51 and 66-69).</summary>
        private static readonly StateHandler s_handler = OnStateChanged;
        private static int s_state;

        public int State => Volatile.Read(ref s_state);

        public bool TryLoad(out string detail)
        {
            if (OffLimitsForTest || PadForge.Engine.Common.VendorProgramGate.Refuses)
            {
                detail = "the test host loads no vendor library";
                return false;
            }
            if (_module != IntPtr.Zero) { detail = "already loaded"; return true; }
            // PadForge's own copy, by name, where the runtime finds the
            // libraries the executable carries. A copy in iCUE's folder is
            // the fallback.
            string path = LibraryName + " (PadForge's copy)";
            IntPtr module = IntPtr.Zero;
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                try { NativeLibrary.TryLoad(LibraryName, typeof(IcueSdkNative).Assembly, DllImportSearchPath.AssemblyDirectory, out module); }
                catch { module = IntPtr.Zero; }
            }
            if (module == IntPtr.Zero)
            {
                path = FindLibrary();
                if (path == null) { detail = LibraryName + " not found"; return false; }
                module = LoadLibraryExW(path, IntPtr.Zero, LoadWithAlteredSearchPath);
                if (module == IntPtr.Zero)
                {
                    detail = $"LoadLibrary failed ({Marshal.GetLastWin32Error()}) for {path}";
                    return false;
                }
            }

            T Resolve<T>(string name) where T : class
            {
                IntPtr fn = GetProcAddress(module, name);
                return fn == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(fn) as T;
            }

            var connect = Resolve<ConnectFn>("CorsairConnect");
            var disconnect = Resolve<NoArgFn>("CorsairDisconnect");
            var getDevices = Resolve<GetDevicesFn>("CorsairGetDevices");
            var getLedPositions = Resolve<GetLedPositionsFn>("CorsairGetLedPositions");
            var setLedColors = Resolve<SetLedColorsFn>("CorsairSetLedColors");
            var requestControl = Resolve<RequestControlFn>("CorsairRequestControl");
            if (connect == null || disconnect == null || getDevices == null || getLedPositions == null
                || setLedColors == null || requestControl == null)
            {
                FreeLibrary(module);
                detail = "iCUE SDK exports missing in " + path;
                return false;
            }
            _module = module;
            _connect = connect;
            _disconnect = disconnect;
            _getDevices = getDevices;
            _getLedPositions = getLedPositions;
            _setLedColors = setLedColors;
            _requestControl = requestControl;
            detail = "loaded " + path;
            return true;
        }

        private static void OnStateChanged(IntPtr context, IntPtr eventData)
        {
            if (eventData == IntPtr.Zero) return;
            try { Volatile.Write(ref s_state, Marshal.ReadInt32(eventData)); } catch { }
        }

        public int Connect() => _connect == null ? IcueRoute.NotConnected : _connect(s_handler, IntPtr.Zero);

        public int GetDevices(int typeMask, out IReadOnlyList<IcueDeviceInfo> devices)
        {
            devices = null;
            if (_getDevices == null) return IcueRoute.NotConnected;
            IntPtr buffer = Marshal.AllocHGlobal(DeviceInfoSize * IcueRoute.DeviceCountMax);
            try
            {
                int filter = typeMask;
                int error = _getDevices(ref filter, IcueRoute.DeviceCountMax, buffer, out int size);
                if (error != IcueRoute.Success) return error;
                var list = new List<IcueDeviceInfo>(size);
                for (int i = 0; i < Math.Min(size, IcueRoute.DeviceCountMax); i++)
                {
                    IntPtr at = buffer + i * DeviceInfoSize;
                    list.Add(new IcueDeviceInfo(
                        Marshal.ReadInt32(at),
                        ReadString(at + 4),
                        ReadString(at + 4 + IcueRoute.StringSizeM),
                        ReadString(at + 4 + 2 * IcueRoute.StringSizeM),
                        Marshal.ReadInt32(at + 4 + 3 * IcueRoute.StringSizeM),
                        Marshal.ReadInt32(at + 8 + 3 * IcueRoute.StringSizeM)));
                }
                devices = list;
                return error;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public int GetLedPositions(string deviceId, out IReadOnlyList<IcueLedPosition> leds)
        {
            leds = null;
            if (_getLedPositions == null) return IcueRoute.NotConnected;
            IntPtr buffer = Marshal.AllocHGlobal(LedPositionSize * IcueRoute.LedCountMax);
            try
            {
                int error = _getLedPositions(Id(deviceId), IcueRoute.LedCountMax, buffer, out int size);
                if (error != IcueRoute.Success) return error;
                var list = new List<IcueLedPosition>(size);
                for (int i = 0; i < Math.Min(size, IcueRoute.LedCountMax); i++)
                {
                    IntPtr at = buffer + i * LedPositionSize;
                    list.Add(new IcueLedPosition(unchecked((uint)Marshal.ReadInt32(at)),
                        BitConverter.Int64BitsToDouble(Marshal.ReadInt64(at + 8)),
                        BitConverter.Int64BitsToDouble(Marshal.ReadInt64(at + 16))));
                }
                leds = list;
                return error;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public int RequestControl(string deviceId, int accessLevel)
            => _requestControl == null ? IcueRoute.NotConnected : _requestControl(Id(deviceId), accessLevel);

        public int SetLedColors(string deviceId, IcueLedColor[] colors)
            => _setLedColors == null ? IcueRoute.NotConnected : _setLedColors(Id(deviceId), colors.Length, colors);

        public int Disconnect() => _disconnect == null ? IcueRoute.NotConnected : _disconnect();

        public void Unload()
        {
            _connect = null;
            _disconnect = null;
            _getDevices = null;
            _getLedPositions = null;
            _setLedColors = null;
            _requestControl = null;
            if (_module != IntPtr.Zero)
            {
                try { FreeLibrary(_module); } catch { }
                _module = IntPtr.Zero;
            }
        }

        /// <summary>A device id as the SDK takes it: the id's bytes, ended
        /// with a zero, in a buffer of CORSAIR_STRING_SIZE_M.</summary>
        internal static byte[] Id(string deviceId)
        {
            var buffer = new byte[IcueRoute.StringSizeM];
            var bytes = Encoding.UTF8.GetBytes(deviceId ?? string.Empty);
            Array.Copy(bytes, buffer, Math.Min(bytes.Length, buffer.Length - 1));
            return buffer;
        }

        /// <summary>A char array of CORSAIR_STRING_SIZE_M up to its first
        /// zero, read as UTF-8.</summary>
        private static string ReadString(IntPtr at)
        {
            var bytes = new byte[IcueRoute.StringSizeM];
            Marshal.Copy(at, bytes, 0, bytes.Length);
            int end = Array.IndexOf(bytes, (byte)0);
            return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
        }
    }
}
