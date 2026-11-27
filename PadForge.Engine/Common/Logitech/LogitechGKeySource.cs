using System;
using System.Runtime.InteropServices;
using System.Threading;
using static PadForge.Engine.Common.Logitech.LogitechGKeyInterop;

namespace PadForge.Engine.Common.Logitech
{
    /// <summary>What the source is doing, for the status line.</summary>
    public enum LogitechGKeyState
    {
        Stopped = 0,
        NoSdk,          // no Logitech Gaming Software on this machine
        SdkPathStale,   // the class is registered, the file is gone
        LoadFailed,     // the file is there and would not load
        MissingExports, // loaded, but not the library we expect
        InitRefused,    // the Logitech software is not running
        Running,
    }

    /// <summary>
    /// The G-key SDK client (issue #454).
    ///
    /// <para>Callback rather than polling, which is Logitech's own guidance:
    /// "When polling if a user clicks and releases a button within a single
    /// game loop the button press will not be seen." A poll loop drops taps
    /// and the callback does not.</para>
    ///
    /// <para>The callback runs on the SDK's thread, which its header states
    /// outright, so this hands the event straight to a subscriber and does no
    /// work of its own there.</para>
    /// </summary>
    public sealed class LogitechGKeySource : IDisposable
    {
        private readonly Action<string> _log;
        private readonly Action<GkeyCode> _onEvent;

        private IntPtr _module;
        private bool _initialized;
        private bool _disposed;

        // The SDK keeps this pointer for the life of the session, so the
        // delegate has to stay rooted for exactly as long. A local would be
        // collected while native code still held its address, and the crash
        // would land on a Logitech thread with our name on it.
        private LogiGkeyCB _callback;
        private GCHandle _contextPin;

        private PFN_LogiGkeyInit _init;
        private PFN_LogiGkeyShutdown _shutdown;
        private PFN_LogiGkeyIsMouseButtonPressed _isMousePressed;
        private PFN_LogiGkeyIsKeyboardGkeyPressed _isKeyPressed;
        private PFN_LogiGkeyGetMouseButtonString _mouseName;
        private PFN_LogiGkeyGetKeyboardGkeyString _keyName;

        private long _events;

        public LogitechGKeyState State { get; private set; } = LogitechGKeyState.Stopped;

        /// <summary>Where the library was loaded from, for the details pane.</summary>
        public string LibraryPath { get; private set; } = string.Empty;

        public long EventCount => Interlocked.Read(ref _events);

        public LogitechGKeySource(Action<GkeyCode> onEvent, Action<string> log = null)
        {
            _onEvent = onEvent ?? (_ => { });
            _log = log ?? (_ => { });
        }

        /// <summary>Loads the library and starts the session. False leaves
        /// <see cref="State"/> saying why.</summary>
        public bool Start()
        {
            if (_disposed || _initialized) return _initialized;
            if (PadForge.Engine.Common.VendorProgramGate.Refuses)
            {
                State = LogitechGKeyState.NoSdk;
                _log("G-Keys: the test host loads no G-key library");
                return false;
            }

            // Every candidate, not just the first that exists. A registry
            // entry can name the x86 build on an x64 host, which exists and
            // will not load, and the correct one is further down the list.
            // Mumble's GKey.cpp walks its candidates the same way.
            var candidates = LogitechGKeyCatalog.Candidates(out var reason);
            if (candidates.Count == 0)
            {
                State = reason == LogitechGKeyCatalog.Reason.RegistryPathMissing
                    ? LogitechGKeyState.SdkPathStale
                    : LogitechGKeyState.NoSdk;
                _log("G-Keys: no G-key SDK found (" + reason + ")");
                return false;
            }

            string path = null;
            foreach (string candidate in candidates)
            {
                if (!NativeLibrary.TryLoad(candidate, out _module)) continue;
                path = candidate;
                break;
            }
            if (path == null)
            {
                State = LogitechGKeyState.LoadFailed;
                _log("G-Keys: " + candidates.Count + " candidate path(s) and none loaded");
                return false;
            }
            LibraryPath = path;

            // Every export or none. A partial resolve means this is not the
            // library the header describes, and calling into it anyway is how
            // a wrong-DLL match turns into a crash instead of a message.
            if (!Resolve(out _init, "LogiGkeyInit")
                || !Resolve(out _shutdown, "LogiGkeyShutdown")
                || !Resolve(out _isMousePressed, "LogiGkeyIsMouseButtonPressed")
                || !Resolve(out _isKeyPressed, "LogiGkeyIsKeyboardGkeyPressed")
                || !Resolve(out _mouseName, "LogiGkeyGetMouseButtonString")
                || !Resolve(out _keyName, "LogiGkeyGetKeyboardGkeyString"))
            {
                State = LogitechGKeyState.MissingExports;
                _log("G-Keys: " + path + " is missing an expected export");
                Teardown();
                return false;
            }

            _callback = OnGkeyEvent;
            var context = new LogiGkeyCBContext
            {
                GkeyCallBack = Marshal.GetFunctionPointerForDelegate(_callback),
                GkeyContext = IntPtr.Zero,
            };
            // Pinned rather than stack-allocated: init reads the struct, and a
            // moved or collected buffer under a native read is not worth the
            // few bytes saved.
            _contextPin = GCHandle.Alloc(context, GCHandleType.Pinned);

            bool ok;
            try { ok = _init(_contextPin.AddrOfPinnedObject()); }
            catch (Exception ex)
            {
                State = LogitechGKeyState.InitRefused;
                _log("G-Keys: init threw: " + ex.Message);
                Teardown();
                return false;
            }

            if (!ok)
            {
                // The usual cause is that the Logitech software is installed
                // but not running, which is the one the status line names.
                State = LogitechGKeyState.InitRefused;
                _log("G-Keys: the SDK refused to start, which usually means the Logitech software is not running");
                Teardown();
                return false;
            }

            _initialized = true;
            State = LogitechGKeyState.Running;
            _log("G-Keys: running from " + path);
            return true;
        }

