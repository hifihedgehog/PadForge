using System.Windows.Media.Imaging;

namespace PadForge.Views
{
    /// <summary>Loads WPF Resource bitmaps (the 2DModels PNGs) from the
    /// assembly manifest resource stream directly, bypassing the pack://
    /// URI scheme entirely, which throws "Part URI cannot end with a
    /// forward slash" on .NET 10 single-file publish. WPF Resource items
    /// are stored as lowercase paths under the assembly's
    /// "&lt;assemblyname&gt;.g.resources" stream. Shared by
    /// ControllerModel2DView (full overlay rig) and the PadPage lightbar
    /// preview (#175).</summary>
    internal static class EmbeddedBitmaps
    {
        /// <summary>Returns a frozen BitmapImage for a Resource path like
        /// "2DModels/DS4/DS4_V2_base.png", or null when the resource does
        /// not exist (callers degrade to an empty placeholder rather than
        /// crash).</summary>
        /// <summary>Decoded bitmaps, keyed by resource path. Every result is
        /// frozen and therefore safe to share, and the set is bounded by the
        /// art the app ships. Without this the mouse preview re-decoded ~10 MB
        /// of Bgra32 on each theme rebuild and the workshop preview decoded
        /// once per bound overlay inside a render pass.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapImage> s_cache
            = new(System.StringComparer.OrdinalIgnoreCase);

        internal static BitmapImage Load(string resourcePath)
        {
            if (resourcePath == null) return null;
            if (s_cache.TryGetValue(resourcePath, out var cached)) return cached;
            var loaded = LoadUncached(resourcePath);
            // Negative results are cached too: a missing resource stays missing
            // for the process, and callers degrade rather than retry per frame.
            s_cache[resourcePath] = loaded;
            return loaded;
        }

        private static BitmapImage LoadUncached(string resourcePath)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            // WPF resources live in "<assemblyname>.g.resources" and are keyed
            // by the forward-slash lowercase path.
            //
            // This used to build a DOTTED name and then undo it with
            // Replace('.', '/') for the lookup. That second replace hit the
            // extension dot too, so "assets/icons/foo.png" went in as
            // "assets/icons/foo/png" and the first lookup could never match
            // anything with a file extension. Every call fell through to what
            // was labelled a fallback and was in fact the only working path.
            // One lookup now, in the form the resource table actually uses.
            var rm = new System.Resources.ResourceManager(
                asm.GetName().Name + ".g", asm);
            using var stream = rm.GetStream(
                resourcePath.Replace('\\', '/').ToLowerInvariant());
            return stream != null ? FromStream(stream) : null;
        }

        private static BitmapImage FromStream(System.IO.Stream stream)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
    }
}
