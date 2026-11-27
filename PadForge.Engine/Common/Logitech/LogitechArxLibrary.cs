using System;
using System.Runtime.InteropServices;

namespace PadForge.Engine.Common.Logitech
{
    /// <summary>The calls an Arx applet makes on <see cref="LogitechArxLibrary"/>,
    /// so its owner can be driven by a stand-in in the tests.</summary>
    public interface ILogitechArx
    {
        bool Init(string identifier, string friendlyName, Action<uint, uint> onEvent);
        bool AddContent(byte[] content, string fileName, string mimeType);
        bool AddImage(byte[] bgra, int width, int height, string fileName);
        bool SetIndex(string fileName);
        bool SetTagProperty(string tagId, string property, string value);
        int LastError { get; }
        void Shutdown();
    }

    /// <summary>
    /// Logitech Gaming Software's Arx Control library (issue #519), the
    /// surface <c>LogitechGArxControlLib.h</c> declares: one applet, a set of
    /// files sent to the Arx Control app on a phone, and an index page the
    /// app shows.
    ///
    /// <para>Loaded the way <see cref="LogitechLcdLibrary"/> is, from the
    /// paths the class's registration names. Logitech's static library and
    /// its engines wrapper read only that registration, and so does this.
    /// The callback runs on LGS's thread. It is rooted, with the context
    /// structure it is handed in, for as long as the session lasts, the
    /// G-key source's rule, and its subscriber only queues.</para>
    /// </summary>
    public sealed class LogitechArxLibrary : ILogitechArx
    {
        /// <summary><see cref="LastError"/>'s codes for a session that is
        /// gone: "SDK not initialized" and "Connection with Logitech Gaming
        /// Software broken", after which LGS takes no further call (the SDK
        /// guide, page 15).</summary>
        public const int ErrorNotInitialized = 4;
        public const int ErrorConnectionBroken = 6;

        public const uint EventFocusActive = 0x01;
        public const uint EventFocusInactive = 0x02;
        public const uint EventTapOnTag = 0x04;
        public const uint EventDeviceArrival = 0x08;
        public const uint EventDeviceRemoval = 0x10;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ArxCallback(uint eventType, uint eventValue, IntPtr eventArg, IntPtr context);

