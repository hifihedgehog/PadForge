using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine.Common.BlissBox;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;

namespace PadForge.Services
{
    /// <summary>What a Bliss-Box port's Dreamcast screen shows (issue #469).
    /// The settings file stores the names, so none is renamed, and the
    /// dialog lists the values in order, so they stay as numbered.</summary>
    public enum DreamcastScreenMode
    {
        /// <summary>The picture the adapter holds. PadForge writes nothing, and
        /// puts back the picture it found there if it wrote one since.</summary>
        Adapter = 0,
        ProfileName = 1,
        ProfileNumber = 2,
        /// <summary>The active profile's own picture, else its name.</summary>
        ProfilePicture = 3,
        /// <summary>The time of day, HH:MM.</summary>
        Clock = 4,
        /// <summary>Hours and minutes since PadForge found the pad.</summary>
        PlayTime = 5,
        /// <summary>A picture chosen for the port.</summary>
        Picture = 6,
    }

    /// <summary>One Bliss-Box port's choices (issue #469), kept in the
    /// settings file by the port's device.</summary>
    public class BlissBoxPortData
    {
        [XmlAttribute] public string Device { get; set; } = string.Empty;

        [XmlElement] public DreamcastScreenMode ScreenMode { get; set; }

        /// <summary>The chosen picture, 192 bytes in image order as base64.</summary>
        [XmlElement] public string Picture { get; set; }

        /// <summary>Poll a 3.x PlayStation digital pad through the native
        /// channel for its four directions.</summary>
        [XmlElement] public bool NativeArrows { get; set; }

        /// <summary>The picture the adapter held before PadForge first wrote
        /// one, in wire order as base64, so Adapter mode can put it back.
        /// Cleared once it is back.</summary>
        [XmlElement] public string AdapterPicture { get; set; }

        [XmlIgnore]
        internal bool IsEmpty => ScreenMode == DreamcastScreenMode.Adapter && string.IsNullOrEmpty(Picture)
                                 && !NativeArrows && string.IsNullOrEmpty(AdapterPicture);

        /// <summary>The loaded list with unreadable devices and entries left
        /// at every default dropped, and of a device's other entries the
        /// first one kept. A picture that does not decode is dropped too: a
        /// copy of the adapter's own that could never be written back would
        /// block a new copy, the restore and the entry's cleanup for good.</summary>
        internal static List<BlissBoxPortData> Normalize(BlissBoxPortData[] ports)
        {
            var list = new List<BlissBoxPortData>();
            if (ports == null) return list;
            var seen = new HashSet<Guid>();
            foreach (var port in ports)
            {
                if (port == null || !Guid.TryParse(port.Device, out var guid)) continue;
                if (!Enum.IsDefined(typeof(DreamcastScreenMode), port.ScreenMode)) port.ScreenMode = DreamcastScreenMode.Adapter;
                if (!string.IsNullOrEmpty(port.AdapterPicture) && !DreamcastScreenService.TryDecode(port.AdapterPicture, out _))
                    port.AdapterPicture = null;
                if (!string.IsNullOrEmpty(port.Picture) && !DreamcastScreenService.TryDecode(port.Picture, out _))
                    port.Picture = null;
                // Checked before the device counts as seen, so an empty entry
                // never hides a real one after it.
                if (port.IsEmpty || !seen.Add(guid)) continue;
                port.Device = guid.ToString("D");
                list.Add(port);
            }
            return list;
        }

        /// <summary>For Reset to Defaults: each port's copy of the adapter's
        /// own picture, with every choice back at its default. The picture
        /// is the adapter's, not a setting, and Adapter mode writes it back
        /// the next time the switch is on. Null when no port kept one.</summary>
        internal static BlissBoxPortData[] KeepAdapterPictures(IEnumerable<BlissBoxPortData> ports)
        {
            var kept = new List<BlissBoxPortData>();
            if (ports != null)
                foreach (var port in ports)
                    if (port != null && !string.IsNullOrEmpty(port.AdapterPicture))
                        kept.Add(new BlissBoxPortData { Device = port.Device, AdapterPicture = port.AdapterPicture });
            return kept.Count > 0 ? kept.ToArray() : null;
        }
    }

