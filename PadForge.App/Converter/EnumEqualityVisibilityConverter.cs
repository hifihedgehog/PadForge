using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Returns <see cref="Visibility.Visible"/> when the bound enum
    /// value's name matches the converter parameter, else
    /// <see cref="Visibility.Collapsed"/>. Used to switch between
    /// per-mode UI sections without adding wrapper properties to the
    /// view-model.
    ///
    /// <para>Parameter is case-insensitive on the enum's name and
    /// supports a pipe-separated list to accept multiple values:
    /// <c>ConverterParameter=Thresholds|Gradient|CrossFade</c> shows
    /// the panel for any of those three. Pipe is used instead of
    /// comma because XAML markup extensions parse commas as their own
    /// argument separator.</para>
    /// </summary>
    public sealed class EnumEqualityVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return Visibility.Collapsed;
            string current = value.ToString();
            string param = parameter.ToString();
            // Split pipe-separated list; trim whitespace per entry.
            foreach (var entry in param.Split('|'))
            {
                if (string.Equals(current, entry.Trim(), StringComparison.OrdinalIgnoreCase))
                    return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // One-way binding only.
            return Binding.DoNothing;
        }
    }
}