        private bool Resolve<T>(out T fn, string name) where T : Delegate
        {
            fn = null;
            if (!NativeLibrary.TryGetExport(_module, name, out var p) || p == IntPtr.Zero) return false;
            fn = Marshal.GetDelegateForFunctionPointer<T>(p);
            return true;
        }

        private void OnGkeyEvent(GkeyCode code, string name, IntPtr context)
        {
            if (_disposed) return;
            Interlocked.Increment(ref _events);
            // Straight through. This is the SDK's thread, and anything slow
            // here stalls its dispatch for every other listener on the machine.
            try { _onEvent(code); }
            catch (Exception) { }
        }

        /// <summary>The SDK's own name for a G-key, or empty.</summary>
        public string KeyName(int key, int mode)
        {
            if (!_initialized || _keyName == null) return string.Empty;
            try { return ReadName(_keyName(key, mode)); }
            catch (Exception) { return string.Empty; }
        }

        /// <summary>The SDK's own name for a mouse button, or empty.</summary>
        public string MouseName(int button)
        {
            if (!_initialized || _mouseName == null) return string.Empty;
            try { return ReadName(_mouseName(button)); }
            catch (Exception) { return string.Empty; }
        }

        /// <summary>A key's state, asked directly. The row runs on the
        /// callback, so this exists for a state resync rather than a poll
        /// loop.</summary>
        public bool IsKeyPressed(int key, int mode)
        {
            if (!_initialized || _isKeyPressed == null) return false;
            try { return _isKeyPressed(key, mode); }
            catch (Exception) { return false; }
        }

        /// <summary>A mouse button's state, asked directly.</summary>
        public bool IsMouseButtonPressed(int button)
        {
            if (!_initialized || _isMousePressed == null) return false;
            try { return _isMousePressed(button); }
            catch (Exception) { return false; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Teardown();
            State = LogitechGKeyState.Stopped;
        }

        /// <summary>
        /// Stops the SDK and drops our side of the wiring.
        ///
        /// <para>Shutdown runs whenever the export resolved, not only when
        /// init reported success. Init can register the callback and start
        /// the SDK's plumbing before returning false, and Mumble's GKey.cpp
        /// calls shutdown unconditionally for the same reason.</para>
        ///
        /// <para>The module is deliberately never freed. A callback can be
        /// running on the SDK's own thread while this executes, and unmapping
        /// the code it stands on is not recoverable by any managed handler.
        /// Mumble does not unload on success either. One mapped library for
        /// the life of the process costs nothing.</para>
        /// </summary>
        private void Teardown()
        {
            try { _shutdown?.Invoke(); } catch (Exception) { }
            _initialized = false;
            if (_contextPin.IsAllocated) _contextPin.Free();
            _callback = null;
            _init = null;
            _shutdown = null;
            _isMousePressed = null;
            _isKeyPressed = null;
            _mouseName = null;
            _keyName = null;
            _module = IntPtr.Zero;
        }
    }
}
