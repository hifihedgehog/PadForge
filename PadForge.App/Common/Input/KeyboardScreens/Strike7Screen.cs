using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using PadForge.Engine.Common.MadCatz;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>
    /// The Mad Catz S.T.R.I.K.E. 7's V.E.N.O.M. touchscreen (issue #519),
    /// through Mad Catz's <c>Strike7API.dll</c>, which reaches the keyboard's
    /// own software over a named pipe.
    ///
    /// <para>The applet is open only while there is something to show, so
    /// its icon leaves the V.E.N.O.M. screen when the kind is set to Off. It
    /// draws only between the active event, when the user picks it, and the
    /// inactive event, when the user presses the home key, the spec's
    /// lifecycle (page 2). A finger dragged across the screen pans the
    /// picture the way Mad Catz's map applet scrolls its map: each step the
    /// distance from the last touch, until the finger lifts.</para>
    ///
    /// <para>A library that will not load for want of the Visual C++ 2008
    /// runtime is not loaded again until the runtime is there: a failed
    /// load hashes the file and asks Windows to bind the runtime each time.
    /// The screen looks for the runtime every <see cref="RuntimeCheckMs"/>,
    /// and loads at once when the Install Visual C++ 2008 Runtime button's own check
    /// found it (<see cref="Strike7Library.RuntimeFoundCount"/>).</para>
    ///
    /// <para>The callbacks run on the library's thread and only queue. Every
    /// draw is made on the host's worker.</para>
    /// </summary>
    internal sealed class Strike7Screen : IDisposable
    {
        internal const int OpenRetryMs = 10000;
        internal const int RuntimeCheckMs = 60000;
        private const int IdleShutdownMs = 2000;
        private const int MinDrawMs = 50;

        /// <summary>Libraries by the path they were loaded from. A library is
        /// never freed, so one loaded once is used again.</summary>
        private static readonly Dictionary<string, IStrike7Library> s_libraries = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object s_lock = new();

        private readonly byte[] _icon;
        private readonly int _iconWidth, _iconHeight;
        private readonly ConcurrentQueue<Strike7Event> _events = new();
        private readonly ConcurrentQueue<(int Dx, int Dy)> _drags = new();
        private IStrike7Library _library;
        private string _path;
        private ushort _applet;
        private ushort _endpoint;
        private bool _shown;
        private long _drawn = -1, _nextOpen, _nextDraw, _idleSince = -1;
        private bool _pressed;
        private int _lastX, _lastY;
        private readonly Func<string, (IStrike7Library Library, string Reason)> _tryLoad;
        private readonly Func<bool> _runtimeInstalled;
        private readonly Func<long> _runtimeFound;
        private bool _waitingForRuntime;
        private long _runtimeSeen;

        /// <summary>Why the library is not loaded: "gate", "platform",
        /// "missing", "hash", "runtime", "load" or "exports", or empty once it
        /// is.</summary>
        public string LoadReason { get; private set; } = "missing";

        public bool Open => _applet != 0;

        /// <summary>The user has the applet on the V.E.N.O.M. screen.</summary>
        public bool Shown => _applet != 0 && _shown;

        public Strike7Screen(byte[] iconBgra, int iconWidth, int iconHeight)
            : this(iconBgra, iconWidth, iconHeight,
                path => (Strike7Library.TryLoad(path, out string reason), reason),
                Strike7Library.IsVc2008RuntimeInstalled, () => Strike7Library.RuntimeFoundCount)
        {
        }

        /// <summary>Test seam: the library's load, the runtime check and the
        /// count of checks that found the runtime.</summary>
        internal Strike7Screen(byte[] iconBgra, int iconWidth, int iconHeight,
            Func<string, (IStrike7Library Library, string Reason)> tryLoad, Func<bool> runtimeInstalled, Func<long> runtimeFound)
        {
            _icon = iconBgra;
            _iconWidth = iconWidth;
            _iconHeight = iconHeight;
            _tryLoad = tryLoad;
            _runtimeInstalled = runtimeInstalled;
            _runtimeFound = runtimeFound;
        }

        /// <summary>A drag's step, for the screen service to pan the
        /// picture with.</summary>
        public bool TryTakeDrag(out int dx, out int dy)
        {
            if (_drags.TryDequeue(out var d))
            {
                dx = d.Dx;
                dy = d.Dy;
                return true;
            }
            dx = dy = 0;
            return false;
        }

        public void Step(byte[] frame, long version, string path, string expectedSha256, long now)
        {
            if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
            {
                CloseApplet();
                _library = null;
                _path = path;
                _nextOpen = 0;
                _waitingForRuntime = false;
            }
            if (_library == null)
            {
                bool installed = _waitingForRuntime && _runtimeFound() != _runtimeSeen;
                if (now < _nextOpen && !installed) return;
                if (_waitingForRuntime && !installed)
                {
                    _nextOpen = now + RuntimeCheckMs;
                    if (!_runtimeInstalled()) return;
                }
                _nextOpen = now + OpenRetryMs;
                _library = Load(path, expectedSha256, _tryLoad, out string reason);
                LoadReason = reason;
                _waitingForRuntime = _library == null && reason == "runtime";
                if (_waitingForRuntime)
                {
                    _runtimeSeen = _runtimeFound();
                    _nextOpen = now + RuntimeCheckMs;
                }
                if (_library == null) return;
                _nextOpen = 0;
            }

            while (_events.TryDequeue(out var e)) Handle(e);

            if (frame == null)
            {
                if (_applet == 0) return;
                if (_idleSince < 0) _idleSince = now;
                if (now - _idleSince >= IdleShutdownMs) CloseApplet();
                return;
            }
            _idleSince = -1;

            if (_applet == 0)
            {
                if (now < _nextOpen) return;
                _nextOpen = now + OpenRetryMs;
                _applet = _library.Open("PadForge", _iconWidth, _iconHeight, _icon, e => _events.Enqueue(e));
                _shown = false;
                if (_applet == 0) return;
            }

            if (!_shown || _drawn == version || now < _nextDraw) return;
            _nextDraw = now + MinDrawMs;
            _library.DrawScreen(_applet, _endpoint, frame);
            _drawn = version;
        }

        private void Handle(Strike7Event e)
        {
            if (_applet == 0 || (e.AppletId != 0 && e.AppletId != _applet)) return;
            switch (e.Kind)
            {
                case Strike7EventKind.Active:
                    _endpoint = e.EndpointId;
                    _shown = true;
                    // The screen shows nothing of the applet's until it draws,
                    // so the picture goes out again.
                    _drawn = -1;
                    _nextDraw = 0;
                    break;
                case Strike7EventKind.Inactive:
                case Strike7EventKind.Reset:
                    _shown = false;
                    _pressed = false;
                    break;
                case Strike7EventKind.Touch:
                    if (_pressed && e.Pressed)
                    {
                        int dx = _lastX - e.X, dy = _lastY - e.Y;
                        if (dx != 0 || dy != 0) _drags.Enqueue((dx, dy));
                    }
                    _pressed = e.Pressed;
                    _lastX = e.X;
                    _lastY = e.Y;
                    break;
            }
        }

        /// <summary>The library at <paramref name="path"/>, loaded once. A
        /// copy PadForge downloaded is checked against the SDK's own hash
        /// before it loads, so a damaged copy is reported and never
        /// loaded.</summary>
        private static IStrike7Library Load(string path, string expectedSha256,
            Func<string, (IStrike7Library Library, string Reason)> tryLoad, out string reason)
        {
            reason = "missing";
            if (string.IsNullOrWhiteSpace(path)) return null;
            lock (s_lock)
            {
                if (s_libraries.TryGetValue(path, out var loaded))
                {
                    reason = string.Empty;
                    return loaded;
                }
            }
            if (!File.Exists(path)) return null;
            if (!string.IsNullOrEmpty(expectedSha256))
            {
                string actual;
                try { actual = DriverInstaller.Sha256Hex(path); }
                catch (Exception) { return null; }
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "hash";
                    return null;
                }
            }
            (var lib, reason) = tryLoad(path);
            if (lib == null) return null;
            lock (s_lock) s_libraries[path] = lib;
            reason = string.Empty;
            return lib;
        }

        private void CloseApplet()
        {
            if (_applet != 0) _library?.Close(_applet);
            _applet = 0;
            _shown = false;
            _pressed = false;
            _idleSince = -1;
            while (_events.TryDequeue(out _)) { }
        }

        public void Dispose() => CloseApplet();
    }
}
