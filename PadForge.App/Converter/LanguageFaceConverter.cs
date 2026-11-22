using System;
using System.Globalization;
using System.Windows.Data;
using PadForge.Common;

namespace PadForge.Converters
{
    /// <summary>
    /// The face a language's own name is drawn in, in the language picker
    /// (#590): Galactic Basic's own name in the Aurebesh font, every other
    /// language's in the standard body face. The picker keeps these faces
    /// under every interface language, so Galactic Basic shows its name in
    /// Aurebesh while every other row, and each row's English name, stays
    /// readable after Galactic Basic is chosen.
    /// </summary>
    public sealed class LanguageFaceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => TextFaces.OwnNameFace(value as LanguageInfo);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
