using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Common.Recording
{
    /// <summary>A recording's line to its percent words, for the review's
    /// lines (#600).</summary>
    public sealed class RecordingPercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => RecordingPercent.Of(value as string);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
