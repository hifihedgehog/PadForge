using System;
using System.Globalization;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// Two-way converts between <see cref="Guid"/> and lowercase
    /// string. Used by the Phase 2C cascading device picker — the
    /// per-source MappingSourceItem stores DeviceGuid as a lowercase
    /// string while the slot's MappedDeviceInfo.InstanceGuid is a
    /// typed Guid. Empty / null on either side maps to
    /// <see cref="Guid.Empty"/> / "" — no exceptions thrown.
    /// </summary>
    public sealed class GuidStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Guid g) return g;
            if (value is string s)
                return Guid.TryParse(s, out var parsed) ? parsed : Guid.Empty;
            return Guid.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Guid g)
                return g == Guid.Empty ? "" : g.ToString().ToLowerInvariant();
            if (value is string s) return s ?? "";
            return "";
        }
    }
}
