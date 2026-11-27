using System;
using System.Runtime.InteropServices;

namespace PadForge.Engine.Common.Logitech
{
    /// <summary>The calls an LCD applet makes on <see cref="LogitechLcdLibrary"/>,
    /// so its owner can be driven by a stand-in in the tests.</summary>
    public interface ILogitechLcd
    {
        bool Init(string friendlyName, int lcdTypes);
        bool IsConnected(int lcdType);
        bool IsButtonPressed(int button);
        void Update();
        void Shutdown();
        bool MonoSetBackground(byte[] monoBitmap);
        bool ColorSetBackground(byte[] colorBitmap);
    }

    /// <summary>
    /// Logitech Gaming Software's LCD library (issue #519), the surface
    /// <c>LogitechLCDLib.h</c> declares: an applet on the monochrome GamePanel
    /// screens and the G19's color screen, and on LGS's LCD Emulator.
    ///
    /// <para>Loaded by path from <see cref="LogitechServerCatalog"/>, the
    /// G-key source's way: every candidate tried until one loads, every
    /// export resolved or none, and the module never freed, since a call can
    /// still be on LGS's side when the session ends. The exports are the
    /// plain names Logitech's static library resolves and logitech-lcd-sys
    /// lists (<c>Library::load</c>). The header's functions return a C++
    /// <c>bool</c>, one byte, so every such return is marshaled as one.</para>
    ///
    /// <para>The library is not documented as thread-safe, so its owner
    /// makes every call from one thread.</para>
    /// </summary>
    public sealed class LogitechLcdLibrary : ILogitechLcd
    {
        public const int TypeMono = 0x00000001;
        public const int TypeColor = 0x00000002;
        public const int MonoWidth = 160, MonoHeight = 43;
        public const int ColorWidth = 320, ColorHeight = 240;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool PFN_LogiLcdInit([MarshalAs(UnmanagedType.LPWStr)] string friendlyName, int lcdType);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool PFN_LogiLcdIsConnected(int lcdType);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool PFN_LogiLcdIsButtonPressed(int button);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PFN_LogiLcdUpdate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PFN_LogiLcdShutdown();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool PFN_LogiLcdSetBackground(byte[] bitmap);

        private PFN_LogiLcdInit _init;
        private PFN_LogiLcdIsConnected _isConnected;
        private PFN_LogiLcdIsButtonPressed _isButtonPressed;
        private PFN_LogiLcdUpdate _update;
        private PFN_LogiLcdShutdown _shutdown;
        private PFN_LogiLcdSetBackground _monoSetBackground;
        private PFN_LogiLcdSetBackground _colorSetBackground;

        /// <summary>Where the library was loaded from.</summary>
        public string Path { get; private set; } = string.Empty;

        private LogitechLcdLibrary() { }

        /// <summary>The library, or null with <paramref name="reason"/>
        /// saying why: "gate" in the test host, "missing" with no
        /// registration, "load" when no registered path loaded, "exports"
        /// when a DLL loaded but is not this library.</summary>
        public static LogitechLcdLibrary TryLoad(out string reason)
        {
            reason = "missing";
            if (PadForge.Engine.Common.VendorProgramGate.Refuses)
            {
                reason = "gate";
                return null;
            }
            foreach (string candidate in LogitechServerCatalog.LcdCandidates())
            {
                if (reason == "missing") reason = "load";
                if (!NativeLibrary.TryLoad(candidate, out IntPtr module)) continue;
                var lib = new LogitechLcdLibrary { Path = candidate };
                if (lib.Resolve(module)) return lib;
                // Loaded, but not the library the header describes. The next
                // candidate may be. The module stays mapped, which costs
                // nothing and is safer than unmapping code that ran.
                reason = "exports";
            }
            return null;
        }

        private bool Resolve(IntPtr module)
            => Bind(module, "LogiLcdInit", out _init)
               && Bind(module, "LogiLcdIsConnected", out _isConnected)
               && Bind(module, "LogiLcdIsButtonPressed", out _isButtonPressed)
               && Bind(module, "LogiLcdUpdate", out _update)
               && Bind(module, "LogiLcdShutdown", out _shutdown)
               && Bind(module, "LogiLcdMonoSetBackground", out _monoSetBackground)
               && Bind(module, "LogiLcdColorSetBackground", out _colorSetBackground);

        private static bool Bind<T>(IntPtr module, string name, out T fn) where T : Delegate
        {
            fn = null;
            if (!NativeLibrary.TryGetExport(module, name, out IntPtr p) || p == IntPtr.Zero) return false;
            fn = Marshal.GetDelegateForFunctionPointer<T>(p);
            return true;
        }

        /// <summary>Registers the applet. False means LGS is not running.</summary>
        public bool Init(string friendlyName, int lcdTypes)
        {
            try { return _init(friendlyName, lcdTypes); }
            catch (Exception) { return false; }
        }

        public bool IsConnected(int lcdType)
        {
            try { return _isConnected(lcdType); }
            catch (Exception) { return false; }
        }

        /// <summary>True while a key is down and the applet is on the screen.
        /// LGS reports keys to the foreground applet alone.</summary>
        public bool IsButtonPressed(int button)
        {
            try { return _isButtonPressed(button); }
            catch (Exception) { return false; }
        }

        public void Update()
        {
            try { _update(); }
            catch (Exception) { }
        }

        public void Shutdown()
        {
            try { _shutdown(); }
            catch (Exception) { }
        }

        /// <summary>160 by 43 bytes, a pixel lit at 128 or more.</summary>
        public bool MonoSetBackground(byte[] monoBitmap)
        {
            if (monoBitmap == null || monoBitmap.Length != MonoWidth * MonoHeight) return false;
            try { return _monoSetBackground(monoBitmap); }
            catch (Exception) { return false; }
        }

        /// <summary>320 by 240 pixels of B, G, R, A.</summary>
        public bool ColorSetBackground(byte[] colorBitmap)
        {
            if (colorBitmap == null || colorBitmap.Length != ColorWidth * ColorHeight * 4) return false;
            try { return _colorSetBackground(colorBitmap); }
            catch (Exception) { return false; }
        }
    }
}
