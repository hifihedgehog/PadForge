using System;
using System.Globalization;
using System.Windows.Data;
using PadForge.Resources.Strings;

namespace PadForge.Converters
{
    /// <summary>Maps a deadzone shape index to its localized display name
    /// for the Sticks header subtitle (#175). Exists so the subtitle Run
    /// can use a plain Text binding: a Style with bound Setters and
    /// DataTriggers on a Run inside a template activates style bindings
    /// through StyleHelper.GetInstanceValue on a FrameworkContentElement,
    /// which throws during template load and (through the error dialog)
    /// recursed to a stack overflow (dump-confirmed 2026-07-04).</summary>
    public class DzShapeNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int idx = value is int i ? i : 0;
            var s = Strings.Instance;
            return idx switch
            {
                1 => s.Pad_DZRadial,
                2 => s.Pad_DZAxial,
                3 => s.Pad_DZHybrid,
                4 => s.Pad_DZSlopedScaledAxial,
                5 => s.Pad_DZSlopedAxial,
                _ => s.Pad_DZScaledRadial,
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
