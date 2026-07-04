using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>Uppercases header strings for the Ember eyebrow treatment
    /// (#175). Cascadia Code has no OpenType small-caps feature, so
    /// Typography.Capitals cannot do this at the style level.</summary>
    public sealed class UppercaseConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is string s ? s.ToUpper(culture) : value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
