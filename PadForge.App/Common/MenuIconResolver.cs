using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PadForge.Engine.Menus;

namespace PadForge.Common
{
    /// <summary>
    /// Display-time resolver for menu cell icons (#9, translator v21).
    /// Imported Workshop menus carry Steam's authored icon names
    /// (MenuItemDefinition.Icon, e.g. "ghost_050_menu_0030.png"). The
    /// files themselves are the local Steam client's own art and are
    /// never copied or shipped. This resolves a name to a frozen, cached
    /// BitmapImage from beneath the Steam install, and returns null when
    /// Steam is absent, the file is absent, or the name fails the shared
    /// shape gate, in which case callers keep the text-label rendering.
    ///
    /// Grounding (local Steam client census, 2026-07-18): the touch-menu
    /// glyph set lives complete under
    /// tenfoot\resource\images\library\controller\binding_icons (460
    /// files, every corpus-referenced name present), while steamui\
    /// images\controller carries a partial mirror and is probed second.
    /// </summary>
    public static class MenuIconResolver
    {
        private static readonly object Sync = new();

        /// <summary>Decode width for every icon source (#413). Steam ships its
        /// binding icons at 256, and a cell can now ask for up to 200% of the
        /// menu's icon box, which at 400% menu scale is 240 DIP. 96 was picked
        /// when the overlay only ever drew a glyph-sized box, and scaling that
        /// decode up softened visibly. One constant, so the three loaders
        /// cannot drift apart.</summary>
        private const int IconDecodePixelWidth = 256;

        /// <summary>Name -> frozen image, misses cached as null so a menu
        /// rebuild never re-probes the disk for a known-absent file.</summary>
        private static readonly Dictionary<string, BitmapImage> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        static MenuIconResolver()
        {
            // #390: a pack registration, rename, or removal changes what
            // pficon:// references resolve to, and misses are cached, so
            // the whole cache drops on any registry change.
            IconPackageManager.RegistryChanged += (_, __) =>
            {
                lock (Sync) Cache.Clear();
            };
        }

        private static string _steamRoot;
        private static bool _steamRootProbed;

        /// <summary>Test seam: overrides the registry-derived Steam root.
        /// Null reverts to the registry probe.</summary>
        internal static string SteamRootOverride
        {
            get { lock (Sync) return _rootOverride; }
            set
            {
                lock (Sync)
                {
                    _rootOverride = value;
                    _steamRootProbed = false;
                    Cache.Clear();
                }
            }
        }
        private static string _rootOverride;

        /// <summary>Icon directories beneath the Steam root, first hit
        /// wins. The tenfoot set is the complete one.</summary>
        private static readonly string[] IconSubdirs =
        {
            Path.Combine("tenfoot", "resource", "images", "library", "controller", "binding_icons"),
            Path.Combine("steamui", "images", "controller"),
        };

        /// <summary>Resolves an authored icon reference to a cached,
        /// frozen image, or null. Three forms (#390):
        /// a <c>pficon://Package/entry</c> pack reference, a loose image
        /// file path (exe-relative or absolute), or a bare Steam
        /// binding-icon name resolved under the Steam install. Never
        /// throws: any load failure caches as a miss.</summary>
        public static ImageSource Resolve(string iconName)
        {
            if (string.IsNullOrEmpty(iconName)) return null;
            bool packRef = IconPackageManager.IsPackageRef(iconName);
            bool loosePath = !packRef && IsLooseImagePath(iconName);
            if (!packRef && !loosePath && !MenuItemDefinition.IsValidIconName(iconName)) return null;
            lock (Sync)
            {
                if (Cache.TryGetValue(iconName, out var cached)) return cached;
                var loaded = packRef ? LoadFromPack(iconName)
                    : loosePath ? LoadFromFile(IconPackageManager.ResolvePath(iconName))
                    : Load(iconName);
                Cache[iconName] = loaded;
                return loaded;
            }
        }

        /// <summary>An emoji or other single character stored as the icon
        /// itself (#471), the form shift layer icons have always used. One
        /// text element under <see cref="System.Globalization.StringInfo"/>,
        /// so a flag, a skin tone or a ZWJ sequence counts as one while a
        /// package reference, a path or a Steam art name (a dozen elements or
        /// more) never does. Whitespace and control characters never count.
        /// Drawn as text: WPF's text stack has no color glyph path, so it
        /// draws in the brush it is given, while the phone's browser draws
        /// it in color.</summary>
        public static bool IsGlyph(string reference)
        {
            if (string.IsNullOrEmpty(reference) || reference.Length > 32) return false;
            if (char.IsWhiteSpace(reference[0]) || char.IsControl(reference[0])) return false;
            return new System.Globalization.StringInfo(reference).LengthInTextElements == 1;
        }

        /// <summary>A reference that names a picture rather than a glyph: a
        /// package entry or a loose image path. A layer icon of this form that
        /// no longer resolves falls back to the default glyph instead of
        /// showing its path as text.</summary>
        public static bool IsImageReference(string reference)
            => !string.IsNullOrEmpty(reference)
               && (IconPackageManager.IsPackageRef(reference) || IsLooseImagePath(reference));

