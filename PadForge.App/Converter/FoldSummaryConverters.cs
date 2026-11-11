using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PadForge.Converters
{
    /// <summary>
    /// The state a folded card shows at the end of its line (#574): On or
    /// Off. Values: one or more flags, then the On text, then the Off text.
    /// The card is on when any flag is. A flag is a bool, or a number or an
    /// enum where anything but zero counts as on (a trigger's rumble source,
    /// whose first value is None). The two texts are bound to the strings
    /// table, so a change of language reads the line again, which a
    /// one-value converter that looked the words up itself would not.
    /// </summary>
    public sealed class OnOffTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 3) return string.Empty;
            bool on = false;
            for (int i = 0; i < values.Length - 2 && !on; i++)
                on = IsOn(values[i]);
            return (on ? values[values.Length - 2] : values[values.Length - 1]) as string ?? string.Empty;
        }

        /// <summary>Whether one flag counts as on. A binding with no value
        /// yet is off.</summary>
        internal static bool IsOn(object flag)
        {
            switch (flag)
            {
                case bool b:
                    return b;
                case Enum e:
                    return System.Convert.ToInt64(e, CultureInfo.InvariantCulture) != 0;
                case string:
                    return false;
                case IConvertible number:
                    try { return number.ToDouble(CultureInfo.InvariantCulture) != 0; }
                    catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException) { return false; }
                default:
                    return false;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// A folded card's line for a setting picked from a list (#574): the
    /// name the list shows for the stored value. Values: the stored value,
    /// then the list of options (each a <see cref="ViewModels.GyroLabeledOption"/>),
    /// then any string of the strings table. The converter never reads the
    /// third. It is bound so that a change of language reads the line
    /// again, since an option looks its name up when asked and raises no
    /// change of its own. A value the list does not hold reads as itself.
    /// </summary>
    public sealed class OptionTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return string.Empty;
            if (values[0] is not string stored) return string.Empty;
            if (values[1] is System.Collections.IEnumerable options)
            {
                foreach (object option in options)
                {
                    if (option is ViewModels.GyroLabeledOption labeled
                        && string.Equals(labeled.Value, stored, StringComparison.Ordinal))
                        return labeled.Display ?? string.Empty;
                }
            }
            return stored;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// A folded card's line made of values (#574): the values, then the
    /// format they go into, bound to the strings table so the words between
    /// the values are the language's own. Empty until every value is there.
    /// </summary>
    public sealed class FormatTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return string.Empty;
            if (values[values.Length - 1] is not string format || format.Length == 0) return string.Empty;
            var args = new object[values.Length - 1];
            for (int i = 0; i < args.Length; i++)
            {
                if (values[i] == null || values[i] == DependencyProperty.UnsetValue) return string.Empty;
                args[i] = values[i];
            }
            try { return string.Format(culture ?? CultureInfo.CurrentCulture, format, args); }
            catch (FormatException) { return string.Empty; }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
