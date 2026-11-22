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
        /// theme's picture, of 255.</summary>
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
        private static string _watchedPath;
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
        /// null when the file holds no theme. Listing a file that is
        /// already listed reads it again.</summary>
        internal static ThemeDefinition Register(string filePath)
        {
            var probe = ThemeFile.Read(filePath);
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
            ThemeDefinition theme = Effective();
            // A picked file that is gone or is no theme: Dark, until it is back.
            bool faulted = theme != null && theme.Fault != ThemeFault.None;
            // Windows' own high contrast colors stand over every theme.
            bool themed = theme != null && !faulted && !SystemParameters.HighContrast;

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
                else if (faulted || _stockIndex == 2) ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                else if (_stockIndex == 1) ApplicationThemeManager.Apply(ApplicationTheme.Light);
                else ApplicationThemeManager.ApplySystemTheme();

                // Theme applies re-derive the accent from the system color,
                // so the stock look's own tables go on first (#175).
                EmberTheme.ApplyAccent();

                if (themed)
                {
                    Action undoEngine = new ThemeEngine(theme.Seeds).Apply(app);
                    Action undoCard = null;
                    if (theme.Picture != null) undoCard = LetPictureThroughEngineCard(app);
                    _undo = () =>
                    {
                        undoCard?.Invoke();
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

        /// <summary>Home's engine card goes translucent while a picture is
        /// up, so the picture shows through it.</summary>
        private static Action LetPictureThroughEngineCard(Application app)
        {
            const string key = EmberTheme.HomeEngineCardKey;
            object before = app.Resources.Contains(key) ? app.Resources[key] : null;
            Color card = app.TryFindResource("CardBackgroundFillColorDefaultBrush") is SolidColorBrush fill
                ? fill.Color : Color.FromRgb(0x12, 0x19, 0x25);
            var through = new SolidColorBrush(Color.FromArgb(PictureCardAlpha, card.R, card.G, card.B));
            through.Freeze();
            app.Resources[key] = through;
            return () =>
            {
                if (before != null) app.Resources[key] = before;
                else app.Resources.Remove(key);
            };
        }

        // ─────────────────────────────────────────────
        //  The picked file, watched
        // ─────────────────────────────────────────────

        /// <summary>Watches the picked theme file, so an edit shows the
        /// moment it is saved and a file that was gone shows when it is
        /// back. A file is read again even while another look stands over
        /// it.</summary>
        private static void Watch(ThemeDefinition theme)
        {
            string path = theme != null && theme.Kind == ThemeKind.File ? theme.Path : null;
            if (string.Equals(path, _watchedPath, StringComparison.OrdinalIgnoreCase)) return;
            _watcher?.Dispose();
            _watcher = null;
            _watchedPath = path;
            if (path == null) return;
            try
            {
                string folder = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
                var dispatcher = Dispatcher.CurrentDispatcher;
                var watcher = new FileSystemWatcher(folder, Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                void Changed(object sender, FileSystemEventArgs e) => dispatcher.BeginInvoke(new Action(ScheduleReload));
                watcher.Changed += Changed;
                watcher.Created += Changed;
                watcher.Deleted += Changed;
                watcher.Renamed += (s, e) => dispatcher.BeginInvoke(new Action(ScheduleReload));
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // A folder that cannot be watched: the theme shows as it was read.
                _watcher = null;
            }
        }

        // An editor saves in several writes. The file is read once they stop.
        private static void ScheduleReload()
        {
            if (_reloadTimer == null)
            {
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

        /// <summary>Reads a listed file again: after the builder saved it.</summary>
        internal static void Reload(string id)
        {
            var listed = _files.FirstOrDefault(f => string.Equals(f.Theme.Id, id, StringComparison.OrdinalIgnoreCase));
            if (listed == null) return;
            Read(listed);
            ListChanged?.Invoke(null, EventArgs.Empty);
            if (ReferenceEquals(Effective(), listed.Theme) || string.Equals(_selectedId, id, StringComparison.OrdinalIgnoreCase)) Reapply();
        }

        /// <summary>The colors a theme's tile is drawn in.</summary>
        internal static ThemeSwatch SwatchOf(ThemeDefinition theme)
            => theme == null || theme.Fault != ThemeFault.None ? ThemeEngine.StockSwatch(true) : new ThemeEngine(theme.Seeds).Swatch();
    }
}