    /// <summary>
    /// The VMU screens of the Dreamcast pads in Bliss-Box ports (issue #469),
    /// and the ports' other choices. Ticked on the UI thread, where WPF draws
    /// the text, from the UI timer while the engine runs, whether or not
    /// PadForge has focus and whether or not a port is open, so a show dies
    /// with the port instance it played on and a request queued as the last
    /// port closed is dropped, rather than playing on the next port for the
    /// same device. The engine's stop resets it once the poll thread and the
    /// ports have stopped (<see cref="Reset"/>).
    /// Each tick decides the picture a port should show and hands it to the
    /// port's session, which writes it only when it differs from the one the
    /// adapter holds and no sooner than a second after the last write (the
    /// EEPROM guard).
    ///
    /// <para>Before PadForge first replaces a picture, the adapter's own is
    /// kept, so Adapter mode can write it back, and the settings are saved
    /// at once. Until a save has written that copy to the settings file, the
    /// port keeps its own picture. A Show Dreamcast Screen macro plays over whatever the
    /// mode shows and hands the screen back when it ends.</para>
    /// </summary>
    public sealed class DreamcastScreenService
    {
        public const int MaxFrames = 8;
        public const int MinFrameMs = BlissBoxSession.ScreenIntervalMs;
        private const int TickMs = 250;
        private const int TextCacheLimit = 64;

        /// <summary>A pad back in its port within this long keeps its play
        /// time: the adapter searching for a moment, or the port's channel
        /// reopening, is not a new session.</summary>
        internal const int PlayTimeGraceMs = 10000;

        /// <summary>Shows waiting for the next tick. A macro on turbo can
        /// fire faster than the ticks run, and only the latest show matters.</summary>
        private const int MaxQueuedShows = 16;

        private static readonly ConcurrentQueue<ShowRequest> _requests = new();

        private readonly ViewModels.SettingsViewModel _settings;
        private readonly Action _markDirty;
        private readonly Func<bool> _saveNow;
        private readonly Func<int> _saves;
        // Devices whose copy of the adapter's own picture no save has written
        // to the settings file yet, with the settings' save count when it was
        // made.
        private readonly Dictionary<Guid, int> _unsavedCopies = new();
        // When each port's pad started its play time and when it was last
        // seen, by port instance, so a pad whose port closed and opened
        // again counts from the new port.
        private readonly Dictionary<BlissBoxPort, (long Start, long Seen)> _attached = new();
        // Macro shows by port instance, as play time is kept.
        private readonly Dictionary<BlissBoxPort, DreamcastShow> _shows = new();
        private readonly Dictionary<string, byte[]> _textCache = new(StringComparer.Ordinal);
        private long _nextTick;

        /// <param name="saveNow">Saves the settings file at once, for the
        /// copy of an adapter's own picture. True when it was written.</param>
        /// <param name="saves">How many times the settings file has been
        /// written, so a copy made before a later write is in the file. A
        /// reload that finds no file clears the unsaved flag without writing,
        /// so the flag cannot stand in for it.</param>
        public DreamcastScreenService(ViewModels.SettingsViewModel settings, Action markDirty,
            Func<bool> saveNow = null, Func<int> saves = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _markDirty = markDirty ?? (() => { });
            _saveNow = saveNow ?? (() => true);
            _saves = saves ?? (() => 0);
        }

        private readonly record struct ShowRequest(int PadIndex, string Frames, int FrameMs, int Repeat);

        /// <summary>Shows waiting for the next tick.</summary>
        internal static int PendingShows => _requests.Count;

