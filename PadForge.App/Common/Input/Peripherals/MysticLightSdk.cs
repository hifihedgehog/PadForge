using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The native seam of the Mystic Light SDK worker (#557): the MLAPI
    /// calls as MSI's Mystic Light Software Development Kit 1.0.0.08
    /// documents them, each answering an MLAPI status, 0 for MLAPI_OK
    /// (pp. 2-9). Production is <see cref="MysticLightNative"/>, the test
    /// bench scripts a fake. Every call is made on the worker's thread: the
    /// SDK "doesn't support parallel access" (mystic-light-sdk README.md,
    /// "Parallelism").
    /// </summary>
    internal interface IMysticLightNative
    {
        /// <summary>Finds and loads the SDK's library and resolves its
        /// exports. False with a reason when it cannot.</summary>
        bool TryLoad(out string detail);

        int Initialize();

        /// <summary>MLAPI_GetDeviceInfo: each device type and its LED count,
        /// which the SDK hands back as strings (RGB.NET _MsiSDK.cs:214-224).</summary>
        int GetDeviceInfo(out string[] types, out string[] ledCounts);

        int GetLedStyle(string type, int index, out string style);

        int GetLedColor(string type, int index, out int r, out int g, out int b);

        int SetLedStyle(string type, int index, string style);

        int SetLedColor(string type, int index, int r, int g, int b);

        /// <summary>MLAPI_Release, 0 when the library has no such export
        /// (the SDK before 1.0.0.08).</summary>
        int Release();

        /// <summary>Frees the library. Idempotent.</summary>
        void Unload();
    }

    /// <summary>
    /// The Mystic Light SDK library, MysticLight_SDK, loaded by its path
    /// and called through its exports as RGB.NET.Devices.Msi calls them
    /// (Native/_MsiSDK.cs:30-62 and 125-209, Cdecl, BSTR strings, SAFEARRAY
    /// lists of BSTR) and as MSI's own sample declares them
    /// (MysticLight_Test(C#).cs:37-70).
    ///
    /// <para>Where the library is found. MSI hands the SDK out as a
    /// download for an application to ship beside itself (RGB.NET.Devices.Msi
    /// README.md, "Required SDK"), and its documentation asks only that
    /// "MSI Mystic Light related applications must be installed" (p.2).
    /// PadForge ships no copy of MSI's library. It looks for one in the
    /// folder of MSI's Mystic Light service, which the service's ImagePath
    /// names (HKLM\SYSTEM\CurrentControlSet\Services\Mystic_Light_Service,
    /// the service MSI's installer registers, Advanced Uninstaller PRO's
    /// entry for "MSI SDK" 1.0.0.46), and in that folder's Lib folder, by
    /// the 64-bit library's own name in MSI's download
    /// (MysticLight_SDK_x64.dll, Mystic_light_SDK.zip 1.0.0.08) and by
    /// the name RGB.NET gives it (MysticLight_SDK.dll). A library built for
    /// another processor than PadForge's is passed over.</para>
    /// </summary>
    internal sealed class MysticLightNative : IMysticLightNative
    {
        internal const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\Mystic_Light_Service";
        internal static readonly string[] LibraryNames = { "MysticLight_SDK_x64.dll", "MysticLight_SDK.dll" };

        private IntPtr _module;
        private InitializeFn _initialize;
        private GetDeviceInfoFn _getDeviceInfo;
        private GetLedStyleFn _getLedStyle;
        private GetLedColorFn _getLedColor;
        private SetLedStyleFn _setLedStyle;
        private SetLedColorFn _setLedColor;
        private InitializeFn _release;

        /// <summary>The library to load, or null when none is found.</summary>
        internal static string FindLibrary()
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return null;
            string folder = ServiceFolder();
            if (folder == null) return null;
            foreach (string dir in new[] { folder, Path.Combine(folder, "Lib") })
                foreach (string name in LibraryNames)
                {
                    string path = Path.Combine(dir, name);
                    if (IsX64Library(path)) return path;
                }
            return null;
        }

        /// <summary>The folder of the service's executable, from its
        /// ImagePath, quoted or not, or null.</summary>
        internal static string ServiceFolder()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
                return FolderOfImagePath(key?.GetValue("ImagePath")?.ToString());
            }
            catch { return null; }
        }

        /// <summary>The folder an ImagePath's executable sits in: the
        /// quoted path, or the text up to ".exe", with environment
        /// variables expanded.</summary>
        internal static string FolderOfImagePath(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            string text = imagePath.Trim();
            string exe;
            if (text.StartsWith("\"", StringComparison.Ordinal))
            {
                int close = text.IndexOf('"', 1);
                if (close < 0) return null;
                exe = text.Substring(1, close - 1);
            }
            else
            {
                int end = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                exe = end < 0 ? text : text.Substring(0, end + 4);
            }
            exe = Environment.ExpandEnvironmentVariables(exe);
            try { return Path.GetDirectoryName(exe); }
            catch { return null; }
        }

        /// <summary>Whether a file is a PE image for x64 (machine 0x8664),
        /// read from its headers alone.</summary>
        internal static bool IsX64Library(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using var stream = File.OpenRead(path);
                var header = new byte[0x40];
                if (stream.Read(header, 0, header.Length) != header.Length || header[0] != (byte)'M' || header[1] != (byte)'Z')
                    return false;
                int pe = BitConverter.ToInt32(header, 0x3C);
                if (pe <= 0 || pe > stream.Length - 6) return false;
                stream.Position = pe;
                var signature = new byte[6];
                if (stream.Read(signature, 0, 6) != 6) return false;
                return signature[0] == (byte)'P' && signature[1] == (byte)'E' && signature[2] == 0 && signature[3] == 0
                       && BitConverter.ToUInt16(signature, 4) == 0x8664;
            }
            catch { return false; }
        }

        public bool TryLoad(out string detail)
        {
            if (PadForge.Engine.Common.VendorProgramGate.Refuses)
            {
                detail = "the test host loads no vendor library";
                return false;
            }
            if (_module != IntPtr.Zero) { detail = "already loaded"; return true; }
            string path = FindLibrary();
            if (path == null) { detail = "MysticLight_SDK not found beside the Mystic Light service"; return false; }
            if (!NativeLibrary.TryLoad(path, out IntPtr module))
            {
                detail = "LoadLibrary failed for " + path;
                return false;
            }

            T Resolve<T>(string name) where T : Delegate
                => NativeLibrary.TryGetExport(module, name, out IntPtr fn) ? Marshal.GetDelegateForFunctionPointer<T>(fn) : null;

            var initialize = Resolve<InitializeFn>("MLAPI_Initialize");
            var getDeviceInfo = Resolve<GetDeviceInfoFn>("MLAPI_GetDeviceInfo");
            var getLedStyle = Resolve<GetLedStyleFn>("MLAPI_GetLedStyle");
            var getLedColor = Resolve<GetLedColorFn>("MLAPI_GetLedColor");
            var setLedStyle = Resolve<SetLedStyleFn>("MLAPI_SetLedStyle");
            var setLedColor = Resolve<SetLedColorFn>("MLAPI_SetLedColor");
            if (initialize == null || getDeviceInfo == null || getLedStyle == null || getLedColor == null
                || setLedStyle == null || setLedColor == null)
            {
                NativeLibrary.Free(module);
                detail = "required MLAPI exports missing in " + path;
                return false;
            }
            _module = module;
            _initialize = initialize;
            _getDeviceInfo = getDeviceInfo;
            _getLedStyle = getLedStyle;
            _getLedColor = getLedColor;
            _setLedStyle = setLedStyle;
            _setLedColor = setLedColor;
            _release = Resolve<InitializeFn>("MLAPI_Release");
            detail = path;
            return true;
        }

        public int Initialize() => _initialize();

        public int GetDeviceInfo(out string[] types, out string[] ledCounts) => _getDeviceInfo(out types, out ledCounts);

        public int GetLedStyle(string type, int index, out string style) => _getLedStyle(type, index, out style);

        public int GetLedColor(string type, int index, out int r, out int g, out int b) => _getLedColor(type, index, out r, out g, out b);

        public int SetLedStyle(string type, int index, string style) => _setLedStyle(type, index, style);

        public int SetLedColor(string type, int index, int r, int g, int b) => _setLedColor(type, index, r, g, b);

        public int Release() => _release?.Invoke() ?? 0;

        public void Unload()
        {
            var module = _module;
            _module = IntPtr.Zero;
            _initialize = null;
            _getDeviceInfo = null;
            _getLedStyle = null;
            _getLedColor = null;
            _setLedStyle = null;
            _setLedColor = null;
            _release = null;
            if (module != IntPtr.Zero)
            {
                try { NativeLibrary.Free(module); } catch { }
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitializeFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetDeviceInfoFn(
            [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)] out string[] types,
            [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)] out string[] ledCounts);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetLedStyleFn([MarshalAs(UnmanagedType.BStr)] string type, int index,
            [MarshalAs(UnmanagedType.BStr)] out string style);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetLedColorFn([MarshalAs(UnmanagedType.BStr)] string type, int index, out int r, out int g, out int b);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetLedStyleFn([MarshalAs(UnmanagedType.BStr)] string type, int index,
            [MarshalAs(UnmanagedType.BStr)] string style);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetLedColorFn([MarshalAs(UnmanagedType.BStr)] string type, int index, int r, int g, int b);
    }
}
