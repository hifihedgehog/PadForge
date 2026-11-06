using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Resources.Strings;

namespace PadForge.Services
{
    /// <summary>What a keyboard screen shows (issue #519). The VMU's list in
    /// the VMU's order (<see cref="DreamcastScreenMode"/>), with a stopwatch
    /// and the light bar's color appended. The settings file stores the
    /// names, so none is renamed, and the values stay as numbered.</summary>
    public enum KeyboardScreenMode
    {
        /// <summary>Nothing from PadForge. Under LGS the screen stays with
        /// LGS's applets.</summary>
        Off = 0,
        ProfileName = 1,
        ProfileNumber = 2,
        /// <summary>The active profile's screen picture, else its name.</summary>
        ProfilePicture = 3,
        /// <summary>The time of day, HH:MM.</summary>
        Clock = 4,
        /// <summary>Hours and minutes since the active profile switched in.</summary>
        PlayTime = 5,
        /// <summary>A picture chosen for the screen, panned when it is larger.</summary>
        Picture = 6,
        /// <summary>The timer the Stopwatch macro runs.</summary>
        Stopwatch = 7,
        /// <summary>The color of a DualShock 4 or DualSense light bar in the
        /// lowest slot the screen feeds, @eVenent's light bar sync mode
        /// (discussion #507).</summary>
        Lightbar = 8,
    }

    /// <summary>What the Stopwatch macro does.</summary>
    public enum KeyboardScreenStopwatchAction
    {
        StartPause = 0,
        Reset = 1,
    }

    /// <summary>Which way the Pan Keyboard Screen Picture macro moves the
    /// screen's window over its picture.</summary>
    public enum KeyboardScreenPanDirection
    {
        Left = 0,
        Right = 1,
        Up = 2,
        Down = 3,
    }

    /// <summary>One keyboard screen's choices (issue #519), kept in the
    /// settings file by the screen's row, the way
    /// <see cref="BlissBoxPortData"/> keeps a Dreamcast pad's.</summary>
    public class KeyboardScreenData
    {
        /// <summary>The mode a screen shows until the user picks another: the
        /// light bar's color, the mode @eVenent asked to have when nothing
        /// else is set.</summary>
        public const KeyboardScreenMode DefaultMode = KeyboardScreenMode.Lightbar;

        /// <summary>The screen's row, its InstanceGuid.</summary>
        [XmlAttribute] public string Device { get; set; } = string.Empty;

        [XmlElement] public KeyboardScreenMode Mode { get; set; } = DefaultMode;

        /// <summary>The chosen picture's file: relative under the app folder,
        /// absolute elsewhere (IconPackageManager.MakeStoredPath).</summary>
        [XmlElement] public string Picture { get; set; }

        /// <summary>The window's top-left corner on a picture larger than the
        /// screen, in the picture's pixels.</summary>
        [XmlElement] public int PanX { get; set; }
        [XmlElement] public int PanY { get; set; }

        [XmlIgnore]
        internal bool IsDefault => Mode == DefaultMode && string.IsNullOrEmpty(Picture) && PanX == 0 && PanY == 0;

        /// <summary>The loaded list with unreadable devices and entries left
        /// at every default dropped, a mode the file names that no build knows
        /// put back to the default, a negative pan put to zero, and of a
        /// device's other entries the first one kept, BlissBoxPortData's
        /// cleanup.</summary>
        internal static List<KeyboardScreenData> Normalize(KeyboardScreenData[] screens)
        {
            var list = new List<KeyboardScreenData>();
            if (screens == null) return list;
            var seen = new HashSet<Guid>();
            foreach (var screen in screens)
            {
                if (screen == null || !Guid.TryParse(screen.Device, out var guid)) continue;
                if (!Enum.IsDefined(typeof(KeyboardScreenMode), screen.Mode)) screen.Mode = DefaultMode;
                if (screen.PanX < 0) screen.PanX = 0;
                if (screen.PanY < 0) screen.PanY = 0;
                if (string.IsNullOrWhiteSpace(screen.Picture)) screen.Picture = null;
                if (screen.IsDefault || !seen.Add(guid)) continue;
                screen.Device = guid.ToString("D");
                list.Add(screen);
            }
            return list;
        }
    }

    /// <summary>What the Screen Overlay shows (#560): text, a picture, a color,
    /// or none of them. The overlay draws it in WPF to the size of its window,
    /// so text stays sharp and a picture keeps its proportions and its
    /// transparency at any size.</summary>
    internal sealed record ScreenOverlayContent(string Text = null, BitmapSource Picture = null, Color? Fill = null)
    {
        public static readonly ScreenOverlayContent Nothing = new();

        public bool IsEmpty => Text == null && Picture == null && Fill == null;
    }

    /// <summary>
    /// The keyboard screens (issue #519): a mode per screen, kept by the
    /// screen's row, the macros' shows, stopwatch and pan, and the engine
    /// host that writes the pictures. <see cref="DreamcastScreenService"/>'s
    /// shape: ticked on the UI thread, where WPF draws, from the UI timer
    /// while the engine runs, whether or not PadForge has focus, with macro
    /// requests queued from the poll thread. A macro plays on the screens
    /// whose rows feed its slot, the Dreamcast macro's rule. The engine's
    /// stop resets it (<see cref="Reset"/>), which closes the host and the
    /// screens' sessions.
    ///
    /// <para>A Show Keyboard Screen macro plays over whatever the mode shows
    /// and hands the screen back when it ends.</para>
    /// </summary>
    public sealed class KeyboardScreenService
    {
        public const int MaxFrames = 8;
        public const int MinFrameMs = 100;
        private const int TickMs = 250;
        private const int MaxQueuedRequests = 64;
        internal const int PictureRetryMs = 5000;

        /// <summary>The S.T.R.I.K.E. 7 library PadForge downloads: Mad Catz's
        /// SDK, kept under the temp folder beside the voice models.</summary>
        internal static string DownloadedStrike7Library
            => Path.Combine(Path.GetTempPath(), "PadForge", "strike7", PadForge.Engine.Common.MadCatz.Strike7Library.LibraryName);

