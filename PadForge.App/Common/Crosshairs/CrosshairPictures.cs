using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace PadForge.Common.Crosshairs
{
    /// <summary>
    /// Crosshair pictures read from their files (#585): every format Windows'
    /// imaging reads, PNG, GIF, BMP, ICO, TIFF, JPEG, and WebP through its
    /// codec, with transparency kept. An icon or a TIFF with several pictures
    /// gives its largest. A picture is frozen, so the overlay and the card's
    /// preview share it, and a file is read again once it is written again.
    /// The file is read whole first, so it is never held open. The cache keeps
    /// the pictures read last, one for each crosshair there can be, and lets
    /// go of the one read longest ago, so a picture tried and dropped does not
    /// stay in memory.
    /// </summary>
    internal static class CrosshairPictures
    {
        private static readonly Dictionary<string, (DateTime Written, BitmapSource Picture, long Read)> s_cache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Counts the reads, so the cache knows which picture was read
        /// longest ago.</summary>
        private static long s_reads;

        /// <summary>The picture in a file, or null when there is none or it
        /// cannot be read. The path is stored as PadForge stores a picked
        /// file (IconPackageManager.MakeStoredPath): relative to PadForge's
        /// folder when the file is inside it, whole otherwise.</summary>
        public static BitmapSource Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                path = PadForge.Common.IconPackageManager.ResolvePath(path);
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                lock (s_cache)
                    if (s_cache.TryGetValue(path, out var hit) && hit.Written == info.LastWriteTimeUtc)
                    {
                        s_cache[path] = (hit.Written, hit.Picture, ++s_reads);
                        return hit.Picture;
                    }
                using var stream = new MemoryStream(File.ReadAllBytes(path));
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapSource largest = null;
                foreach (var frame in decoder.Frames)
                    if (largest == null || (long)frame.PixelWidth * frame.PixelHeight > (long)largest.PixelWidth * largest.PixelHeight)
                        largest = frame;
                if (largest == null) return null;
                if (largest.CanFreeze) largest.Freeze();
                lock (s_cache)
                {
                    if (!s_cache.ContainsKey(path) && s_cache.Count >= PadForge.Services.CrosshairData.MaxCrosshairs)
                        s_cache.Remove(ReadLongestAgo());
                    s_cache[path] = (info.LastWriteTimeUtc, largest, ++s_reads);
                }
                return largest;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                or FormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                return null;
            }
        }

        /// <summary>The path of the cached picture read longest ago. Called
        /// under the cache's lock.</summary>
        private static string ReadLongestAgo()
        {
            string oldest = null;
            long read = long.MaxValue;
            foreach (var (path, entry) in s_cache)
                if (entry.Read < read)
                {
                    read = entry.Read;
                    oldest = path;
                }
            return oldest;
        }
    }
}
