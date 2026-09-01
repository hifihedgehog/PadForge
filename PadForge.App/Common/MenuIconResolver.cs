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
                img.DecodePixelWidth = 96;
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
                img.DecodePixelWidth = 96;
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
                    // The source art is 256px and the overlay renders it
                    // at glyph size, so decode small and keep the cache
                    // cheap.
                    img.DecodePixelWidth = 96;
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