        /// <summary>The SHA-256 of API/x64/Strike7API.dll in strike7_api 1.1.</summary>
        internal const string Strike7LibrarySha256 = "86808D4FCE73E05D06CE01A1269D8FD711F45B8A31A6BE0AF648DCF8F1E9194C";

        private abstract record Request;
        private sealed record ShowRequest(int PadIndex, KeyboardScreenKind Kind, string Frames, int FrameMs, int Repeat) : Request;
        private sealed record ModeRequest(int PadIndex, KeyboardScreenKind Kind, KeyboardScreenMode Mode) : Request;
        private sealed record StopwatchRequest(KeyboardScreenStopwatchAction Action) : Request;
        private sealed record PanRequest(int PadIndex, KeyboardScreenPanDirection Direction, int Step) : Request;

        private static readonly ConcurrentQueue<Request> _requests = new();

        private readonly ViewModels.SettingsViewModel _settings;
        private readonly Action _markDirty;
        private readonly Action<string> _log;
        private KeyboardScreenHost _host;
        private readonly Dictionary<string, KeyboardScreenShow> _shows = new(StringComparer.Ordinal);
        /// <summary>Each screen's last composed picture and what it shows. A
        /// frame of the larger screens is over half a megabyte and the tick
        /// runs four times a second, so a picture is drawn again only when
        /// what it shows changes.</summary>
        private readonly Dictionary<string, (string What, byte[] Frame)> _composed = new(StringComparer.Ordinal);
        /// <summary>Each picture file's decoded picture, or a failed load and
        /// when to try it again.</summary>
        private readonly Dictionary<string, (BitmapSource Picture, long RetryAt)> _pictures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Shown> _shown = new(StringComparer.Ordinal);
        private FrameShow<BitmapSource> _overlayShow;
        private bool _overlayOn;

        /// <summary>Takes what the Screen Overlay shows each tick (#560).</summary>
        internal Action<ScreenOverlayContent> OverlaySink { get; set; }
        private long _nextTick;
        private bool _playStarted;
        private string _playProfile;
        private long _playStart;

        // One stopwatch for every screen.
        private static readonly object _stopwatchLock = new();
        private static bool _stopwatchRunning;
        private static long _stopwatchElapsed, _stopwatchSince;

        /// <summary>Why a screen shows nothing in a mode that shows something,
        /// for its line.</summary>
        internal enum Blank { None, Off, NoLightbar, NoPicture }

        private readonly record struct Shown(Blank Blank);

        public KeyboardScreenService(ViewModels.SettingsViewModel settings, Action markDirty, Action<string> log = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _markDirty = markDirty ?? (() => { });
            _log = log ?? (_ => { });
        }

        // ─────────────────────────────────────────────
        //  Macro requests, from the poll thread
        // ─────────────────────────────────────────────

        private static void Enqueue(Request request)
        {
            if (!KeyboardScreensRuntime.Enabled && !KeyboardScreensRuntime.OverlayEnabled) return;
            if (_requests.Count >= MaxQueuedRequests) _requests.TryDequeue(out _);
            _requests.Enqueue(request);
        }

        /// <summary>Show Keyboard Screen: frames over the mode of each screen
        /// of the kind that feeds the slot.</summary>
        public static void RequestShow(int padIndex, KeyboardScreenKind kind, string frames, int frameMs, int repeat)
            => Enqueue(new ShowRequest(padIndex, kind, frames, frameMs, repeat));

        /// <summary>Keyboard Screen Mode: the mode of each screen of the kind
        /// that feeds the slot, kept as its setting.</summary>
        public static void RequestMode(int padIndex, KeyboardScreenKind kind, KeyboardScreenMode mode)
            => Enqueue(new ModeRequest(padIndex, kind, mode));

        public static void RequestStopwatch(KeyboardScreenStopwatchAction action)
            => Enqueue(new StopwatchRequest(action));

        /// <summary>Pan Keyboard Screen Picture: moves the window of each
        /// screen that feeds the slot over its picture, stopping at the
        /// picture's edges.</summary>
        public static void RequestPan(int padIndex, KeyboardScreenPanDirection direction, int step)
            => Enqueue(new PanRequest(padIndex, direction, step));

        internal static int PendingRequests => _requests.Count;

        internal static void DropRequests()
        {
            while (_requests.TryDequeue(out _)) { }
        }

        // ─────────────────────────────────────────────
        //  Settings
        // ─────────────────────────────────────────────

        public KeyboardScreenData Get(Guid device)
        {
            string key = device.ToString("D");
            foreach (var screen in _settings.KeyboardScreens)
                if (string.Equals(screen.Device, key, StringComparison.OrdinalIgnoreCase)) return screen;
            return null;
        }

        /// <summary>Changes a screen's choices, drops an entry left at every
        /// default, and marks the settings to save.</summary>
        public void Update(Guid device, Action<KeyboardScreenData> change)
        {
            var screen = Get(device);
            if (screen == null)
            {
                screen = new KeyboardScreenData { Device = device.ToString("D") };
                _settings.KeyboardScreens.Add(screen);
            }
            change(screen);
            if (screen.IsDefault) _settings.KeyboardScreens.Remove(screen);
            _pictures.Clear();
            _composed.Clear();
            _nextTick = 0;
            _markDirty();
        }

