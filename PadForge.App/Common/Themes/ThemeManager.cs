using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace PadForge.Common.Themes
{
    /// <summary>
    /// The themes PadForge knows and the one place a look is put on (#591).
    ///
    /// There are three kinds beside PadForge's own System, Light and Dark:
    /// the starters inside PadForge, the theme files the user listed, and a
    /// Steam profile's game look. A theme file is listed where it is and
    /// nothing is copied, as <see cref="SoundPackageManager"/> lists a sound
    /// package: a path under PadForge's folder is stored relative, so
    /// PadForge, its settings and its themes move together.
    ///
    /// The look in force is, in this order: the theme the builder is working
    /// on, the active profile's game look, the theme the user picked. Every
    /// member runs on the UI thread.
    /// </summary>
    internal static class ThemeManager
    {
        internal const string StarterPrefix = "Starter:", FilePrefix = "File:", GamePrefix = "Game:";
        private const string ResourcePrefix = "PadForge.Themes.";

        /// <summary>How much of the engine card's fill stands over a
        /// theme's picture, of 255, where the card's text reads over the
        /// picture at that, and the least it takes otherwise
        /// (<see cref="EngineCardAlpha"/>).</summary>
        internal const byte PictureCardAlpha = 118;

        /// <summary>The starters, in the gallery's order.</summary>
        private static readonly string[] StarterSlugs =
        {
            "sonic-blue-blur", "sonic-night-run", "rings-the-one-ring", "rings-map-of-middle-earth",
            "potter-gryffindor", "potter-four-houses", "starwars-opening-crawl", "starwars-dark-side",
            "startrek-lcars", "startrek-starfleet",
        };

        private sealed class Listed
        {
            internal string Name;
            /// <summary>Relative when under PadForge's folder, absolute otherwise.</summary>
            internal string StoredPath;
            internal ThemeDefinition Theme;
        }

        private static List<ThemeDefinition> _starters;
        private static readonly List<Listed> _files = new();
        private static int _stockIndex;
        private static string _selectedId = string.Empty;
        private static ThemeDefinition _gameLook, _preview, _current;
        private static Action _undo;
        private static FileSystemWatcher _watcher;
        private static string _watchedPath, _watchedFolder;
        private static DispatcherTimer _reloadTimer;

        /// <summary>Raised after a look is put on.</summary>
        internal static event EventHandler Applied;

        /// <summary>Raised when the list of theme files changes or one of them is read again.</summary>
        internal static event EventHandler ListChanged;

        /// <summary>Raised when the user lists a file or takes one off the
        /// list: the settings have a change to save. Loading the saved list
        /// does not raise it.</summary>
        internal static event EventHandler ListEdited;

        /// <summary>The theme in force, or null under PadForge's own look.</summary>
        internal static ThemeDefinition Current => _current;

        /// <summary>The picture in force, or null.</summary>
        internal static BitmapSource Picture => _current?.Picture;

        /// <summary>The theme the user picked: empty for System, Light or Dark.</summary>
        internal static string SelectedId => _selectedId;

        /// <summary>PadForge's own look the user picked: 0 System, 1 Light, 2 Dark.</summary>
        internal static int StockIndex => _stockIndex;

        /// <summary>The active profile's game look, when one is in force over the pick.</summary>
        internal static ThemeDefinition GameLook => _gameLook;

        /// <summary>The starters, read from PadForge on first use.</summary>
        internal static IReadOnlyList<ThemeDefinition> Starters
        {
            get
            {
                if (_starters == null)
                {
                    var loaded = new List<ThemeDefinition>();
                    foreach (string slug in StarterSlugs)
                    {
                        var theme = ReadStarter(slug);
                        if (theme != null) loaded.Add(theme);
                    }
                    _starters = loaded;
                }
                return _starters;
            }
        }

        /// <summary>The listed theme files, in the order they were listed.</summary>
        internal static IReadOnlyList<ThemeDefinition> Files => _files.Select(f => f.Theme).ToList();

        private static ThemeDefinition ReadStarter(string slug)
        {
            byte[] data = StarterBytes(slug);
            var theme = data == null ? null : ThemeFile.Parse(data, slug);
            if (theme == null) return null;
            theme.Kind = ThemeKind.Starter;
            theme.Id = StarterPrefix + slug;
            return theme;
        }

        /// <summary>A starter's file as it is inside PadForge: what Save a Copy writes.</summary>
        internal static byte[] StarterBytes(string slug)
        {
            using var stream = typeof(ThemeManager).Assembly.GetManifestResourceStream(ResourcePrefix + slug + ThemeFile.Extension);
            if (stream == null) return null;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        // ─────────────────────────────────────────────
        //  The list of theme files
        // ─────────────────────────────────────────────

        /// <summary>Replaces the list from the saved settings and reads each file.</summary>
        internal static void LoadRegistry(IEnumerable<(string Name, string Path)> entries)
        {
            _files.Clear();
            if (entries != null)
                foreach (var (name, path) in entries)
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (_files.Any(f => string.Equals(f.StoredPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                    _files.Add(Read(new Listed { Name = name, StoredPath = path }));
                }
            ListChanged?.Invoke(null, EventArgs.Empty);
        }

        internal static List<(string Name, string Path)> SaveRegistry()
            => _files.Select(f => (f.Name, f.StoredPath)).ToList();

        private static Listed Read(Listed listed)
        {
            var theme = ThemeFile.Read(ResolvePath(listed.StoredPath), listed.Name);
            theme.Id = FilePrefix + listed.StoredPath;
            listed.Theme = theme;
            // The list keeps the name the file last gave, so a file that is gone still has one.
            if (theme.Fault == ThemeFault.None) listed.Name = theme.Name;
            return listed;
        }

        /// <summary>Lists a theme file where it is. Returns its theme, or
        /// null when the file cannot be listed. Listing a file that is
        /// already listed reads it again.</summary>
        internal static ThemeDefinition Register(string filePath) => Register(filePath, out _);

        /// <summary>Lists a theme file where it is, and says why when it
        /// cannot: the file is not there, cannot be read, or holds no theme.</summary>
        internal static ThemeDefinition Register(string filePath, out ThemeFault fault)
        {
            var probe = ThemeFile.Read(filePath);
            fault = probe.Fault;
            if (probe.Fault != ThemeFault.None) return null;
            string stored = MakeStoredPath(filePath);
            string full = Path.GetFullPath(filePath);
            var existing = _files.FirstOrDefault(f => string.Equals(ResolvePath(f.StoredPath), full, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new Listed { StoredPath = stored };
                _files.Add(existing);
            }
            existing.Name = probe.Name;
            existing.StoredPath = stored;
            probe.Id = FilePrefix + stored;
            existing.Theme = probe;
            ListChanged?.Invoke(null, EventArgs.Empty);
            ListEdited?.Invoke(null, EventArgs.Empty);
            return probe;
        }

        /// <summary>Takes a theme file off the list. The file stays where it is.</summary>
        internal static void Unregister(string id)
        {
            if (_files.RemoveAll(f => string.Equals(f.Theme.Id, id, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                ListChanged?.Invoke(null, EventArgs.Empty);
                ListEdited?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>The theme of that id among the starters and the listed files, or null.</summary>
        internal static ThemeDefinition Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id.StartsWith(StarterPrefix, StringComparison.Ordinal))
                return Starters.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
            return _files.FirstOrDefault(f => string.Equals(f.Theme.Id, id, StringComparison.OrdinalIgnoreCase))?.Theme;
        }

        private static string AppDir => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

        private static string MakeStoredPath(string filePath)
        {
            string full = Path.GetFullPath(filePath);
            string app = AppDir + Path.DirectorySeparatorChar;
            return full.StartsWith(app, StringComparison.OrdinalIgnoreCase) ? full.Substring(app.Length) : full;
        }

        internal static string ResolvePath(string storedPath)
        {
            if (string.IsNullOrEmpty(storedPath)) return storedPath;
            return Path.IsPathRooted(storedPath) ? storedPath : Path.Combine(AppDir, storedPath);
        }

        // ─────────────────────────────────────────────
        //  Putting a look on
        // ─────────────────────────────────────────────

        /// <summary>Puts on the look the settings name: the stock look of
        /// that index (0 System, 1 Light, 2 Dark), or the theme of that id
        /// over its own base.</summary>
        internal static void Apply(int stockIndex, string themeId)
        {
            _stockIndex = stockIndex;
            _selectedId = themeId ?? string.Empty;
            Reapply();
        }

        /// <summary>The active profile's game look goes on over the user's
        /// pick, and null takes it off.</summary>
        internal static void SetGameLook(ThemeDefinition look)
        {
            if (ReferenceEquals(_gameLook, look)) return;
            _gameLook = look;
            Reapply();
        }

        /// <summary>The theme the builder is working on goes on over
        /// everything, so each change shows on the real pages, and null
        /// takes it off.</summary>
        internal static void SetPreview(ThemeDefinition theme)
        {
            _preview = theme;
            Reapply();
        }

        /// <summary>Whether Windows' high contrast is on. A test gives its own.</summary>
        internal static Func<bool> HighContrastOn = () => SystemParameters.HighContrast;

        private static bool _followsHighContrast;

        /// <summary>Puts the look on again when Windows' high contrast is
        /// turned on or off while PadForge runs, so it stands over a theme
        /// from that moment and the theme is back when it goes.</summary>
        private static void FollowHighContrast()
        {
            if (_followsHighContrast) return;
            _followsHighContrast = true;
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        }

        private static void OnSystemParameterChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SystemParameters.HighContrast)) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(new Action(Reapply));
        }

        /// <summary>The theme in force, of the three that can be.</summary>
        private static ThemeDefinition Effective()
        {
            if (_preview != null) return _preview;
            if (_gameLook != null) return _gameLook;
            return Find(_selectedId);
        }

        private static void Reapply()
        {
            var app = Application.Current;
            if (app == null) return;
            FollowHighContrast();
            ThemeDefinition theme = Effective();
            // A picked file that is gone or is no theme: Dark, until it is
            // back. Under high contrast the contrast theme stands over that
            // Dark too.
            bool faulted = theme != null && theme.Fault != ThemeFault.None;
            // Windows' own high contrast colors stand over every look,
            // PadForge's Light and Dark included.
            bool contrast = HighContrastOn();
            bool themed = theme != null && !faulted && !contrast;

            // One redraw for the whole change: while the application's
            // dictionary is between BeginInit and EndInit a changed key
            // tells no one, and EndInit tells every element once.
            var res = app.Resources;
            res.BeginInit();
            try
            {
                _undo?.Invoke();
                _undo = null;

                if (themed) ApplicationThemeManager.Apply(theme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
                else if (contrast) ApplicationThemeManager.Apply(ApplicationTheme.HighContrast);
                else if (faulted || _stockIndex == 2) ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                else if (_stockIndex == 1) ApplicationThemeManager.Apply(ApplicationTheme.Light);
                else ApplicationThemeManager.ApplySystemTheme();

                // Theme applies re-derive the accent from the system color,
                // so the stock look's own tables go on first (#175).
                EmberTheme.ApplyAccent();

                if (themed)
                {
                    Action undoEngine = new ThemeEngine(theme.Seeds).Apply(app);
                    Action undoCard = SetEngineCard(app, theme.Picture, theme.Dark);
                    _undo = () =>
                    {
                        undoCard();
                        undoEngine();
                    };
                }
            }
            finally
            {
                res.EndInit();
            }

            _current = themed ? theme : null;
            // The picked file, also while a profile's look or the builder's
            // theme stands over it: its edit is there when they come off.
            Watch(Find(_selectedId));
            ThemeTokens.Instance.Refresh();
            Applied?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>The text on Home's engine card and the floor each holds,
        /// as the engine holds them on a card (ThemeEngine): the state, the
        /// line under it and the stats' numbers, their labels, the measured
        /// rate, and the rate's quiet mark while nothing is measured.</summary>
        private static readonly (string Key, double Floor)[] EngineCardText =
        {
            ("TextFillColorPrimaryBrush", 7.0), ("TextFillColorSecondaryBrush", 5.5), ("TextFillColorTertiaryBrush", 4.5),
            ("ColdBrush", ThemeEngine.TextContrast), ("QuietGlyphBrush", 3.0),
        };

        /// <summary>Home's engine card under a theme: the card's own fill in
        /// force (the engine leaves the key alone, since the card it copies
        /// is no stock color), and translucent while a picture is up, so the
        /// picture shows through it.</summary>
        private static Action SetEngineCard(Application app, BitmapSource picture, bool dark)
        {
            const string key = EmberTheme.HomeEngineCardKey;
            object before = ThemeEngine.IsOwn(app.Resources, key) ? app.Resources[key] : null;
            object inForce = app.TryFindResource("CardBackgroundFillColorDefaultBrush");
            if (picture == null)
            {
                if (inForce != null) app.Resources[key] = inForce;
            }
            else
            {
                Color card = inForce is SolidColorBrush fill ? fill.Color : Color.FromRgb(0x12, 0x19, 0x25);
                Color page = Solid(app.TryFindResource("ApplicationBackgroundBrush"), dark ? Colors.Black : Colors.White);
                var texts = new List<(Color, double)>();
                foreach (var (text, floor) in EngineCardText)
                    if (app.TryFindResource(text) is SolidColorBrush brush) texts.Add((brush.Color, floor));
                byte alpha = EngineCardAlpha(picture, Color.FromRgb(card.R, card.G, card.B), page, texts);
                var through = new SolidColorBrush(Color.FromArgb(alpha, card.R, card.G, card.B));
                through.Freeze();
                app.Resources[key] = through;
            }
            return () =>
            {
                if (before != null) app.Resources[key] = before;
                else app.Resources.Remove(key);
            };
        }

        private static Color Solid(object brush, Color ground)
            => brush is SolidColorBrush solid ? Mix(solid.Color, ground) : ground;

        /// <summary>A color laid over a solid ground, as WPF draws it.</summary>
        private static Color Mix(Color color, Color ground)
        {
            if (color.A == 255) return color;
            double a = color.A / 255.0;
            static byte Channel(byte top, byte under, double share) => (byte)Math.Round(top * share + under * (1 - share));
            return Color.FromRgb(Channel(color.R, ground.R, a), Channel(color.G, ground.G, a), Channel(color.B, ground.B, a));
        }

        /// <summary>
        /// How much of the engine card's fill stands over a picture, of 255:
        /// the approved <see cref="PictureCardAlpha"/> where every text on
        /// the card holds its floor over every pixel of the picture, else
        /// the least that does. A pixel is laid over the page first, then
        /// the card's fill over it. A text holds when its lightness is
        /// outside the range the grounds cover and it reads at its floor on
        /// the nearest of them, which covers every ground between.
        /// </summary>
        internal static byte EngineCardAlpha(BitmapSource picture, Color card, Color page, IReadOnlyList<(Color Text, double Floor)> texts)
        {
            if (picture == null || texts == null || texts.Count == 0) return PictureCardAlpha;
            var bgra = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight;
            var pixels = new byte[width * height * 4];
            bgra.CopyPixels(pixels, width * 4, 0);
            // Each pixel over the page, once.
            for (int i = 0; i < pixels.Length; i += 4)
            {
                Color under = Mix(Color.FromArgb(pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]), page);
                pixels[i] = under.B;
                pixels[i + 1] = under.G;
                pixels[i + 2] = under.R;
            }

            bool Holds(byte alpha)
            {
                // The light each channel gives at each value under the card's fill at this alpha.
                double a = alpha / 255.0;
                var red = new double[256];
                var green = new double[256];
                var blue = new double[256];
                for (int v = 0; v < 256; v++)
                {
                    red[v] = 0.2126 * ThemeColor.Linear((byte)Math.Round(card.R * a + v * (1 - a)));
                    green[v] = 0.7152 * ThemeColor.Linear((byte)Math.Round(card.G * a + v * (1 - a)));
                    blue[v] = 0.0722 * ThemeColor.Linear((byte)Math.Round(card.B * a + v * (1 - a)));
                }
                double darkest = double.MaxValue, lightest = double.MinValue;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    double light = red[pixels[i + 2]] + green[pixels[i + 1]] + blue[pixels[i]];
                    if (light < darkest) darkest = light;
                    if (light > lightest) lightest = light;
                }
                foreach (var (text, floor) in texts)
                {
                    double own = ThemeColor.Luminance(text);
                    if (own >= darkest && own <= lightest) return false;
                    double reads = own > lightest ? (own + 0.05) / (lightest + 0.05) : (darkest + 0.05) / (own + 0.05);
                    if (reads < floor) return false;
                }
                return true;
            }

            if (Holds(PictureCardAlpha)) return PictureCardAlpha;
            // More fill only brings every ground nearer the card's own, where
            // the text holds, so the least alpha that holds is found by halves.
            int low = PictureCardAlpha, high = 255;
            while (high - low > 1)
            {
                int mid = (low + high) / 2;
                if (Holds((byte)mid)) high = mid; else low = mid;
            }
            return (byte)high;
        }

        // ─────────────────────────────────────────────
        //  The picked file, watched
        // ─────────────────────────────────────────────

        /// <summary>Watches the picked theme file, so an edit shows the
        /// moment it is saved and a file that was gone shows when it is
        /// back. A file is read again even while another look stands over
        /// it. A path is recorded only once a watcher stands behind it, so
        /// a file whose folder was absent is tried again on the next look.</summary>
        private static void Watch(ThemeDefinition theme)
        {
            string path = theme != null && theme.Kind == ThemeKind.File ? theme.Path : null;
            string full = path == null ? null : FullPath(path);
            string folder = full == null ? null : WatchedFolder(full);
            // The same file watched from the same folder: nothing to change.
            if (_watcher != null && string.Equals(path, _watchedPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(folder, _watchedFolder, StringComparison.OrdinalIgnoreCase))
                return;
            _watcher?.Dispose();
            _watcher = null;
            _watchedPath = null;
            _watchedFolder = null;
            if (path == null || folder == null) return;
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                // The file's own folder, or, while that is gone, the nearest
                // folder above it that is there, with every folder under it:
                // the folder coming back is seen there.
                bool own = string.Equals(folder, Path.GetDirectoryName(full), StringComparison.OrdinalIgnoreCase);
                var watcher = own
                    ? new FileSystemWatcher(folder, Path.GetFileName(full))
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                    }
                    : new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,
                    };
                // Above the file's folder, only a change on the way to the file counts.
                bool OnTheWay(string changed) => own || (changed != null
                    && (string.Equals(full, changed, StringComparison.OrdinalIgnoreCase)
                        || full.StartsWith(changed.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
                void Changed(object sender, FileSystemEventArgs e)
                {
                    if (OnTheWay(e.FullPath)) dispatcher.BeginInvoke(new Action(ScheduleReload));
                }
                watcher.Changed += Changed;
                watcher.Created += Changed;
                watcher.Deleted += Changed;
                watcher.Renamed += (s, e) =>
                {
                    if (OnTheWay(e.FullPath) || OnTheWay(e.OldFullPath)) dispatcher.BeginInvoke(new Action(ScheduleReload));
                };
                watcher.Error += (s, e) => dispatcher.BeginInvoke(new Action(() => WatcherFailed(watcher)));
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
                _watchedPath = path;
                _watchedFolder = folder;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // A folder that cannot be watched: the theme shows as it was
                // read, and the next look put on tries again.
                _watcher = null;
                return;
            }
            // A file that was gone may be back already: one that came back
            // before this watcher was made sends it nothing.
            if (theme.Fault == ThemeFault.Missing && File.Exists(full)) ScheduleReload();
        }

        /// <summary>A watcher raises Error when its folder can no longer be
        /// read, as when the drive that holds it is taken out, and it stops
        /// watching. It is let go and the file read again, and the look put
        /// on after the read watches the file anew. A watcher that was
        /// replaced before its error came is left alone.</summary>
        private static void WatcherFailed(FileSystemWatcher watcher)
        {
            if (!ReferenceEquals(_watcher, watcher)) return;
            watcher.Dispose();
            _watcher = null;
            ScheduleReload();
        }

        /// <summary>The path in full, or null when it is no path.</summary>
        private static string FullPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
            {
                return null;
            }
        }

        /// <summary>The folder a file is watched from: its own, or the
        /// nearest folder above it that is there. Null when none is.</summary>
        private static string WatchedFolder(string full)
        {
            string folder = Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) folder = Path.GetDirectoryName(folder);
            return string.IsNullOrEmpty(folder) ? null : folder;
        }

        // An editor saves in several writes. The file is read once they stop.
        // The timer ticks only on the dispatcher that made it, and this runs
        // on the dispatcher that watched the file, so a timer made on another
        // one is made again here.
        private static void ScheduleReload()
        {
            if (_reloadTimer == null || _reloadTimer.Dispatcher != Dispatcher.CurrentDispatcher)
            {
                _reloadTimer?.Stop();
                _reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _reloadTimer.Tick += (s, e) =>
                {
                    _reloadTimer.Stop();
                    ReloadWatched();
                };
            }
            _reloadTimer.Stop();
            _reloadTimer.Start();
        }

        /// <summary>Reads the watched file again once its writes stop, and
        /// puts the look on again.</summary>
        private static void ReloadWatched()
        {
            string path = _watchedPath;
            if (path == null) return;
            var listed = _files.FirstOrDefault(f => string.Equals(ResolvePath(f.StoredPath), path, StringComparison.OrdinalIgnoreCase));
            if (listed == null) return;
            Read(listed);
            ListChanged?.Invoke(null, EventArgs.Empty);
            Reapply();
        }

        /// <summary>The colors a theme's tile is drawn in.</summary>
        internal static ThemeSwatch SwatchOf(ThemeDefinition theme)
            => theme == null || theme.Fault != ThemeFault.None ? ThemeEngine.StockSwatch(true) : new ThemeEngine(theme.Seeds).Swatch();
    }
}
