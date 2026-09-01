using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PadForge.Services
{
    /// <summary>
    /// Production native layer for <see cref="LightsyncLightbarService"/>:
    /// replicates the LogitechLedEnginesWrapper.dll shim (#382). The shim's
    /// entire loader, proven by its PE imports and string table, is a
    /// registry read of the CLSID ServerBinary default value, a version
    /// check, LoadLibraryW, and GetProcAddress of the undecorated cdecl
    /// LogiLed names. This class does the same from managed code, so
    /// PadForge redistributes nothing of Logitech's.
    ///
    /// Marshaling facts from the references: every function is __cdecl
    /// (the official header's manglings carry YA in both bitnesses), and
    /// returns are 1-byte C++ bool, marshaled here as U1 to avoid the
    /// 4-byte BOOL width mismatch the C# reference wrappers silently
    /// carry. LogiLedInitWithName takes an ANSI char*, per the mangling
    /// (QEBD) and the Rust binding's CString call site. Optional exports
    /// (InitWithName, SetTargetDevice, Save, Restore) degrade per export:
    /// old engines carry only 13 exports (proven by PE parse), and a
    /// missing optional must not kill the feature.
    ///
    /// Single-caller contract: the service worker owns every call here,
    /// the serialization discipline all references keep.
    /// </summary>
    internal sealed class LogiLedEngineNative : LightsyncLightbarService.ILogiLedNative
    {
        // The key the wrapper shim reads (its only embedded wide string,
        // identical across both wrapper generations), and the same key
        // Aurora and Artemis read from HKLM's 64-bit view. Default value.
        internal const string ServerBinaryKey =
            @"SOFTWARE\Classes\CLSID\{a6519e67-7632-4375-afdf-caa889744403}\ServerBinary";

        // Aurora validates the registered engine by FileDescription
        // before trusting it (LgsInstallationUtils.cs:48-64).
        internal const string EngineFileDescription = "Logitech Gaming LED SDK";

        private const int LogiDeviceTypeAll = 7; // MONOCHROME 1 | RGB 2 | PERKEY_RGB 4

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        // The engine lives in G HUB's or LGS's own directory and may pull
        // dependencies from there; the altered search path makes the
        // engine's directory part of its dependency resolution.
        private const uint LoadWithAlteredSearchPath = 0x00000008;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte BoolFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte InitWithNameFn(IntPtr ansiName);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte SetTargetFn(int targetDevice);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte SetLightingFn(int redPct, int greenPct, int bluePct);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void VoidFn();

        private IntPtr _module;
        private BoolFn _init;
        private InitWithNameFn _initWithName;
        private SetTargetFn _setTarget;
        private BoolFn _save;
        private BoolFn _restore;
        private SetLightingFn _setLighting;
        private VoidFn _shutdown;

        public bool SoftwarePresent()
        {
            // The process names every reference gates on: G HUB's agent,
            // and both LGS-era hosts.
            foreach (string name in new[] { "lghub_agent", "lgs", "LCore" })
            {
                var procs = Process.GetProcessesByName(name);
                bool any = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (any) return true;
            }
            return false;
        }

        public bool TryLoad(out string detail)
        {
            if (_module != IntPtr.Zero) { detail = "already loaded"; return true; }

            string path = null;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(ServerBinaryKey);
                path = key?.GetValue(null)?.ToString();
            }
            catch { }
            if (string.IsNullOrEmpty(path)) { detail = "ServerBinary key absent"; return false; }
            if (!File.Exists(path)) { detail = $"engine missing: {path}"; return false; }

            try
            {
                string desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.Equals(desc, EngineFileDescription, StringComparison.Ordinal))
                {
                    detail = $"engine description '{desc}' unexpected";
                    return false;
                }
            }
            catch { detail = "engine version info unreadable"; return false; }

            IntPtr module = LoadLibraryExW(path, IntPtr.Zero, LoadWithAlteredSearchPath);
            if (module == IntPtr.Zero)
            {
                detail = $"LoadLibrary failed ({Marshal.GetLastWin32Error()}) for {path}";
                return false;
            }

            T Resolve<T>(string name) where T : class
            {
                IntPtr fn = GetProcAddress(module, name);
                return fn == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(fn) as T;
            }

            var init = Resolve<BoolFn>("LogiLedInit");
            var setLighting = Resolve<SetLightingFn>("LogiLedSetLighting");
            var shutdown = Resolve<VoidFn>("LogiLedShutdown");
            if (init == null || setLighting == null || shutdown == null)
            {
                FreeLibrary(module);
                detail = "required LogiLed exports missing";
                return false;
            }

            _module = module;
            _init = init;
            _setLighting = setLighting;
            _shutdown = shutdown;
            _initWithName = Resolve<InitWithNameFn>("LogiLedInitWithName");
            _setTarget = Resolve<SetTargetFn>("LogiLedSetTargetDevice");
            _save = Resolve<BoolFn>("LogiLedSaveCurrentLighting");
            _restore = Resolve<BoolFn>("LogiLedRestoreLighting");
            detail = $"loaded {path}";
            return true;
        }

        public bool Init()
        {
            try
            {
                if (_initWithName != null)
                {
                    IntPtr name = Marshal.StringToHGlobalAnsi("PadForge");
                    try { return _initWithName(name) != 0; }
                    finally { Marshal.FreeHGlobal(name); }
                }
                return _init != null && _init() != 0;
            }
            catch { return false; }
        }

        public bool SetTargetAll()
        {
            try { return _setTarget == null || _setTarget(LogiDeviceTypeAll) != 0; }
            catch { return false; }
        }

        public bool SaveCurrent()
        {
            try { return _save == null || _save() != 0; }
            catch { return false; }
        }

        public bool SetLighting(int rPct, int gPct, int bPct)
        {
            var fn = _setLighting;
            if (fn == null) return false;
            try { return fn(rPct, gPct, bPct) != 0; }
            catch { return false; }
        }

        public void RestoreAndShutdown()
        {
            // Restore FIRST, then shutdown, each swallowed: the RGB.NET
            // dispose shape, adopted because its author does not trust
            // Shutdown to restore and knows both calls can fail.
            try { _restore?.Invoke(); } catch { }
            try { _shutdown?.Invoke(); } catch { }
        }

        public void Unload()
        {
            // Null the delegates BEFORE freeing so a stray call lands on
            // a null check instead of a freed code page (the RGB.NET
            // UnloadLogitechGSDK discipline).
            _init = null;
            _initWithName = null;
            _setTarget = null;
            _save = null;
            _restore = null;
            _setLighting = null;
            _shutdown = null;
            if (_module != IntPtr.Zero)
            {
                try { FreeLibrary(_module); } catch { }
                _module = IntPtr.Zero;
            }
        }
    }
}
