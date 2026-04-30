using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Bridges an enum-typed source property and an int-typed target
    /// (typically <c>ComboBox.SelectedIndex</c>). The enum's underlying
    /// values are assumed to be sequential 0..N matching the
    /// ComboBoxItem order — every PadForge enum used with this converter
    /// satisfies that contract (e.g. <c>AdaptiveTriggerMode</c>'s seven
    /// values map 1:1 to the seven trigger-mode dropdown rows).
    /// </summary>
    public sealed class EnumIndexConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return 0;
            if (value is int i) return i;
            if (value is Enum) return System.Convert.ToInt32(value, culture);
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return 0;
            int i = System.Convert.ToInt32(value, culture);
            if (targetType != null && targetType.IsEnum)
                return Enum.ToObject(targetType, i);
            return i;
        }
    }
}