        /// <summary>The macro loops' Show Dreamcast Screen, from the poll
        /// thread: queued, and started on the next tick for the Dreamcast pads
        /// that feed the slot. A request no port is open for has no pad to
        /// play on, and one queued as the last port closes is dropped by the
        /// next tick.</summary>
        public static void RequestShow(int padIndex, string frames, int frameMs, int repeat)
        {
            if (BlissBoxRuntime.Ports.Length == 0) return;
            if (_requests.Count >= MaxQueuedShows) _requests.TryDequeue(out _);
            _requests.Enqueue(new ShowRequest(padIndex, frames, frameMs, repeat));
        }

        public BlissBoxPortData Get(Guid device)
        {
            string key = device.ToString("D");
            foreach (var port in _settings.BlissBoxPorts)
                if (string.Equals(port.Device, key, StringComparison.OrdinalIgnoreCase)) return port;
            return null;
        }

        /// <summary>Changes a port's choices, drops an entry left at every
        /// default, and marks the settings to save.</summary>
        public void Update(Guid device, Action<BlissBoxPortData> change)
        {
            var port = Get(device);
            if (port == null)
            {
                port = new BlissBoxPortData { Device = device.ToString("D") };
                _settings.BlissBoxPorts.Add(port);
            }
            change(port);
            if (port.IsEmpty) _settings.BlissBoxPorts.Remove(port);
            _nextTick = 0;
            _markDirty();
        }

        /// <summary>Drops a port's choices: its player number changed, so it
        /// returns as a new device and they would never be read again.</summary>
        public void Remove(Guid device)
        {
            var port = Get(device);
            if (port == null) return;
            _settings.BlissBoxPorts.Remove(port);
            _markDirty();
        }

        public void Tick()
        {
            long now = Environment.TickCount64;
            if (now < _nextTick) return;
            _nextTick = now + TickMs;

            while (_requests.TryDequeue(out var request)) StartShow(request, now);

            var ports = BlissBoxRuntime.Ports;
            Prune(ports);
            foreach (var port in ports)
            {
                var session = port.Session;
                var data = Get(port.InstanceGuid);
                session.NativeArrows = data?.NativeArrows == true;

                // A port a player change replaced answers until its channel
                // closes, and a show on it would copy the adapter's picture
                // into the entry the change dropped.
                if (port.Replaced || !TrackPad(port, session.LiveInfo, now))
                {
                    _shows.Remove(port);
                    session.SetScreen(null);
                    continue;
                }

                // Nothing is written before the adapter's own picture is known.
                var stored = session.StoredScreen;
                if (stored == null) continue;

                byte[] image = ShowFrame(port, stored, now) ?? Compose(data, port, now);
                if (image == null)
                {
                    RestoreAdapterPicture(port, data, stored);
                    continue;
                }

                var wire = BlissBoxScreen.ToWire(image);
                if (!MayReplace(port.InstanceGuid, data, stored, wire)) continue;
                if (session.SetScreen(wire)) port.Wake();
            }
        }

        /// <summary>Play time's bookkeeping for one port, and whether a
        /// Dreamcast pad is in it. A pad seen keeps its start when it was
        /// last seen within <see cref="PlayTimeGraceMs"/>, else starts now. A
        /// pad not in the port leaves its record alone, so it keeps counting
        /// if it is back in time.</summary>
        internal bool TrackPad(BlissBoxPort port, BlissBoxInfo info, long now)
        {
            if (info == null || !BlissBoxControllers.HasScreen(info.Type)) return false;
            _attached[port] = (PlayStart(_attached.TryGetValue(port, out var record) ? record : null, now), now);
            return true;
        }

        /// <summary>When the pad in a port started its play time, or now when
        /// none is on record.</summary>
        internal long PlayTimeStart(BlissBoxPort port, long now)
            => port != null && _attached.TryGetValue(port, out var record) ? record.Start : now;