        /// <summary>How a shift layer icon draws (#471): the picture when the
        /// reference names one that resolves, else the glyph. Anything that is
        /// neither an emoji nor a picture that resolves, an empty reference
        /// included, draws the default ⇧, so a path never shows as text. The
        /// layer flyout and the layer dialog both draw through this.</summary>
        public static ImageSource ResolveLayerIcon(string icon, out string glyph)
        {
            icon ??= "";
            bool isGlyph = IsGlyph(icon);
            glyph = isGlyph ? icon : "\u21E7";
            return isGlyph ? null : Resolve(icon);
        }

        /// <summary>A loose image path: carries a directory separator or
        /// a drive colon (which the Steam-name gate rejects) and one of
        /// the pack image extensions. Purely a shape test; existence is
        /// the loader's problem and a miss caches like any other.</summary>
        internal static bool IsLooseImagePath(string reference)
        {
            if (string.IsNullOrEmpty(reference) || reference.Length > 1024) return false;
            if (reference.IndexOf('/') < 0 && reference.IndexOf('\\') < 0 && reference.IndexOf(':') < 0)
                return false;
            string ext;
            try { ext = Path.GetExtension(reference); }
            catch (ArgumentException) { return false; }
            foreach (var e in IconPackageManager.ImageExtensions)
                if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Largest icon file read for the Web Menus page, the package
        /// reader's own per-icon bound.</summary>
        private const long MaxIconFileBytes = 16L * 1024 * 1024;

        /// <summary>The icon's own file bytes, for the Web Menus page (#471):
        /// a package entry, a loose image file or a Steam art name, found the
        /// way <see cref="Resolve"/> finds each one. Null when the reference
        /// is none of those or nothing readable is there. Never throws.</summary>
        internal static byte[] TryReadIconBytes(string reference)
        {
            if (string.IsNullOrEmpty(reference) || IsGlyph(reference)) return null;
            try
            {
                if (IconPackageManager.IsPackageRef(reference))
                    return IconPackageManager.TryReadIcon(reference);
                if (IsLooseImagePath(reference))
                    return ReadCapped(IconPackageManager.ResolvePath(reference));
                if (!MenuItemDefinition.IsValidIconName(reference)) return null;
                string root;
                lock (Sync) root = SteamRoot();
                if (string.IsNullOrEmpty(root)) return null;
                foreach (var subdir in IconSubdirs)
                {
                    var bytes = ReadCapped(Path.Combine(root, subdir, reference));
                    if (bytes != null) return bytes;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException or System.Security.SecurityException
                or PathTooLongException)
            {
            }
            return null;
        }

        private static byte[] ReadCapped(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxIconFileBytes) return null;
            return File.ReadAllBytes(info.FullName);
        }

        private static BitmapImage LoadFromPack(string iconRef)
        {
            byte[] bytes = IconPackageManager.TryReadIcon(iconRef);
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.StreamSource = new MemoryStream(bytes, writable: false);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = IconDecodePixelWidth;
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException
                or ArgumentException or InvalidOperationException
                or System.IO.FileFormatException)
            {
                return null;
            }
        }

        private static BitmapImage LoadFromFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = IconDecodePixelWidth;
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException or UriFormatException
                or InvalidOperationException or System.Security.SecurityException
                or System.IO.FileFormatException or PathTooLongException)
            {
                return null;
            }
        }

        private static BitmapImage Load(string iconName)
        {
            string root = SteamRoot();
            if (string.IsNullOrEmpty(root)) return null;
            foreach (var subdir in IconSubdirs)
            {
                string path = Path.Combine(root, subdir, iconName);
                try
                {
                    if (!File.Exists(path)) continue;
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.UriSource = new Uri(path, UriKind.Absolute);
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    // The source art is 256px, so decoding at its native
                    // size covers a 200% cell at 400% menu scale (240 DIP)
                    // up to 100% display scaling without upsampling (#413).
                    // Past that the decode is the art's ceiling either way.
                    img.DecodePixelWidth = IconDecodePixelWidth;
                    img.EndInit();
                    img.Freeze();
                    return img;
                }
                // FileFormatException is the one WPF's own decoder raises for a
                // truncated or corrupt image, which is exactly the case this
                // catch exists for, and it was the one type the filter did not
                // list. Since this method's contract is "never throws" and it
                // runs on the 30 Hz UI tick, the omission turned one bad PNG in
                // a Steam icon directory into an aborted tick.
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or NotSupportedException or ArgumentException or UriFormatException
                    or InvalidOperationException or System.Security.SecurityException
                    or System.IO.FileFormatException)
                {
                    // Unreadable or undecodable file: fall through to the
                    // next directory, then cache the miss.
                }
            }
            return null;
        }

        private static string SteamRoot()
        {
            if (_rootOverride != null) return _rootOverride;
            if (_steamRootProbed) return _steamRoot;
            _steamRootProbed = true;
            try
            {
                string root = PadForge.SteamWorkshop.Local.LocalWorkshopConfigStore
                    .GetSteamInstallPath();
                _steamRoot = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException
                or NotSupportedException or System.Security.SecurityException)
            {
                _steamRoot = null;
            }
            return _steamRoot;
        }
    }
}
