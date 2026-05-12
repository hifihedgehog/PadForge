using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>Converts <see cref="PadForge.ViewModels.PadViewModel.ActiveLayerMask"/>
    /// to a Visibility value. Returns <c>Collapsed</c> when the active mask
    /// is <c>"Base"</c> (the NoInherit column has no meaning on Base) and
    /// <c>Visible</c> otherwise. Used by the per-row NoInherit checkbox
    /// in the Mappings DataGrid.</summary>
    public class ShiftLayerVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string mask = value as string;
            return string.IsNullOrEmpty(mask) || string.Equals(mask, "Base", StringComparison.Ordinal)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
