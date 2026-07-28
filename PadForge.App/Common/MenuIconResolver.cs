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

        /// <summary>Resolves an authored icon name to a cached, frozen
        /// image, or null (invalid name shape, no Steam install, or no
        /// such file). Never throws: any load failure caches as a miss.</summary>
        public static ImageSource Resolve(string iconName)
        {
            if (!MenuItemDefinition.IsValidIconName(iconName)) return null;
            lock (Sync)
            {
                if (Cache.TryGetValue(iconName, out var cached)) return cached;
                var loaded = Load(iconName);
                Cache[iconName] = loaded;
                return loaded;
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
