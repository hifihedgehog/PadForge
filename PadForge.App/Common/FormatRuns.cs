using System;
using System.Globalization;

namespace PadForge.Common
{
    /// <summary>
    /// A sentence taken apart around one of its values (#574), so a dialog
    /// can draw that value in a run of its own: a file's name in the code
    /// face, a spoken phrase in bold, a countdown's number. Home's offer
    /// line splits its sentence the same way.
    /// </summary>
    internal static class FormatRuns
    {
        /// <summary>A sentence in two parts around its {0}, its other places
        /// filled from <paramref name="others"/> ({1} is others[0]). A
        /// language may put {1} before {0}. A format with no {0} is all
        /// before.</summary>
        internal static (string Before, string After) Split(string format, params object[] others)
        {
            var args = new object[others.Length + 1];
            args[0] = string.Empty;
            Array.Copy(others, 0, args, 1, others.Length);
            int at = format.IndexOf("{0}", StringComparison.Ordinal);
            if (at < 0) return (string.Format(CultureInfo.CurrentCulture, format, args), string.Empty);
            return (string.Format(CultureInfo.CurrentCulture, format.Substring(0, at), args),
                    string.Format(CultureInfo.CurrentCulture, format.Substring(at + 3), args));
        }

        /// <summary>A text of one value in three parts, so the value can be
        /// drawn larger than its unit: what the language puts before the
        /// value, the value written as its place asks ({0:F0} writes no
        /// decimals), and what comes after. A format with no place for the
        /// value is all before.</summary>
        internal static (string Before, string Value, string After) SplitValue(string format, object value)
        {
            var culture = CultureInfo.CurrentCulture;
            var hole = ValueHole.Match(format);
            if (!hole.Success) return (string.Format(culture, format, value), string.Empty, string.Empty);
            return (string.Format(culture, format.Substring(0, hole.Index), value),
                    string.Format(culture, hole.Value, value),
                    string.Format(culture, format.Substring(hole.Index + hole.Length), value));
        }

        private static readonly System.Text.RegularExpressions.Regex ValueHole = new(@"\{0(:[^{}]*)?\}");

        /// <summary>A sentence in five parts around its two values, so both
        /// can be drawn in runs of their own: the words before the first
        /// value, that value, the words between, the second value and the
        /// words after, in the order this language's sentence puts them. A
        /// sentence without both plain places, {0} and {1}, is all in the
        /// first part, written whole as string.Format writes it.</summary>
        internal static string[] SplitTwo(string format, object first, object second)
        {
            var culture = CultureInfo.CurrentCulture;
            int a = format.IndexOf("{0}", StringComparison.Ordinal), b = format.IndexOf("{1}", StringComparison.Ordinal);
            if (a < 0 || b < 0)
                return new[] { string.Format(culture, format, first, second), string.Empty, string.Empty, string.Empty, string.Empty };
            var (earlier, earlierValue, later, laterValue) = a < b ? (a, first, b, second) : (b, second, a, first);
            return new[]
            {
                string.Format(culture, format.Substring(0, earlier), first, second),
                string.Format(culture, "{0}", earlierValue),
                string.Format(culture, format.Substring(earlier + 3, later - earlier - 3), first, second),
                string.Format(culture, "{0}", laterValue),
                string.Format(culture, format.Substring(later + 3), first, second),
            };
        }
    }
}
