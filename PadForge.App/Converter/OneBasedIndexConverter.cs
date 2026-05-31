using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Renders a 0-based index as its 1-based number for display
    /// ("0" -> "1", "1" -> "2"). The bound value stays 0-based; only the
    /// shown text shifts. Used by the Touchpad-tab pad selector so the
    /// dropdown reads "1"/"2" next to the "Touchpad" label, matching the
    /// 1-based pad numbering used by the previews and mapping picker.
    /// </summary>
    public sealed class OneBasedIndexConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is int i ? i + 1 : 0).ToString(culture);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
