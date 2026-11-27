using System;
using System.Threading;
using PadForge.Engine.Common.ForceDimension;

namespace PadForge.Common.Input.Falcon
{
    /// <summary>What the last sweep found (issue #521), for the Settings
    /// card's status lines. Immutable, published whole. NodeDrivers holds
    /// the driver of each Falcon node that is not on Force Dimension's own
    /// package, and OwnDriverNodes counts those that are. OpenError is the
    /// library's reason for the last open that failed for a reason other
    /// than another program holding the Falcon. PickNotLoaded is set when
    /// the dhd64.dll the user picked did not load and another copy
    /// runs.</summary>
    internal sealed record FalconSweepStatus(
        string LibraryReason,
        string LibraryPath,
        string SdkVersion,
        string SdkRoot,
        int InUse,
        string[] NodeDrivers,
        string DriverPackage,
        bool PickNotLoaded = false,
        int OwnDriverNodes = 0,
        string OpenError = null)
    {
        public static readonly FalconSweepStatus None = new("missing", string.Empty, string.Empty, null, 0, Array.Empty<string>(), null);

        /// <summary>A Falcon's USB node with no driver, which the Install
        /// button can give Force Dimension's.</summary>
        public bool HasDriverlessNode => Array.IndexOf(NodeDrivers, string.Empty) >= 0;

        /// <summary>The drivers on the Falcon's USB nodes other than none.</summary>
        public string OtherDrivers => string.Join(", ", Array.FindAll(NodeDrivers, d => d.Length > 0));
    }

    /// <summary>A loaded Force Dimension library: its calls, the file it
    /// came from and the SDK version it gives.</summary>
    internal sealed record FalconLibrary(IFalconLink Link, string Path, string Version);

    /// <summary>
    /// The Settings switch, the picked library and the loaded library for
    /// the Novint Falcon (issue #521), shared by the Settings card and the
    /// engine's sweep, the <see cref="KeyboardScreensRuntime"/> shape. The
    /// library is loaded on the sweep's worker, and a failed load is tried
    /// again on the next sweep after 30 seconds. A copy the user picks
    /// after one loaded takes its place once the Falcons on the old copy
    /// have closed (<see cref="StagePick"/>). No module is ever freed.
    /// </summary>
    internal static class FalconRuntime
    {
        private const int LoadRetryMs = 30000;

        private static volatile bool _enabled;
        private static int _version;
        private static volatile string _picked;
        private static readonly object _libraryLock = new();
        private static FalconLibrary _library;
        private static string _reason = "missing";
        private static long _nextLoad;
        private static volatile FalconSweepStatus _status = FalconSweepStatus.None;
        private static volatile Func<string, FalconLibrary> _loaderForTest;

        /// <summary>True while a test's scripted loader stands in for Force
        /// Dimension's library. Never in the app.</summary>
        internal static bool ScriptedForTest => _loaderForTest != null;

        /// <summary>Test seam: <paramref name="loader"/> loads a scripted
        /// library for a picked path (null for the installed copies), or
        /// returns null for a load that fails, in place of Force Dimension's
        /// library. Forgets any library loaded before. Null restores the
        /// app's loader.</summary>
        internal static void UseLoaderForTest(Func<string, FalconLibrary> loader)
        {
            lock (_libraryLock)
            {
                _loaderForTest = loader;
                _library = _staged = null;
                _pickPending = false;
                _reason = "missing";
                _nextLoad = 0;
                _status = FalconSweepStatus.None;
            }
        }

        // Caller holds _libraryLock.
        private static FalconLibrary Load(string picked, out string reason)
        {
            var loader = _loaderForTest;
            if (loader != null)
            {
                var scripted = loader(picked);
                reason = scripted == null ? "load" : string.Empty;
                return scripted;
            }
            var lib = ForceDimensionLibrary.TryLoad(picked, out reason);
            if (lib == null) return null;
            reason = string.Empty;
            return new FalconLibrary(lib, lib.Path, lib.SdkVersion);
        }

        /// <summary>Drive Novint Falcon, mirrored from Settings.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                Interlocked.Increment(ref _version);
            }
        }

        public static int Version => Volatile.Read(ref _version);

        /// <summary>A dhd64.dll the user picked, tried before the installed
        /// copies.</summary>
        public static string PickedLibrary
        {
            get => _picked;
            set
            {
                if (string.Equals(_picked, value, StringComparison.OrdinalIgnoreCase)) return;
                _picked = value;
                lock (_libraryLock)
                {
                    // A new pick is tried at the next sweep. With a library
                    // already loaded, the sweep takes it up in StagePick.
                    if (_library == null) _nextLoad = 0;
                    else _pickPending = true;
                }
            }
        }

        private static bool _pickPending;
        private static FalconLibrary _staged;

        /// <summary>
        /// The sweep's first step after the user picked another dhd64.dll
        /// while one was loaded: loads the picked copy beside the one in use.
        /// True while a copy waits to take over, so the sweep closes every
        /// Falcon on the old one and then calls <see cref="SwapToStaged"/>.
        /// False when the pick is the copy in use, and when the picked copy
        /// does not load, which keeps the one in use and its Falcons and
        /// shows on the Settings card (<see cref="FalconSweepStatus.PickNotLoaded"/>).
        /// The old module stays mapped, the never-free rule, since a haptic
        /// thread stuck in a call returns into it.
        /// </summary>
        public static bool StagePick()
        {
            lock (_libraryLock)
            {
                if (!_pickPending) return _staged != null;
                _pickPending = false;
                _staged = null;
                if (_library == null) return false;
                string picked = _picked;
                var next = Load(picked, out string reason);
                if (next == null || (picked != null && !SamePath(next.Path, picked)))
                {
                    string why = next == null ? reason : "another copy loaded in its place";
                    PadForge.Engine.SdlDiagLog.WriteLine($"Falcon: {picked ?? "the installed library"} did not load ({why}), so {_library.Path} stays.");
                    return false;
                }
                if (SamePath(next.Path, _library.Path)) return false;
                _staged = next;
                return true;
            }
        }

        /// <summary>The staged copy takes over. The sweep calls this once no
        /// Falcon is open on the old one.</summary>
        public static void SwapToStaged()
        {
            lock (_libraryLock)
            {
                if (_staged == null) return;
                _library = _staged;
                _staged = null;
                PadForge.Engine.SdlDiagLog.WriteLine("Falcon: switched to the library at " + _library.Path);
            }
        }

        /// <summary>Whether a library is loaded and is not the copy the user
        /// picked: the pick did not load, and an installed copy runs.</summary>
        public static bool PickNotLoaded
        {
            get
            {
                lock (_libraryLock)
                    return _library != null && _picked != null && _staged == null && !SamePath(_library.Path, _picked);
            }
        }

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        public static FalconSweepStatus Status
        {
            get => _status;
            set => _status = value ?? FalconSweepStatus.None;
        }

        /// <summary>The library, loading it when due, or null with
        /// <paramref name="reason"/> saying why.</summary>
        public static IFalconLink Acquire(long now, out string reason, out string path, out string version)
        {
            path = version = string.Empty;
            lock (_libraryLock)
            {
                if (_library == null && now >= _nextLoad)
                {
                    _nextLoad = now + LoadRetryMs;
                    _library = Load(_picked, out _reason);
                }
                if (_library == null)
                {
                    reason = _reason;
                    return null;
                }
                reason = string.Empty;
                path = _library.Path;
                version = _library.Version;
                return _library.Link;
            }
        }
    }
}