        /// <summary>Whether a port may be given <paramref name="wire"/> in
        /// place of the picture it holds. Before the first such picture the
        /// adapter's own is copied into the port's settings and saved at once,
        /// not after the autosave's quiet time, which a crash, a kill or a
        /// reload could beat. Until a save has written the copy to the settings
        /// file, the port keeps its own picture: the copy would be the only
        /// one.</summary>
        internal bool MayReplace(Guid device, BlissBoxPortData data, byte[] stored, byte[] wire)
        {
            if (wire.AsSpan().SequenceEqual(stored)) return true;
            if (string.IsNullOrEmpty(data?.AdapterPicture))
            {
                Update(device, d => d.AdapterPicture = Convert.ToBase64String(stored));
                // Held before the save, so a save that throws leaves the hold.
                _unsavedCopies[device] = _saves();
                if (_saveNow()) _unsavedCopies.Remove(device);
            }
            if (!_unsavedCopies.TryGetValue(device, out int saves)) return true;
            // A later save, the autosave's own retry among them, carries it.
            if (_saves() == saves) return false;
            _unsavedCopies.Remove(device);
            return true;
        }

        /// <summary>The start of a pad's play time when it is seen now: the
        /// start on record when the pad was last seen within
        /// <see cref="PlayTimeGraceMs"/>, else now.</summary>
        internal static long PlayStart((long Start, long Seen)? record, long now)
            => record is { } r && now - r.Seen <= PlayTimeGraceMs ? r.Start : now;

        /// <summary>Drops the play-time starts and shows of port instances
        /// that have closed, a row that reconnected included.</summary>
        internal void Prune(BlissBoxPort[] ports)
        {
            List<BlissBoxPort> gone = null;
            foreach (var port in _attached.Keys)
                if (Array.IndexOf(ports, port) < 0) (gone ??= new List<BlissBoxPort>()).Add(port);
            if (gone != null) foreach (var port in gone) _attached.Remove(port);

            List<BlissBoxPort> ended = null;
            foreach (var port in _shows.Keys)
                if (Array.IndexOf(ports, port) < 0) (ended ??= new List<BlissBoxPort>()).Add(port);
            if (ended != null) foreach (var port in ended) _shows.Remove(port);
        }

        /// <summary>Plays a show on this port instance.</summary>
        internal void ShowOn(BlissBoxPort port, DreamcastShow show) => _shows[port] = show;

        internal bool HasShow(BlissBoxPort port) => _shows.ContainsKey(port);

        /// <summary>The engine stopped: its poll thread and ports are gone,
        /// and a show or a request from before would otherwise play on the
        /// next start's ports. The copies of the adapters' pictures stay
        /// held.</summary>
        public void Reset()
        {
            _shows.Clear();
            _attached.Clear();
            DropRequests();
        }

        /// <summary>Drops every queued show, for an engine stop before any
        /// tick made the service.</summary>
        internal static void DropRequests()
        {
            while (_requests.TryDequeue(out _)) { }
        }

        /// <summary>Adapter mode: writes back the picture PadForge found, then
        /// forgets it once the adapter holds it again.</summary>
        private void RestoreAdapterPicture(BlissBoxPort port, BlissBoxPortData data, byte[] stored)
        {
            if (data != null && TryDecode(data.AdapterPicture, out var original))
            {
                if (original.AsSpan().SequenceEqual(stored))
                {
                    Update(port.InstanceGuid, d => d.AdapterPicture = null);
                    port.Session.SetScreen(null);
                }
                else if (port.Session.SetScreen(original)) port.Wake();
                return;
            }
            port.Session.SetScreen(null);
        }

        private void StartShow(ShowRequest request, long now)
        {
            var frames = DecodeFrames(request.Frames);
            if (frames.Count == 0) return;
            var devices = new HashSet<Guid>();
            var settings = SettingsManager.UserSettings;
            if (settings != null)
            {
                lock (settings.SyncRoot)
                    foreach (var us in settings.Items)
                        if (us != null && us.MapTo == request.PadIndex) devices.Add(us.InstanceGuid);
            }
            foreach (var port in BlissBoxRuntime.Ports)
            {
                if (!devices.Contains(port.InstanceGuid)) continue;
                var info = port.Session.LiveInfo;
                if (info == null || !BlissBoxControllers.HasScreen(info.Type)) continue;
                ShowOn(port, new DreamcastShow(frames, request.FrameMs, request.Repeat, now));
            }
        }

