using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace PadForge.Engine.Common.MadCatz
{
    /// <summary>What a S.T.R.I.K.E. 7 applet is told (issue #519).</summary>
    public enum Strike7EventKind
    {
        /// <summary>The user picked the applet on the V.E.N.O.M. screen. It
        /// may draw until the next <see cref="Inactive"/>.</summary>
        Active,
        /// <summary>The user pressed the home key. Draws are ignored.</summary>
        Inactive,
        /// <summary>A finger on the screen, at a point, down or lifted.</summary>
        Touch,
        /// <summary>The keyboard was unplugged.</summary>
        Reset,
    }

    /// <summary>One event, as the library's thread delivered it.</summary>
    public readonly record struct Strike7Event(Strike7EventKind Kind, ushort AppletId, ushort EndpointId, int X, int Y, bool Pressed);

    /// <summary>The applet library as the Strike 7 screen uses it: the
    /// library itself in the library host (<see cref="Strike7Library"/>),
    /// and PadForge's side of it over the host's channel (#597).</summary>
    public interface IStrike7Library
    {
        string Path { get; }
        ushort Open(string name, int iconWidth, int iconHeight, byte[] iconBgra, Action<Strike7Event> subscriber);
        void Close(ushort appletId);
        void DrawScreen(ushort appletId, ushort endpointId, byte[] bgra);
    }

    /// <summary>
    /// Mad Catz's S.T.R.I.K.E. 7 applet library, <c>Strike7API.dll</c>
    /// (issue #519), the C surface its SDK's <c>strike7api.h</c> declares and
    /// <c>STRIKE7 API v2_2.pdf</c> documents. PadForge never ships it. The
    /// owner points PadForge at a copy (the SDK's, kept under the temp
    /// folder, or one the user picks), and this loads that one path.
    ///
    /// <para>The order is the spec's (page 2) and its C example's
    /// (<c>Program.cpp</c>): the four events registered for every applet of
    /// this process (applet ID 0, pages 9 to 12), then <c>OpenApplet</c>.
    /// An applet draws only between its active and inactive events, and a
    /// draw shows once <c>AppletUpdateScreen</c> follows it (page 15). The
    /// callbacks run on the library's thread. Their delegates are made once
    /// per object and the object is never dropped, and the subscriber only
    /// queues. The module is never freed.</para>
    /// </summary>
    public sealed class Strike7Library : IStrike7Library
    {
        public const string LibraryName = "Strike7API.dll";

        /// <summary>The S.T.R.I.K.E. 7's VID and PID in one word, the device
        /// list's entry for it (spec page 13).</summary>
        public const uint DeviceId = 0x0738A109;

        /// <summary>The V.E.N.O.M. screen's view, the size both of Mad Catz's
        /// examples draw (<c>mapApplet.cpp</c>, <c>MapApplet.cs</c>).</summary>
        public const int ScreenWidth = 480, ScreenHeight = 320;

        // The header declares the callbacks __stdcall and the functions with
        // the compiler's default, which x64 folds into one convention.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ActiveEventFunc(ushort appletId, ushort endpointId, uint endpointType);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void InactiveEventFunc(ushort appletId, ushort endpointId);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void TouchEventFunc(ushort appletId, ushort endpointId, uint x, uint y, int pressed);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ResetEventFunc(ushort appletId, ushort endpointId);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PFN_RegisterEvent(IntPtr func, ushort appletId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private delegate ushort PFN_OpenApplet(string name, uint iconWidth, uint iconHeight, byte[] iconBgra, uint[] supportedDevices);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PFN_CloseApplet(ushort appletId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PFN_AppletDraw(ushort appletId, ushort endpointId, int x, int y, uint width, uint height, byte[] bgra);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PFN_AppletUpdateScreen(ushort appletId, ushort endpointId);

        private PFN_RegisterEvent _registerActive;
        private PFN_RegisterEvent _registerInactive;
        private PFN_RegisterEvent _registerTouch;
        private PFN_RegisterEvent _registerReset;
        private PFN_OpenApplet _openApplet;
        private PFN_CloseApplet _closeApplet;
        private PFN_AppletDraw _draw;
        private PFN_AppletUpdateScreen _updateScreen;

        private readonly ActiveEventFunc _onActive;
        private readonly InactiveEventFunc _onInactive;
        private readonly TouchEventFunc _onTouch;
        private readonly ResetEventFunc _onReset;
        private bool _registered;
        private volatile Action<Strike7Event> _subscriber;

        /// <summary>Where the library was loaded from.</summary>
        public string Path { get; private set; } = string.Empty;

        private Strike7Library()
        {
            _onActive = (applet, endpoint, _) => Raise(new Strike7Event(Strike7EventKind.Active, applet, endpoint, 0, 0, false));
            _onInactive = (applet, endpoint) => Raise(new Strike7Event(Strike7EventKind.Inactive, applet, endpoint, 0, 0, false));
            _onTouch = (applet, endpoint, x, y, pressed) => Raise(new Strike7Event(Strike7EventKind.Touch, applet, endpoint,
                (int)Math.Min(x, int.MaxValue), (int)Math.Min(y, int.MaxValue), pressed != 0));
            _onReset = (applet, endpoint) => Raise(new Strike7Event(Strike7EventKind.Reset, applet, endpoint, 0, 0, false));
        }

        private void Raise(Strike7Event e)
        {
            try { _subscriber?.Invoke(e); }
            catch (Exception) { }
        }

        /// <summary>The library at <paramref name="path"/>, or null with
        /// <paramref name="reason"/> saying why: "gate" in the test host,
        /// "platform" when this process cannot load an x64 library, "missing"
        /// with no file there, "runtime" when it would not load and the
        /// Visual C++ 2008 runtime it links is not installed, "load" when it
        /// would not load for another reason, "exports" when the file is not
        /// this library.</summary>
        public static Strike7Library TryLoad(string path, out string reason)
        {
            reason = "missing";
            if (PadForge.Engine.Common.VendorProgramGate.Refuses)
            {
                reason = "gate";
                return null;
            }
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                reason = "platform";
                return null;
            }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            if (!NativeLibrary.TryLoad(path, out IntPtr module))
            {
                reason = IsVc2008RuntimeInstalled() ? "load" : "runtime";
                return null;
            }
            var lib = new Strike7Library { Path = path };
            if (lib.Resolve(module)) return lib;
            reason = "exports";
            return null;
        }

        private bool Resolve(IntPtr module)
            => Bind(module, "RegisterAppletActiveEvent", out _registerActive)
               && Bind(module, "RegisterAppletInactiveEvent", out _registerInactive)
               && Bind(module, "RegisterAppletTouchEvent", out _registerTouch)
               && Bind(module, "RegisterAppletResetEvent", out _registerReset)
               && Bind(module, "OpenApplet", out _openApplet)
               && Bind(module, "CloseApplet", out _closeApplet)
               && Bind(module, "AppletDraw", out _draw)
               && Bind(module, "AppletUpdateScreen", out _updateScreen);

        private static bool Bind<T>(IntPtr module, string name, out T fn) where T : Delegate
        {
            fn = null;
            if (!NativeLibrary.TryGetExport(module, name, out IntPtr p) || p == IntPtr.Zero) return false;
            fn = Marshal.GetDelegateForFunctionPointer<T>(p);
            return true;
        }

        /// <summary>Registers the applet with its name and an icon of
        /// <paramref name="iconWidth"/> by <paramref name="iconHeight"/>
        /// pixels in the order B, G, R, A, rows from the top (spec page 13).
        /// Answers its ID, 0 when the library refused it.</summary>
        public ushort Open(string name, int iconWidth, int iconHeight, byte[] iconBgra, Action<Strike7Event> subscriber)
        {
            if (iconBgra == null || iconWidth <= 0 || iconHeight <= 0 || iconBgra.Length != iconWidth * iconHeight * 4) return 0;
            _subscriber = subscriber;
            try
            {
                if (!_registered)
                {
                    _registerActive(Marshal.GetFunctionPointerForDelegate(_onActive), 0);
                    _registerInactive(Marshal.GetFunctionPointerForDelegate(_onInactive), 0);
                    _registerTouch(Marshal.GetFunctionPointerForDelegate(_onTouch), 0);
                    _registerReset(Marshal.GetFunctionPointerForDelegate(_onReset), 0);
                    _registered = true;
                }
                // The list ends in 0, and names the one keyboard the applet
                // draws for.
                return _openApplet(name, (uint)iconWidth, (uint)iconHeight, iconBgra, new uint[] { DeviceId, 0 });
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>Takes the applet's icon off the V.E.N.O.M. screen.</summary>
        public void Close(ushort appletId)
        {
            _subscriber = null;
            if (appletId == 0) return;
            try { _closeApplet(appletId); }
            catch (Exception) { }
        }

        /// <summary>Draws a whole screen of B, G, R, A pixels, rows from the
        /// top (spec page 17), and shows it.</summary>
        public void DrawScreen(ushort appletId, ushort endpointId, byte[] bgra)
        {
            if (appletId == 0 || bgra == null || bgra.Length != ScreenWidth * ScreenHeight * 4) return;
            try
            {
                _draw(appletId, endpointId, 0, 0, ScreenWidth, ScreenHeight, bgra);
                _updateScreen(appletId, endpointId);
            }
            catch (Exception) { }
        }

        private static long s_runtimeFound;

        /// <summary>How many checks have found the runtime, so a screen
        /// waiting for it learns of an install the moment the installer's
        /// own check finds it.</summary>
        public static long RuntimeFoundCount => System.Threading.Interlocked.Read(ref s_runtimeFound);

        /// <summary>Whether Microsoft's Visual C++ 2008 runtime for x64 is in
        /// the side-by-side store, where the library's manifest binds
        /// Microsoft.VC90.CRT. A read of the Windows folder alone.</summary>
        public static bool IsVc2008RuntimeInstalled()
        {
            try
            {
                string store = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS");
                bool found = Directory.EnumerateDirectories(store, "amd64_microsoft.vc90.crt_1fc8b3b9a1e18e3b_*").Any();
                if (found) System.Threading.Interlocked.Increment(ref s_runtimeFound);
                return found;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
