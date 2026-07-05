using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PadForge.Converters
{
    /// <summary>
    /// Converts a full executable path into that exe's shell icon for the
    /// profile cards (#175). Returns null when the path is empty, the file
    /// no longer exists, or extraction fails. The card collapses its icon
    /// slot in that case. Extracted icons are cached per path (frozen, so
    /// they are shareable across items and re-templates) because profile
    /// cards re-render on every list refresh.
    /// </summary>
    public sealed class ExeIconConverter : IValueConverter
    {
        // Case-insensitive: profile paths come from file dialogs and XML
        // round-trips, which can differ only in casing. Converters run on
        // the UI thread only, so no lock is needed.
        private static readonly Dictionary<string, ImageSource> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string path || string.IsNullOrEmpty(path))
                return null;

            if (Cache.TryGetValue(path, out var cached))
                return cached;

            ImageSource source = null;
            try
            {
                // Only a resolvable on-disk path yields an icon; imported
                // profiles can carry paths from another machine.
                if (File.Exists(path))
                {
                    // Icon.Dispose destroys the extracted HICON after the
                    // frozen BitmapSource has copied the pixels.
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    if (icon != null)
                    {
                        var bmp = Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        bmp.Freeze();
                        source = bmp;
                    }
                }
            }
            catch
            {
                // Missing shell association, locked file, etc. No icon.
                source = null;
            }

            // Failures cache as null too, so a dead path is probed once.
            Cache[path] = source;
            return source;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