        /// <summary>Remove Device on a screen's row: its choices go with the
        /// row. The row's GUID comes from the screen's identity, so a screen
        /// still plugged in returns at the defaults. True when an entry
        /// went.</summary>
        internal static bool Forget(ViewModels.SettingsViewModel settings, Guid device)
        {
            string key = device.ToString("D");
            return settings.KeyboardScreens.RemoveAll(
                screen => string.Equals(screen.Device, key, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        /// <summary>The screens present now, empty while the service is not
        /// running.</summary>
        internal KeyboardScreenInfo[] Screens => _host?.Screens ?? Array.Empty<KeyboardScreenInfo>();

        /// <summary>The present screen a row is, or null.</summary>
        internal KeyboardScreenInfo ScreenOf(Guid row)
        {
            foreach (var screen in Screens)
                if (screen.RowGuid == row) return screen;
            return null;
        }

        // ─────────────────────────────────────────────
        //  The tick
        // ─────────────────────────────────────────────

        /// <summary>One pass while the engine runs and the feature is on.
        /// Four a second, every tick while a show plays, and at once after a
        /// change. <paramref name="hardware"/> is the Keyboard Screens switch:
        /// only that opens devices and vendor libraries (<see cref="EnsureHost"/>).
        /// <paramref name="overlay"/> is the Screen Overlay's window, up, which
        /// needs neither.</summary>
        public void Tick(bool hardware, bool overlay)
        {
            long now = Environment.TickCount64;
            if (now < _nextTick) return;

            _overlayOn = overlay;
            if (!overlay) _overlayShow = null;
            if (hardware) EnsureHost();
            var screens = _host?.Screens ?? Array.Empty<KeyboardScreenInfo>();
            while (_requests.TryDequeue(out var request)) Apply(request, screens, now);
            TrackPlayTime(now);
            if (hardware) TickScreens(screens, now);
            if (overlay) TickOverlay(now);
            _nextTick = now + (_shows.Count > 0 || _overlayShow != null ? 0 : TickMs);
        }

        private void TickScreens(KeyboardScreenInfo[] screens, long now)
        {
            while (_host.TryTakeDrag(out int dx, out int dy))
            {
                var strike7 = Array.Find(screens, s => s.Kind == KeyboardScreenKind.Strike7);
                if (strike7 != null) PanScreen(strike7, dx, dy);
            }

            _host.SetStrike7Library(Strike7LibraryPath(out string sha256), sha256);
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var screen in screens)
            {
                present.Add(screen.Id);
                byte[] frame = ShowFrame(screen.Id, now);
                var blank = Blank.None;
                frame ??= Compose(screen, Get(screen.RowGuid), now, out blank);
                _shown[screen.Id] = new Shown(blank);
                _host.Publish(screen.Id, frame);
            }
            // A screen that left takes its show and its picture with it.
            foreach (string gone in _shows.Keys.Where(id => !present.Contains(id)).ToList()) _shows.Remove(gone);
            foreach (string gone in _composed.Keys.Where(id => !present.Contains(id)).ToList()) _composed.Remove(gone);
            foreach (string gone in _shown.Keys.Where(id => !present.Contains(id)).ToList()) _shown.Remove(gone);
        }

        private void TickOverlay(long now)
        {
            var frame = _overlayShow?.Frame(now);
            if (frame == null) _overlayShow = null;
            OverlaySink?.Invoke(frame != null
                ? new ScreenOverlayContent(Picture: frame)
                : OverlayContent(Get(KeyboardScreenInfo.OverlayRow), now));
        }

        /// <summary>The Screen Overlay's mode, as <see cref="Compose"/> gives a
        /// keyboard screen its picture.</summary>
        internal ScreenOverlayContent OverlayContent(KeyboardScreenData data, long now)
        {
            switch (data?.Mode ?? KeyboardScreenData.DefaultMode)
            {
                case KeyboardScreenMode.ProfileName:
                    return new(DreamcastScreenService.ActiveProfileName());
                case KeyboardScreenMode.ProfileNumber:
                    return new(DreamcastScreenService.ActiveProfileNumber().ToString(CultureInfo.InvariantCulture));
                case KeyboardScreenMode.ProfilePicture:
                {
                    var picture = Picture(ActiveProfilePicture(), now);
                    return picture != null ? new(Picture: picture) : new(DreamcastScreenService.ActiveProfileName());
                }
                case KeyboardScreenMode.Clock:
                    return new(DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture));
                case KeyboardScreenMode.PlayTime:
                    return new(DreamcastScreenService.FormatPlayTime(
                        TimeSpan.FromMilliseconds(Math.Max(0, now - (_playStarted ? _playStart : now)))));
                case KeyboardScreenMode.Picture:
                {
                    var picture = Picture(data?.Picture, now);
                    return picture != null ? new(Picture: picture) : ScreenOverlayContent.Nothing;
                }
                case KeyboardScreenMode.Stopwatch:
                    return new(FormatStopwatch(StopwatchTime(now)));
                case KeyboardScreenMode.Lightbar:
                    return LightbarContent(IsOnItsSlot);
                default:
                    return ScreenOverlayContent.Nothing;
            }
        }

        /// <summary>The overlay has no row and feeds no slot, so it follows the
        /// lowest slot of all, as a keyboard screen with no slots does.</summary>
        internal static ScreenOverlayContent LightbarContent(Func<Guid, int, bool> current)
            => LightbarColors.TryGetLowest(current, null, out var color)
                ? new(Fill: Color.FromRgb(color.R, color.G, color.B))
                : ScreenOverlayContent.Nothing;

        /// <summary>What the Screen Overlay's dialog previews for a mode and a
        /// picture, from the choices not saved yet.</summary>
        internal ScreenOverlayContent OverlayPreview(KeyboardScreenData data, string profilePicture, string profileName)
        {
            switch (data?.Mode)
            {
                case KeyboardScreenMode.ProfilePicture:
                {
                    var picture = KeyboardScreenRenderer.LoadPicture(profilePicture);
                    return picture != null ? new(Picture: picture) : new(profileName);
                }
                case KeyboardScreenMode.Picture:
                {
                    var picture = KeyboardScreenRenderer.LoadPicture(data.Picture);
                    return picture != null ? new(Picture: picture) : ScreenOverlayContent.Nothing;
                }
                default:
                    return OverlayContent(data, Environment.TickCount64);
            }
        }

        /// <summary>The library the S.T.R.I.K.E. 7 engine loads: the user's
        /// pick, else PadForge's download. Only the download is held to the
        /// SDK's hash.</summary>
        internal string Strike7LibraryPath(out string sha256)
        {
            string picked = _settings.Strike7LibraryPath;
            if (!string.IsNullOrWhiteSpace(picked))
            {
                sha256 = string.Empty;
                return IconPackageManager.ResolvePath(picked);
            }
            sha256 = Strike7LibrarySha256;
            return DownloadedStrike7Library;
        }

        /// <summary>Download Strike7API.dll: runs the download through
        /// <paramref name="run"/>, which reports a failure itself and
        /// returns, and drops the copy the user picked only when this
        /// download produced the library. A failed download leaves an earlier
        /// one on disk, and the pick stays in its place.</summary>
        internal static async System.Threading.Tasks.Task DownloadStrike7Library(
            Func<Action, System.Threading.Tasks.Task> run, Action download,
            ViewModels.SettingsViewModel settings, Action markDirty)
        {
            bool downloaded = false;
            await run(() =>
            {
                download();
                downloaded = true;
            });
            if (!downloaded || string.IsNullOrEmpty(settings.Strike7LibraryPath)) return;
            settings.Strike7LibraryPath = null;
            markDirty();
        }

        private void EnsureHost()
        {
            if (_host != null) return;
            var (icon, width, height) = Strike7Icon();
            _host = new KeyboardScreenHost(icon, width, height, _log);
            _host.Start();
        }

        /// <summary>The engine stopped, or the feature was switched off: the
        /// host closes every screen's session, and the shows, requests and
        /// loaded pictures go, so the next start reads each picture file
        /// again. Play time starts over with the next start. The stopwatch
        /// keeps its time.</summary>
        public void Reset()
        {
            CloseHost();
            _overlayOn = false;
            _overlayShow = null;
            _pictures.Clear();
            _playStarted = false;
            DropRequests();
            LightbarColors.Clear();
        }

        /// <summary>The Keyboard Screens switch went off while the Screen Overlay
        /// stays on: the screens' sessions close, and the overlay's picture, its
        /// play time and its queued macros carry on.</summary>
        internal void CloseHost()
        {
            var host = _host;
            _host = null;
            host?.Dispose();
            _shows.Clear();
            _shown.Clear();
            _composed.Clear();
            KeyboardScreensRuntime.Screens = Array.Empty<KeyboardScreenInfo>();
            KeyboardScreenKeys.ClearAll();
        }

        internal bool Running => _host != null;

        /// <summary>Anything for <see cref="Reset"/> to end: the screens' host, or
        /// the Screen Overlay's show, pictures and play time.</summary>
        internal bool Active => _host != null || _overlayOn;

        private void Apply(Request request, KeyboardScreenInfo[] screens, long now)
        {
            switch (request)
            {
                case ShowRequest { Kind: KeyboardScreenKind.Overlay } show:
                {
                    // The overlay has no row to feed a slot, so any slot's macro
                    // reaches it.
                    var frames = OverlayFrames(show.Frames);
                    if (_overlayOn && frames.Count > 0)
                        _overlayShow = new FrameShow<BitmapSource>(frames, show.FrameMs, show.Repeat, now);
                    break;
                }
                case ModeRequest { Kind: KeyboardScreenKind.Overlay } mode when Enum.IsDefined(typeof(KeyboardScreenMode), mode.Mode):
                    Update(KeyboardScreenInfo.OverlayRow, d => d.Mode = mode.Mode);
                    break;
                case ShowRequest show:
                {
                    var frames = DecodeFrames(show.Kind, show.Frames);
                    if (frames.Count == 0) break;
                    foreach (var screen in Feeding(screens, show.PadIndex))
                        if (screen.Kind == show.Kind)
                            _shows[screen.Id] = new KeyboardScreenShow(frames, show.FrameMs, show.Repeat, now);
                    break;
                }
                case ModeRequest mode when Enum.IsDefined(typeof(KeyboardScreenMode), mode.Mode):
                    foreach (var screen in Feeding(screens, mode.PadIndex))
                        if (screen.Kind == mode.Kind) Update(screen.RowGuid, d => d.Mode = mode.Mode);
                    break;
                case StopwatchRequest stopwatch:
                    Stopwatch(stopwatch.Action, now);
                    break;
                case PanRequest pan:
                {
                    int step = Math.Max(1, pan.Step);
                    var (dx, dy) = pan.Direction switch
                    {
                        KeyboardScreenPanDirection.Left => (-step, 0),
                        KeyboardScreenPanDirection.Right => (step, 0),
                        KeyboardScreenPanDirection.Up => (0, -step),
                        _ => (0, step),
                    };
                    foreach (var screen in Feeding(screens, pan.PadIndex)) PanScreen(screen, dx, dy);
                    break;
                }
            }
        }

        /// <summary>The screens whose rows feed the slot.</summary>
        internal static List<KeyboardScreenInfo> Feeding(IReadOnlyList<KeyboardScreenInfo> screens, int padIndex)
            => Feeding(screens, padIndex, SlotsFed);

        /// <summary>The same, with the slots each row feeds given.</summary>
        internal static List<KeyboardScreenInfo> Feeding(IReadOnlyList<KeyboardScreenInfo> screens, int padIndex,
            Func<Guid, HashSet<int>> slotsOf)
        {
            var list = new List<KeyboardScreenInfo>();
            foreach (var screen in screens)
                if (slotsOf(screen.RowGuid).Contains(padIndex)) list.Add(screen);
            return list;
        }

        /// <summary>The slots a row feeds.</summary>
        internal static HashSet<int> SlotsFed(Guid row)
        {
            var slots = new HashSet<int>();
            var settings = SettingsManager.UserSettings;
            if (settings == null) return slots;
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                    if (us != null && us.InstanceGuid == row && us.MapTo >= 0) slots.Add(us.MapTo);
            }
            return slots;
        }

