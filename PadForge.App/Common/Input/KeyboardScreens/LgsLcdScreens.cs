using PadForge.Engine.Common.Logitech;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>
    /// The Logitech screens drawn through LGS's LCD library (issue #519):
    /// the monochrome GamePanel screens, the G19's color screen and LGS's
    /// LCD Emulator beside them. LGS takes one picture for every screen of
    /// a type, so the host hands it each kind's leading screen's picture.
    ///
    /// <para>The applet is registered only for the kinds that have something
    /// to show, so a kind set to Off stays with LGS's own applets, and a
    /// change in that set registers it again. <c>LogiLcdInit</c> failing
    /// means LGS is not running, and it is tried again every
    /// <see cref="InitRetryMs"/>. <c>LogiLcdUpdate</c> runs on every pass,
    /// as logitech-lcd's examples call it every frame, and the keys are read
    /// after it: LGS reports them to the applet in front alone. LGS names no
    /// keyboard a key came from, so the host gives each one to a screen
    /// (<see cref="ButtonFlags"/>).</para>
    ///
    /// <para>LGS restarting ends the session from LGS's side. The LCD
    /// library's own behavior then is not documented in any reference on
    /// hand (logitech-lcd, dcspy, LogitechStuff, foo_logitech_lcd, the lgLcd
    /// wrappers), so the sibling SDK's rule is followed: Arx's guide
    /// (LogitechGArxControl.pdf page 15) says a broken connection to LGS
    /// takes no further call and wants a shutdown, and page 8 a new init on a
    /// timer. Every <see cref="LgsCheckMs"/> while the session is up, the
    /// worker reads which LGS process runs (lgs or LCore). A session that
    /// saw one and then sees it gone, or replaced by another, is shut down
    /// and opened again on the next init. A re-init is harmless where the
    /// library had already reconnected on its own.</para>
    ///
    /// <para>The library is loaded once for the process and never freed
    /// (<see cref="LogitechLcdLibrary"/>). Every call is made on the host's
    /// worker, so the library sees one thread.</para>
    /// </summary>
    internal sealed class LgsLcdScreens : System.IDisposable
    {
        public const int InitRetryMs = 10000;
        /// <summary>How often a session looks for LGS's process: the init
        /// retry's slow lane, one read of the process list
        /// (RunningProcessList, the id list and one wait per process).</summary>
        public const int LgsCheckMs = InitRetryMs;
        /// <summary>The image names of LGS's process, both eras
        /// (LogiLedEngineNative.HostProcesses without G HUB's agent).</summary>
        internal static readonly string[] LgsProcesses = { "lgs", "LCore" };
        private const int LoadRetryMs = 30000;
        private const int ConnectedPollMs = 1000;
        /// <summary>How long the screens may have nothing to show before the
        /// applet leaves LGS, so a mode switched in passing does not take it
        /// down and back up.</summary>
        private const int IdleShutdownMs = 2000;

        private static readonly object s_loadLock = new();
        private static LogitechLcdLibrary s_library;
        private static long s_nextLoad;

        private readonly System.Func<long, ILogitechLcd> _library;
        private readonly System.Func<uint[]> _lgsIds;
        private int _types;
        private long _nextInit, _nextConnected, _nextLgsCheck, _idleSince = -1;
        private long _monoWritten = -1, _colorWritten = -1;
        /// <summary>The ids of LGS's processes the session first saw, or
        /// null before it saw one.</summary>
        private uint[] _sessionLgs;

        public LgsLcdScreens() : this(Library, RunningLgsIds) { }

        /// <summary>Test seam: the library, null while it does not load, and
        /// the ids of LGS's running processes, null when the list cannot be
        /// read.</summary>
        internal LgsLcdScreens(System.Func<long, ILogitechLcd> library, System.Func<uint[]> lgsIds)
        {
            _library = library;
            _lgsIds = lgsIds;
        }

        /// <summary>LGS took the applet: it draws every Logitech screen it
        /// sees, and the direct writers stand down for those.</summary>
        public bool Active { get; private set; }
        public bool MonoConnected { get; private set; }
        public bool ColorConnected { get; private set; }

        /// <summary>LGS's button flags held now
        /// (<see cref="KeyboardScreenFormats.LgsButtons"/>), zero while the
        /// applet is down.</summary>
        public int ButtonFlags { get; private set; }

        private static LogitechLcdLibrary Library(long now)
        {
            lock (s_loadLock)
            {
                if (s_library != null || now < s_nextLoad) return s_library;
                s_nextLoad = now + LoadRetryMs;
                s_library = LogitechLcdLibrary.TryLoad(out _);
                return s_library;
            }
        }

        /// <summary>One worker pass with the monochrome and color pictures,
        /// each null when PadForge shows nothing there.</summary>
        public void Step(byte[] mono, long monoVersion, byte[] color, long colorVersion, long now)
        {
            int wanted = (mono != null ? LogitechLcdLibrary.TypeMono : 0) | (color != null ? LogitechLcdLibrary.TypeColor : 0);
            var lib = _library(now);
            if (lib == null)
            {
                Active = MonoConnected = ColorConnected = false;
                ButtonFlags = 0;
                return;
            }

            if (wanted == 0)
            {
                if (!Active) return;
                if (_idleSince < 0) _idleSince = now;
                if (now - _idleSince < IdleShutdownMs)
                {
                    lib.Update();
                    return;
                }
                Shutdown(lib);
                return;
            }
            _idleSince = -1;

            if (Active && now >= _nextLgsCheck) CheckLgs(lib, now);

            if (!Active || _types != wanted)
            {
                if (now < _nextInit) return;
                if (Active) Shutdown(lib);
                if (!lib.Init("PadForge", wanted))
                {
                    _nextInit = now + InitRetryMs;
                    return;
                }
                Active = true;
                _types = wanted;
                _monoWritten = _colorWritten = -1;
                _nextConnected = 0;
                _nextLgsCheck = 0;
            }

            if (mono != null && _monoWritten != monoVersion
                && lib.MonoSetBackground(KeyboardScreenFormats.ToLgsMono(mono)))
                _monoWritten = monoVersion;
            if (color != null && _colorWritten != colorVersion && lib.ColorSetBackground(color))
                _colorWritten = colorVersion;
            lib.Update();

            int flags = 0;
            foreach (var (flag, _, _) in KeyboardScreenFormats.LgsButtons)
                if (lib.IsButtonPressed(flag)) flags |= flag;
            ButtonFlags = flags;

            if (now >= _nextConnected)
            {
                _nextConnected = now + ConnectedPollMs;
                MonoConnected = lib.IsConnected(LogitechLcdLibrary.TypeMono);
                ColorConnected = lib.IsConnected(LogitechLcdLibrary.TypeColor);
            }
        }

        /// <summary>LGS's process against the one the session first saw (see
        /// the class notes). Gone: the session is shut down and the init
        /// tried again on its timer, the direct writers drawing meanwhile.
        /// Another one: shut down and opened again at once. A list that
        /// cannot be read decides nothing, and a session that never saw an
        /// LGS process is left alone.</summary>
        private void CheckLgs(ILogitechLcd lib, long now)
        {
            _nextLgsCheck = now + LgsCheckMs;
            var ids = _lgsIds();
            if (ids == null) return;
            System.Array.Sort(ids);
            if (_sessionLgs == null)
            {
                if (ids.Length > 0) _sessionLgs = ids;
                return;
            }
            if (System.Linq.Enumerable.SequenceEqual(ids, _sessionLgs)) return;
            Shutdown(lib);
            _nextInit = ids.Length > 0 ? 0 : now + InitRetryMs;
        }

        /// <summary>The ids of the running LGS processes, or null when the
        /// list cannot be read. Worker thread.</summary>
        private static uint[] RunningLgsIds()
        {
            var running = RunningProcessList.Read();
            if (running == null) return null;
            var ids = new System.Collections.Generic.List<uint>();
            foreach (var process in running)
                foreach (string name in LgsProcesses)
                    if (string.Equals(process.Name, name, System.StringComparison.OrdinalIgnoreCase)) ids.Add(process.Id);
            return ids.ToArray();
        }

        private void Shutdown(ILogitechLcd lib)
        {
            lib.Shutdown();
            Active = MonoConnected = ColorConnected = false;
            ButtonFlags = 0;
            _types = 0;
            _idleSince = -1;
            _sessionLgs = null;
        }

        public void Dispose()
        {
            if (!Active) return;
            // A time before any load answers the library already loaded and
            // never loads one.
            var lib = _library(long.MinValue);
            if (lib != null) Shutdown(lib);
        }
    }
}