        /// <summary>The frame a macro show puts on this port now, or null once
        /// it has played every frame.</summary>
        private byte[] ShowFrame(BlissBoxPort port, byte[] stored, long now)
        {
            if (!_shows.TryGetValue(port, out var show)) return null;
            var frame = show.Frame(stored, now);
            if (frame == null) _shows.Remove(port);
            return frame;
        }

        /// <summary>The picture a port's mode shows now, in image order, or
        /// null for the adapter's own.</summary>
        internal byte[] Compose(BlissBoxPortData data, BlissBoxPort port, long now)
        {
            var mode = data?.ScreenMode ?? DreamcastScreenMode.Adapter;
            switch (mode)
            {
                case DreamcastScreenMode.ProfileName:
                    return Text(ActiveProfileName());
                case DreamcastScreenMode.ProfileNumber:
                    return Text(ActiveProfileNumber().ToString(CultureInfo.InvariantCulture));
                case DreamcastScreenMode.ProfilePicture:
                    return TryDecode(ActiveProfilePicture(), out var picture) ? picture : Text(ActiveProfileName());
                case DreamcastScreenMode.Clock:
                    return Text(DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture));
                case DreamcastScreenMode.PlayTime:
                {
                    long since = PlayTimeStart(port, now);
                    var played = TimeSpan.FromMilliseconds(Math.Max(0, now - since));
                    return Text(FormatPlayTime(played));
                }
                case DreamcastScreenMode.Picture:
                    return TryDecode(data?.Picture, out var chosen) ? chosen : null;
                default:
                    return null;
            }
        }