        /// <summary>Moves a screen's window over its picture, held to the
        /// picture's edges. A screen with no picture is left as it is.</summary>
        private void PanScreen(KeyboardScreenInfo screen, int dx, int dy)
        {
            var data = Get(screen.RowGuid);
            var picture = Picture(data?.Picture, Environment.TickCount64);
            if (picture == null) return;
            var (maxX, maxY) = KeyboardScreenRenderer.PanLimits(picture, screen.Kind);
            int x = Math.Clamp(data.PanX + dx, 0, maxX), y = Math.Clamp(data.PanY + dy, 0, maxY);
            if (x == data.PanX && y == data.PanY) return;
            var pictures = new Dictionary<string, (BitmapSource Picture, long RetryAt)>(_pictures, StringComparer.OrdinalIgnoreCase);
            Update(screen.RowGuid, d =>
            {
                d.PanX = x;
                d.PanY = y;
            });
            // A pan is no change to the picture, so the decoded one stays.
            foreach (var (path, cached) in pictures) _pictures[path] = cached;
        }

        private void TrackPlayTime(long now)
        {
            string profile = SettingsManager.ActiveProfileId ?? string.Empty;
            if (_playStarted && string.Equals(profile, _playProfile, StringComparison.Ordinal)) return;
            _playStarted = true;
            _playProfile = profile;
            _playStart = now;
        }

