using System;
using System.Globalization;
using System.Windows.Data;
using PadForge.Views.Controls;

namespace PadForge.Converters
{
    /// <summary>
    /// Converts a boolean (IconRightSide) to a <see cref="LabeledShapeKind"/>.
    /// false → TriggerLeft, true → TriggerRight.
    /// </summary>
    public sealed class BoolToTriggerShapeKindConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? LabeledShapeKind.TriggerRight : LabeledShapeKind.TriggerLeft;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
