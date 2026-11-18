using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PadForge.Common
{
    /// <summary>The app's one family for text made in code: the body face
    /// (App.xaml's BodyFontFamily), set on the text itself so text measured
    /// before it joins the visual tree measures as it draws. A number also
    /// takes tabular figures, so each digit is as wide as the next and a
    /// readout keeps its width while its value changes.</summary>
    public static class TextFaces
    {
        /// <summary>The faces for an emoji a user picked as an icon: the
        /// color emoji face, the symbol face, then the app's body face for a
        /// character neither has. XAML takes it by x:Static, so a window
        /// built without the app's resources still draws it.</summary>
        public static readonly FontFamily Emoji = new("Segoe UI Emoji, Segoe UI Symbol, Segoe UI Variable Text, Segoe UI");

        /// <summary>The body face, on a TextBlock or a Run.</summary>
        internal static T Body<T>(T text) where T : DependencyObject
        {
            if (Application.Current?.TryFindResource("BodyFontFamily") is FontFamily body)
                text.SetValue(TextElement.FontFamilyProperty, body);
            return text;
        }

        /// <summary>The body face with tabular figures, on a TextBlock or a
        /// Run, for a number that changes or stands in a column.</summary>
        internal static T Numbers<T>(T text) where T : DependencyObject
        {
            Body(text);
            Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
            return text;
        }

        /// <summary>The body face as a Typeface, for text measured with
        /// FormattedText.</summary>
        internal static Typeface BodyTypeface(FontWeight weight)
            => new(Application.Current?.TryFindResource("BodyFontFamily") as FontFamily ?? SystemFonts.MessageFontFamily,
                FontStyles.Normal, weight, FontStretches.Normal);

        /// <summary>The display face as a Typeface, for a large glyph drawn
        /// as geometry.</summary>
        internal static Typeface DisplayTypeface(FontWeight weight)
            => new(Application.Current?.TryFindResource("DisplayFontFamily") as FontFamily ?? SystemFonts.MessageFontFamily,
                FontStyles.Normal, weight, FontStretches.Normal);
    }
}
