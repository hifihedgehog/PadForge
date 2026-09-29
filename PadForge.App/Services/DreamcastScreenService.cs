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
    /// Values are persisted, so they stay as numbered.</summary>
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
        /// <summary>Hours and minutes since the pad attached.</summary>
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

        /// <summary>The loaded list with unreadable and repeated devices
        /// dropped, the first entry for a device kept.</summary>
        internal static List<BlissBoxPortData> Normalize(BlissBoxPortData[] ports)
        {
            var list = new List<BlissBoxPortData>();
            if (ports == null) return list;
            var seen = new HashSet<Guid>();
            foreach (var port in ports)
            {
                if (port == null || !Guid.TryParse(port.Device, out var guid) || !seen.Add(guid)) continue;
                if (!Enum.IsDefined(typeof(DreamcastScreenMode), port.ScreenMode)) port.ScreenMode = DreamcastScreenMode.Adapter;
                port.Device = guid.ToString("D");
                list.Add(port);
            }
            return list;
        }
    }

    /// <summary>
    /// The VMU screens of the Dreamcast pads in Bliss-Box ports (issue #469),
    /// and the ports' other choices. Ticked from the dashboard cadence on the
    /// UI thread, where WPF draws the text. Each tick decides the picture a
    /// port should show and hands it to the port's session, which writes it
    /// only when it differs from the one the adapter holds and no sooner than
    /// a second after the last write (the EEPROM guard).
    ///
    /// <para>Before PadForge first replaces a picture, the adapter's own is
    /// kept, so Adapter mode can write it back. A Show Dreamcast Screen macro
    /// plays over whatever the mode shows and hands the screen back when it
    /// ends.</para>
    /// </summary>
    public sealed class DreamcastScreenService
    {
        public const int MaxFrames = 8;
        public const int MinFrameMs = BlissBoxSession.ScreenIntervalMs;
        private const int TickMs = 250;
        private const int TextCacheLimit = 64;

        private static readonly ConcurrentQueue<ShowRequest> _requests = new();

        private readonly ViewModels.SettingsViewModel _settings;
        private readonly Action _markDirty;
        private readonly Dictionary<Guid, long> _attached = new();
        private readonly Dictionary<Guid, Show> _shows = new();
        private readonly Dictionary<string, byte[]> _textCache = new(StringComparer.Ordinal);
        private long _nextTick;

        public DreamcastScreenService(ViewModels.SettingsViewModel settings, Action markDirty)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _markDirty = markDirty ?? (() => { });
        }

        private readonly record struct ShowRequest(int PadIndex, string Frames, int FrameMs, int Repeat);

        private sealed class Show
        {
            public List<byte[]> Frames;
            public int FrameMs;
            public int Repeat;
            public long Start;
        }

        /// <summary>The macro loops' Show Dreamcast Screen, from the poll
        /// thread: queued, and started on the next tick for the Dreamcast pads
        /// that feed the slot.</summary>
        public static void RequestShow(int padIndex, string frames, int frameMs, int repeat)
            => _requests.Enqueue(new ShowRequest(padIndex, frames, frameMs, repeat));

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

        public void Tick()
        {
            long now = Environment.TickCount64;
            if (now < _nextTick) return;
            _nextTick = now + TickMs;

            while (_requests.TryDequeue(out var request)) StartShow(request, now);

            foreach (var port in BlissBoxRuntime.Ports)
            {
                var session = port.Session;
                var data = Get(port.InstanceGuid);
                session.NativeArrows = data?.NativeArrows == true;

                var info = session.LiveInfo;
                if (info == null || !BlissBoxControllers.HasScreen(info.Type))
                {
                    _attached.Remove(port.InstanceGuid);
                    _shows.Remove(port.InstanceGuid);
                    session.SetScreen(null);
                    continue;
                }
                if (!_attached.ContainsKey(port.InstanceGuid)) _attached[port.InstanceGuid] = now;

                // Nothing is written before the adapter's own picture is known.
                var stored = session.StoredScreen;
                if (stored == null) continue;

                byte[] image = ShowFrame(port.InstanceGuid, now) ?? Compose(data, port.InstanceGuid, now);
                if (image == null)
                {
                    RestoreAdapterPicture(port, data, stored);
                    continue;
                }

                var wire = BlissBoxScreen.ToWire(image);
                if (!wire.AsSpan().SequenceEqual(stored) && string.IsNullOrEmpty(data?.AdapterPicture))
                    Update(port.InstanceGuid, d => d.AdapterPicture = Convert.ToBase64String(stored));
                session.SetScreen(wire);
            }
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
                else port.Session.SetScreen(original);
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
                _shows[port.InstanceGuid] = new Show
                {
                    Frames = frames,
                    FrameMs = Math.Max(MinFrameMs, request.FrameMs),
                    Repeat = Math.Max(1, request.Repeat),
                    Start = now,
                };
            }
        }

        /// <summary>The frame a macro show puts on this port now, or null once
        /// it has played its repeats.</summary>
        private byte[] ShowFrame(Guid device, long now)
        {
            if (!_shows.TryGetValue(device, out var show)) return null;
            long elapsed = now - show.Start;
            long total = (long)show.FrameMs * show.Frames.Count * show.Repeat;
            if (elapsed >= total)
            {
                _shows.Remove(device);
                return null;
            }
            return show.Frames[(int)(elapsed / show.FrameMs % show.Frames.Count)];
        }

        /// <summary>The picture a port's mode shows now, in image order, or
        /// null for the adapter's own.</summary>
        internal byte[] Compose(BlissBoxPortData data, Guid device, long now)
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
                    long since = _attached.TryGetValue(device, out var start) ? start : now;
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
            var image = Compose(data, port?.InstanceGuid ?? Guid.Empty, now);
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
        /// 48 by 32 and cut to one bit a pixel. Null when the file holds no
        /// picture.</summary>
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
}
