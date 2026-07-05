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
        internal static BitmapImage Load(string resourcePath)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            string resName = resourcePath.Replace('/', '.').Replace('\\', '.').ToLowerInvariant();

            // WPF resources are in "<assemblyname>.g.resources" → find via ResourceManager.
            var rm = new System.Resources.ResourceManager(
                asm.GetName().Name + ".g", asm);
            using var stream = rm.GetStream(resName.Replace('.', '/'));
            if (stream == null)
            {
                // Fallback: try the original path as-is (forward slashes, lowercase).
                using var stream2 = rm.GetStream(resourcePath.ToLowerInvariant());
                return stream2 != null ? FromStream(stream2) : null;
            }
            return FromStream(stream);
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