        // logiArxCbContext, declared inside the header's #pragma pack(push, 1).
        // Two pointers, so packing moves nothing on x64.
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct ArxCallbackContext
        {
            public IntPtr Callback;
            public IntPtr Context;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool PFN_LogiArxInit(string identifier, string friendlyName, IntPtr callbackContext);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool PFN_LogiArxAddContentAs(byte[] content, int size, string fileName, string mimeType);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool PFN_LogiArxAddImageFromBitmap(byte[] bitmap, int width, int height, string fileName);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool PFN_LogiArxSetIndex(string fileName);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool PFN_LogiArxSetTagPropertyById(string tagId, string prop, string newValue);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PFN_LogiArxGetLastError();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PFN_LogiArxShutdown();

        private PFN_LogiArxInit _init;
        private PFN_LogiArxAddContentAs _addContentAs;
        private PFN_LogiArxAddImageFromBitmap _addImageFromBitmap;
        private PFN_LogiArxSetIndex _setIndex;
        private PFN_LogiArxSetTagPropertyById _setTagPropertyById;
        private PFN_LogiArxGetLastError _getLastError;
        private PFN_LogiArxShutdown _shutdown;

        // The library keeps the context's address for the session. The
        // structure is pinned for the session, and the delegate is made once
        // and lives as long as this object, which its owner never drops, so
        // no session's thunk is ever collected under LGS's thread.
        private readonly ArxCallback _callback;
        private GCHandle _contextPin;
        private volatile Action<uint, uint> _onEvent;

        /// <summary>Where the library was loaded from.</summary>
        public string Path { get; private set; } = string.Empty;

        private LogitechArxLibrary() => _callback = OnEvent;

        /// <summary>The library, or null with <paramref name="reason"/>
        /// saying why: "gate" in the test host, "missing" with no
        /// registration, "load" when none of its paths loaded, "exports" when
        /// a DLL loaded but is not this library.</summary>
        public static LogitechArxLibrary TryLoad(out string reason)
        {
            reason = "missing";
            if (PadForge.Engine.Common.VendorProgramGate.Refuses)
            {
                reason = "gate";
                return null;
            }
            foreach (string candidate in LogitechServerCatalog.ArxCandidates())
            {
                reason = "load";
                if (!NativeLibrary.TryLoad(candidate, out IntPtr module)) continue;
                var lib = new LogitechArxLibrary { Path = candidate };
                if (lib.Resolve(module)) return lib;
                reason = "exports";
            }
            return null;
        }

        private bool Resolve(IntPtr module)
            => Bind(module, "LogiArxInit", out _init)
               && Bind(module, "LogiArxAddContentAs", out _addContentAs)
               && Bind(module, "LogiArxAddImageFromBitmap", out _addImageFromBitmap)
               && Bind(module, "LogiArxSetIndex", out _setIndex)
               && Bind(module, "LogiArxSetTagPropertyById", out _setTagPropertyById)
               && Bind(module, "LogiArxGetLastError", out _getLastError)
               && Bind(module, "LogiArxShutdown", out _shutdown);

        private static bool Bind<T>(IntPtr module, string name, out T fn) where T : Delegate
        {
            fn = null;
            if (!NativeLibrary.TryGetExport(module, name, out IntPtr p) || p == IntPtr.Zero) return false;
            fn = Marshal.GetDelegateForFunctionPointer<T>(p);
            return true;
        }

        /// <summary>Registers the applet. <paramref name="onEvent"/> gets
        /// each event's type and value on LGS's thread. False means LGS is
        /// not running or refused the applet.</summary>
        public bool Init(string identifier, string friendlyName, Action<uint, uint> onEvent)
        {
            if (_contextPin.IsAllocated) return false;
            _onEvent = onEvent ?? ((_, _) => { });
            var context = new ArxCallbackContext
            {
                Callback = Marshal.GetFunctionPointerForDelegate(_callback),
                Context = IntPtr.Zero,
            };
            _contextPin = GCHandle.Alloc(context, GCHandleType.Pinned);
            bool ok;
            try { ok = _init(identifier, friendlyName, _contextPin.AddrOfPinnedObject()); }
            catch (Exception) { ok = false; }
            if (!ok) Shutdown();
            return ok;
        }

        private void OnEvent(uint eventType, uint eventValue, IntPtr eventArg, IntPtr context)
        {
            try { _onEvent?.Invoke(eventType, eventValue); }
            catch (Exception) { }
        }

        /// <summary>Sends bytes as a file the applet's pages can name.</summary>
        public bool AddContent(byte[] content, string fileName, string mimeType)
        {
            if (content == null) return false;
            try { return _addContentAs(content, content.Length, fileName, mimeType ?? string.Empty); }
            catch (Exception) { return false; }
        }

        /// <summary>Sends a picture of width by height pixels, four bytes
        /// each in the order B, G, R, A (the SDK guide, page 12). LGS
        /// compresses it to PNG.</summary>
        public bool AddImage(byte[] bgra, int width, int height, string fileName)
        {
            if (bgra == null || width <= 0 || height <= 0 || bgra.Length != width * height * 4) return false;
            try { return _addImageFromBitmap(bgra, width, height, fileName); }
            catch (Exception) { return false; }
        }

        /// <summary>The page the app shows. The first call on a sent file
        /// brings the applet to the front (the SDK guide, page 13).</summary>
        public bool SetIndex(string fileName)
        {
            try { return _setIndex(fileName); }
            catch (Exception) { return false; }
        }

        /// <summary>Changes one property of the tag with that id on the
        /// applet's pages.</summary>
        public bool SetTagProperty(string tagId, string property, string value)
        {
            try { return _setTagPropertyById(tagId, property, value); }
            catch (Exception) { return false; }
        }

        /// <summary>The last call's error code, 0 when it succeeded.</summary>
        public int LastError
        {
            get
            {
                try { return _getLastError(); }
                catch (Exception) { return -1; }
            }
        }

        /// <summary>Ends the applet. The module stays mapped, since LGS's
        /// thread can still be inside the callback.</summary>
        public void Shutdown()
        {
            _onEvent = null;
            try { _shutdown(); } catch (Exception) { }
            if (_contextPin.IsAllocated) _contextPin.Free();
        }
    }
}