        internal static void Stopwatch(KeyboardScreenStopwatchAction action, long now)
        {
            lock (_stopwatchLock)
            {
                if (action == KeyboardScreenStopwatchAction.Reset)
                {
                    _stopwatchRunning = false;
                    _stopwatchElapsed = 0;
                    return;
                }
                if (_stopwatchRunning) _stopwatchElapsed += now - _stopwatchSince;
                else _stopwatchSince = now;
                _stopwatchRunning = !_stopwatchRunning;
            }
        }

        internal static TimeSpan StopwatchTime(long now)
        {
            lock (_stopwatchLock)
                return TimeSpan.FromMilliseconds(Math.Max(0, _stopwatchElapsed + (_stopwatchRunning ? now - _stopwatchSince : 0)));
        }

        private byte[] ShowFrame(string screen, long now)
        {
            if (!_shows.TryGetValue(screen, out var show)) return null;
            var frame = show.Frame(now);
            if (frame == null) _shows.Remove(screen);
            return frame;
        }

        // ─────────────────────────────────────────────
        //  What a mode shows
        // ─────────────────────────────────────────────

        /// <summary>The picture a screen's mode shows now, or null for nothing
        /// with <paramref name="blank"/> saying why.</summary>
        internal byte[] Compose(KeyboardScreenInfo screen, KeyboardScreenData data, long now, out Blank blank)
        {
            blank = Blank.None;
            var kind = screen.Kind;
            var mode = data?.Mode ?? KeyboardScreenData.DefaultMode;
            switch (mode)
            {
                case KeyboardScreenMode.ProfileName:
                    return Text(screen, DreamcastScreenService.ActiveProfileName());
                case KeyboardScreenMode.ProfileNumber:
                    return Text(screen, DreamcastScreenService.ActiveProfileNumber().ToString(CultureInfo.InvariantCulture));
                case KeyboardScreenMode.ProfilePicture:
                {
                    string path = ActiveProfilePicture();
                    var picture = Picture(path, now);
                    return picture != null
                        ? Memo(screen.Id, "fitted|" + path, () => KeyboardScreenRenderer.RenderFitted(picture, kind))
                        : Text(screen, DreamcastScreenService.ActiveProfileName());
                }
                case KeyboardScreenMode.Clock:
                    return Text(screen, DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture));
                case KeyboardScreenMode.PlayTime:
                    return Text(screen, DreamcastScreenService.FormatPlayTime(
                        TimeSpan.FromMilliseconds(Math.Max(0, now - (_playStarted ? _playStart : now)))));
                case KeyboardScreenMode.Picture:
                {
                    var picture = Picture(data?.Picture, now);
                    if (picture != null)
                        return Memo(screen.Id, string.Format(CultureInfo.InvariantCulture, "window|{0}|{1}|{2}", data.Picture, data.PanX, data.PanY),
                            () => KeyboardScreenRenderer.RenderWindow(picture, kind, data.PanX, data.PanY));
                    blank = Blank.NoPicture;
                    return null;
                }
                case KeyboardScreenMode.Stopwatch:
                    return Text(screen, FormatStopwatch(StopwatchTime(now)));
                case KeyboardScreenMode.Lightbar:
                    if (LightbarColors.TryGetLowest(IsOnItsSlot, SlotFilter(screen.RowGuid), out var color))
                        return Memo(screen.Id, string.Format(CultureInfo.InvariantCulture, "color|{0}|{1}|{2}", color.R, color.G, color.B),
                            () => KeyboardScreenRenderer.RenderColor(color.R, color.G, color.B, kind));
                    blank = Blank.NoLightbar;
                    return null;
                default:
                    blank = Blank.Off;
                    return null;
            }
        }

        /// <summary>The slots whose light bar a screen follows: the ones its
        /// row feeds, or every slot when it feeds none.</summary>
        internal static Func<int, bool> SlotFilter(Guid row)
        {
            var slots = SlotsFed(row);
            return slots.Count == 0 ? null : slots.Contains;
        }

