using System;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input
{
    /// <summary>
    /// In-process hook that makes specific XInput slots invisible to SDL3's
    /// XInput backend by patching SDL3.dll's stored XInput function pointers
    /// AFTER SDL_Init. SDL calls XInputGetState/GetCapabilities through
    /// global function pointers (SDL_XInputGetState, etc.) that are resolved
    /// once via GetProcAddress during SDL_Init. We scan SDL3.dll's loaded
    /// image for these pointer values and overwrite them with our hook
    /// addresses. Our hooks check a volatile slot mask and return
    /// ERROR_DEVICE_NOT_CONNECTED for masked slots.
    ///
    /// This approach avoids function prologue patching entirely — no
    /// instruction boundary issues, no RIP-relative fixups, no trampolines.
    /// The real function addresses are saved and called directly from the
    /// hook when forwarding unmasked slots.
    ///
    /// Games and other processes are unaffected — we only modify SDL3.dll's
    /// data section in THIS process's memory.
    /// </summary>
    internal static class XInputHook
    {
        private const int ERROR_DEVICE_NOT_CONNECTED = 0x048F;

        private static volatile int _ignoreSlotMask;
        private static bool _installed;

        // Real XInput function pointers (saved before overwrite).
        private static IntPtr _realGetState;
        private static IntPtr _realGetCaps;

        // Locations in SDL3.dll's image where the pointers live.
        private static IntPtr _patchLocationGetState;
        private static IntPtr _patchLocationGetCaps;

        // Pinned delegates (prevent GC).
        private static GCHandle _hookedGetStateHandle;
        private static GCHandle _hookedGetCapsHandle;

        // Cached delegates for calling the real functions from the hook.
        private static XInputGetStateDelegate _realGetStateDel;
        private static XInputGetCapsDelegate _realGetCapsDel;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int XInputGetStateDelegate(int dwUserIndex, out XINPUT_STATE pState);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int XInputGetCapsDelegate(int dwUserIndex, int dwFlags, out XINPUT_CAPABILITIES pCaps);

        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger, bRightTrigger;
            public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_CAPABILITIES
        {
            public byte Type, SubType;
            public ushort Flags;
            public XINPUT_GAMEPAD Gamepad;
            public ushort wLeftMotorSpeed, wRightMotorSpeed;
        }

        // ─────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────

        /// <summary>Set the bitmask of XInput slots to hide (bit N = hide slot N).</summary>
        public static void SetIgnoreSlotMask(int mask) => _ignoreSlotMask = mask & 0xF;

        /// <summary>Current ignore mask.</summary>
        public static int IgnoreSlotMask => _ignoreSlotMask;

        /// <summary>Whether the hook is installed.</summary>
        public static bool IsInstalled => _installed;

        /// <summary>
        /// Call the REAL XInputGetState, bypassing the hook.
        /// Used by PadForge's own slot detection after creating a virtual.
        /// </summary>
        public static int GetStateOriginal(int slot, out XINPUT_STATE state)
        {
            if (_realGetStateDel != null)
                return _realGetStateDel(slot, out state);
            state = default;
            return ERROR_DEVICE_NOT_CONNECTED;
        }

        /// <summary>
        /// Install hooks by patching SDL3.dll's global XInput function
        /// pointers. MUST be called AFTER SDL_Init (which populates the
        /// pointers via GetProcAddress) and before SDL starts polling.
        /// </summary>
        public static bool Install()
        {
            if (_installed) return true;

            try
            {
                // Get the real XInput function addresses that SDL stored.
                IntPtr xinputModule = LoadLibraryW("xinput1_4.dll");
                if (xinputModule == IntPtr.Zero) { Log("LoadLibrary failed"); return false; }

                // SDL3 prefers ordinal 100 (XInputGetStateEx).
                _realGetState = GetProcAddressByOrdinal(xinputModule, (IntPtr)100);
                if (_realGetState == IntPtr.Zero)
                    _realGetState = GetProcAddressByName(xinputModule, "XInputGetState");
                _realGetCaps = GetProcAddressByName(xinputModule, "XInputGetCapabilities");

                if (_realGetState == IntPtr.Zero || _realGetCaps == IntPtr.Zero)
                { Log("GetProcAddress failed"); return false; }

                Log($"Real GetState=0x{_realGetState:X} GetCaps=0x{_realGetCaps:X}");

                // Cache delegates for calling the real functions from hooks.
                _realGetStateDel = Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(_realGetState);
                _realGetCapsDel = Marshal.GetDelegateForFunctionPointer<XInputGetCapsDelegate>(_realGetCaps);

                // Create and pin hook delegates.
                XInputGetStateDelegate hookedGetState = HookedGetState;
                XInputGetCapsDelegate hookedGetCaps = HookedGetCaps;
                _hookedGetStateHandle = GCHandle.Alloc(hookedGetState);
                _hookedGetCapsHandle = GCHandle.Alloc(hookedGetCaps);
                IntPtr hookGetStatePtr = Marshal.GetFunctionPointerForDelegate(hookedGetState);
                IntPtr hookGetCapsPtr = Marshal.GetFunctionPointerForDelegate(hookedGetCaps);

                // Scan SDL3.dll's loaded image for the stored pointers and overwrite.
                IntPtr sdlModule = GetModuleHandleW("SDL3.dll");
                if (sdlModule == IntPtr.Zero) { Log("SDL3.dll not loaded"); Cleanup(); return false; }

                Log($"SDL3.dll=0x{sdlModule:X} hookGetState=0x{hookGetStatePtr:X} hookGetCaps=0x{hookGetCapsPtr:X}");

                _patchLocationGetState = FindPointerInModule(sdlModule, _realGetState);
                _patchLocationGetCaps = FindPointerInModule(sdlModule, _realGetCaps);

                if (_patchLocationGetState == IntPtr.Zero)
                { Log("Could not find GetState pointer in SDL3.dll"); Cleanup(); return false; }
                if (_patchLocationGetCaps == IntPtr.Zero)
                { Log("Could not find GetCaps pointer in SDL3.dll"); Cleanup(); return false; }

                Log($"PatchLocation GetState=0x{_patchLocationGetState:X} GetCaps=0x{_patchLocationGetCaps:X}");

                // Overwrite the pointers. .data section is typically writable,
                // but VirtualProtect just in case.
                WritePointer(_patchLocationGetState, hookGetStatePtr);
                WritePointer(_patchLocationGetCaps, hookGetCapsPtr);

                _installed = true;
                Log("Install complete");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Install EXCEPTION: {ex}");
                try { Cleanup(); } catch { }
                return false;
            }
        }

        /// <summary>Remove hooks, restore original pointers.</summary>
        public static void Uninstall()
        {
            if (_patchLocationGetState != IntPtr.Zero && _realGetState != IntPtr.Zero)
                WritePointer(_patchLocationGetState, _realGetState);
            if (_patchLocationGetCaps != IntPtr.Zero && _realGetCaps != IntPtr.Zero)
                WritePointer(_patchLocationGetCaps, _realGetCaps);

            Cleanup();
            _installed = false;
        }

        // ─────────────────────────────────────────────
        //  Hook implementations
        // ─────────────────────────────────────────────

        private static int HookedGetState(int dwUserIndex, out XINPUT_STATE pState)
        {
            if (dwUserIndex >= 0 && dwUserIndex < 4
                && (_ignoreSlotMask & (1 << dwUserIndex)) != 0)
            {
                pState = default;
                return ERROR_DEVICE_NOT_CONNECTED;
            }
            return _realGetStateDel(dwUserIndex, out pState);
        }

        private static int HookedGetCaps(int dwUserIndex, int dwFlags, out XINPUT_CAPABILITIES pCaps)
        {
            if (dwUserIndex >= 0 && dwUserIndex < 4
                && (_ignoreSlotMask & (1 << dwUserIndex)) != 0)
            {
                pCaps = default;
                return ERROR_DEVICE_NOT_CONNECTED;
            }
            return _realGetCapsDel(dwUserIndex, dwFlags, out pCaps);
        }

        // ─────────────────────────────────────────────
        //  Pointer scanning + patching
        // ─────────────────────────────────────────────

        /// <summary>
        /// Scan a loaded module's image for an 8-byte pointer value.
        /// Returns the address of the first match, or IntPtr.Zero.
        /// </summary>
        private static IntPtr FindPointerInModule(IntPtr moduleBase, IntPtr targetValue)
        {
            if (!GetModuleInformation(GetCurrentProcess(), moduleBase, out MODULEINFO info, (uint)Marshal.SizeOf<MODULEINFO>()))
                return IntPtr.Zero;

            long baseAddr = (long)moduleBase;
            long endAddr = baseAddr + (long)info.SizeOfImage - 8;
            long target = (long)targetValue;

            for (long addr = baseAddr; addr < endAddr; addr += 8)
            {
                if (Marshal.ReadInt64((IntPtr)addr) == target)
                    return (IntPtr)addr;
            }
            return IntPtr.Zero;
        }

        private static void WritePointer(IntPtr location, IntPtr value)
        {
            VirtualProtect(location, 8, PAGE_READWRITE, out uint oldProtect);
            Marshal.WriteIntPtr(location, value);
            VirtualProtect(location, 8, oldProtect, out _);
        }

        private static void Cleanup()
        {
            if (_hookedGetStateHandle.IsAllocated) _hookedGetStateHandle.Free();
            if (_hookedGetCapsHandle.IsAllocated) _hookedGetCapsHandle.Free();
            _realGetStateDel = null;
            _realGetCapsDel = null;
            _patchLocationGetState = IntPtr.Zero;
            _patchLocationGetCaps = IntPtr.Zero;
        }

        internal static void Log(string msg)
        {
            try { System.IO.File.AppendAllText(@"C:\PadForge\xinput-hook.log",
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
        }

        // ─────────────────────────────────────────────
        //  P/Invoke
        // ─────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct MODULEINFO
        {
            public IntPtr lpBaseOfDll;
            public uint SizeOfImage;
            public IntPtr EntryPoint;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpLibFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, EntryPoint = "GetProcAddress")]
        private static extern IntPtr GetProcAddressByName(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetProcAddress")]
        private static extern IntPtr GetProcAddressByOrdinal(IntPtr hModule, IntPtr lpProcName);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, uint cb);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtect(IntPtr lpAddress, uint dwSize, uint flNewProtect, out uint lpflOldProtect);

        private const uint PAGE_READWRITE = 0x04;
    }
}