        internal static string FormatPlayTime(TimeSpan played)
            => string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}", (int)played.TotalHours, played.Minutes);

        private byte[] Text(string text)
        {
            text ??= string.Empty;
            if (_textCache.TryGetValue(text, out var image)) return image;
            if (_textCache.Count >= TextCacheLimit) _textCache.Clear();
            image = RenderText(text);
            _textCache[text] = image;
            return image;
        }

        /// <summary>The Default profile's localized name, or the active named
        /// profile's.</summary>
        internal static string ActiveProfileName()
        {
            string id = SettingsManager.ActiveProfileId;
            if (string.IsNullOrEmpty(id)) return Strings.Instance.Profile_Default;
            return SettingsManager.Profiles.Find(p => p.Id == id)?.Name ?? Strings.Instance.Profile_Default;
        }

        /// <summary>The Default profile is 0, and each named profile its place
        /// in the list, from 1.</summary>
        internal static int ActiveProfileNumber()
        {
            string id = SettingsManager.ActiveProfileId;
            if (string.IsNullOrEmpty(id)) return 0;
            int index = SettingsManager.Profiles.FindIndex(p => p.Id == id);
            return index < 0 ? 0 : index + 1;
        }

        private string ActiveProfilePicture()
        {
            string id = SettingsManager.ActiveProfileId;
            if (string.IsNullOrEmpty(id)) return _settings.DefaultProfileDreamcastPicture;
            return SettingsManager.Profiles.Find(p => p.Id == id)?.DreamcastPicture;
        }

        /// <summary>The picture a port's dialog previews for a mode: what the
        /// mode would show, or the adapter's own.</summary>
        internal byte[] Preview(BlissBoxPort port, BlissBoxPortData data)
        {
            long now = Environment.TickCount64;
            var image = Compose(data, port, now);
            if (image != null) return image;
            if (data != null && TryDecode(data.AdapterPicture, out var original)) return BlissBoxScreen.FromWire(original);
            var stored = port?.Session.StoredScreen;
            return stored == null ? null : BlissBoxScreen.FromWire(stored);
        }

        // ─────────────────────────────────────────────
        //  Pictures
        // ─────────────────────────────────────────────

        public static string Encode(byte[] image) => image == null ? null : Convert.ToBase64String(image);

        public static bool TryDecode(string base64, out byte[] image)
        {
            image = null;
            if (string.IsNullOrEmpty(base64)) return false;
            try { image = Convert.FromBase64String(base64); }
            catch (FormatException) { return false; }
            return image.Length == BlissBoxScreen.Bytes;
        }

        /// <summary>Up to eight pictures from a Show Dreamcast Screen action.</summary>
        public static List<byte[]> DecodeFrames(string frames)
        {
            var list = new List<byte[]>();
            if (string.IsNullOrEmpty(frames)) return list;
            foreach (var part in frames.Split(','))
            {
                if (list.Count == MaxFrames) break;
                if (TryDecode(part.Trim(), out var image)) list.Add(image);
            }
            return list;
        }

        public static string EncodeFrames(IEnumerable<byte[]> frames)
        {
            var parts = new List<string>();
            foreach (var frame in frames)
            {
                if (parts.Count == MaxFrames) break;
                if (frame != null && frame.Length == BlissBoxScreen.Bytes) parts.Add(Convert.ToBase64String(frame));
            }
            return string.Join(",", parts);
        }

        /// <summary>A picture from a file: a VMU Animator .lcd, an
        /// ICONDATA_VMS .vms, or any image WPF decodes (BMP, PNG), fitted to
        /// 48 by 32 and cut to one bit a pixel. Null when an .lcd or .vms file
        /// holds no picture. Throws when the file cannot be read, or holds an
        /// image WPF cannot decode, which the callers report the same way.</summary>
        public static byte[] ImportPicture(string path)
        {
            byte[] file = File.ReadAllBytes(path);
            string extension = Path.GetExtension(path);
            if (string.Equals(extension, ".lcd", StringComparison.OrdinalIgnoreCase)) return BlissBoxScreen.FromLcd(file);
            if (string.Equals(extension, ".vms", StringComparison.OrdinalIgnoreCase)) return BlissBoxScreen.FromVms(file);
            using var stream = new MemoryStream(file);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return Threshold(Fit(frame));
        }

        /// <summary>Draws a bitmap centered on a white 48 by 32 field, scaled
        /// down to fit and never up past its own size.</summary>
        private static BitmapSource Fit(BitmapSource source)
        {
            double scale = Math.Min(1.0, Math.Min(
                (double)BlissBoxScreen.Width / source.PixelWidth, (double)BlissBoxScreen.Height / source.PixelHeight));
            double width = source.PixelWidth * scale, height = source.PixelHeight * scale;
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, BlissBoxScreen.Width, BlissBoxScreen.Height));
                dc.DrawImage(source, new Rect((BlissBoxScreen.Width - width) / 2, (BlissBoxScreen.Height - height) / 2, width, height));
            }
            var target = new RenderTargetBitmap(BlissBoxScreen.Width, BlissBoxScreen.Height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            return target;
        }

        /// <summary>One bit a pixel: dark where the luminance falls below
        /// half. The field is white, so transparency reads light.</summary>
        private static byte[] Threshold(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = BlissBoxScreen.Width * 4;
            var pixels = new byte[stride * BlissBoxScreen.Height];
            converted.CopyPixels(pixels, stride, 0);
            var image = new byte[BlissBoxScreen.Bytes];
            for (int y = 0; y < BlissBoxScreen.Height; y++)
            {
                for (int x = 0; x < BlissBoxScreen.Width; x++)
                {
                    int i = y * stride + x * 4;
                    int luminance = (pixels[i + 2] * 299 + pixels[i + 1] * 587 + pixels[i] * 114) / 1000;
                    if (luminance < 128) BlissBoxScreen.SetDark(image, x, y, true);
                }
            }
            return image;
        }

        /// <summary>Text drawn aliased as large as fits the screen, on up to
        /// as many lines as the words need, centered.</summary>
        public static byte[] RenderText(string text)
        {
            text = (text ?? string.Empty).Trim();
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            FormattedText fitted = null;
            for (double size = BlissBoxScreen.Height; size >= 6; size -= 1)
            {
                var candidate = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    typeface, size, Brushes.Black, 1.0)
                {
                    MaxTextWidth = BlissBoxScreen.Width,
                    TextAlignment = TextAlignment.Center,
                };
                fitted = candidate;
                if (candidate.MinWidth <= BlissBoxScreen.Width && candidate.Height <= BlissBoxScreen.Height) break;
            }
            var visual = new DrawingVisual();
            TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Aliased);
            TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
            RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, BlissBoxScreen.Width, BlissBoxScreen.Height));
                if (fitted != null && text.Length > 0)
                    dc.DrawText(fitted, new Point(0, Math.Max(0, (BlissBoxScreen.Height - fitted.Height) / 2)));
            }
            var target = new RenderTargetBitmap(BlissBoxScreen.Width, BlissBoxScreen.Height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            return Threshold(target);
        }

        /// <summary>A picture as the VMU shows it, dark pixels on the LCD's
        /// pale green, for previews scaled up with nearest-neighbor.</summary>
        public static BitmapSource ToBitmap(byte[] image)
        {
            const uint dark = 0xFF1F2A1C, light = 0xFFBFD0B4;
            var pixels = new uint[BlissBoxScreen.Width * BlissBoxScreen.Height];
            for (int y = 0; y < BlissBoxScreen.Height; y++)
                for (int x = 0; x < BlissBoxScreen.Width; x++)
                    pixels[y * BlissBoxScreen.Width + x] =
                        image != null && image.Length == BlissBoxScreen.Bytes && BlissBoxScreen.IsDark(image, x, y) ? dark : light;
            var bitmap = BitmapSource.Create(BlissBoxScreen.Width, BlissBoxScreen.Height, 96, 96,
                PixelFormats.Bgra32, null, pixels, BlissBoxScreen.Width * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }

    /// <summary>
    /// A Show Dreamcast Screen action playing on one port (#469). Each frame
    /// lasts its frame time from the moment the adapter holds it, not from a
    /// fixed schedule: the EEPROM guard can hold a write back past a frame's
    /// start, and a schedule would then drop frames. A frame that has not
    /// reached the adapter after <see cref="DeliveryLimitMs"/> counts from
    /// when it became current, so a port that refuses writes cannot hold a
    /// show forever.
    /// </summary>
    internal sealed class DreamcastShow
    {
        public const int DeliveryLimitMs = 5000;

        private readonly List<byte[]> _frames;
        private readonly List<byte[]> _wires;
        private readonly int _frameMs;
        private readonly int _total;
        private int _index;
        private long _current;
        private long _shownAt = -1;

        public DreamcastShow(List<byte[]> frames, int frameMs, int repeat, long now)
        {
            _frames = frames ?? throw new ArgumentNullException(nameof(frames));
            if (frames.Count == 0) throw new ArgumentException("A show needs a picture.", nameof(frames));
            _wires = frames.ConvertAll(frame => BlissBoxScreen.ToWire(frame));
            _frameMs = Math.Max(DreamcastScreenService.MinFrameMs, frameMs);
            _total = frames.Count * Math.Max(1, repeat);
            _current = now;
        }

        /// <summary>The picture to show, in image order, given the one the
        /// adapter holds now, or null once every frame has had its time.</summary>
        public byte[] Frame(ReadOnlySpan<byte> stored, long now)
        {
            int frame = _index % _frames.Count;
            if (_shownAt < 0)
            {
                if (stored.SequenceEqual(_wires[frame])) _shownAt = now;
                else if (now - _current >= DeliveryLimitMs) _shownAt = _current;
                else return _frames[frame];
            }
            if (now - _shownAt < _frameMs) return _frames[frame];
            if (++_index >= _total) return null;
            _current = now;
            _shownAt = -1;
            frame = _index % _frames.Count;
            if (stored.SequenceEqual(_wires[frame])) _shownAt = now;
            return _frames[frame];
        }
    }
}
