using System;
using System.Globalization;
using System.Windows.Data;
using PadForge.Common.Input;
using PadForge.Resources.Strings;

namespace PadForge.Converters
{
    /// <summary>Maps an <see cref="EqBandType"/> to its localized display
    /// name for the EQ band picker (#347). The picker binds the enum values
    /// directly, so without this it rendered ToString(): six raw English
    /// identifiers, untranslated in every locale and not even spaced
    /// ("LowShelf", "HighPass").
    ///
    /// <para>Same shape as <see cref="DzShapeNameConverter"/>, and the
    /// default arm is Peak deliberately rather than defensively: Peaking is
    /// the enum's zero and the type a new band starts as.</para></summary>
    public class EqBandTypeNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = Strings.Instance;
            return value is EqBandType t ? t switch
            {
                EqBandType.LowShelf => s.Pad_Audio_EqType_LowShelf,
                EqBandType.HighShelf => s.Pad_Audio_EqType_HighShelf,
                EqBandType.HighPass => s.Pad_Audio_EqType_HighPass,
                EqBandType.LowPass => s.Pad_Audio_EqType_LowPass,
                EqBandType.Notch => s.Pad_Audio_EqType_Notch,
                _ => s.Pad_Audio_EqType_Peaking,
            } : s.Pad_Audio_EqType_Peaking;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