        /// <summary>Whether a pad whose light bar color is on record is still
        /// online and mapped to the slot the color came from.</summary>
        private static bool IsOnItsSlot(Guid device, int slot)
        {
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            if (devices == null || settings == null) return false;
            bool online = false;
            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                    if (ud != null && ud.InstanceGuid == device) { online = ud.IsOnline; break; }
            }
            if (!online) return false;
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                    if (us != null && us.InstanceGuid == device && us.MapTo == slot) return true;
            }
            return false;
        }

        internal static string FormatStopwatch(TimeSpan time)
            => time.TotalHours >= 1
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}:{2:D2}", (int)time.TotalHours, time.Minutes, time.Seconds)
                : string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}", time.Minutes, time.Seconds);

        private byte[] Text(KeyboardScreenInfo screen, string text)
            => Memo(screen.Id, "text|" + text, () => KeyboardScreenRenderer.RenderText(text, screen.Kind));

        /// <summary>A screen's picture of <paramref name="what"/>, drawn only
        /// when the screen last showed something else.</summary>
        private byte[] Memo(string screen, string what, Func<byte[]> draw)
        {
            if (_composed.TryGetValue(screen, out var last) && string.Equals(last.What, what, StringComparison.Ordinal))
                return last.Frame;
            byte[] frame = draw();
            _composed[screen] = (what, frame);
            return frame;
        }

        private BitmapSource Picture(string storedPath, long now)
        {
            if (string.IsNullOrWhiteSpace(storedPath)) return null;
            if (_pictures.TryGetValue(storedPath, out var cached) && (cached.Picture != null || now < cached.RetryAt))
                return cached.Picture;
            var picture = KeyboardScreenRenderer.LoadPicture(storedPath);
            // A picture that will not load is asked for again after
            // PictureRetryMs, not four times a second, so a file on a drive
            // that was not ready at start shows once it is.
            _pictures[storedPath] = (picture, picture == null ? now + PictureRetryMs : 0);
            return picture;
        }

        private string ActiveProfilePicture()
        {
            string id = SettingsManager.ActiveProfileId;
            if (string.IsNullOrEmpty(id)) return _settings.DefaultProfileKeyboardScreenPicture;
            return SettingsManager.Profiles.Find(p => p.Id == id)?.KeyboardScreenPicture;
        }

        /// <summary>What a screen's dialog previews for a mode and a picture,
        /// drawn the way the screen draws it.</summary>
        internal byte[] Preview(KeyboardScreenInfo screen, KeyboardScreenData data, string profilePicture, string profileName)
        {
            long now = Environment.TickCount64;
            var kind = screen.Kind;
            if (data?.Mode == KeyboardScreenMode.ProfilePicture)
            {
                var picture = KeyboardScreenRenderer.LoadPicture(profilePicture);
                return picture != null
                    ? KeyboardScreenRenderer.RenderFitted(picture, kind)
                    : KeyboardScreenRenderer.RenderText(profileName, kind);
            }
            if (data?.Mode == KeyboardScreenMode.Picture)
            {
                var picture = KeyboardScreenRenderer.LoadPicture(data.Picture);
                return picture == null ? null : KeyboardScreenRenderer.RenderWindow(picture, kind, data.PanX, data.PanY);
            }
            // The screen's own picture stays as the screen shows it.
            bool had = _composed.TryGetValue(screen.Id, out var kept);
            var frame = Compose(screen, data, now, out _);
            if (had) _composed[screen.Id] = kept;
            else _composed.Remove(screen.Id);
            return frame;
        }

        /// <summary>A screen's line for its panel on the Devices page: what
        /// draws it, or why nothing does, and when Logitech Gaming Software
        /// shows one picture on every screen of its kind, whose. Empty while
        /// the service is not running.</summary>
        internal string StatusLine(KeyboardScreenInfo screen)
        {
            if (_host == null || screen == null) return string.Empty;
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            string line;
            var shown = _shown.TryGetValue(screen.Id, out var sh) ? sh.Blank : Blank.None;
            var status = _host.StatusOf(screen.Id);
            if (shown == Blank.Off)
                line = string.Format(culture, s.KeyboardScreens_Status_Line_Format, KindName(screen.Kind), s.KeyboardScreens_Status_Off);
            else if (shown != Blank.None && status.Route is KeyboardScreenRoute.ArxNotRunning or KeyboardScreenRoute.Strike7NotRunning)
            {
                // A vendor applet is closed while its screen has nothing to
                // show, so its route says nothing about the vendor's
                // software. The line names the wait alone.
                line = string.Format(culture, shown == Blank.NoLightbar
                    ? s.KeyboardScreens_Status_NoLightbar_Format : s.KeyboardScreens_Status_NoPicture_Format, KindName(screen.Kind));
            }
            else
            {
                string what = RouteText(status);
                if (shown == Blank.NoLightbar) what = string.Format(culture, s.KeyboardScreens_Status_NoLightbar_Format, what);
                else if (shown == Blank.NoPicture) what = string.Format(culture, s.KeyboardScreens_Status_NoPicture_Format, what);
                line = string.Format(culture, s.KeyboardScreens_Status_Line_Format, KindName(screen.Kind), what);
            }
            var leader = _host.SharedLeader(screen.Kind);
            if (leader != null)
                line += Environment.NewLine + string.Format(culture, s.KeyboardScreens_LgsShared_Format, leader.Name);
            return line;
        }

        /// <summary>The Settings card's lines: only what belongs to the
        /// feature as a whole. Arx Control's library while no Arx screen is
        /// present, the S.T.R.I.K.E. 7's while no S.T.R.I.K.E. 7 is, and no
        /// screen found when there is none at all. Each screen's own line is on its row. Empty
        /// while the service is not running.</summary>
        internal string SubsystemLines()
        {
            if (_host == null) return string.Empty;
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            var screens = _host.Screens;
            var lines = new List<string>();
            if (!Array.Exists(screens, x => x.Kind == KeyboardScreenKind.Arx))
                lines.Add(string.Format(culture, s.KeyboardScreens_Status_Line_Format,
                    KindName(KeyboardScreenKind.Arx), RouteText(_host.ArxStatus)));
            if (!Array.Exists(screens, x => x.Kind == KeyboardScreenKind.Strike7))
                lines.Add(string.Format(culture, s.KeyboardScreens_Status_Line_Format,
                    KindName(KeyboardScreenKind.Strike7), RouteText(_host.Strike7Status)));
            if (screens.Length == 0)
                lines.Add(string.Format(culture, s.KeyboardScreens_Status_Line_Format,
                    s.Settings_KeyboardScreens_Title, s.KeyboardScreens_Status_NotFound));
            return string.Join(Environment.NewLine, lines);
        }

        private static string RouteText(KeyboardScreenStatus status)
        {
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            return status.Route switch
            {
                KeyboardScreenRoute.Lgs => s.KeyboardScreens_Status_Lgs,
                KeyboardScreenRoute.Direct => string.Format(culture, s.KeyboardScreens_Status_Direct_Format, status.Detail),
                KeyboardScreenRoute.G19Preparing => s.KeyboardScreens_Status_G19Preparing,
                KeyboardScreenRoute.G19OtherDriver => string.Format(culture, s.KeyboardScreens_Status_G19OtherDriver_Format, status.Detail),
                KeyboardScreenRoute.G19ForeignDriver => string.Format(culture, s.KeyboardScreens_Status_G19ForeignDriver_Format, status.Detail),
                KeyboardScreenRoute.ArxNoLibrary => s.KeyboardScreens_Status_ArxNoLibrary,
                KeyboardScreenRoute.ArxNotRunning => s.KeyboardScreens_Status_ArxNotRunning,
                KeyboardScreenRoute.ArxWaiting => s.KeyboardScreens_Status_ArxWaiting,
                KeyboardScreenRoute.ArxOnPhone => s.KeyboardScreens_Status_ArxOnPhone,
                KeyboardScreenRoute.Strike7NoLibrary => s.KeyboardScreens_Status_Strike7NoLibrary,
                KeyboardScreenRoute.Strike7Changed => s.KeyboardScreens_Status_Strike7Changed,
                KeyboardScreenRoute.Strike7NeedsRuntime => s.KeyboardScreens_Status_Strike7NeedsRuntime,
                KeyboardScreenRoute.Strike7LoadFailed => s.KeyboardScreens_Status_Strike7LoadFailed,
                KeyboardScreenRoute.Strike7NotRunning => s.KeyboardScreens_Status_Strike7NotRunning,
                KeyboardScreenRoute.Strike7Waiting => s.KeyboardScreens_Status_Strike7Waiting,
                KeyboardScreenRoute.Strike7Showing => s.KeyboardScreens_Status_Strike7Showing,
                KeyboardScreenRoute.NotOnArm64 => s.Common_NotAvailableOnArm64,
                _ => s.KeyboardScreens_Status_NotFound,
            };
        }

        internal static string KindName(KeyboardScreenKind kind)
        {
            var s = Strings.Instance;
            return kind switch
            {
                KeyboardScreenKind.Monochrome => s.KeyboardScreens_Kind_Monochrome,
                KeyboardScreenKind.Color => s.KeyboardScreens_Kind_Color,
                KeyboardScreenKind.Arx => s.KeyboardScreens_Kind_Arx,
                KeyboardScreenKind.Overlay => s.KeyboardScreens_Kind_Overlay,
                _ => s.KeyboardScreens_Kind_Strike7,
            };
        }

        /// <summary>The label of a screen row's button on the Devices page,
        /// which names the kind of screen the way Dreamcast Screen… does.</summary>
        internal static string ButtonText(KeyboardScreenKind kind)
        {
            var s = Strings.Instance;
            return kind switch
            {
                KeyboardScreenKind.Monochrome => s.KeyboardScreens_Configure_Monochrome,
                KeyboardScreenKind.Color => s.KeyboardScreens_Configure_Color,
                KeyboardScreenKind.Arx => s.KeyboardScreens_Configure_Arx,
                _ => s.KeyboardScreens_Configure_Strike7,
            };
        }

        /// <summary>A mode's name. The VMU's modes keep the VMU's words.</summary>
        internal static string ModeName(KeyboardScreenMode mode)
        {
            var s = Strings.Instance;
            return mode switch
            {
                KeyboardScreenMode.ProfileName => s.DreamcastScreen_Mode_ProfileName,
                KeyboardScreenMode.ProfileNumber => s.DreamcastScreen_Mode_ProfileNumber,
                KeyboardScreenMode.ProfilePicture => s.DreamcastScreen_Mode_ProfilePicture,
                KeyboardScreenMode.Clock => s.DreamcastScreen_Mode_Clock,
                KeyboardScreenMode.PlayTime => s.DreamcastScreen_Mode_PlayTime,
                KeyboardScreenMode.Picture => s.DreamcastScreen_Mode_Picture,
                KeyboardScreenMode.Stopwatch => s.KeyboardScreens_Mode_Stopwatch,
                KeyboardScreenMode.Lightbar => s.KeyboardScreens_Mode_Lightbar,
                _ => s.KeyboardScreens_Mode_Off,
            };
        }

        internal static string DirectionName(KeyboardScreenPanDirection direction)
        {
            var s = Strings.Instance;
            return direction switch
            {
                KeyboardScreenPanDirection.Left => s.Pad_ScrollLeft,
                KeyboardScreenPanDirection.Right => s.Pad_ScrollRight,
                KeyboardScreenPanDirection.Up => s.Pad_ScrollUp,
                _ => s.Pad_ScrollDown,
            };
        }

        // ─────────────────────────────────────────────
        //  Show frames
        // ─────────────────────────────────────────────

        /// <summary>Up to eight pictures from a Show Keyboard Screen action,
        /// each a base64 PNG, drawn at the kind's size.</summary>
        internal static List<byte[]> DecodeFrames(KeyboardScreenKind kind, string frames)
        {
            var list = new List<byte[]>();
            if (string.IsNullOrEmpty(frames)) return list;
            foreach (var part in frames.Split(','))
            {
                if (list.Count == MaxFrames) break;
                var picture = FramePicture(kind, part);
                if (picture != null) list.Add(KeyboardScreenRenderer.RenderFitted(picture, kind));
            }
            return list;
        }

        /// <summary>Up to eight pictures from a Show Keyboard Screen action for the
        /// Screen Overlay, each as it was imported, with its own proportions and
        /// transparency.</summary>
        internal static List<BitmapSource> OverlayFrames(string frames)
        {
            var list = new List<BitmapSource>();
            if (string.IsNullOrEmpty(frames)) return list;
            foreach (var part in frames.Split(','))
            {
                if (list.Count == MaxFrames) break;
                var picture = FramePicture(KeyboardScreenKind.Overlay, part);
                if (picture != null) list.Add(picture);
            }
            return list;
        }

        /// <summary>A Show Keyboard Screen action's pictures as the macro editor
        /// lists them, drawn the way the kind's screen draws them.</summary>
        internal static List<ImageSource> FrameImages(KeyboardScreenKind kind, string frames)
        {
            var images = new List<ImageSource>();
            if (kind == KeyboardScreenKind.Overlay)
                images.AddRange(OverlayFrames(frames));
            else
                foreach (var frame in DecodeFrames(kind, frames))
                    images.Add(KeyboardScreenRenderer.ToBitmap(frame, kind));
            return images;
        }

        /// <summary>One entry of a Show Keyboard Screen action's pictures,
        /// decoded no larger than the kind's screen, or null when it holds no
        /// picture. <see cref="DecodeFrames"/> skips such an entry.</summary>
        internal static BitmapSource FramePicture(KeyboardScreenKind kind, string part)
        {
            byte[] png;
            try { png = Convert.FromBase64String((part ?? string.Empty).Trim()); }
            catch (FormatException) { return null; }
            var (width, height) = KeyboardScreenFormats.SizeOf(kind);
            return BoundedBitmap.FromBytes(png, width, height, scaleUp: kind != KeyboardScreenKind.Overlay);
        }

        /// <summary>A picture file as one Show Keyboard Screen frame: fitted to
        /// the kind's size, cut to one bit on the monochrome screen, and
        /// encoded as PNG in base64, or for the Screen Overlay kept at its own
        /// proportions up to <see cref="KeyboardScreenFormats.OverlayFrameSide"/>.
        /// Null when the file holds no picture WPF decodes.</summary>
        internal static string ImportFrame(string path, KeyboardScreenKind kind)
        {
            byte[] file = MenuIconResolver.ReadCapped(path);
            if (kind == KeyboardScreenKind.Overlay)
            {
                // The overlay scales a frame itself, so it keeps the picture's
                // proportions and transparency, with no field around it, and a
                // small picture stays small in the settings file.
                var (side, _) = KeyboardScreenFormats.SizeOf(kind);
                var own = BoundedBitmap.FromBytes(file, side, side, scaleUp: false);
                return own == null ? null : EncodePng(new FormatConvertedBitmap(own, PixelFormats.Bgra32, null, 0));
            }
            var picture = BoundedBitmap.FromBytes(file, KeyboardScreenRenderer.MaxPictureSide,
                KeyboardScreenRenderer.MaxPictureSide, scaleUp: false);
            return picture == null ? null : EncodeFrame(KeyboardScreenRenderer.RenderFitted(picture, kind), kind);
        }

        internal static string EncodeFrame(byte[] bgra, KeyboardScreenKind kind)
        {
            var (width, height) = KeyboardScreenFormats.SizeOf(kind);
            return EncodePng(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4));
        }

        private static string EncodePng(BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return Convert.ToBase64String(stream.ToArray());
        }

        public static string JoinFrames(IEnumerable<string> frames)
        {
            var parts = new List<string>();
            foreach (var frame in frames)
            {
                if (parts.Count == MaxFrames) break;
                if (!string.IsNullOrEmpty(frame)) parts.Add(frame);
            }
            return string.Join(",", parts);
        }

        /// <summary>The applet's icon on the V.E.N.O.M. screen: PadForge's
        /// name on its ember, at the 100 by 70 of Mad Catz's example
        /// icon.</summary>
        private static (byte[] Bgra, int Width, int Height) Strike7Icon()
        {
            const int width = 100, height = 70;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // App.xaml's EmberBrush.
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x2C)), null, new System.Windows.Rect(0, 0, width, height));
                var text = new FormattedText("PadForge", CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Bold,
                        System.Windows.FontStretches.Normal), 18, Brushes.White, 1.0);
                dc.DrawText(text, new System.Windows.Point((width - text.Width) / 2, (height - text.Height) / 2));
            }
            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var converted = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
            var bgra = new byte[width * height * 4];
            converted.CopyPixels(bgra, width * 4, 0);
            return (bgra, width, height);
        }
    }

    /// <summary>A Show Keyboard Screen action playing on one screen (#519):
    /// each frame for its frame time, the frames repeated the given number
    /// of times.</summary>
    internal sealed class KeyboardScreenShow : FrameShow<byte[]>
    {
        public KeyboardScreenShow(List<byte[]> frames, int frameMs, int repeat, long now)
            : base(frames, frameMs, repeat, now)
        {
        }
    }

    /// <summary>The timing of a show, whatever its frames are.</summary>
    internal class FrameShow<TFrame> where TFrame : class
    {
        private readonly List<TFrame> _frames;
        private readonly int _frameMs;
        private readonly int _total;
        private readonly long _start;

        public FrameShow(List<TFrame> frames, int frameMs, int repeat, long now)
        {
            _frames = frames ?? throw new ArgumentNullException(nameof(frames));
            if (frames.Count == 0) throw new ArgumentException("A show needs a picture.", nameof(frames));
            _frameMs = Math.Max(KeyboardScreenService.MinFrameMs, frameMs);
            _total = frames.Count * Math.Max(1, repeat);
            _start = now;
        }

        /// <summary>The frame to show now, or null once every frame has had
        /// its time.</summary>
        public TFrame Frame(long now)
        {
            long index = Math.Max(0, now - _start) / _frameMs;
            return index >= _total ? null : _frames[(int)(index % _frames.Count)];
        }
    }
}
